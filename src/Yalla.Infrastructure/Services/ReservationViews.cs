using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// How a booking is answered: its view, built from the row and what the caller may see of it - and
/// which staff reads show the guest's record beside it.
/// </summary>
internal sealed class ReservationViews(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ReservationLookups lookups)
{
    // ---------------------------------------------------------------- views

    /// <summary>
    /// Builds the view, fetching only the table facts the caller did not already have in hand.
    /// </summary>
    /// <remarks>
    /// A <paramref name="table"/> passed in must be the booking's own and loaded with its room, as
    /// <see cref="ReservationLookups.LoadTableAsync"/> loads it.
    /// </remarks>
    internal async Task<ReservationView> ToViewAsync(
        Reservation reservation,
        Branch branch,
        DiningTable? table,
        bool wasReplay,
        ApprovalTrigger? trigger,
        CancellationToken cancellationToken,
        string? manageToken = null)
    {
        var facts = reservation.DiningTableId is null
            ? new TableFacts(null, null, null)
            : table is not null
            ? new TableFacts(table.Label, table.FloorAreaId, table.FloorArea?.Name)
            : await db.DiningTables
                  .AsNoTracking()
                  .Where(t => t.Id == reservation.DiningTableId)
                  .Select(t => new TableFacts(
                      t.Label, t.FloorAreaId, t.FloorArea == null ? null : t.FloorArea.Name))
                  .FirstOrDefaultAsync(cancellationToken)
              ?? new TableFacts(string.Empty, null, null);

        // The question to this booking's guest, on their own views of it only.
        IncomingRow? incoming = null;

        if (actor.Type == ActorType.Diner
            && actor.DinerUserId is { } caller
            && reservation.DinerUserId == caller)
        {
            var nowUtc = clock.UtcNow;

            incoming = await db.TableShareRequests
                .AsNoTracking()
                .Where(q => q.EarlierReservationId == reservation.Id
                            && q.Status == TableShareRequestStatus.Pending
                            && q.AsksEarlierGuest
                            && q.ExpiresAtUtc > nowUtc)
                .OrderBy(q => q.CreatedAtUtc)
                .Select(q => new IncomingRow
                {
                    RequestId = q.Id,
                    RequestedStartUtc = q.RequestedStartUtc,
                    FreeByUtc = q.FreeByUtc,
                    ExpiresAtUtc = q.ExpiresAtUtc,
                })
                .FirstOrDefaultAsync(cancellationToken);
        }

        // Whether it waits on the guest before it to answer in the app - only a waiting booking can.
        var awaitsEarlierGuestAnswer = reservation.Status == ReservationStatus.PendingApproval
                                       && await db.TableShareRequests
                                           .AsNoTracking()
                                           .AnyAsync(
                                               q => q.LaterReservationId == reservation.Id
                                                    && q.Status == TableShareRequestStatus.Pending
                                                    && q.AsksEarlierGuest,
                                               cancellationToken);

        // Made without proof by an account that has proved its number since: no longer "not verified".
        var phoneUnverifiedNow = reservation.PhoneUnverified
                                 && !(reservation.DinerUserId is { } account
                                      && await db.DinerUsers.AnyAsync(
                                          d => d.Id == account && d.PhoneVerifiedAtUtc != null, cancellationToken));

        var view = BuildView(
            reservation,
            branch.Name,
            branch.TimeZoneId,
            facts,
            branch.ReservationPolicy.CancellationDeadlineMinutes,
            wasReplay,
            trigger,
            manageToken,
            incoming,
            awaitsEarlierGuestAnswer,
            phoneUnverifiedNow);

        // Until when the guest may pre-order, on the guest's own answers only.
        return await PreOrderReads.AttachAsync(db, view, clock.UtcNow, cancellationToken, forGuest: actor.Type == ActorType.Diner);
    }

    internal static ReservationView ToView(MineRow row) =>
        BuildView(
            row.Reservation,
            row.BranchName,
            row.TimeZoneId,
            new TableFacts(row.TableLabel, row.RoomId, row.RoomName),
            row.CancellationDeadlineMinutes,
            wasReplay: false,
            trigger: null,
            incoming: row.Incoming,
            awaitsEarlierGuestAnswer: row.AwaitsEarlierGuestAnswer,
            phoneUnverifiedNow: row.PhoneUnverifiedNow);

