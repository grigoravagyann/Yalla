using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Infrastructure;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A throwaway SQL Server database for one integration test class, leased from <see cref="TestDatabasePool"/>
/// for as long as the class runs.
/// </summary>
/// <remarks>
/// <para>
/// These tests need a real database rather than the in-memory provider, because the two things
/// they are proving are things only SQL Server does: <c>rowversion</c> concurrency tokens and
/// filtered unique indexes. The in-memory provider would pass every assertion while enforcing
/// neither, which is worse than not running at all.
/// </para>
/// <para>
/// When no server is reachable the tests <b>skip</b> rather than fail or silently pass, so a
/// machine without SQL Server gives an honest result.
/// </para>
/// <para>
/// The schema is created with <c>Migrate</c>, not <c>EnsureCreated</c>, so the migrations
/// themselves are exercised - including the check constraints and filtered indexes that
/// <c>EnsureCreated</c> would build from the model and never validate. The pool migrates its
/// first database and restores copies of it for the rest, so every class gets that schema.
/// </para>
/// <para>
/// <b>One class per database at a time.</b> SQL-backed classes run in parallel, each with its
/// own fixture and its own leased database, and no other class touches that database until it is
/// handed back. Inside a class the tests still run one at a time against data earlier classes
/// may have left, as they always did. <c>YALLA_TEST_SERIAL=1</c> puts every SQL-backed class back
/// into one collection and one database; see <see cref="TestDatabasePool"/>.
/// </para>
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>
    /// Environment variable naming the server to test against, e.g. <c>localhost\SQLEXPRESS</c>.
    /// </summary>
    /// <remarks>
    /// Set this in CI. Locally the probe in <see cref="TestDatabasePool"/> usually finds the right one on its own.
    /// </remarks>
    public const string ServerEnvironmentVariable = "YALLA_TEST_SQL_SERVER";

    /// <summary>
    /// SQL login to authenticate with. Unset means Windows authentication.
    /// </summary>
    /// <remarks>
    /// A developer machine uses <c>Trusted_Connection</c> and needs no credentials at all, which is
    /// why that stays the default. CI runs SQL Server in a Linux container that has no notion of a
    /// Windows identity, so it sets these two - and without them the fixture would find no server,
    /// <b>skip</b> every integration test, and report green while proving nothing.
    /// </remarks>
    public const string UserEnvironmentVariable = "YALLA_TEST_SQL_USER";

    /// <summary>Password for <see cref="UserEnvironmentVariable"/>.</summary>
    public const string PasswordEnvironmentVariable = "YALLA_TEST_SQL_PASSWORD";

    private TestDatabasePool.Lease? _lease;

    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The database this class has to itself while it runs.</summary>
    public string DatabaseName => _lease?.Database ?? string.Empty;

    public async Task InitializeAsync()
    {
        var lease = await TestDatabasePool.LeaseAsync();

        if (lease.SkipReason is { } reason)
        {
            SkipReason = reason;

            return;
        }

        _lease = lease;
        ConnectionString = lease.ConnectionString!;
        IsAvailable = true;
    }

    /// <summary>
    /// Hands the database back for the next class. It is dropped with the rest when the run ends
    /// (<see cref="TestDatabasePool.DropAllAsync"/>), not here.
    /// </summary>
    public Task DisposeAsync()
    {
        if (_lease is { } lease)
        {
            _lease = null;
            TestDatabasePool.Return(lease);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A fresh context over the test database. Each one is its own unit of work. Interceptors are
    /// how the floor tests count the statements actually sent, which is the only way to catch an
    /// N+1 - the results look identical either way.
    /// </summary>
    public YallaDbContext CreateContext(IClock clock, params IInterceptor[] interceptors) =>
        new(
            new DbContextOptionsBuilder<YallaDbContext>()
                .UseSqlServer(ConnectionString)
                .EnableSensitiveDataLogging()
                .AddInterceptors(interceptors)
                .Options,
            clock);

    /// <summary>The state machine wired over one context, acting as the given staff member.</summary>
    /// <remarks>
    /// <paramref name="lockOptions"/> is how a test asks for a short wait. The default five seconds
    /// is right for a venue and far too long for a test that wants to prove a writer gives up.
    /// </remarks>
    internal TableStateService CreateService(
        YallaDbContext db,
        IClock clock,
        ICurrentActor actor,
        BookingLockOptions? lockOptions = null) =>
        new(db, clock, actor, CreateTableLock(db, lockOptions), CreateActivityLog(db, clock, actor),
            CreateLedger(db, clock, actor), NullLogger<TableStateService>.Instance);

    /// <summary>The activity log over one context, acting as the given caller - what every writing service records through.</summary>
    internal static ActivityLog CreateActivityLog(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, actor, clock);

    internal FloorQuery CreateFloorQuery(YallaDbContext db, IClock clock) => new(db, clock);

    /// <summary>
    /// The tab service over one context, acting as the given caller.
    /// </summary>
    /// <remarks>
    /// It composes the real table state machine and the real read model over the same context, so
    /// opening a tab exercises the actual seating transition and its concurrency handling rather
    /// than a stand-in. The two concurrency tests build two of these over <b>separate</b> contexts,
    /// which is the only way to make two commits genuinely race.
    /// </remarks>
    internal TabService CreateTabService(YallaDbContext db, IClock clock, ICurrentActor actor)
    {
        var jwt = Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            // Participant tokens are minted for real here; an empty key cannot sign one.
            SigningKey = "test-signing-key-that-is-comfortably-longer-than-thirty-two-bytes",
        });
        var issuer = new TokenIssuer(jwt, clock);
        var tokens = new TabParticipantTokens(issuer, clock, jwt);
        var tableState = CreateService(db, clock, actor);
        var query = new TabQuery(db);

        return new TabService(
            db,
            clock,
            actor,
            tableState,
            query,
            CreateTokenAuthority(db, clock),
            tokens,
            Microsoft.Extensions.Options.Options.Create(new TabOptions()),
            CreateLedger(db, clock, actor),
            CreateOutbox(db, clock),
            CreateBranchGuard(db, actor),
            CreateActivityLog(db, clock, actor),
            NullLogger<TabService>.Instance);
    }

    /// <summary>
    /// The tab's books over one context: totals recomputation, the event stream, and the retry - and the domain events
    /// of whatever it saves, handled by the handlers production registers, for this caller (<see cref="CreateUnitOfWork"/>).
    /// </summary>
    internal TabLedger CreateLedger(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        CreateUnitOfWork(db, clock, actor).GetRequiredService<TabLedger>();

    /// <summary>
    /// What a request's scope holds for the domain-event handlers (<c>docs/domain-events.md</c>): this context, clock and
    /// caller, the outbox, the activity log, the ledger and the dispatcher, and every handler
    /// <c>DependencyInjection.AddDomainEventHandlers</c> registers - the same list production runs.
    /// </summary>
    /// <remarks>
    /// Per caller, not per context: these tests often act as several people over one shared context, and a request
    /// never does - so a ledger dispatches with its own caller's handlers, and the activity log names that caller. A bare
    /// <see cref="CreateContext"/> has none, and a save on it that finds an event pending fails rather than drop its
    /// side effects.
    /// </remarks>
    internal static IServiceProvider CreateUnitOfWork(YallaDbContext db, IClock clock, ICurrentActor actor)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(clock);
        services.AddSingleton(actor);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<Yalla.Application.Messaging.IOutbox>(new Outbox(db, clock, NullLogger<Outbox>.Instance));
        services.AddSingleton<Yalla.Application.Activity.IActivityLog>(CreateActivityLog(db, clock, actor));
        services.AddSingleton<TabLedger>();
        services.AddSingleton<DomainEventDispatcher>();
        services.AddDomainEventHandlers();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The branch boundary, over one context. Composed in rather than stubbed: it is the check
    /// that keeps one branch off another's tabs, so a double here could pass while the real one
    /// was missing - which is exactly how it came to be missing from two services.
    /// </summary>
    internal StaffBranchGuard CreateBranchGuard(YallaDbContext db, ICurrentActor actor) =>
        new(db, actor);

    /// <summary>Ordering, voiding and adjusting, over one context.</summary>
    internal TabOrderService CreateOrderService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateBranchGuard(db, actor), CreateLedger(db, clock, actor),
            NullLogger<TabOrderService>.Instance);

    /// <summary>Cash, over one context. Two of these over separate contexts is how payments race.</summary>
    internal TabPaymentService CreatePaymentService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateBranchGuard(db, actor), CreateLedger(db, clock, actor),
            NullLogger<TabPaymentService>.Instance);

    /// <summary>Calling a waiter, over one context.</summary>
    internal ServiceRequestService CreateServiceRequests(
        YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateBranchGuard(db, actor), CreateLedger(db, clock, actor),
            NullLogger<ServiceRequestService>.Instance);

    /// <summary>Shares and the event stream, over one context.</summary>
    internal TabBillingQuery CreateBillingQuery(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        CreateBillingQuery(db, clock, actor, new TabQuery(db));

    /// <summary>The same, reading the tab through <paramref name="tabs"/> - to count the reads.</summary>
    internal TabBillingQuery CreateBillingQuery(
        YallaDbContext db, IClock clock, ICurrentActor actor, Yalla.Application.Tabs.ITabQuery tabs) =>
        new(db, CreateLedger(db, clock, actor), tabs, NullLogger<TabBillingQuery>.Instance);

    /// <summary>The diner-facing menu read.</summary>
    internal static MenuQuery CreateMenuQuery(YallaDbContext db) => new(db);

    /// <summary>
    /// The real authority check over one context, with its own cache so tests do not share one.
    /// </summary>
    internal TokenAuthorityCheck CreateTokenAuthority(YallaDbContext db, IClock clock) =>
        new(
            new AuthorizationQueries(db),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            clock,
            Microsoft.Extensions.Options.Options.Create(new JwtOptions()));

    /// <summary>The tab read model over one context, for asserting on a projected view directly.</summary>
    internal TabQuery CreateTabQuery(YallaDbContext db) => new(db);

    /// <summary>The platform tier over one context, acting as the given caller - normally a platform admin.</summary>
    internal PlatformService CreatePlatformService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateReadinessQuery(db), NullLogger<PlatformService>.Instance);

    /// <summary>
    /// The platform console's read models over one context - Home, a venue's bookings, the activity
    /// feed - with the real checklist and the real outbox composed in, so Home's numbers are the ones
    /// those answer.
    /// </summary>
    internal PlatformOverviewQuery CreatePlatformOverviewQuery(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateReadinessQuery(db), CreateOutbox(db, clock));

    /// <summary>The venue console's Overview over one context, with the real branch guard.</summary>
    internal BranchOverviewQuery CreateBranchOverviewQuery(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, CreateBranchGuard(db, actor));

    /// <summary>Review moderation over one context, with the real branch guard.</summary>
    internal ReviewModerationService CreateReviewModerationService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateBranchGuard(db, actor), NullLogger<ReviewModerationService>.Instance);

    /// <summary>
    /// The platform admin's own account over one context: the real hasher and the real token store,
    /// so a changed password is the hash sign-in checks and a revoked token is the row refresh reads.
    /// </summary>
    internal PlatformAdminAccountService CreatePlatformAdminAccountService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, actor, CreateOwnAccountChanges(db, clock, actor));

    /// <summary>
    /// The rules a console user's own name and password change by - the platform admin's and a venue
    /// user's - over one context, with the real hasher, token store and issuer.
    /// </summary>
    internal OwnAccountChanges CreateOwnAccountChanges(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(
            db,
            clock,
            actor,
            new SecretHasher(),
            new RefreshTokenStore(db, clock),
            new TokenIssuer(
                Microsoft.Extensions.Options.Options.Create(new JwtOptions
                {
                    SigningKey = "test-signing-key-that-is-comfortably-longer-than-thirty-two-bytes",
                }),
                clock),
            CreateTokenAuthority(db, clock),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            NullLogger<OwnAccountChanges>.Instance);

    /// <summary>
    /// The onboarding checklist over one context.
    /// </summary>
    /// <remarks>
    /// Composed into the platform service rather than stubbed, so the going-live gate is tested
    /// against the same count the readiness endpoint reports. A stub here could pass while the two
    /// disagreed, which is the failure the single definition of completeness exists to prevent.
    /// </remarks>
    internal static BranchReadinessQuery CreateReadinessQuery(YallaDbContext db) => new(db);

    /// <summary>The five report groups over one context, acting as the given caller.</summary>
    /// <remarks>
    /// The actor is composed in rather than stubbed because a venue rollup is refused for anyone
    /// below an owner, and that rule lives in the query rather than at the endpoint - a rollup
    /// crosses branches, so the branch-scoped policy no longer covers it.
    /// </remarks>
    internal static ReportQuery CreateReportQuery(YallaDbContext db, ICurrentActor actor) => new(db, actor);

    /// <summary>The anonymous public surface over one context, with its own cache.</summary>
    internal static PublicVenueQuery CreatePublicQuery(YallaDbContext db, IClock clock) =>
        new(
            db,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            clock,
            CreateMenuQuery(db));

    internal BranchSettingsService CreateBranchSettingsService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateBranchGuard(db, actor), CreateActivityLog(db, clock, actor),
            NullLogger<BranchSettingsService>.Instance);

    internal MenuService CreateMenuService(YallaDbContext db, ICurrentActor? actor = null, IClock? clock = null) =>
        new(db, CreateActivityLog(db, clock ?? new TestClock(DateTime.UtcNow), actor ?? new TestActor(ActorType.System, null, null)));

    /// <summary>The Staff screen's shifts, guarded as the console's other branch writes are.</summary>
    internal StaffShiftService CreateStaffShiftService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, CreateBranchGuard(db, actor), NullLogger<StaffShiftService>.Instance);

    internal StaffManagementService CreateStaffManagementService(
        YallaDbContext db,
        IClock clock,
        ICurrentActor actor,
        ILogger<StaffManagementService>? logger = null) =>
        new(
            db,
            clock,
            actor,
            new SecretHasher(),
            new RefreshTokenStore(db, clock),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            CreateActivityLog(db, clock, actor),
            new TokenAuthorityCheck(
                new AuthorizationQueries(db),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                clock,
                Microsoft.Extensions.Options.Options.Create(new JwtOptions())),
            logger ?? NullLogger<StaffManagementService>.Instance);

    /// <summary>
    /// The admin-panel sign-in and password-reset flows over one context.
    /// </summary>
    /// <remarks>
    /// The real hasher and the real token store, so a test that issues a sign-in through the
    /// staff service and then consumes the link here is proving the two agree about the hash on
    /// disk - which is the whole hand-off. The sender is a stand-in that delivers nothing, because
    /// the issue path never calls it and the request path's delivery is the HTTP tests' concern.
    /// </remarks>
    internal VenueUserAuthService CreateVenueUserAuthService(YallaDbContext db, IClock clock)
    {
        var jwt = Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            SigningKey = "test-signing-key-that-is-comfortably-longer-than-thirty-two-bytes",
        });

        return new VenueUserAuthService(
            db,
            clock,
            new TokenIssuer(jwt, clock),
            new RefreshTokenStore(db, clock),
            new SecretHasher(),
            new SilentPasswordResetSender(),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            CreateTokenAuthority(db, clock),
            NullLogger<VenueUserAuthService>.Instance);
    }

    private sealed class SilentPasswordResetSender : IPasswordResetSender
    {
        public Task SendAsync(string email, string resetLink, string localeCode, CancellationToken ct) =>
            Task.CompletedTask;
    }

    internal AvailabilityQuery CreateAvailabilityQuery(YallaDbContext db, IClock clock) => new(db, clock);

    /// <summary>
    /// The booking service over one context.
    /// </summary>
    /// <remarks>
    /// Each concurrency test builds two of these over <b>separate</b> contexts and therefore
    /// separate connections, which is the only way to make two transactions genuinely race. Two
    /// services sharing one context would serialise on the context itself and prove nothing.
    /// </remarks>
    internal ReservationService CreateReservationService(
        YallaDbContext db,
        IClock clock,
        ICurrentActor actor,
        NoShowPolicy? noShowPolicy = null,
        BookingLockOptions? lockOptions = null)
    {
        EnsureVerifiedDinerRow(clock, actor);

        var tableState = CreateService(db, clock, actor);
        var outbox = CreateOutbox(db, clock);

        return new(
            db,
            clock,
            actor,
            CreateAvailabilityQuery(db, clock),
            new AuthorizationQueries(db),
            CreateBranchGuard(db, actor),
            noShowPolicy ?? new NoShowPolicy(),
            CreateReservationWriter(db, clock, lockOptions),
            tableState,
            new NoShowBlocks(db, outbox, tableState, CreateActivityLog(db, clock, actor), NullLogger<NoShowBlocks>.Instance),
            outbox,

            // The manage-link template. Its default is the local dev page, which is all a test
            // asserting on the payload needs; the one test that pins the URL sets it explicitly.
            Options.Create(new PublicWebOptions()),
            CreateActivityLog(db, clock, actor),
            NullLogger<ReservationService>.Instance);
    }

    /// <summary>Pre-orders over one context: the guest's edits, the floor's rail and actions, the console's settings.</summary>
    internal PreOrderService CreatePreOrderService(YallaDbContext db, IClock clock, ICurrentActor actor)
    {
        EnsureVerifiedDinerRow(clock, actor);

        return new(
            db,
            clock,
            actor,
            new AuthorizationQueries(db),
            CreateBranchGuard(db, actor),
            CreateTableLock(db),
            CreateOutbox(db, clock),
            CreateLedger(db, clock, actor),
            CreateActivityLog(db, clock, actor),
            NullLogger<PreOrderService>.Instance);
    }

    /// <summary>
    /// Gives a diner actor with no account row a verified one, so the booking service's phone gate
    /// has something real to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TestActor.Diner()</c> mints an id and nothing else, which was enough while booking only
    /// looked at the actor. Booking now reads <c>DinerUsers.PhoneVerifiedAtUtc</c>, because a number
    /// typed at sign-up and never proved must not collect reminders and no-shows - so a service test
    /// about table rules needs the account a real diner would have: one the code flow created.
    /// </para>
    /// <para>
    /// Insert-only and on its own context. A row that already exists is left exactly as the test
    /// made it, so a test that wants an unverified diner makes one and is refused as production
    /// would refuse it; and nothing is added to the context the service runs on, so no test's
    /// change tracking or transaction sees the seed. Raw SQL because <c>DinerUser.Id</c> is assigned
    /// by its constructor and the actor's id is already fixed.
    /// </para>
    /// </remarks>
    private void EnsureVerifiedDinerRow(IClock clock, ICurrentActor actor)
    {
        if (actor.Type != ActorType.Diner || actor.DinerUserId is not { } dinerUserId || dinerUserId == Guid.Empty)
        {
            return;
        }

        var nowUtc = clock.UtcNow;
        var phone = $"+3740{Random.Shared.Next(10_000_000, 99_999_999)}";

        using var seed = CreateContext(clock);

        seed.Database.ExecuteSql(
            $"""
            IF NOT EXISTS (SELECT 1 FROM dbo.DinerUsers WHERE Id = {dinerUserId})
                INSERT INTO dbo.DinerUsers (Id, CreatedAtUtc, IsActive, LocaleCode, PhoneE164, PhoneVerifiedAtUtc, LastSignInAtUtc)
                VALUES ({dinerUserId}, {nowUtc}, 1, 'en', {phone}, {nowUtc}, {nowUtc});
            """);
    }

    /// <summary>
    /// Lets a tab a scan opened order, as a waiter's admission would - for the tests about everything after
    /// it (ordering, paying, the stream). Admission itself is <see cref="TabAdmissionTests"/>'s subject.
    /// </summary>
    /// <summary>
    /// Makes bookings what a booking from before the one-active-booking-per-number rule (2026-10-20) is:
    /// held by no number. For the tests of rules about one diner's several live bookings - overlaps, a link
    /// reaching only its own booking - which can only be arranged that way now.
    /// </summary>
    internal async Task MakeBookingsFromBeforeTheNumberRuleAsync(IClock clock, params Guid[] reservationIds)
    {
        await using var db = CreateContext(clock);

        foreach (var id in reservationIds)
        {
            await db.Database.ExecuteSqlAsync($"UPDATE dbo.Reservations SET BookerPhoneE164 = NULL WHERE Id = {id}");
        }
    }

    /// <summary>
    /// The same, through the test's own context, reloading the bookings it tracks so the next write through
    /// it carries their new row versions.
    /// </summary>
    internal async Task MakeBookingsFromBeforeTheNumberRuleAsync(YallaDbContext db, params Guid[] reservationIds)
    {
        foreach (var id in reservationIds)
        {
            await db.Database.ExecuteSqlAsync($"UPDATE dbo.Reservations SET BookerPhoneE164 = NULL WHERE Id = {id}");
        }

        foreach (var entry in db.ChangeTracker.Entries<Domain.Occupancy.Reservation>()
                     .Where(e => reservationIds.Contains(e.Entity.Id))
                     .ToList())
        {
            await entry.ReloadAsync();
        }
    }

    /// <summary>
    /// Moves bookings' sittings earlier by <paramref name="by"/> - for the tests of the no-show history, whose
    /// no-shows must be days apart: three whose starts fall within 72 hours block the guest for a week
    /// (<c>NoShowBlockRules</c>, 2026-10-20), which is not what those tests are about.
    /// </summary>
    internal async Task MoveBookingsBackAsync(IClock clock, TimeSpan by, params Guid[] reservationIds)
    {
        await using var db = CreateContext(clock);
        var seconds = (int)by.TotalSeconds;

        foreach (var id in reservationIds)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.Reservations SET StartUtc = DATEADD(second, -{seconds}, StartUtc), EndUtc = DATEADD(second, -{seconds}, EndUtc) WHERE Id = {id}");
        }
    }

    internal Task AdmitTabAsync(Guid tabId) => AdmitTabAsync(new TestClock(TestNow.UtcNow), tabId);

    /// <inheritdoc cref="AdmitTabAsync(Guid)"/>
    internal async Task AdmitTabAsync(IClock clock, Guid tabId)
    {
        await using var db = CreateContext(clock);

        await db.Database.ExecuteSqlAsync(
            $"UPDATE dbo.Tabs SET Admission = 2, AdmittedBecause = 1, AdmissionDecidedAtUtc = {clock.UtcNow} WHERE Id = {tabId} AND Admission = 1");
    }

    /// <summary>The outbox over one context. Writes only - dispatching is its own thing.</summary>
    internal Outbox CreateOutbox(YallaDbContext db, IClock clock) =>
        new(db, clock, NullLogger<Outbox>.Instance);

    /// <summary>
    /// Empties the outbox table, for a test that is about to drive the dispatcher.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other kind of test gets its isolation from a fresh branch, because everything else in
    /// the system is scoped to one. The dispatcher is not: it claims whatever is due, whoever wrote
    /// it, which is the whole point of it. So "it sent two messages" is only a statement about this
    /// test if the table held only this test's messages.
    /// </para>
    /// <para>
    /// Scoping the assertions to our own keys instead would be weaker - it would say our message was
    /// handled, and say nothing about a dispatcher that also sent somebody else's twice. The
    /// database is this class's alone while it runs and its tests run one at a time, so nothing
    /// else is mid-flight when this runs.
    /// </para>
    /// <para>
    /// Call it <b>before</b> creating the bookings whose messages the test is about.
    /// </para>
    /// </remarks>
    internal async Task ClearOutboxAsync(IClock clock)
    {
        await using var db = CreateContext(clock);

        await db.OutboxMessages.ExecuteDeleteAsync();
    }

    /// <summary>
    /// The short-notice approval sweep over one context, as one pass of the hosted service runs it.
    /// </summary>
    internal ApprovalExpirySweep CreateApprovalExpirySweep(
        YallaDbContext db,
        IClock clock,
        Microsoft.Extensions.Logging.ILogger<ApprovalExpirySweep>? logger = null) =>
        new(
            db,
            clock,
            CreateOutbox(db, clock),
            CreateReservationWriter(db, clock),
            CreateActivityLog(db, clock, new TestActor(ActorType.System, null, null)),
            logger ?? NullLogger<ApprovalExpirySweep>.Instance);

    /// <summary>The one writer allowed to insert a booking, over this context's connection.</summary>
    internal ReservationWriter CreateReservationWriter(
        YallaDbContext db,
        IClock clock,
        BookingLockOptions? lockOptions = null) =>
        new(db, CreateTableLock(db, lockOptions), clock, NullLogger<ReservationWriter>.Instance);

    /// <summary>
    /// The per-table write lock. Tests that want to <i>hold</i> it - to prove another writer waits
    /// and then gives up - take a scope from this directly.
    /// </summary>
    internal TableLock CreateTableLock(YallaDbContext db, BookingLockOptions? lockOptions = null) =>
        new(db, lockOptions ?? new BookingLockOptions(), NullLogger<TableLock>.Instance);
}

