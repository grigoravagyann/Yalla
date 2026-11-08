using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>The rules and reads the other parts share, and the records they keep.</summary>
internal sealed partial class PreOrderService
{
    // ---------------------------------------------------------------- rules

    /// <summary>A booking still to come, pending or confirmed: what a guest may still change.</summary>
    private static void RequireBookingTakesEdits(Reservation reservation, DateTime nowUtc)
    {
        if (!PreOrderRules.BookingTakesEdits(reservation.Status) || reservation.StartUtc <= nowUtc)
        {
            throw new ReservationNotChangeableException(reservation.Id, reservation.Code, reservation.Status, reservation.StartUtc);
        }
    }

    /// <summary>Not with the kitchen, and more than two hours before the start.</summary>
    private static void RequireUnlocked(Reservation reservation, PreOrder? preOrder, DateTime nowUtc)
    {
        if (preOrder is { IsSent: true })
        {
            throw new PreOrderAlreadySentException(reservation.Id, preOrder.SentAtUtc!.Value);
        }

        var lockedAt = PreOrderRules.LockedAtUtc(reservation.StartUtc);

        if (nowUtc >= lockedAt)
        {
            throw new PreOrderLockedException(reservation.Id, lockedAt);
        }
    }

    /// <summary>
    /// A confirmed booking from two hours before its start until its end, or a seated one with its sitting open - and
    /// that sitting, when seated.
    /// </summary>
    private async Task<Domain.Occupancy.TableSession?> RequireSendableAsync(
        Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (reservation.Status == ReservationStatus.Seated)
        {
            return await db.TableSessions.FirstOrDefaultAsync(
                       s => s.ReservationId == reservation.Id && s.ClosedAtUtc == null, cancellationToken)
                   ?? throw NotSendable(reservation, "This party's sitting is over; the pre-order can no longer be sent.");
        }

        if (reservation.Status != ReservationStatus.Confirmed
            || nowUtc < PreOrderRules.LockedAtUtc(reservation.StartUtc)
            || nowUtc >= reservation.EndUtc)
        {
            throw NotSendable(
                reservation,
                "A pre-order goes to the kitchen for a confirmed booking from two hours before it until it ends.");
        }

        return null;
    }

    private static PreOrderNotSendableException NotSendable(Reservation reservation, string message) =>
        new(reservation.Id, reservation.Status, PreOrderRules.LockedAtUtc(reservation.StartUtc), reservation.EndUtc, message);

    // ---------------------------------------------------------------- the floor's reads

    /// <summary>The booking's facts the floor's actions need, and the caller's staff id when they may act on it.</summary>
    private async Task<(BookingFacts Booking, Guid StaffId)> RequireFloorForBookingAsync(
        Guid reservationId, string operation, CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        var booking = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == reservationId)
            .Select(r => new BookingFacts(r.BranchId, r.DiningTableId!.Value, r.DiningTable!.Label))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Booking {reservationId} was not found.");

        var staffId = await FloorStaffGuard.RequireAtBranchAsync(db, actor, authorization, booking.BranchId, operation, cancellationToken);

