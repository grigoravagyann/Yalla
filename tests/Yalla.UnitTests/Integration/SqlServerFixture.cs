using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A throwaway SQL Server database, migrated from scratch, shared by the integration tests.
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
/// <c>EnsureCreated</c> would build from the model and never validate.
/// </para>
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>
    /// Environment variable naming the server to test against, e.g. <c>localhost\SQLEXPRESS</c>.
    /// </summary>
    /// <remarks>
    /// Set this in CI. Locally the probe below usually finds the right one on its own.
    /// </remarks>
    public const string ServerEnvironmentVariable = "YALLA_TEST_SQL_SERVER";

    /// <summary>
    /// Where a developer machine actually keeps SQL Server, in the order worth trying.
    /// </summary>
    /// <remarks>
    /// The fixture used to hard-code <c>localhost</c>, which is the default instance. A machine
    /// with SQL Server Express installed - the common case on Windows - has a <i>named</i> instance
    /// and no default one, so every integration test skipped and the suite reported green while
    /// proving nothing about the two things only a real server can prove: rowversion tokens and
    /// the locking these bookings depend on.
    /// </remarks>
    private static readonly string[] CandidateServers =
    [
        "localhost",
        @"localhost\SQLEXPRESS",
        @"(localdb)\MSSQLLocalDB",
    ];

    private readonly string _databaseName = $"Yalla_Tests_{Guid.NewGuid():N}";

    private string _server = string.Empty;

    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var attempts = new List<string>();

        foreach (var server in Candidates())
        {
            try
            {
                await using var connection = new SqlConnection(MasterConnectionString(server));
                await connection.OpenAsync();

                _server = server;
                break;
            }
            catch (Exception ex)
                when (ex is SqlException or InvalidOperationException or PlatformNotSupportedException)
            {
                attempts.Add($"{server}: {ex.Message.Split('\n')[0]}");
            }
        }

        if (_server.Length == 0)
        {
            SkipReason =
                $"No SQL Server reachable. Set {ServerEnvironmentVariable} to one. Tried - "
                + string.Join(" | ", attempts);
            return;
        }

        ConnectionString =
            $"Server={_server};Database={_databaseName};Trusted_Connection=True;"
            + "TrustServerCertificate=True;MultipleActiveResultSets=True";

        await using var db = CreateContext(new TestClock(DateTime.UtcNow));
        await db.Database.MigrateAsync();

        IsAvailable = true;
    }

    private static IEnumerable<string> Candidates()
    {
        var configured = Environment.GetEnvironmentVariable(ServerEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        foreach (var server in CandidateServers)
        {
            yield return server;
        }
    }

    private static string MasterConnectionString(string server) =>
        $"Server={server};Database=master;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=5";

    public async Task DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        // Force it closed first: a lingering pooled connection makes DROP DATABASE hang.
        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(MasterConnectionString(_server));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             IF DB_ID(N'{_databaseName}') IS NOT NULL
             BEGIN
                 ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                 DROP DATABASE [{_databaseName}];
             END
             """;

        await command.ExecuteNonQueryAsync();
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
        new(db, clock, actor, CreateTableLock(db, lockOptions), NullLogger<TableStateService>.Instance);

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
            NullLogger<TabService>.Instance);
    }

    /// <summary>
    /// The tab's books over one context: totals recomputation, the event stream, and the retry.
    /// </summary>
    internal TabLedger CreateLedger(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, NullLogger<TabLedger>.Instance);

    /// <summary>Ordering, voiding and adjusting, over one context.</summary>
    internal TabOrderService CreateOrderService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateLedger(db, clock, actor), NullLogger<TabOrderService>.Instance);

    /// <summary>Cash, over one context. Two of these over separate contexts is how payments race.</summary>
    internal TabPaymentService CreatePaymentService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateLedger(db, clock, actor), NullLogger<TabPaymentService>.Instance);

    /// <summary>Calling a waiter, over one context.</summary>
    internal ServiceRequestService CreateServiceRequests(
        YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, CreateLedger(db, clock, actor), NullLogger<ServiceRequestService>.Instance);

    /// <summary>Shares and the event stream, over one context.</summary>
    internal TabBillingQuery CreateBillingQuery(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, CreateLedger(db, clock, actor), NullLogger<TabBillingQuery>.Instance);

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
        new(db, clock, actor, NullLogger<PlatformService>.Instance);

    internal BranchSettingsService CreateBranchSettingsService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(db, clock, actor, NullLogger<BranchSettingsService>.Instance);

    internal MenuService CreateMenuService(YallaDbContext db) => new(db);

    internal StaffManagementService CreateStaffManagementService(YallaDbContext db, IClock clock, ICurrentActor actor) =>
        new(
            db,
            clock,
            actor,
            new SecretHasher(),
            Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            NullLogger<StaffManagementService>.Instance);

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
        BookingLockOptions? lockOptions = null) =>
        new(
            db,
            clock,
            actor,
            CreateAvailabilityQuery(db, clock),
            new AuthorizationQueries(db),
            noShowPolicy ?? new NoShowPolicy(),
            CreateReservationWriter(db, lockOptions),
            CreateService(db, clock, actor),
            CreateOutbox(db, clock),
            NullLogger<ReservationService>.Instance);

    /// <summary>The outbox over one context. Writes only - dispatching is its own thing.</summary>
    internal Outbox CreateOutbox(YallaDbContext db, IClock clock) =>
        new(db, clock, NullLogger<Outbox>.Instance);

    /// <summary>The one writer allowed to insert a booking, over this context's connection.</summary>
    internal ReservationWriter CreateReservationWriter(
        YallaDbContext db,
        BookingLockOptions? lockOptions = null) =>
        new(db, CreateTableLock(db, lockOptions), NullLogger<ReservationWriter>.Instance);

    /// <summary>
    /// The per-table write lock. Tests that want to <i>hold</i> it - to prove another writer waits
    /// and then gives up - take a scope from this directly.
    /// </summary>
    internal TableLock CreateTableLock(YallaDbContext db, BookingLockOptions? lockOptions = null) =>
        new(db, lockOptions ?? new BookingLockOptions(), NullLogger<TableLock>.Instance);
}

/// <summary>Groups the integration tests so the database is created once, not per class.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

/// <summary>A clock the tests drive, so "reserved soon" is deterministic.</summary>
public sealed class TestClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>A stand-in actor, so permission paths can be exercised without authentication.</summary>
public sealed class TestActor(ActorType type, Guid? staffMemberId, StaffRole? role, Guid? dinerUserId = null)
    : ICurrentActor
{
    public ActorType Type { get; } = type;

    public Guid? StaffMemberId { get; } = staffMemberId;

    public Guid? DinerUserId { get; } = dinerUserId;

    public StaffRole? Role { get; } = role;

    public static TestActor Waiter(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.Waiter);

    public static TestActor Manager(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.Manager);

    public static TestActor Owner(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.Owner);

    public static TestActor PlatformAdmin(Guid staffId) => new(ActorType.Staff, staffId, StaffRole.PlatformAdmin);

    /// <summary>
    /// A diner. An id is supplied by default because booking requires a <i>verified</i> one, and a
    /// test that wants the anonymous case should say so by passing null.
    /// </summary>
    public static TestActor Diner(Guid? dinerUserId = null) =>
        new(ActorType.Diner, null, null, dinerUserId ?? Guid.CreateVersion7());

    /// <summary>
    /// A phone on a tab, identified by its participant row.
    /// </summary>
    /// <remarks>
    /// A tab participant has no account, so the participant id is what the token carries and what
    /// <c>ICurrentActor.DinerUserId</c> holds for them - see <c>docs/auth.md</c>. Ordering reads it
    /// from there and never from a request body, so one phone cannot order in another's name.
    /// </remarks>
    public static TestActor Participant(Guid participantId) =>
        new(ActorType.Diner, null, null, participantId);
}
