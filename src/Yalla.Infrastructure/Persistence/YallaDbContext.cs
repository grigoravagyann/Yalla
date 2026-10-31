using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Yalla.Application.Abstractions;
using Yalla.Domain.Audit;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Menus;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// The single unit of work over the Yalla database.
/// </summary>
/// <remarks>
/// All mapping lives in <c>IEntityTypeConfiguration</c> classes in this assembly and is applied
/// by <see cref="ModelBuilder.ApplyConfigurationsFromAssembly(System.Reflection.Assembly, System.Func{System.Type, bool})"/>.
/// The domain entities carry no data annotations and no fluent configuration, so the domain
/// project has no dependency on EF Core at all.
/// </remarks>
public sealed class YallaDbContext : DbContext
{
    private readonly IClock _clock;

    private readonly DomainEventDispatcher _domainEvents;

    /// <summary>Bookings whose pre-order rail card changed in this save - see <see cref="SignalPreOrderRail"/>.</summary>
    private readonly HashSet<Guid> _preOrderRailSignals = [];

    /// <summary>A transaction a domain-event handler asked for (<see cref="EnsureTransactionAsync"/>), committed by the save.</summary>
    private IDbContextTransaction? _ownedTransaction;

    /// <summary>A context with no domain-event handlers: the design-time factory, and tests seeding rows directly.</summary>
    /// <remarks>A save on it that finds an event pending fails - see <see cref="DomainEventDispatcher.None"/>.</remarks>
    public YallaDbContext(DbContextOptions<YallaDbContext> options, IClock clock)
        : this(options, clock, DomainEventDispatcher.None)
    {
    }

    /// <summary>The context as the service provider composes it, dispatching domain events to the scope's handlers.</summary>
    public YallaDbContext(DbContextOptions<YallaDbContext> options, IClock clock, DomainEventDispatcher domainEvents)
        : base(options)
    {
        _clock = clock;
        _domainEvents = domainEvents;
    }

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<OpeningHours> OpeningHours => Set<OpeningHours>();

    public DbSet<SpecialOpeningDay> SpecialOpeningDays => Set<SpecialOpeningDay>();

    public DbSet<FloorArea> FloorAreas => Set<FloorArea>();

    public DbSet<FloorAreaPhoto> FloorAreaPhotos => Set<FloorAreaPhoto>();

    public DbSet<DiningTable> DiningTables => Set<DiningTable>();

    public DbSet<BranchReview> BranchReviews => Set<BranchReview>();

    public DbSet<BranchReviewReport> BranchReviewReports => Set<BranchReviewReport>();

    public DbSet<BranchGalleryPhoto> BranchGalleryPhotos => Set<BranchGalleryPhoto>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    /// <summary>Guests blocked from booking for no-shows - see <see cref="BookingBlock"/>.</summary>
    public DbSet<BookingBlock> BookingBlocks => Set<BookingBlock>();

    /// <summary>Later bookings asking the booking before them to free the table - see <see cref="TableShareRequest"/>.</summary>
    public DbSet<TableShareRequest> TableShareRequests => Set<TableShareRequest>();

    /// <summary>Dishes guests chose ahead of their bookings - see <see cref="PreOrder"/>.</summary>
    public DbSet<PreOrder> PreOrders => Set<PreOrder>();

    public DbSet<PreOrderLine> PreOrderLines => Set<PreOrderLine>();

    public DbSet<Yalla.Domain.Takeaway.TakeawayOrder> TakeawayOrders => Set<Yalla.Domain.Takeaway.TakeawayOrder>();

    public DbSet<Yalla.Domain.Takeaway.TakeawayOrderLine> TakeawayOrderLines => Set<Yalla.Domain.Takeaway.TakeawayOrderLine>();

    /// <summary>Guest messages: one thread per booking or takeaway order. See <c>docs/messages.md</c>.</summary>
    public DbSet<Yalla.Domain.Messages.Conversation> Conversations => Set<Yalla.Domain.Messages.Conversation>();

    public DbSet<Yalla.Domain.Messages.ConversationMessage> ConversationMessages => Set<Yalla.Domain.Messages.ConversationMessage>();

    public DbSet<TableSession> TableSessions => Set<TableSession>();

    public DbSet<Tab> Tabs => Set<Tab>();

    public DbSet<TabParticipant> TabParticipants => Set<TabParticipant>();

    public DbSet<TabJoinToken> TabJoinTokens => Set<TabJoinToken>();

    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();

    public DbSet<MenuItem> MenuItems => Set<MenuItem>();

    public DbSet<TabOrder> TabOrders => Set<TabOrder>();

    public DbSet<TabOrderLine> TabOrderLines => Set<TabOrderLine>();

