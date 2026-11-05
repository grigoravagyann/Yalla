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

/// <summary>The floor's half: asking a guest whether they are coming, the due list, sending a ticket, not coming.</summary>
internal sealed partial class PreOrderService
{
    // ---------------------------------------------------------------- the floor

    /// <summary>
    /// "Ask: are you coming?" from the tablet: a message in the booking's thread with who asked, and the guest told now -
    /// push <c>message.to-guest</c> (<c>categoryId</c> <c>venue-ask</c>) and feed <c>venue-message</c>. The booking's
    /// rules (<see cref="Reservation.RecordStaffAskOnMyWay"/>) refuse it before anything is written. Under the account's
    /// lock and then the booking's, as every thread write is, in one transaction with the ask's stamp.
    /// </summary>
    public async Task<AskOnMyWayResult> AskOnMyWayAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        var (_, staffId) = await RequireFloorForBookingAsync(reservationId, "Ask a guest whether they are coming", cancellationToken);

        var reservation = await db.Reservations.FirstAsync(r => r.Id == reservationId, cancellationToken);
        var nowUtc = clock.UtcNow;

        reservation.RecordStaffAskOnMyWay(nowUtc);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var accountLive = reservation.DinerUserId is { } account
                          && await DinerAccountLock.IsLiveAsync(db, account, cancellationToken);

        // The guest told and the History's line hang on the ask's event (VenueAskedComing), in the save below; signal 6 on
        // the booking's stamp (OnMyWayAskRecorded).
        var notified = await GuestThreads.AskAsync(db, reservation, staffId, accountLive, nowUtc, cancellationToken);

        try
        {
            await domainEvents.DispatchAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another tablet asked, or the guest answered, in the same moment. Read what won and refuse as it now earns.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var current = await db.Reservations.AsNoTracking().FirstAsync(r => r.Id == reservationId, cancellationToken);
            current.RecordStaffAskOnMyWay(nowUtc);

            throw;
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Booking {Code}: the venue asked whether the guest is coming ({Notified}).",
            reservation.Code,
            notified ? "pushed" : "no account to push to");