        return (booking, staffId);
    }

    private Task<bool> SentAlreadyAsync(Guid reservationId, CancellationToken cancellationToken) =>
        db.PreOrders.AsNoTracking().AnyAsync(p => p.ReservationId == reservationId && p.SentAtUtc != null, cancellationToken);

    /// <summary>One booking's card, for an action's answer, wherever the rail would place it.</summary>
    private async Task<DuePreOrder> CardAsync(Guid reservationId, bool wasReplay, CancellationToken cancellationToken)
    {
        var minutes = await db.PreOrders
            .AsNoTracking()
            .Where(p => p.ReservationId == reservationId)
            .Select(p => (int?)p.Reservation.Branch.PreOrderMinutesBefore)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("This booking has no pre-order.");

        var cards = await CardsAsync(
            db.PreOrders.Where(p => p.ReservationId == reservationId), minutes, clock.UtcNow, wasReplay, cancellationToken);

        return cards.Single();
    }

    /// <summary>
    /// The cards for these pre-orders: one statement for the bookings, one (two at most) for the pre-orders, one for the
    /// guests' records - whatever the number of cards.
    /// </summary>
    private async Task<IReadOnlyList<DuePreOrder>> CardsAsync(
        IQueryable<PreOrder> preOrders, int minutesBefore, DateTime nowUtc, bool wasReplay, CancellationToken cancellationToken)
    {
        var rows = await preOrders
            .AsNoTracking()
            .OrderBy(p => p.Reservation.StartUtc)
            .ThenBy(p => p.Reservation.DiningTable!.Label)
            .Select(p => new CardRow
            {
                ReservationId = p.ReservationId,
                Code = p.Reservation.Code,
                // Pre-orders are only on bookings with a table (ReplaceCoreAsync).
                TableId = p.Reservation.DiningTableId!.Value,
                TableLabel = p.Reservation.DiningTable!.Label,
                RoomName = p.Reservation.DiningTable.FloorArea == null ? null : p.Reservation.DiningTable.FloorArea.Name,
                StartUtc = p.Reservation.StartUtc,
                LocalDate = p.Reservation.LocalDate,
                LocalStartTime = p.Reservation.LocalStartTime,
                TimeZoneId = p.Reservation.Branch.TimeZoneId,
                PartySize = p.Reservation.PartySize,
                Status = p.Reservation.Status,
                GuestName = p.Reservation.GuestName,
                GuestPhone = p.Reservation.GuestPhone,
                GuestConfirmedOnWayAtUtc = p.Reservation.GuestConfirmedOnWayAtUtc,
                GuestRunningLateMinutes = p.Reservation.GuestRunningLateMinutes,
                GuestRunningLateAtUtc = p.Reservation.GuestRunningLateAtUtc,
                StaffAskedOnMyWayAtUtc = p.Reservation.StaffAskedOnMyWayAtUtc,
                DinerUserId = p.Reservation.DinerUserId,
                PhoneUnverified = p.Reservation.PhoneUnverified,

                // The names alone, joined here - never a lookup per card.
                SentByName = db.StaffMembers.Where(s => s.Id == p.SentByStaffId).Select(s => s.FullName).FirstOrDefault(),
                NotComingByName = db.StaffMembers.Where(s => s.Id == p.NotComingByStaffId).Select(s => s.FullName).FirstOrDefault(),
                TabId = p.TabOrderId == null
                    ? null
                    : db.TabOrders.Where(o => o.Id == p.Id).Select(o => (Guid?)o.TabId).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var views = await PreOrderReads.ForReservationsAsync(db, rows.Select(r => r.ReservationId), nowUtc, cancellationToken);

        // The guest's record, under the console list's rule: a proved account's booking, confirmed, today or later.
        var records = await GuestRecords.ForAccountsAsync(
            db,
            rows.Where(ShowsRecord).Select(r => r.DinerUserId!.Value),
            nowUtc,
            cancellationToken);

        // The guests' numbers are the venue's to call, not the platform's to read.
        var withPhones = actor.Role != StaffRole.PlatformAdmin;

        return
        [
            .. rows.Select(r => new DuePreOrder(
                r.ReservationId,
                r.Code,
                r.TableId,
                r.TableLabel,
                r.RoomName,
                r.StartUtc,
                r.LocalStartTime,
                r.PartySize,
                r.Status,
                r.GuestName,
                withPhones ? r.GuestPhone : string.Empty,
                r.GuestConfirmedOnWayAtUtc,
                ShowsRecord(r) && records.TryGetValue(r.DinerUserId!.Value, out var record) ? record : null,
                PreOrderRules.DueAtUtc(r.StartUtc, minutesBefore),
                views[r.ReservationId],
                r.SentByName,
                r.NotComingByName,
                r.TabId,
                r.GuestRunningLateMinutes,
                r.GuestRunningLateAtUtc,
                r.StaffAskedOnMyWayAtUtc,
                wasReplay)),
        ];

        bool ShowsRecord(CardRow r) =>
            r.DinerUserId is not null
            && !r.PhoneUnverified
            && r.Status == ReservationStatus.Confirmed
            && r.LocalDate >= BranchZone.For(r.TimeZoneId).LocalDateAt(nowUtc);
    }

    // ---------------------------------------------------------------- the guest's reads and records

    private async Task<PreOrderResult> AnswerAsync(Guid reservationId, bool wasReplay, CancellationToken cancellationToken) =>
        new(reservationId, await PreOrderReads.ForReservationAsync(db, reservationId, clock.UtcNow, cancellationToken), wasReplay);

    /// <summary>
    /// Whether this command was already applied - or throws when the id belongs to another command, another booking or
    /// another person: the reschedule's rule, so a reused id never answers with somebody else's pre-order.
    /// </summary>
    private async Task<bool> IsReplayAsync(
        Guid clientCommandId, string commandType, Guid account, Guid reservationId, CancellationToken cancellationToken)
    {
        var processed = await db.ProcessedCommands
            .AsNoTracking()
            .Where(c => c.ClientCommandId == clientCommandId)
            .Select(c => new { c.CommandType, c.ActorId, c.ResponseJson })
            .FirstOrDefaultAsync(cancellationToken);

        if (processed is null)
        {
            return false;
        }

        if (processed.CommandType != commandType
            || processed.ActorId != account
            || JsonSerializer.Deserialize<PreOrderRecord>(processed.ResponseJson, RecordJson) is not { } record
            || record.ReservationId != reservationId)
        {
            throw new ClientCommandIdAlreadyUsedException(
                clientCommandId,
                "That clientCommandId already belongs to another command. Generate a fresh one per change, and reuse it "
                + "only when retrying that same change.");
        }

        return true;
    }

    private void Record(Guid clientCommandId, string commandType, Guid account, Guid reservationId, DateTime nowUtc) =>
        db.ProcessedCommands.Add(new ProcessedCommand(
            clientCommandId,
            commandType,
            ActorType.Diner,
            account,
            JsonSerializer.Serialize(new PreOrderRecord(reservationId), RecordJson),
            200,
            nowUtc));

    /// <summary>
    /// Saves a guest's change. True when the same command committed first in a race with its own retry - answer it as
    /// a replay; a first save racing another first save of the same booking is a concurrent update.
    /// </summary>
    private async Task<bool> SaveGuestChangeAsync(
        Guid clientCommandId, string commandType, Guid account, Guid reservationId, CancellationToken cancellationToken)
    {
        try
        {
            await domainEvents.DispatchAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            return false;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
        {
            db.ChangeTracker.Clear();

            return await IsReplayAsync(clientCommandId, commandType, account, reservationId, cancellationToken)
                ? true
                : throw new ClientCommandIdAlreadyUsedException(clientCommandId);
        }
        catch (DbUpdateException ex)
            when (UniqueViolation.IsOn(ex, DatabaseIndexNames.PreOrderPerReservation)
                  || UniqueViolation.IsOn(ex, DatabaseIndexNames.OutboxIdempotency))
        {
            // The account's lock makes these unreachable for two saves of one booking; kept as the backstop. The same
            // command that committed first is a replay; anything else is a concurrent update - never a 500.
            db.ChangeTracker.Clear();

            return await IsReplayAsync(clientCommandId, commandType, account, reservationId, cancellationToken)
                ? true
                : throw new DbUpdateConcurrencyException(
                    "Another change to this pre-order was saved at the same moment. Read it again and retry.", ex);
        }
    }

    private async Task<Reservation> OwnBookingAsync(Guid reservationId, Guid dinerUserId, CancellationToken cancellationToken) =>

        // Somebody else's booking is not found, word for word as an id that is no booking: the reschedule's rule - a
        // stranger guessing ids learns nothing.
        await db.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId && r.DinerUserId == dinerUserId, cancellationToken)
        ?? throw new KeyNotFoundException($"Booking {reservationId} was not found.");

    /// <summary>The booking behind a manage link, or the one refusal every bad link shares.</summary>
    private async Task<Reservation> ByManageTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw ManageBookingFailure.Raise();
        }

        var hash = Secrets.Hash(token);
        var reservation = await db.Reservations.FirstOrDefaultAsync(r => r.ManageTokenHash == hash, cancellationToken);

        return reservation is not null && reservation.ManageTokenIsLiveAt(clock.UtcNow)
            ? reservation
            : throw ManageBookingFailure.Raise();
    }

    /// <summary>The account a link's change is recorded against - the guest's, as if from the app.</summary>
    private static Guid AccountBehind(Reservation reservation) =>
        reservation.DinerUserId ?? throw new ManageLinkAccountUnavailableException();

    private Guid RequireDiner(string operation) =>
        actor.Type == ActorType.Diner && actor.DinerUserId is { } id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException($"{operation} requires a verified diner account.");

    private sealed record BookingFacts(Guid BranchId, Guid DiningTableId, string TableLabel);

    /// <summary>How one try at sending ended.</summary>
    private abstract record SendOutcome
    {
        /// <summary>Somebody sent it first.</summary>
        public sealed record AlreadySent : SendOutcome;

        /// <summary>Sent; the tab whose totals to recompute after the commit, when it went onto an open one.</summary>
        public sealed record Sent(Guid? RecomputeTabId) : SendOutcome;

        /// <summary>Nothing was written: try again under this table's lock.</summary>
        public sealed record Again(Guid TableId, string TableLabel) : SendOutcome;
    }

    /// <summary>What a guest's command recorded: which booking - never the pre-order itself.</summary>
    private sealed record PreOrderRecord(Guid ReservationId);

    private sealed class CardRow
    {
        public Guid ReservationId { get; init; }

        public string Code { get; init; } = string.Empty;

        public Guid TableId { get; init; }

        public string TableLabel { get; init; } = string.Empty;

        public string? RoomName { get; init; }

        public DateTime StartUtc { get; init; }

        public DateOnly LocalDate { get; init; }

        public TimeOnly LocalStartTime { get; init; }

        public string TimeZoneId { get; init; } = string.Empty;

        public int PartySize { get; init; }

        public ReservationStatus Status { get; init; }

        public string GuestName { get; init; } = string.Empty;

        public string GuestPhone { get; init; } = string.Empty;

        public DateTime? GuestConfirmedOnWayAtUtc { get; init; }

        public int? GuestRunningLateMinutes { get; init; }

        public DateTime? GuestRunningLateAtUtc { get; init; }

        public DateTime? StaffAskedOnMyWayAtUtc { get; init; }

        public Guid? DinerUserId { get; init; }

        public bool PhoneUnverified { get; init; }

        public string? SentByName { get; init; }

        public string? NotComingByName { get; init; }

        public Guid? TabId { get; init; }
    }
}
