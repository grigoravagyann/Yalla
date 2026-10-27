using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What a requested slot becomes, for a new booking and a moved one alike: the branch's gates and rules,
/// the status it lands as, the question to the guest before it at the table, and the refusals.
/// </summary>
internal sealed class ReservationSlotRules(
    YallaDbContext db,
    IAvailabilityQuery availabilityQuery,
    NoShowPolicy noShowPolicy,
    ILogger<ReservationService> logger)
{
    /// <summary>
    /// Refuses a booking - or a move of one - from a channel the branch is not taking bookings from.
    /// </summary>
    /// <param name="channel">
    /// The channel as the server decided it: the route's for a new booking (<c>POST /api/reservations</c> is
    /// <see cref="ReservationChannel.App"/>, the public page's route <see cref="ReservationChannel.Web"/>), the booking's
    /// stored channel for a move - never one a client named.
    /// </param>
    /// <param name="branch">The branch, loaded.</param>
    /// <param name="viaManageLink">Whether a move came in on the booking's manage link - the public page's route.</param>
    internal static void RequireChannelAccepted(ReservationChannel channel, Branch branch, bool viaManageLink = false)
    {
        // The platform's switch first (docs/branch-features.md): a channel the branch is not allowed is
        // feature-not-enabled whatever the owner chose, so the client shows it locked rather than "switched off".
        branch.RequireAllowed(FeatureFor(channel, viaManageLink));

        // A branch that never agreed to take bookings from its public page does not take them.
        // Checked here rather than at the endpoint because a rule that lives in the service holds for
        // any caller, not only the one that remembered.
        RequireWebBookingsAccepted(channel, branch);

        // The app channel's gate (K9): bookings switched on AND a reservation policy somebody at the
        // venue has saved. A branch ships with default rules nobody chose, and an app that lists
        // every branch in the city would otherwise book against those guesses from the day it is
        // created. Web keeps its own rule above, unchanged; Unknown and Staff are not gated.
        if (channel == ReservationChannel.App
            && !(branch.AcceptsWebBookings && branch.ReservationPolicyReviewedAtUtc is not null))
        {
            throw new AppBookingsNotAcceptedException(branch.Id, branch.Name);
        }
    }

    /// <summary>
    /// The platform feature a diner's booking or move needs (docs/branch-features.md, "Booking channels"):
    /// <see cref="BranchFeature.WebBookings"/> on the public page's routes - its booking route, and any booking's manage
    /// link - and for a booking made there moved from a diner's session; <see cref="BranchFeature.AppBookings"/>
    /// otherwise: the app's routes, and a booking made before the channel was recorded.
    /// </summary>
    internal static BranchFeature FeatureFor(ReservationChannel channel, bool viaManageLink) =>
        viaManageLink || channel == ReservationChannel.Web ? BranchFeature.WebBookings : BranchFeature.AppBookings;

    /// <summary>
    /// Refuses a booking from the public page at a branch that has not switched web bookings on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since 2026-10-27 the channel is the route's, not the client's: the public page books through its own route
    /// (<see cref="ReservationChannel.Web"/>), the app through <c>POST /api/reservations</c>
    /// (<see cref="ReservationChannel.App"/>). Both take any proved diner's session, so a hand-written client can
    /// still pick the other route; for the venue's switch that is the right trade - it is a stated preference about
    /// its own page - and for the platform's it was judged an acceptable risk (docs/branch-features.md).
    /// </para>
    /// <para>
    /// The public page reads <c>acceptsWebBookings</c> and hides its booking UI, so in practice
    /// this fires for a stale page whose branch was switched off while somebody had it open.
    /// </para>
    /// </remarks>
    private static void RequireWebBookingsAccepted(ReservationChannel channel, Branch branch)
    {
        if (channel == ReservationChannel.Web && !branch.AcceptsWebBookings)
        {
            throw new WebBookingsNotAcceptedException(branch.Id, branch.Name);
        }
    }

    // ---------------------------------------------------------------- rules

    /// <summary>
    /// Whether the table is pinned on a photo of its own room - the rule availability and the rooms
    /// read apply: a pin left on another room's picture is not a table a diner could have tapped.
    /// </summary>
    internal async Task<bool> IsPinnedOnItsRoomAsync(DiningTable table, CancellationToken cancellationToken) =>
        table.PhotoId is { } photoId
        && table.FloorAreaId is { } roomId
        && await db.FloorAreaPhotos.AnyAsync(p => p.FloorAreaId == roomId && p.PhotoId == photoId, cancellationToken);

    /// <summary>
    /// Every branch rule, each answering with its own named exception.
    /// </summary>
    /// <remarks>
    /// The rules themselves are pure functions in the domain and are shared with the availability
    /// read model, so a table the screen offers is a table this method accepts. All this does is
    /// turn the reason into the exception that carries the numbers behind it.
    /// </remarks>
    internal static void Validate(
        DateOnly localDate,
        TimeOnly localTime,
        int partySize,
        DiningTable table,
        bool pinned,
        OpeningCalendar hours,
        ReservationPolicy policy,
        BranchZone zone,
        BookedInterval proposed,
        DateTime nowUtc)
    {
        var localToday = zone.LocalDateAt(nowUtc);

        var timing = ReservationRules.CheckTiming(
            proposed.StartUtc, nowUtc, localDate, localToday, policy);

        switch (timing)
        {
            case ReservationRejectionReason.LeadTimeTooShort:
                throw new LeadTimeTooShortException(
                    proposed.StartUtc, nowUtc.AddMinutes(policy.MinLeadMinutes), policy.MinLeadMinutes);

            case ReservationRejectionReason.OutsideBookingWindow:
                throw new OutsideBookingWindowException(
                    localDate, localToday.AddDays(policy.BookingWindowDays), policy.BookingWindowDays);
        }

        var localStart = localDate.ToDateTime(localTime);
        var localEnd = zone.ToLocal(proposed.EndUtc);

        if (ReservationRules.CheckOpeningHours(hours, localStart, localEnd) is not null)
        {
            throw new OutsideOpeningHoursException(localDate, localTime, TimeOnly.FromDateTime(localEnd));
        }

        // A table with no pin on one of its room's photos is not offered to diners - availability
        // leaves it out - so a booking that names one anyway is refused as not bookable, the same
        // answer a walk-in-only table gets. Every booking here is a diner's (RequireDiner above); staff seat such a table
        // through the floor, not through this.
        var tableReason = ReservationRules.CheckTable(
            table.Status, table.IsBookable && pinned, table.Seats, partySize, policy);

        switch (tableReason)
        {
            case ReservationRejectionReason.TableNotBookable:
                throw new TableNotBookableException(table.Id, table.Label);

            case ReservationRejectionReason.TableOutOfService:
                throw new TableOutOfServiceException(table.Id, table.Label);

            case ReservationRejectionReason.PartyExceedsCapacity:
                throw new PartyExceedsTableCapacityException(
                    table.Id, table.Label, partySize, table.Seats);

            case ReservationRejectionReason.SeatOverhangExceeded:
                throw new SeatOverhangExceededException(
                    table.Id, table.Label, partySize, table.Seats, policy.MaxSeatOverhang!.Value);
        }
    }

    /// <summary>
    /// Confirmed, or waiting for a human - which of the four reasons it was, and until when the venue
    /// may answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked of a new booking and again of a moved one, with the party size it now has: the same
    /// four reasons, in the same order, either way, and the first that applies is the one stored.
    /// </para>
    /// <para>
    /// <b>Short notice first</b> (<see cref="ShortNoticeRule"/>), because it is the one with a
    /// deadline: tonight's booking waits for the floor for fifteen minutes at most, whatever else is
    /// true of it, and a party of twelve tonight is still a table somebody may be sitting at.
    /// </para>
    /// <para>
    /// A moved booking that was confirmed passes its confirmed size as
    /// <paramref name="approvedPartySize"/>, and a party no larger than that is not held back by the
    /// size threshold - the venue has said yes to that many people already. The other reasons are
    /// asked as for any booking.
    /// </para>
    /// </remarks>
    /// <param name="partySize">How many are coming.</param>
    /// <param name="approvedPartySize">
    /// The party size the booking was confirmed for, on a move of a confirmed booking; null on a new
    /// booking and on a move of one still waiting.
    /// </param>
    /// <param name="policy">The branch's policy.</param>
    /// <param name="dinerUserId">Whose booking, for the no-show count and the guest record.</param>
    /// <param name="startUtc">When the booking starts, which caps the answer window.</param>
    /// <param name="shortNotice">Whether the booking is at short notice - see <see cref="ShortNoticeRule.Applies"/>.</param>
    /// <param name="nowUtc">Now, for the no-show window and the answer window.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<StatusDecision> DecideStatusAsync(
        int partySize,
        int? approvedPartySize,
        ReservationPolicy policy,
        Guid dinerUserId,
        DateTime startUtc,
        bool shortNotice,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (shortNotice)
        {
            // Not "no", and not a guess either: somebody may have just sat down at that table, and
            // only the floor can see. They have fifteen minutes - or until the start - to say.
            return new StatusDecision(
                ReservationStatus.PendingApproval,
                ApprovalTrigger.ShortNotice,
                ShortNoticeRule.AnswerDeadline(nowUtc, startUtc));
        }

        // The venue looks at every booking - unless it auto-confirms guests with a good record and this is one.
        // That skips this reason only: the large party and the no-show history below are still asked, and short
        // notice above and an earlier guest's consent (DecideWithTheTableAsync) never reach here to be skipped.
        if (!policy.AutoConfirm
            && !(policy.AutoConfirmGuestsWithGoodRecord
                 && (await GuestRecords.ForAccountAsync(db, dinerUserId, nowUtc, cancellationToken)).QualifiesAsGood()))
        {
            return new StatusDecision(
                ReservationStatus.PendingApproval, ApprovalTrigger.BranchApprovesEveryBooking, null);
        }

        var alreadyConfirmedThisMany = approvedPartySize is { } approved && partySize <= approved;

        if (!alreadyConfirmedThisMany && ReservationRules.NeedsApproval(partySize, policy))
        {
            // Not a rejection. Twelve people is the most valuable booking of the night and the one
            // most likely to need tables moved, so a human looks at it.
            return new StatusDecision(ReservationStatus.PendingApproval, ApprovalTrigger.LargeParty, null);
        }

        if (!noShowPolicy.Enabled)
        {
            return new StatusDecision(ReservationStatus.Confirmed, null, null);
        }

        var windowStartUtc = noShowPolicy.WindowStartUtc(nowUtc);
        var noShows = await db.Reservations
            .AsNoTracking()
            .CountAsync(
                r => r.DinerUserId == dinerUserId
                     && r.Status == ReservationStatus.NoShow
                     && r.StartUtc >= windowStartUtc,
                cancellationToken);

        if (!noShowPolicy.RequiresApproval(noShows))
        {
            return new StatusDecision(ReservationStatus.Confirmed, null, null);
        }

        logger.LogInformation(
            "Diner {DinerUserId} has {NoShows} no-shows in the last {WindowDays} days; this booking needs approval.",
            dinerUserId, noShows, noShowPolicy.WindowDays);

        return new StatusDecision(ReservationStatus.PendingApproval, ApprovalTrigger.NoShowHistory, null);
    }

    /// <summary>
    /// What a booking becomes once the table's evening is known: the status creation's rules gave it,
    /// or - when the guest before it may still be at the table - a wait for that guest's yes, and the
    /// question to ask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Tonight</b> (short notice) the floor decides, as for any short-notice booking: the same
    /// fifteen minutes, with the guest before named on the awaiting card and asked by the floor - a
    /// request the floor answers is opened beside it, so approving it holds the guest before to the time.
    /// </para>
    /// <para>
    /// <b>Another day</b> the booking waits <c>EarlierGuestConsent</c>, two hours at most, never past the
    /// earlier booking's start nor its own. The earlier guest is asked in the app when they booked from it,
    /// have a phone to push to, and nothing else about this booking needs a human - unanswered, it is
    /// declined <c>EarlierGuestNoAnswer</c>. Otherwise the floor asks them by telephone from the awaiting
    /// list, with the same two hours - unanswered, it is declined <c>NoAnswer</c>, the venue's silence. Why
    /// it went to the floor is stored on the question (<c>FallbackTrigger</c>), so it is decided again
    /// without it when the earlier guest gets out of the way.
    /// </para>
    /// </remarks>
    internal async Task<(StatusDecision Status, ShareAsk? Ask)> DecideWithTheTableAsync(
        TableSequencingVerdict verdict,
        StatusDecision status,
        bool shortNotice,
        DateTime startUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!verdict.NeedsEarlierGuest)
        {
            return (status, null);
        }

        var earlier = verdict.Earlier!.Value;
        var freeBy = verdict.EarlierGuestFreeByUtc!.Value;

        if (shortNotice && status is { Trigger: ApprovalTrigger.ShortNotice, ApprovalExpiresAtUtc: { } deadline })
        {
            return (
                status,
                new ShareAsk(earlier.ReservationId, freeBy, deadline, AsksEarlierGuest: false, ApprovalTrigger.ShortNotice));
        }

        // Something else about this booking already needs a human - a venue that approves every booking,
        // a large party, a no-show history - so the floor decides it with the guest before on the card.
        var otherwise = status.Status == ReservationStatus.PendingApproval ? status.Trigger : null;

        var inApp = otherwise is null
                    && status.Status != ReservationStatus.PendingApproval
                    && earlier.StartUtc > nowUtc
                    && await TableShares.CanAskInAppAsync(db, earlier.ReservationId, cancellationToken);

        // The same two hours whoever answers - never past the earlier booking's start, nor this one's.
        // Asked by the floor, a wait with no clock would leave the later guest waiting for days on a
        // venue that never gets round to the call; past it the sweep declines it for the venue not
        // answering (NoAnswer), as it declines tonight's.
        var expiresAtUtc = TableSequencing.AnswerDeadline(
            nowUtc, TableShareRequest.AnswerWindowMinutes, earlier.StartUtc, startUtc);

        return (
            new StatusDecision(ReservationStatus.PendingApproval, ApprovalTrigger.EarlierGuestConsent, expiresAtUtc),
            new ShareAsk(
                earlier.ReservationId, freeBy, expiresAtUtc, AsksEarlierGuest: inApp, FallbackTrigger: inApp ? null : otherwise));
    }

    // ---------------------------------------------------------------- refusals

    /// <summary>The refusal for a time to leave by the booking did not accept, in the branch's wall clock.</summary>
    internal static FreeByNotAcceptedException FreeByNotAccepted(
        DiningTable table,
        FreeByRequirement requirement,
        BranchZone zone,
        DateTime? accepted) =>
        new(
            table.Id,
            table.Label,
            requirement.FreeByUtc,
            zone.LocalTimeAt(requirement.FreeByUtc),
            requirement.NextStartUtc,
            accepted);

    /// <summary>
    /// Builds the "somebody is sitting there" refusal, with a fresh floor snapshot attached.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ConflictAsync"/> deliberately - same shape, same best-effort snapshot -
    /// because the two refusals differ in what they mean to the diner, not in how they are built.
    /// </remarks>
    internal async Task<TableCurrentlyOccupiedException> OccupiedAsync(
        AvailabilityRequest snapshot,
        DiningTable table,
        BookedInterval proposed,
        TableSession sitting,
        ReservationPolicy policy,
        CancellationToken cancellationToken)
    {
        var projected = SessionOccupancy.ProjectedInterval(sitting.SeatedAtUtc, policy.TurnTimeMinutes);

        logger.LogInformation(
            "Table {TableLabel} has been occupied since {SeatedAt:o}; the sitting runs into the {Start:o} booking.",
            table.Label, sitting.SeatedAtUtc, proposed.StartUtc);

        BranchAvailability? availability = null;

        try
        {
            availability = await availabilityQuery.GetAvailabilityAsync(snapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not attach an availability snapshot to the occupancy conflict.");
        }

        return new TableCurrentlyOccupiedException(
            snapshot.BranchId,
            table.Id,
            table.Label,
            proposed,
            sitting.SeatedAtUtc,
            projected.EndUtc,
            sitting.Id,
            availability);
    }

    internal async Task<TableAlreadyBookedException> ConflictAsync(
        AvailabilityRequest snapshot,
        DiningTable table,
        BookedInterval proposed,
        BookingClash conflict,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Table {TableLabel} cannot take {Start:o}: booking {ReservationId} is in the way ({Reason}).",
            table.Label, proposed.StartUtc, conflict.ReservationId, conflict.Reason);

        BranchAvailability? availability = null;

        try
        {
            // So the app can redraw the floor and show what changed, rather than only being told
            // no. Best effort: failing to build it must not replace a 409 the client can act on
            // with a 500 it cannot.
            availability = await availabilityQuery.GetAvailabilityAsync(snapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not attach an availability snapshot to the booking conflict.");
        }

        return new TableAlreadyBookedException(
            snapshot.BranchId,
            table.Id,
            table.Label,
            proposed,
            conflict.Interval,
            conflict.ReservationId,
            availability,
            conflict.Reason);
    }

    /// <summary>What a booking lands as: the status, why it waits, and until when the venue may answer.</summary>
    internal sealed record StatusDecision(
        ReservationStatus Status,
        ApprovalTrigger? Trigger,
        DateTime? ApprovalExpiresAtUtc);

    /// <summary>The question a booking asks the booking before it on the table, before it is staged.</summary>
    /// <param name="EarlierReservationId">The booking whose guest is asked.</param>
    /// <param name="FreeByUtc">The time to have left by.</param>
    /// <param name="ExpiresAtUtc">The deadline - the later booking's own <c>ApprovalExpiresAtUtc</c>.</param>
    /// <param name="AsksEarlierGuest">In the app; or false, the floor asks.</param>
    /// <param name="FallbackTrigger">Why the booking would still wait without the question, or null.</param>
    internal sealed record ShareAsk(
        Guid EarlierReservationId,
        DateTime FreeByUtc,
        DateTime ExpiresAtUtc,
        bool AsksEarlierGuest,
        ApprovalTrigger? FallbackTrigger);
}