        return new AskOnMyWayResult(
            reservation.Id, nowUtc, PreOrderRules.NextStaffAskAtUtc(nowUtc), notified);
    }

    public async Task<DuePreOrders> ListDueAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await FloorStaffGuard.RequireAtBranchAsync(
            db, actor, authorization, branchId, "Read the pre-order rail", cancellationToken);

        var minutes = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => (int?)b.PreOrderMinutesBefore)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var nowUtc = clock.UtcNow;
        var dueBy = nowUtc.AddMinutes(minutes);

        // On the rail: a confirmed booking from its card's time until its end, or a seated one whose pre-order is not on
        // their tab yet (sent before they sat and somehow not attached, or not sent - the waiter can still send it). A
        // held ticket the kitchen voided before they sat is never put on the tab, so a seated party's leaves the rail.
        var due = db.PreOrders
            .Where(p => p.BranchId == branchId && p.Lines.Any())
            .Where(p => (p.Reservation.Status == ReservationStatus.Confirmed
                         && p.Reservation.StartUtc <= dueBy
                         && p.Reservation.EndUtc > nowUtc)
                        || (p.Reservation.Status == ReservationStatus.Seated
                            && p.TabOrderId == null
                            && p.KitchenStatus != TabOrderStatus.Voided
                            && db.TableSessions.Any(s => s.ReservationId == p.ReservationId && s.ClosedAtUtc == null)));

        var cards = await CardsAsync(due, minutes, nowUtc, wasReplay: false, cancellationToken);

        // When the next card appears, so the tablet reads again then rather than polling: time passing has no writer.
        var nextStart = await db.PreOrders
            .Where(p => p.BranchId == branchId
                        && p.Lines.Any()
                        && p.Reservation.Status == ReservationStatus.Confirmed
                        && p.Reservation.StartUtc > dueBy)
            .MinAsync(p => (DateTime?)p.Reservation.StartUtc, cancellationToken);

        return new DuePreOrders(cards, nextStart?.AddMinutes(-minutes));
    }

    public async Task<DuePreOrder> SendAsync(PreOrderStaffCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (booking, staffId) = await RequireFloorForBookingAsync(command.ReservationId, "Send a pre-order to the kitchen", cancellationToken);

        if (!await db.PreOrders.AsNoTracking().AnyAsync(p => p.ReservationId == command.ReservationId, cancellationToken))
        {
            throw new KeyNotFoundException("This booking has no pre-order.");
        }

        // Two tablets, one ticket: already sent is answered as done, whoever sent it and under whatever command id.
        if (await SentAlreadyAsync(command.ReservationId, cancellationToken))
        {
            return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);
        }

        var table = (Id: booking.DiningTableId, Label: booking.TableLabel);

        for (var attempt = 1; ; attempt++)
        {
            var outcome = await SendUnderTableLockAsync(command.ReservationId, staffId, table.Id, table.Label, cancellationToken);

            switch (outcome)
            {
                case SendOutcome.AlreadySent:
                    return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);

                case SendOutcome.Sent sent:
                    if (sent.RecomputeTabId is { } recompute)
                    {
                        await ledger.RecomputeTotalsAsync(recompute, cancellationToken);
                    }

                    logger.LogInformation(
                        "Pre-order of booking {ReservationId} sent to the kitchen by staff {StaffId}.", command.ReservationId, staffId);

                    return await CardAsync(command.ReservationId, wasReplay: false, cancellationToken);

                case SendOutcome.Again again when attempt < MaxSendAttempts:
                    table = (again.TableId, again.TableLabel);
                    continue;

                default:
                    throw new DbUpdateConcurrencyException(
                        "This booking's table kept changing while its pre-order was being sent. Read the rail again.");
            }
        }
    }

    /// <summary>A send moved on by a table that changed under it - the booking's, or the sitting's tab - at most this often.</summary>
    private const int MaxSendAttempts = 3;

    /// <summary>
    /// One try at sending, under the table's lock - the one every seating takes: a send that finds the party not seated
    /// and a seating that finds the pre-order not sent cannot both be right, so they take turns, and whichever goes
    /// second puts the pre-order on the tab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The table is the booking's as read under the lock.</b> It was read before the lock was taken; a guest who
    /// moved the booking to another table in between is seated under <i>that</i> table's lock, so this one would not
    /// keep them apart. A booking found on another table is tried again under that table's lock.
    /// </para>
    /// <para>
    /// <b>A tab opened at the same moment.</b> A phone's scan and a waiter's "Open tab" give a sitting its tab without
    /// the table's lock; the database's one-tab-per-sitting key settles it. A send that loses is tried again, finds the
    /// tab that won and puts the pre-order on it.
    /// </para>
    /// </remarks>
    private async Task<SendOutcome> SendUnderTableLockAsync(
        Guid reservationId, Guid staffId, Guid tableId, string tableLabel, CancellationToken cancellationToken)
    {
        try
        {
            await using var locked = await tableLock.AcquireAsync(tableId, tableLabel, cancellationToken);

            var reservation = await db.Reservations.FirstAsync(r => r.Id == reservationId, cancellationToken);

            if (reservation.DiningTableId != tableId)
            {
                var moved = await db.DiningTables
                    .AsNoTracking()
                    .Where(t => t.Id == reservation.DiningTableId)
                    .Select(t => t.Label)
                    .FirstAsync(cancellationToken);

                db.ChangeTracker.Clear();

                return new SendOutcome.Again(reservation.DiningTableId!.Value, moved);
            }

            var preOrder = await db.PreOrders
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.ReservationId == reservationId, cancellationToken)
                ?? throw new KeyNotFoundException("This booking has no pre-order.");

            if (preOrder.IsSent)
            {
                return new SendOutcome.AlreadySent();
            }

            var nowUtc = clock.UtcNow;
            var session = await RequireSendableAsync(reservation, nowUtc, cancellationToken);

            // The History's line and the question withdrawn - "are you on your way?" has nothing left to decide - hang on
            // PreOrderSent; on a tab, its arrival tells the tab's stream (PreOrderArrivedOnTab).
            preOrder.MarkSent(staffId, session, nowUtc);

            Guid? recomputeTab = null;

            if (session is null)
            {
                // Not seated yet: the pre-order is the kitchen's ticket now, held until the party sits.
                await domainEvents.DispatchAsync(db, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (session.TabId is { } tabId)
            {
                var tab = await ledger.LoadForWriteAsync(tabId, cancellationToken);
                PreOrderSittings.AddToTab(db, preOrder, tab, nowUtc);
                await ledger.SaveAppendedAsync(cancellationToken);
                recomputeTab = tabId;
            }
            else
            {
                var serviceCharge = await db.Branches
                    .Where(b => b.Id == reservation.BranchId)
                    .Select(b => b.ReservationPolicy.ServiceChargePercent)
                    .FirstAsync(cancellationToken);

                PreOrderSittings.OpenTabWithOrder(
                    db, preOrder, session, serviceCharge, TabAdmissionReason.OpenedByStaff, staffId, nowUtc);

                try
                {
                    await domainEvents.DispatchAsync(db, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TabPerSession))
                {
                    // A scan or a waiter's "Open tab" gave the sitting its tab first, without this lock. Try again:
                    // the sitting now has a tab, and the pre-order goes on it.
                    logger.LogInformation(
                        "Sending the pre-order of booking {ReservationId} lost the tab race on session {SessionId}; "
                        + "putting it on the tab that won.",
                        reservationId,
                        session.Id);

                    db.ChangeTracker.Clear();
                    ledger.DiscardPending();

                    return new SendOutcome.Again(tableId, tableLabel);
                }
            }

            await locked.CommitAsync(cancellationToken);

            return new SendOutcome.Sent(recomputeTab);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The pre-order changed under the send - another tablet's send through another path, or a move. Read it again.
            db.ChangeTracker.Clear();
            ledger.DiscardPending();

            if (await SentAlreadyAsync(reservationId, cancellationToken))
            {
                return new SendOutcome.AlreadySent();
            }

            throw;
        }
    }

    public async Task<DuePreOrder> MarkNotComingAsync(PreOrderStaffCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (_, staffId) = await RequireFloorForBookingAsync(command.ReservationId, "Mark a party not coming", cancellationToken);

        var reservation = await db.Reservations.FirstAsync(r => r.Id == command.ReservationId, cancellationToken);
        var preOrder = await db.PreOrders.FirstOrDefaultAsync(p => p.ReservationId == command.ReservationId, cancellationToken)
                       ?? throw new KeyNotFoundException("This booking has no pre-order.");

        if (preOrder.IsSent)
        {
            throw new PreOrderAlreadySentException(reservation.Id, preOrder.SentAtUtc!.Value);
        }

        if (preOrder.NotComingAtUtc is not null)
        {
            return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);
        }

        var nowUtc = clock.UtcNow;

        // Only for a party expected and not here: a seated party is coming by definition.
        if (reservation.Status != ReservationStatus.Confirmed
            || nowUtc < PreOrderRules.LockedAtUtc(reservation.StartUtc)
            || nowUtc >= reservation.EndUtc)
        {
            throw NotSendable(reservation, "Only a confirmed booking within two hours of its start can be marked not coming.");
        }

        // The History's line, and the question withdrawn - the waiter has called and decided: the guest is not asked
        // "are you on your way?" on top - hang on PartyMarkedNotComing.
        preOrder.MarkNotComing(staffId, nowUtc);

        try
        {
            await domainEvents.DispatchAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost to another tablet. Read what won: a send is 409 pre-order-already-sent - this tap achieved nothing
            // and must not say it did; another "not coming" is a replay; anything else is the concurrent update it is.
            db.ChangeTracker.Clear();

            var current = await db.PreOrders
                .AsNoTracking()
                .Where(p => p.ReservationId == command.ReservationId)
                .Select(p => new { p.SentAtUtc, p.NotComingAtUtc })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new KeyNotFoundException("This booking has no pre-order.");

            if (current.SentAtUtc is { } sentAtUtc)
            {
                throw new PreOrderAlreadySentException(command.ReservationId, sentAtUtc);
            }

            if (current.NotComingAtUtc is null)
            {
                throw;
            }

            return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);
        }

        return await CardAsync(command.ReservationId, wasReplay: false, cancellationToken);
    }
}