    internal static ReservationView BuildView(
        Reservation reservation,
        string branchName,
        string timeZoneId,
        TableFacts table,
        int cancellationDeadlineMinutes,
        bool wasReplay,
        ApprovalTrigger? trigger,
        string? manageToken = null,
        IncomingRow? incoming = null,
        bool awaitsEarlierGuestAnswer = false,
        bool? phoneUnverifiedNow = null,
        string? takenByName = null)
    {
        var zone = BranchZone.For(timeZoneId);

        return new ReservationView
        {
            Id = reservation.Id,
            Code = reservation.Code,
            BranchId = reservation.BranchId,
            BranchName = branchName,
            TimeZoneId = timeZoneId,
            TableId = reservation.DiningTableId,
            TableLabel = reservation.DiningTableId is null ? null : table.Label,
            BookedWithoutTable = reservation.BookedWithoutTable,
            RoomId = table.RoomId,
            RoomName = table.RoomName,
            PartySize = reservation.PartySize,
            StartUtc = reservation.StartUtc,
            EndUtc = reservation.EndUtc,
            LocalDate = reservation.LocalDate,
            LocalStartTime = reservation.LocalStartTime,

            // Derived for display only. The stored wall-clock start is the one the diner was
            // shown and is never recomputed.
            LocalEndTime = zone.LocalTimeAt(reservation.EndUtc),

            // Set only when the platform cancelled it - a guest's no-show block.
            CancelledBecause = reservation.CancelledBecause,

            Status = reservation.Status,

            // Where it came from, and - taken by hand from the floor - by whom (2026-10-31; the taker lands with the
            // column, in the commit after the contract).
            Channel = reservation.Channel,
            TakenByStaffId = null,
            TakenByName = takenByName,
            GuestName = reservation.GuestName,
            GuestPhone = reservation.GuestPhone,
            // Made without proof, and - for an account's booking - not proved since: staff see "not verified"
            // only while it is true (the stored column records how it was made, and stays).
            PhoneUnverified = phoneUnverifiedNow ?? reservation.PhoneUnverified,

            // What the diner asked for, on every view of the booking - the approval queue included,
            // which is where the venue reads it (K9).
            Note = reservation.Note,
            ConfirmedAtUtc = reservation.ConfirmedAtUtc,
            CancelledAtUtc = reservation.CancelledAtUtc,
            CancellationReason = reservation.CancellationReason,
            CancelledAfterDeadline = reservation.CancelledAfterDeadline,
            CancellationDeadlineUtc =
                ReservationRules.CancellationDeadline(reservation.StartUtc, cancellationDeadlineMinutes),
            ClientCommandId = reservation.ClientCommandId,
            WasReplay = wasReplay,

            // The reason stored when it went to approval; the one passed in only for a row from before
            // the column, which has none (see PendingTriggerFor).
            AwaitingApprovalBecause = reservation.Status == ReservationStatus.PendingApproval
                ? reservation.ApprovalTrigger ?? trigger
                : null,

            // Cleared on the booking the moment it stops waiting, so this is only ever a live deadline.
            ApprovalExpiresAtUtc = reservation.Status == ReservationStatus.PendingApproval
                ? reservation.ApprovalExpiresAtUtc
                : null,

            // Only while it waits: a decided booking is waiting on nobody.
            AwaitsEarlierGuestAnswer = awaitsEarlierGuestAnswer && reservation.Status == ReservationStatus.PendingApproval,

            // Only on a booking that ended declined. A confirmed one carrying a reason is one whose last
            // change was declined and that went back to the slot it had - it is not declined.
            DeclinedBecause = reservation.Status == ReservationStatus.CancelledByVenue
                ? reservation.DeclineReason
                : null,

            // Null on every path but creation. The server holds only the hash, so a later read
            // could not return it even if one wanted to.
            ManageToken = manageToken,

            // The time the guest promised to free the table by, kept on the row (EndUtc is cut to it).
            PromisedFreeByUtc = reservation.PromisedFreeByUtc,

            // "Yes, on my way" - the pre-order's question, 45 minutes before.
            GuestConfirmedOnWayAtUtc = reservation.GuestConfirmedOnWayAtUtc,
            GuestRunningLateMinutes = reservation.GuestRunningLateMinutes,
            GuestRunningLateAtUtc = reservation.GuestRunningLateAtUtc,
            StaffAskedOnMyWayAtUtc = reservation.StaffAskedOnMyWayAtUtc,
            OnMyWayOpensAtUtc = PreOrderRules.LockedAtUtc(reservation.StartUtc),

            // Only on a booking still holding its table: a cancelled one has nothing to answer about.
            IncomingTableRequest = incoming is not null && ReservationOverlap.Blocks(reservation.Status)
                ? new IncomingTableRequest(
                    incoming.RequestId,
                    incoming.RequestedStartUtc,
                    incoming.FreeByUtc,
                    incoming.ExpiresAtUtc,
                    zone.LocalTimeAt(incoming.RequestedStartUtc),
                    zone.LocalTimeAt(incoming.FreeByUtc))
                : null,
        };
    }

    /// <summary>
    /// A replayed change's answer: the booking as it stands now, read fresh.
    /// </summary>
    /// <remarks>
    /// The booking as it is, rather than a stored copy of the first answer, because a booking's view
    /// carries the guest's name and number and a stored copy would be one more place to scrub them
    /// from. The retry asked to be told where the booking is, and this tells it.
    /// </remarks>
    internal async Task<ReservationView> ReplayViewAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var current = await db.Reservations
            .AsNoTracking()
            .FirstAsync(r => r.Id == reservationId, cancellationToken);

        var branch = await lookups.LoadBranchAsync(current.BranchId, cancellationToken);