/// <summary>
/// Marks a class as SQL-backed: it gets a <see cref="SqlServerFixture"/>, and with it a database of its own.
/// </summary>
/// <remarks>
/// Every class marked <c>[Collection(SqlServerCollection.Name)]</c> used to be one collection - one fixture,
/// one database, one class at a time, which made the suite as slow as the sum of its integration classes.
/// <see cref="SqlServerClassCollectionFactory"/> now gives each such class a collection of its own built
/// from this definition, so each gets its own fixture and they run in parallel. Mark new SQL-backed
/// classes exactly as before.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

/// <summary>
/// A clock backed by a <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>, so the
/// domain's clock and the scheduler's timer are the same clock.
/// </summary>
/// <remarks>
/// Two independent fakes is how a scheduler test passes while the thing it schedules never fires:
/// the test advances one, the <c>PeriodicTimer</c> reads the other, and nothing ticks. Advancing
/// this moves both.
/// </remarks>
public sealed class FakeClock(DateTime utcNow) : IClock
{
    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Provider { get; } =
        new(new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)));

    public DateTime UtcNow => Provider.GetUtcNow().UtcDateTime;

    /// <summary>Moves time forward, for the domain and for any timer built from the provider.</summary>
    public void Advance(TimeSpan by) => Provider.Advance(by);
}

