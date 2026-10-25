using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;
using Yalla.Domain.Tabs;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// A booking whose party is late: the diner's one hold extension, the floor letting the booking go - a
/// no-show, or the venue's cancel - and withdrawing a no-show marked by mistake.
/// </summary>
internal sealed class ReservationRelease(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ReservationWriter reservations,
    ITableStateService tableState,
    NoShowBlocks noShowBlocks,
    IOutbox outbox,
    IActivityLog activity,
    ILogger<ReservationService> logger,
    ReservationLookups lookups,
    ReservationViews views)
{
    // ---------------------------------------------------------------- the diner extends their hold

    public async Task<ExtendHoldResult> ExtendHoldAsync(
        ExtendHoldCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = lookups.RequireDiner("Extend a hold");

        await DinerPhoneGate.RequireVerifiedPhoneAsync(db, dinerUserId, "Extending a hold", cancellationToken);
        var reservation = await lookups.LoadReservationAsync(command.ReservationId, cancellationToken);

        // Their own booking, read from the token rather than the body. A diner must not be able to
        // hold somebody else's table by guessing an id.
        if (reservation.DinerUserId != dinerUserId)
        {
            throw new TabPermissionException("Extending this hold", "the diner who made the booking");
        }

        // Already extended by this exact command: answer with what it did rather than refusing. The
        // notification is tappable twice and the second tap is not an error.
        if (reservation.GraceExtensionsUsed > 0
            && await db.TableStateChanges.AnyAsync(
                c => c.ClientCommandId == command.ClientCommandId, cancellationToken))
        {
            return new ExtendHoldResult(
                reservation.Id, reservation.HoldExpiresAtUtc ?? clock.UtcNow, 0, 0, WasReplay: true);
        }

        var branch = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);
        var minutes = branch.ReservationPolicy.GraceExtensionMinutes;

        // Throws when the one extension is spent, or the booking is not confirmed.
        reservation.ExtendHold(clock.UtcNow, minutes);

        await BookingActivity.RecordAsync(
            activity, db, reservation, ActivityActions.BookingHoldExtended, cancellationToken,
            extra: new { minutes, holdExpiresAtUtc = reservation.HoldExpiresAtUtc }, byGuest: true);

        await reservations.SaveAsync(cancellationToken);

        // Onto the branch change sequence, so the waiter watching that table sees it. Held to Held -
        // nothing about the table changed, but something happened at it.
        await tableState.RecordHoldExtendedAsync(
            new TableStateCommand(
                reservation.BranchId,
                reservation.DiningTableId,
                command.ClientCommandId,
                $"the diner extended their hold by {minutes} minutes"),
            reservation.Id,
            reservation.HoldExpiresAtUtc!.Value,
            cancellationToken);

        logger.LogInformation(
            "Booking {Code} extended its hold to {HoldExpiresAtUtc} ({Minutes} minutes).",
            reservation.Code, reservation.HoldExpiresAtUtc, minutes);

        return new ExtendHoldResult(
            reservation.Id, reservation.HoldExpiresAtUtc!.Value, minutes, 0, WasReplay: false);
    }

    // ---------------------------------------------------------------- releasing a late booking

    public async Task<ReservationReleaseResult> ReleaseAsync(
        ReleaseReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await lookups.LoadReservationAsync(command.ReservationId, cancellationToken);
        var staffId = await lookups.RequireStaffForBranchAsync(reservation.BranchId, "Release a booking", cancellationToken);

        var table = await db.DiningTables
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == reservation.DiningTableId, cancellationToken)
            ?? throw new KeyNotFoundException($"Table {reservation.DiningTableId} was not found.");

        // Already let go. A tablet replaying its queue must not count a second no-show against a
        // diner, and the answer it gets is the one the first attempt got.
        if (reservation.Status is ReservationStatus.NoShow or ReservationStatus.CancelledByVenue)
        {
            logger.LogInformation(
                "Booking {Code} was already released as {Status}; returning that answer.",
                reservation.Code, reservation.Status);

            // The block this very no-show made, if it made one - its BlockedAtUtc is the instant it was marked.
            var blockedByThis = reservation is { Status: ReservationStatus.NoShow, DinerUserId: { } blockedAccount }
                ? await db.BookingBlocks
                    .AsNoTracking()
                    .Where(b => b.DinerUserId == blockedAccount && b.BlockedAtUtc == reservation.CancelledAtUtc)
                    .Select(b => (DateTime?)b.BlockedUntilUtc)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            return await BuildReleaseAsync(
                reservation, OutcomeOf(reservation.Status), tableFreed: false, table.Id, table.Label,
                table.Status, wasReplay: true, cancellationToken, blockedByThis);
        }

        var nowUtc = clock.UtcNow;
        var reason = command.Reason ?? DefaultReleaseReason(command.Outcome);
        var releasedFrom = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);

        // A no-show of a proved account may be the third in three days: it is recorded under the booking's table
        // lock and then the account's, in a transaction of its own, so two marked at once at two venues are judged
        // one after the other - the second sees the first - and the block is neither missed nor written twice.
        var guest = command.Outcome == ReleaseOutcome.NoShow && !reservation.PhoneUnverified
            ? reservation.DinerUserId
            : null;

        IReadOnlyList<Reservation> cancelledByBlock = [];
        DateTime? blockedUntilUtc = null;

        await using (var locked = await LockForNoShowAsync(reservation, table.Label, guest, cancellationToken))
        {
            if (command.Outcome == ReleaseOutcome.NoShow)
            {
                // It counts toward a block only when marked after the guest's grace: the start plus the branch's
                // grace, or the extended hold when that is later (review B1a). Recorded either way, with who marked it.
                var graceEndsUtc = reservation.StartUtc.AddMinutes(releasedFrom.ReservationPolicy.GraceMinutes);

                if (reservation.HoldExpiresAtUtc is { } hold && hold > graceEndsUtc)
                {
                    graceEndsUtc = hold;
                }

                reservation.MarkNoShow(nowUtc, staffId, countsTowardBlock: nowUtc >= graceEndsUtc);
            }
            else
            {
                reservation.CancelByVenue(nowUtc, reason);
            }

            // As for a cancellation: the table is free of this booking, so a question it asked goes, and a
            // booking waiting on this one's guest no longer has to.
            await TableShares.CloseForLaterAsync(db, reservation.Id, TableShareRequestStatus.Withdrawn, nowUtc, cancellationToken);
            await TableShares.EarlierChangedAsync(db, reservation, releasedFrom, nowUtc, cancellationToken);

            // Whatever is left unsent for this booking goes with it. A nudge asking "still coming?"
            // after a waiter has already given the table away is the worst of both.
            await outbox.CancelAsync(
                OutboxMessageTypes.PrefixFor("reservation", reservation.Id), cancellationToken);

            await DinerNotices.CancelUnshownAsync(db, reservation.Id, nowUtc, cancellationToken);

            await BookingActivity.RecordAsync(
                activity,
                db,
                reservation,
                command.Outcome == ReleaseOutcome.NoShow ? ActivityActions.BookingNoShow : ActivityActions.BookingReleased,
                cancellationToken,
                extra: new { countsTowardBlock = reservation.NoShowCountsTowardBlock },
                reason: command.Reason);

            if (reservation.DinerUserId is not null)
            {
                if (command.Outcome == ReleaseOutcome.NoShow)
                {
                    // Every no-show is told to the guest, so a mistake is put right with the venue before it counts
                    // (review B1d). After the cancel above, which would otherwise take it with the reminder.
                    TellDinerNoShow(reservation, releasedFrom, nowUtc);
                }
                else
                {
                    // The venue let an accepted booking go, and the diner's feed hears about it (K12).
                    DinerNotices.BookingCancelledByVenue(
                        db, reservation, releasedFrom.Venue?.Name ?? releasedFrom.Name, releasedFrom.Name, nowUtc);
                }
            }

            await reservations.SaveAsync(cancellationToken);

            // Saved first, so the count reads this one as it reads the others.
            if (guest is { } account)
            {
                (cancelledByBlock, blockedUntilUtc) = await noShowBlocks.BlockIfDueAsync(account, releasedFrom, nowUtc, cancellationToken);
                await reservations.SaveAsync(cancellationToken);
            }

            await locked.CommitAsync(cancellationToken);
        }

        // The tables a block's cancellations held, given back through the state machine - so every floor sees it.
        await noShowBlocks.ReleaseHoldsOfAsync(cancelledByBlock, cancellationToken);

        // The table is freed only when it is being held for THIS booking. A table somebody is
        // sitting at is left exactly as it is: the booking is released either way, but a floor plan
        // that shows an occupied table as free costs more than a stale hold does.
        var freed = false;
        var tableStatus = table.Status;

        if (table.Status == TableStatus.Held
            && await NoShowBlocks.HoldIsForAsync(db, table.Id, reservation.Id, cancellationToken))
        {
            // ReleaseHold, not FreeTable: the machine has no Held-to-Free transition, because
            // vacating is what happens when a party leaves and a hold has nobody at it. Going
            // through the machine at all is the point - the audit row is written and the branch
            // change sequence moves, which is what puts this on every other tablet in the room.
            var change = await tableState.ReleaseHoldAsync(
                new TableStateCommand(reservation.BranchId, table.Id, command.ClientCommandId, reason),
                cancellationToken);

            freed = true;
            tableStatus = change.ToStatus;
        }

        logger.LogInformation(
            "Booking {Code} released as {Outcome} by staff {StaffId}. Table {TableLabel} {TableAction}.",
            reservation.Code, command.Outcome, staffId, table.Label,
            freed ? "was freed" : $"was left {tableStatus}");

        return await BuildReleaseAsync(
            reservation, command.Outcome, freed, table.Id, table.Label, tableStatus,
            wasReplay: false, cancellationToken, blockedUntilUtc);
    }

    public async Task<ReservationView> UndoNoShowAsync(
        UndoNoShowCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await lookups.LoadReservationAsync(command.ReservationId, cancellationToken);
        var staffId = await lookups.RequireStaffForBranchAsync(reservation.BranchId, "Withdraw a no-show", cancellationToken);
        var branch = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);

        // Already withdrawn: the booking as it stands - a tap sent twice is not refused.
        if (reservation.Status != ReservationStatus.NoShow && reservation.NoShowUndoneAtUtc is not null)
        {
            return await views.ToViewAsync(reservation, branch, table: null, wasReplay: true, trigger: null, cancellationToken);
        }

        if (reservation.Status != ReservationStatus.NoShow)
        {
            throw new Domain.DomainStateException(
                $"Only a no-show can be withdrawn; {reservation.Code} is {reservation.Status}.");
        }

        var nowUtc = clock.UtcNow;

        // Until the end of its service day, 05:00 the next morning on the branch's wall clock. Past it, the record
        // is settled; a mistake then is support's to put right.
        var zone = BranchZone.For(branch.TimeZoneId);
        var undoableUntilUtc = zone.ToUtc(
            NoShowBlockRules.ServiceDateOf(reservation.LocalDate, reservation.LocalStartTime).AddDays(1),
            new TimeOnly(NoShowBlockRules.UndoUntilLocalHourNextDay, 0));

        if (nowUtc >= undoableUntilUtc)
        {
            throw new NoShowUndoWindowClosedException(undoableUntilUtc);
        }

        var table = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == reservation.DiningTableId)
            .Select(t => t.Label)
            .FirstAsync(cancellationToken);

        var guest = reservation.PhoneUnverified ? null : reservation.DinerUserId;

        await using (var locked = await LockForNoShowAsync(reservation, table, guest, cancellationToken))
        {
            reservation.UndoNoShow(nowUtc, staffId, command.Reason);

            await BookingActivity.RecordAsync(
                activity, db, reservation, ActivityActions.BookingNoShowUndone, cancellationToken, reason: command.Reason);

            await reservations.SaveAsync(cancellationToken);

            // The block this no-show helped make, judged again without it: fewer than three left, and it is lifted.
            if (guest is { } account)
            {
                await LiftBlockIfNoLongerHeldAsync(account, staffId, command.Reason, nowUtc, cancellationToken);
                await reservations.SaveAsync(cancellationToken);
            }

            await locked.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Staff {StaffId} withdrew the no-show on booking {Code}: {Reason}",
            staffId, reservation.Code, command.Reason ?? "(no reason given)");

        var view = await views.ToViewAsync(reservation, branch, table: null, wasReplay: false, trigger: null, cancellationToken);

        return actor.Role == StaffRole.PlatformAdmin ? view with { GuestPhone = string.Empty } : view;
    }

    /// <summary>
    /// The writer's lock for recording a no-show or withdrawing one, with a lock timeout re-labelled as the
    /// booking-specific one (503, retryable).
    /// </summary>
    private async Task<TableLockScope> LockForNoShowAsync(
        Reservation reservation,
        string tableLabel,
        Guid? account,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reservations.LockForNoShowAsync(reservation.DiningTableId, tableLabel, account, cancellationToken);
        }
        catch (TableLockTimeoutException ex) when (ex is not ReservationLockTimeoutException)
        {
            throw new ReservationLockTimeoutException(ex.TableId, ex.TableLabel, ex.TimeoutMilliseconds);
        }
    }

    /// <summary>
    /// "Marked as a no-show at {venue}" - the push and the feed entry beside it, in the caller's unit of work. Keyed
    /// under the booking with the instant, so a no-show withdrawn and marked again is told again.
    /// </summary>
    private void TellDinerNoShow(Reservation reservation, Branch branch, DateTime nowUtc)
    {
        if (reservation.DinerUserId is not { } dinerUserId)
        {
            return;
        }

        var venueName = branch.Venue?.Name ?? branch.Name;

        outbox.Enqueue(
            OutboxMessageTypes.ReservationNoShow,
            new
            {
                reservationId = reservation.Id,
                dinerUserId,
                venueName,
                branchName = branch.Name,
                tableLabel = string.Empty,
                localStartTime = reservation.LocalStartTime,
                reservationCode = reservation.Code,
                localDate = reservation.LocalDate,
                partySize = reservation.PartySize,
                status = reservation.Status,
            },
            nowUtc,
            OutboxMessageTypes.KeyFor(
                "reservation",
                reservation.Id,
                "no-show:" + nowUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)));

        DinerNotices.BookingNoShow(db, reservation, venueName, branch.Name, nowUtc);
    }

    /// <summary>
    /// Lifts the guest's block in force when, without the no-show just withdrawn, fewer than
    /// <see cref="NoShowBlockRules.Threshold"/> are left to hold it - counted as of when it began. Nothing it
    /// cancelled comes back; the guest is told they can book again. Stages in the caller's unit of work.
    /// </summary>
    private async Task LiftBlockIfNoLongerHeldAsync(
        Guid account,
        Guid staffId,
        string? reason,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var block = await db.BookingBlocks
            .Where(b => b.DinerUserId == account && b.LiftedAtUtc == null && b.BlockedUntilUtc > nowUtc)
            .OrderByDescending(b => b.BlockedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (block is null
            || await GuestRecords.BlockingNoShowDaysAsync(db, account, block.BlockedAtUtc, cancellationToken)
               >= NoShowBlockRules.Threshold)
        {
            return;
        }

        block.Lift(nowUtc, BookingBlockLiftCause.NoShowUndone, staffId, reason);
        ReservationNotices.TellDinerUnblocked(db, outbox, block, nowUtc);

        logger.LogInformation(
            "The no-show block on diner {DinerUserId} was lifted: a no-show was withdrawn and fewer than {Threshold} remain.",
            account, NoShowBlockRules.Threshold);
    }

    private static ReleaseOutcome OutcomeOf(ReservationStatus status) =>
        status == ReservationStatus.NoShow ? ReleaseOutcome.NoShow : ReleaseOutcome.CancelledByVenue;

    private static string DefaultReleaseReason(ReleaseOutcome outcome) =>
        outcome == ReleaseOutcome.NoShow
            ? "released: nobody arrived"
            : "released: the venue let the booking go";

    private async Task<ReservationReleaseResult> BuildReleaseAsync(
        Reservation reservation,
        ReleaseOutcome outcome,
        bool tableFreed,
        Guid tableId,
        string tableLabel,
        TableStatus tableStatus,
        bool wasReplay,
        CancellationToken cancellationToken,
        DateTime? guestBlockedUntilUtc = null)
    {
        var branch = await lookups.LoadBranchAsync(reservation.BranchId, cancellationToken);

        var view = await views.ToViewAsync(
            reservation, branch, table: null, wasReplay: wasReplay, trigger: null, cancellationToken);

        return new ReservationReleaseResult(
            view,
            outcome,
            tableFreed,
            tableId,
            tableLabel,
            tableStatus,

            // Stated rather than implied. A client should be able to tell the waiter "this one goes
            // on their record" without knowing the rule.
            CountsTowardNoShowThreshold: outcome == ReleaseOutcome.NoShow,
            wasReplay,

            // Set when this no-show blocked the guest, so the floor can say so at once.
            GuestBlockedUntilUtc: guestBlockedUntilUtc);
    }
}