    public DbSet<TabOrderLineShare> TabOrderLineShares => Set<TabOrderLineShare>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();

    public DbSet<StaffShift> StaffShifts => Set<StaffShift>();

    public DbSet<TableStateChange> TableStateChanges => Set<TableStateChange>();

    public DbSet<PlatformAuditLog> PlatformAuditLogs => Set<PlatformAuditLog>();

    /// <summary>Who did what at a venue - the console's History. See <c>docs/activity.md</c>.</summary>
    public DbSet<ActivityEntry> ActivityEntries => Set<ActivityEntry>();

    public DbSet<ProcessedCommand> ProcessedCommands => Set<ProcessedCommand>();

    public DbSet<TabAdjustment> TabAdjustments => Set<TabAdjustment>();

    public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();

    public DbSet<TabEvent> TabEvents => Set<TabEvent>();

    public DbSet<Yalla.Domain.Media.Photo> Photos => Set<Yalla.Domain.Media.Photo>();

    public DbSet<Yalla.Domain.Messaging.OutboxMessage> OutboxMessages =>
        Set<Yalla.Domain.Messaging.OutboxMessage>();

    public DbSet<DinerDevice> DinerDevices => Set<DinerDevice>();

    public DbSet<DinerFavorite> DinerFavorites => Set<DinerFavorite>();

    public DbSet<DinerNotification> DinerNotifications => Set<DinerNotification>();

    public DbSet<DinerUser> DinerUsers => Set<DinerUser>();

    public DbSet<PhoneVerificationCode> PhoneVerificationCodes => Set<PhoneVerificationCode>();

    /// <summary>Phone proofs in flight through the Telegram bot - see <see cref="TelegramVerification"/>.</summary>
    public DbSet<TelegramVerification> TelegramVerifications => Set<TelegramVerification>();

    public DbSet<StaffDevice> StaffDevices => Set<StaffDevice>();

    public DbSet<StaffEnrolmentCode> StaffEnrolmentCodes => Set<StaffEnrolmentCode>();

    public DbSet<StaffSession> StaffSessions => Set<StaffSession>();

    /// <summary>Where staff devices receive Web Push - see <see cref="StaffPushSubscription"/>.</summary>
    public DbSet<StaffPushSubscription> StaffPushSubscriptions => Set<StaffPushSubscription>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(YallaDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every instant in this system is UTC and every one of them is datetime2. Setting it as a
        // convention means no configuration class can quietly forget it. DateOnly and TimeOnly
        // (opening hours, the local date and time on a booking) are wall-clock values and map to
        // date and time - they are deliberately not instants and are never converted to UTC.
        //
        // The conversion is what makes that true on the way BACK. datetime2 stores no offset, so
        // SQL Server hands every instant back as DateTimeKind.Unspecified - which System.Text.Json
        // then serialises without a trailing Z, and a client parses as its own local time. A
        // booking read from the database was going out as "2026-09-11T15:00:00" next to one
        // computed in memory as "2026-09-11T15:00:00Z", four hours apart in Yerevan and identical
        // to the eye. Stamping the kind on read costs nothing and removes the ambiguity entirely.
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("datetime2")
            .HaveConversion<UtcDateTimeConverter>();

        // Enums persist as int, which is EF Core's default and is left in place deliberately:
        // storing them as strings would let a member rename orphan existing rows. No
        // configuration class converts an enum to a string, and none should.
    }

    /// <summary>
    /// Dispatches the tracked entities' domain events, writes the pre-order rail's signal 6 rows they asked for, stamps
    /// <see cref="Entity.CreatedAtUtc"/> on every inserted row that does not already carry one, and saves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the overload every other <c>SaveChangesAsync</c> entry point funnels into.
    /// </para>
    /// <para>
    /// <b>Domain events first</b> (<c>docs/domain-events.md</c>): their handlers add rows - an activity entry, an outbox
    /// message, a change-stream row, a staff push - in the same save. Each event is handled once: a save that fails and is
    /// retried on the same context keeps what the handlers added and does not run them again. The change-tracker hooks
    /// that detected the booking signal, the held pre-order of an ended booking and the approvals push are gone
    /// (2026-10-25): they are the bookings' handlers.
    /// </para>
    /// </remarks>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        await _domainEvents.DispatchAsync(this, cancellationToken);
        await SignalPreOrderChangesAsync(sync: false, cancellationToken);
        StampCreationTimes();
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        if (TakeOwnedTransaction() is { } owned)
        {
            // Disposed whatever the commit does: a commit that throws must not leave the context joined to a dead
            // transaction (and the diner account lock held) for whatever it does next.
            try
            {
                await owned.CommitAsync(cancellationToken);
            }
            finally
            {
                await owned.DisposeAsync();
            }
        }

