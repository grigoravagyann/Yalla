using Microsoft.EntityFrameworkCore;
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

    /// <summary>Bookings whose pre-order rail card changed without their pre-order row changing - see <see cref="SignalPreOrderRail"/>.</summary>
    private readonly HashSet<Guid> _preOrderRailSignals = [];

    public YallaDbContext(DbContextOptions<YallaDbContext> options, IClock clock)
        : base(options) => _clock = clock;

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
    /// Stamps <see cref="Entity.CreatedAtUtc"/> on every inserted row that does not already carry
    /// one, so no caller has to remember to and no row is ever missing its creation time.
    /// </summary>
    /// <remarks>
    /// This is the overload every other <c>SaveChangesAsync</c> entry point funnels into.
    /// </remarks>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        await VoidHeldPreOrdersOfEndedBookingsAsync(sync: false, cancellationToken);
        await SignalBookingChangesAsync(sync: false, cancellationToken);
        await SignalPreOrderChangesAsync(sync: false, cancellationToken);
        StampCreationTimes();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc cref="SaveChangesAsync(bool, CancellationToken)"/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        VoidHeldPreOrdersOfEndedBookingsAsync(sync: true, CancellationToken.None).GetAwaiter().GetResult();
        SignalBookingChangesAsync(sync: true, CancellationToken.None).GetAwaiter().GetResult();
        SignalPreOrderChangesAsync(sync: true, CancellationToken.None).GetAwaiter().GetResult();
        StampCreationTimes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// Puts every booking event on its table's change stream: a booking made, or one whose status, table, time or
    /// party changed - approved, declined, moved (both tables), cancelled, marked a no-show, a no-show withdrawn - or
    /// whose guest answered "on my way" (2026-10-21: the floor shows it beside the table's next booking, so the waiter
    /// need not call; once per booking, so no flood) - writes one same-status row on each table it touches, naming the booking, with signal
    /// <see cref="TableChangeSignal.BookingChanged"/>, in the same save and so the same transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than at each call site because a booking changes in a dozen places - the services, the approval
    /// sweep, a guest's answer about their table - and a tablet that follows the change stream instead of reading the
    /// floor every few seconds must hear about all of them: a booking made at 12:50 for 13:00 has to make table 4
    /// "reserved soon" on every tablet, or a waiter seats a walk-in there (GR-FE-REVIEW B1).
    /// </para>
    /// <para>
    /// The row carries the table's status as this save sees it, and nothing a client should fold: on signal 5 a
    /// tablet refreshes that table's booking line (re-reads the floor). A booking a no-show block cancelled already
    /// has its signal-4 row and gets no second one; a save that retries (a door code collision) adds none twice.
    /// </para>
    /// </remarks>
    private async Task SignalBookingChangesAsync(bool sync, CancellationToken cancellationToken)
    {
        var touched = new List<(Guid BranchId, Guid TableId, Guid ReservationId)>();

        foreach (var entry in ChangeTracker.Entries<Reservation>())
        {
            if (entry.State == EntityState.Added)
            {
                touched.Add((entry.Entity.BranchId, entry.Entity.DiningTableId, entry.Entity.Id));
                continue;
            }

            if (entry.State != EntityState.Modified
                || !(entry.Property(r => r.Status).IsModified
                     || entry.Property(r => r.DiningTableId).IsModified
                     || entry.Property(r => r.StartUtc).IsModified
                     || entry.Property(r => r.EndUtc).IsModified
                     || entry.Property(r => r.PartySize).IsModified
                     || entry.Property(r => r.GuestConfirmedOnWayAtUtc).IsModified))
            {
                continue;
            }

            touched.Add((entry.Entity.BranchId, entry.Entity.DiningTableId, entry.Entity.Id));

            if (entry.Property(r => r.DiningTableId).OriginalValue is var before && before != entry.Entity.DiningTableId)
            {
                touched.Add((entry.Entity.BranchId, before, entry.Entity.Id));
            }
        }

        if (touched.Count == 0)
        {
            return;
        }

        // Already signalled in this save - a block's own row, or this method on an earlier attempt of the same save.
        var signalled = ChangeTracker.Entries<TableStateChange>()
            .Where(e => e.State == EntityState.Added && e.Entity.ReservationId is not null)
            .Select(e => (e.Entity.DiningTableId, e.Entity.ReservationId!.Value))
            .ToHashSet();

        touched = touched.Distinct().Where(t => !signalled.Contains((t.TableId, t.ReservationId))).ToList();

        if (touched.Count == 0)
        {
            return;
        }

        var tableIds = touched.Select(t => t.TableId).Distinct().ToList();
        var query = DiningTables.AsNoTracking().Where(t => tableIds.Contains(t.Id)).Select(t => new { t.Id, t.Status });
        var statuses = sync
            ? query.ToDictionary(t => t.Id, t => t.Status)
            : await query.ToDictionaryAsync(t => t.Id, t => t.Status, cancellationToken);

        var now = _clock.UtcNow;

        foreach (var (branchId, tableId, reservationId) in touched)
        {
            if (!statuses.TryGetValue(tableId, out var status))
            {
                continue;
            }

            TableStateChanges.Add(
                new TableStateChange(
                    branchId, tableId, status, status, "booking changed", ActorType.System, now, Guid.CreateVersion7(),
                    reservationId: reservationId)
                .WithSignal(TableChangeSignal.BookingChanged));
        }
    }

    /// <summary>
    /// Asks for a signal 6 naming each of these bookings in the next save, for a change that alters their rail card
    /// without touching their pre-order row - the branch's pre-order settings (when the card appears, which dishes are
    /// still available).
    /// </summary>
    internal void SignalPreOrderRail(IEnumerable<Guid> reservationIds) => _preOrderRailSignals.UnionWith(reservationIds);

    /// <summary>
    /// Voids the held kitchen ticket of every booking this save ends before its party sat - cancelled by the guest or
    /// the venue, declined, expired, cancelled by a no-show block - so the kitchen stops cooking for nobody.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here, beside the signals, for their reason: a booking is ended in six places (the app's and the link's cancel,
    /// the floor's release, a decline or an expiry, a block, a crowded-out request), and a rule each of them had to
    /// remember is a rule one of them forgets. The voided ticket is a modified pre-order, so the signal hook below puts
    /// it on the change stream (signal 6) in the same save; the kitchen's list drops it as it drops any voided ticket.
    /// </para>
    /// <para>
    /// A no-show is not an end here: a sent pre-order followed by a no-show is an ordinary no-show, and its ticket is
    /// the kitchen's to deal with.
    /// </para>
    /// </remarks>
    private async Task VoidHeldPreOrdersOfEndedBookingsAsync(bool sync, CancellationToken cancellationToken)
    {
        var ended = ChangeTracker.Entries<Reservation>()
            .Where(e => e.State == EntityState.Modified
                        && e.Entity.Status is ReservationStatus.CancelledByDiner or ReservationStatus.CancelledByVenue
                        && e.Property(r => r.Status).OriginalValue != e.Entity.Status)
            .Select(e => e.Entity.Id)
            .ToList();

        if (ended.Count == 0)
        {
            return;
        }

        var query = PreOrders.Where(p => ended.Contains(p.ReservationId) && p.SentAtUtc != null && p.TabOrderId == null);
        var held = sync ? query.ToList() : await query.ToListAsync(cancellationToken);
        var now = _clock.UtcNow;

        foreach (var preOrder in held)
        {
            preOrder.VoidHeldBecauseBookingEnded(now);
        }
    }

    /// <summary>
    /// Puts every pre-order change on its booking's table's change stream - signal
    /// <see cref="TableChangeSignal.PreOrderChanged"/>, one same-status row per booking per save, naming it - so a tablet
    /// re-reads its pre-order rail: a pre-order saved, edited or cleared by the guest, sent, marked not coming, put on the
    /// party's tab, its held ticket moved by the kitchen, or the guest answering "on my way".
    /// </summary>
    /// <remarks>
    /// Here, beside signal 5, for signal 5's reason: a pre-order changes from the app, the manage link, the tablet, the
    /// kitchen and the seating paths, and a rail that hears about only some of them shows a waiter a card that is wrong.
    /// </remarks>
    private async Task SignalPreOrderChangesAsync(bool sync, CancellationToken cancellationToken)
    {
        var bookings = new HashSet<Guid>();

        foreach (var entry in ChangeTracker.Entries<PreOrder>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                bookings.Add(entry.Entity.ReservationId);
            }
        }

        foreach (var entry in ChangeTracker.Entries<Reservation>())
        {
            if (entry.State == EntityState.Modified && entry.Property(r => r.GuestConfirmedOnWayAtUtc).IsModified)
            {
                bookings.Add(entry.Entity.Id);
            }
        }

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
        var targets = rows
            .Select(r => (r.Id, r.BranchId, TableId: tracked.TryGetValue(r.Id, out var moved) ? moved : r.DiningTableId))
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

    private void StampCreationTimes()
    {
        var now = _clock.UtcNow;

        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            var createdAt = entry.Property(e => e.CreatedAtUtc);
            if (createdAt.CurrentValue == default)
            {
                createdAt.CurrentValue = now;
            }
        }
    }
}