        return await ToViewAsync(
            current,
            branch,
            table: null,
            wasReplay: true,
            PendingTriggerFor(current, branch.ReservationPolicy),
            cancellationToken);
    }

    /// <summary>
    /// Why a pending booking is waiting: the reason stored when it went to approval.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off the booking, never worked out from the clock: a booking made at short notice is still
    /// waiting for that reason at any later read, and a policy edited since cannot rename it.
    /// </para>
    /// <para>
    /// <b>The fallback is for rows from before the column existed</b>, which carry none. Those replay
    /// <see cref="ReservationSlotRules.DecideStatusAsync"/>'s old order against the branch's current policy -
    /// approve-everything, then the party-size threshold, and otherwise the diner's no-show history -
    /// which is what every pending booking used to be told. It never names a reason for a booking
    /// that is not pending.
    /// </para>
    /// </remarks>
    internal static ApprovalTrigger? PendingTriggerFor(Reservation reservation, ReservationPolicy policy)
    {
        if (reservation.Status != ReservationStatus.PendingApproval)
        {
            return null;
        }

        if (reservation.ApprovalTrigger is { } stored)
        {
            return stored;
        }

        if (!policy.AutoConfirm)
        {
            return ApprovalTrigger.BranchApprovesEveryBooking;
        }

        return ReservationRules.NeedsApproval(reservation.PartySize, policy)
            ? ApprovalTrigger.LargeParty
            : ApprovalTrigger.NoShowHistory;
    }

    // ---------------------------------------------------------------- the guest's record, for staff

    /// <summary>
    /// Whether a staff read shows a booking's guest record: a proved account is behind it, and it is pending or
    /// confirmed, today or still to come (review S3). Never on a finished, cancelled or past booking - an old booking
    /// must not be a window on the guest's evenings since.
    /// </summary>
    internal static bool ShowsRecord(Reservation reservation, bool phoneUnverifiedNow, DateOnly branchToday) =>
        !phoneUnverifiedNow
        && reservation.DinerUserId is not null
        && reservation.Status is ReservationStatus.PendingApproval or ReservationStatus.Confirmed
        && reservation.LocalDate >= branchToday;

    /// <summary>The accounts whose records a staff list shows - see <see cref="ShowsRecord"/>.</summary>
    internal static IEnumerable<Guid> RecordedAccounts(
        IEnumerable<(Reservation Reservation, bool PhoneUnverifiedNow)> rows, DateOnly branchToday) =>
        rows.Where(r => ShowsRecord(r.Reservation, r.PhoneUnverifiedNow, branchToday))
            .Select(r => r.Reservation.DinerUserId!.Value);

    /// <summary>A booking's guest record from a list's batch, or null where it is not shown.</summary>
    internal static GuestRecord? RecordOf(
        IReadOnlyDictionary<Guid, GuestRecord> records, Reservation reservation, bool phoneUnverifiedNow, DateOnly branchToday) =>
        ShowsRecord(reservation, phoneUnverifiedNow, branchToday) && records.TryGetValue(reservation.DinerUserId!.Value, out var record)
            ? record
            : null;

    /// <summary>One booking's guest record for a staff answer, or null where it is not shown.</summary>
    internal async Task<GuestRecord?> RecordForStaffAsync(
        Reservation reservation, bool phoneUnverifiedNow, string timeZoneId, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        return ShowsRecord(reservation, phoneUnverifiedNow, BranchZone.For(timeZoneId).LocalDateAt(nowUtc))
            ? await GuestRecords.ForAccountAsync(db, reservation.DinerUserId!.Value, nowUtc, cancellationToken)
            : null;
    }

    /// <summary>What a booking's view says about its table: the label, and the room it is in.</summary>
    internal sealed record TableFacts(string? Label, Guid? RoomId, string? RoomName);

    internal sealed class MineRow
    {
        public Reservation Reservation { get; init; } = null!;

        public string BranchName { get; init; } = null!;

        public string TimeZoneId { get; init; } = null!;

        public string? TableLabel { get; init; }

        public Guid? RoomId { get; init; }

        public string? RoomName { get; init; }

        /// <summary>The branch's setting as it stands, which a cancellation now is judged by.</summary>
        public int CancellationDeadlineMinutes { get; init; }

        /// <summary>A later guest's open request for this booking's table, or null.</summary>
        public IncomingRow? Incoming { get; init; }

        /// <summary>Whether this booking waits on the guest before it to answer in the app.</summary>
        public bool AwaitsEarlierGuestAnswer { get; init; }

        /// <summary>
        /// Made without a proved number by a booker who has not proved it since. Null where the read did not
        /// work it out, and the stored flag stands.
        /// </summary>
        public bool? PhoneUnverifiedNow { get; init; }
    }

    /// <summary>A later guest's open request for a booking's table, as the read finds it.</summary>
    internal sealed class IncomingRow
    {
        public Guid RequestId { get; init; }

        public DateTime RequestedStartUtc { get; init; }

        public DateTime FreeByUtc { get; init; }

        public DateTime ExpiresAtUtc { get; init; }
    }
}