        return saved;
    }

    /// <inheritdoc cref="SaveChangesAsync(bool, CancellationToken)"/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        _domainEvents.DispatchAsync(this, CancellationToken.None).GetAwaiter().GetResult();
        SignalPreOrderChangesAsync(sync: true, CancellationToken.None).GetAwaiter().GetResult();
        StampCreationTimes();
        var saved = base.SaveChanges(acceptAllChangesOnSuccess);

        if (TakeOwnedTransaction() is { } owned)
        {
            try
            {
                owned.Commit();
            }
            finally
            {
                owned.Dispose();
            }
        }

        return saved;
    }

    /// <summary>
    /// The transaction <see cref="EnsureTransactionAsync"/> began, for the save that just succeeded to commit - or null
    /// when there is none, or when it is no longer the connection's (something rolled it back or committed it through
    /// <c>Database</c>), in which case there is nothing of ours left to commit and only the handle to let go of.
    /// </summary>
    private IDbContextTransaction? TakeOwnedTransaction()
    {
        if (_ownedTransaction is not { } owned)
        {
            return null;
        }

        _ownedTransaction = null;

        if (ReferenceEquals(Database.CurrentTransaction, owned))
        {
            return owned;
        }

        owned.Dispose();

        return null;
    }

    /// <summary>
    /// Takes every pending event off the tracked entities, in the order they were raised. Walks the tracker without
    /// detecting changes: an entity raises from a domain method, and anything that can raise is tracked - added to its
    /// set, or loaded - so a save that raised nothing pays a list walk and no more.
    /// </summary>
    internal List<IDomainEvent> DequeueDomainEvents()
    {
        List<RaisedDomainEvent>? raised = null;
        var detecting = ChangeTracker.AutoDetectChangesEnabled;
        ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            foreach (var entry in ChangeTracker.Entries<Entity>())
            {
                if (entry.Entity.DequeueEvents() is { Count: > 0 } events)
                {
                    (raised ??= []).AddRange(events);
                }
            }
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = detecting;
        }

        return raised is null ? [] : [.. raised.OrderBy(r => r.Order).Select(r => r.Event)];
    }

    /// <summary>
    /// Makes sure what the save writes runs in a transaction that is open now - for a domain-event handler that takes a
    /// lock which must be held to the commit (<c>DinerAccountLock.IsLiveAsync</c>). Joins the caller's transaction when
    /// there is one (a lock scope's); otherwise begins one that the next successful save commits.
    /// </summary>
    /// <remarks>
    /// A save that fails leaves it open, as a caller's own transaction would be, so a retried save on the same context
    /// (the ledger's renumbering) still holds the lock; disposing the context rolls it back.
    /// </remarks>
    internal async Task EnsureTransactionAsync(CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is not null)
        {
            return;
        }

        _ownedTransaction = await Database.BeginTransactionAsync(cancellationToken);
    }

    /// <summary>
    /// Asks for a signal 6 naming each of these bookings in this save - from <c>PreOrderRailSignal</c>, the handler of
    /// every pre-order event - written by <see cref="SignalPreOrderChangesAsync"/> once the save's events are handled.
    /// </summary>
    internal void SignalPreOrderRail(IEnumerable<Guid> reservationIds) => _preOrderRailSignals.UnionWith(reservationIds);

    /// <summary>
    /// Writes the signal 6 rows this save's pre-order events asked for (<see cref="SignalPreOrderRail"/>) - signal
    /// <see cref="TableChangeSignal.PreOrderChanged"/>, one same-status row per booking per save, naming it - so a tablet
    /// re-reads its pre-order rail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pre-order events' handler (<c>PreOrderRailSignal</c>) decides which bookings; the rows are written here, once
    /// every event of the save is handled, where the tracker hook that detected pre-order changes wrote them: after the
    /// save's other change-stream rows - the booking's signal 5 among them - and with the table's status as the save
    /// leaves it. Not in the handler: a save's rows are numbered in the order they are added, and a booking's own event
    /// can be raised after a pre-order event of the same save - a move forgets the "on my way" answers
    /// (<c>OnMyWayAnswersForgotten</c>) before it says it moved - so a row written per event would put 6 before 5 on a
    /// stream that tablets read in sequence.
    /// </para>
    /// </remarks>
    private async Task SignalPreOrderChangesAsync(bool sync, CancellationToken cancellationToken)
    {
        var bookings = new HashSet<Guid>();

        bookings.UnionWith(_preOrderRailSignals);
        _preOrderRailSignals.Clear();

        if (bookings.Count == 0)
        {
            return;
        }

        // Already signalled in this save: a retried save adds none twice.
        bookings.ExceptWith(ChangeTracker.Entries<TableStateChange>()
            .Where(e => e.State == EntityState.Added
                        && e.Entity.Signal == TableChangeSignal.PreOrderChanged
                        && e.Entity.ReservationId is not null)
            .Select(e => e.Entity.ReservationId!.Value));

        if (bookings.Count == 0)
        {
            return;
        }

        var ids = bookings.ToList();
        var query = Reservations.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.BranchId, r.DiningTableId });
        var rows = sync ? query.ToList() : await query.ToListAsync(cancellationToken);

        // The table the booking is on as this save leaves it: a move in the same save has the tracked value.
        var tracked = ChangeTracker.Entries<Reservation>().ToDictionary(e => e.Entity.Id, e => e.Entity.DiningTableId);
        // A booking by time only that nobody has seated has no table to tell.
        var targets = rows
            .Select(r => (r.Id, r.BranchId, TableId: tracked.TryGetValue(r.Id, out var moved) ? moved : r.DiningTableId))
            .Where(t => t.TableId is not null)
            .Select(t => (t.Id, t.BranchId, TableId: t.TableId!.Value))
            .ToList();

        var tableIds = targets.Select(t => t.TableId).Distinct().ToList();
        var statusQuery = DiningTables.AsNoTracking().Where(t => tableIds.Contains(t.Id)).Select(t => new { t.Id, t.Status });
        var statuses = sync
            ? statusQuery.ToDictionary(t => t.Id, t => t.Status)
            : await statusQuery.ToDictionaryAsync(t => t.Id, t => t.Status, cancellationToken);

        // The table's status as this save leaves it: a seating that puts the held ticket on the tab occupies the table
        // in the same save, and the row read above is from before it. A tablet that does not know signal 6 folds the
        // row's status into its table, so the row must say what the table is now - never Free beside Free -> Occupied.
        foreach (var table in ChangeTracker.Entries<DiningTable>().Where(e => tableIds.Contains(e.Entity.Id)))
        {
            statuses[table.Entity.Id] = table.Entity.Status;
        }

        var now = _clock.UtcNow;

        foreach (var (reservationId, branchId, tableId) in targets)
        {
            if (!statuses.TryGetValue(tableId, out var status))
            {
                continue;
            }

            TableStateChanges.Add(
                new TableStateChange(
                    branchId, tableId, status, status, "pre-order changed", ActorType.System, now, Guid.CreateVersion7(),
                    reservationId: reservationId)
                .WithSignal(TableChangeSignal.PreOrderChanged));
        }
    }

    /// <summary>
    /// The last walk before the write: stamps creation times, and refuses a save that would write a change without what
    /// it tells the rest of the system (<c>docs/domain-events.md</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An event still pending here</b> was raised where dispatch could not see it - on an entity the context only
    /// started tracking when the hooks above detected changes (one reachable only through a navigation), or by a hook
    /// itself. Saved, the change would go out without its side effects, and the event would wait on the entity for a
    /// later save, in another unit of work. Raise on the tracked aggregate root.
    /// </para>
    /// <para>
    /// <b>A tab event with no place on its stream</b> was appended by something whose save does not go through
    /// <c>TabLedger.SaveAppendedAsync</c> - a tab-order event handled by this save's own dispatch, say. Written at
    /// sequence 0, no phone catching up would ever read it.
    /// </para>
    /// </remarks>
    private void StampCreationTimes()
    {
        var now = _clock.UtcNow;

        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.Entity.HasPendingEvents())
            {
                throw new InvalidOperationException(
                    $"A {entry.Entity.GetType().Name} is being saved with domain events its save never dispatched: they "
                    + "were raised on an entity the context was not tracking when it dispatched (reachable only through a "
                    + "navigation) or during the save's own hooks. Raise on the aggregate root the context tracks "
                    + "(docs/domain-events.md).");
            }

            if (entry.State != EntityState.Added)
            {
                continue;
            }

            if (entry.Entity is TabEvent { Sequence: 0L } unnumbered)
            {
                throw new InvalidOperationException(
                    $"A {unnumbered.Type} tab event is being saved with no place on tab {unnumbered.TabId}'s stream. "
                    + "Whatever appends to the stream - a tab-order domain event's handler included - must be saved "
                    + "through TabLedger.SaveAppendedAsync, which numbers it.");
            }

            var createdAt = entry.Property(e => e.CreatedAtUtc);
            if (createdAt.CurrentValue == default)
            {
                createdAt.CurrentValue = now;
            }
        }
    }
}