/// <summary>A clock the tests drive, so "reserved soon" is deterministic.</summary>
/// <remarks>
/// <b>The instant is normalised to UTC, whatever kind it arrives with.</b> It used to store the
/// value as given, so a test written with an unspecified-kind literal produced an
/// unspecified-kind clock - and <c>Guard.NotLocalTime</c>, the one check between a local instant
/// and a UTC column, passes on unspecified. The real clock always returns
/// <see cref="DateTimeKind.Utc"/>, so a double that does not is telling the suite something
/// production cannot. Pinned by <c>ClockContract</c>.
/// </remarks>
public sealed class TestClock : IClock
{
    private DateTime _utcNow;

    public TestClock(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow
    {
        get => _utcNow;

        set => _utcNow = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(
                value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
                DateTimeKind.Utc);
    }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>A stand-in actor, so permission paths can be exercised without authentication.</summary>
public sealed class TestActor(
    ActorType type,
    Guid? staffMemberId,
    StaffRole? role,
    Guid? dinerUserId = null,
    Guid? participantId = null)
    : ICurrentActor
{
    public ActorType Type { get; } = type;

    public Guid? StaffMemberId { get; } = staffMemberId;

    public Guid? DinerUserId { get; } = dinerUserId;

    public Guid? ParticipantId { get; } = participantId;

    public StaffRole? Role { get; } = role;

    /// <summary>The tablet acting, when a test says so; null - the console - otherwise.</summary>
    public Guid? DeviceId { get; init; }

    public static TestActor Waiter(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.Waiter);

    public static TestActor Manager(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.Manager);

    public static TestActor Owner(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.Owner);

    public static TestActor GeneralManager(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.GeneralManager);

    public static TestActor PlatformAdmin(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.PlatformAdmin);

    /// <summary>
    /// A diner. An id is supplied by default because booking requires a <i>verified</i> one, and a
    /// test that wants the anonymous case should say so by passing null.
    /// </summary>
    public static TestActor Diner(Guid? dinerUserId = null) =>
        new(ActorType.Diner, null, null, dinerUserId ?? Guid.CreateVersion7());

    /// <summary>
    /// A phone on a tab, identified by its participant row and by nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This double used to put the participant id into <c>DinerUserId</c>, and production never
    /// does.</b> A participant token carries <c>PrincipalType = TabParticipant</c> and a
    /// <c>participantId</c> claim, and <c>ClaimsCurrentActor.DinerUserId</c> returns null for it -
    /// correctly, because a tab participant has no account. So every test that exercised ordering
    /// was asserting against a shape the real actor cannot produce, and the suite reported the
    /// ordering flow as working when it had never once run with a real token.
    /// </para>
    /// <para>
    /// <c>DinerUserId</c> is left null here deliberately. A participant who <i>also</i> holds a
    /// diner account still presents a participant token on a tab, so the account id is not on this
    /// path at all. <c>CurrentActorContract</c> is what now stops this drifting back.
    /// </para>
    /// </remarks>
    public static TestActor Participant(Guid participantId) =>
        new(ActorType.Diner, null, null, dinerUserId: null, participantId: participantId);
}
