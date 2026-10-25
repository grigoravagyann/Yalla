using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Messaging;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What a booking's account is told when somebody else acts on the booking - it was decided, changed or
/// cancelled from its link, or the guest's no-show block was lifted - the push and the feed entry beside it,
/// in the caller's unit of work.
/// </summary>
internal static class ReservationNotices
{
    /// <summary>
    /// The idempotency key a decision's push is written under: <c>reservation:{id}:{what}</c>, or - when
    /// that is taken already - the same with the decision's moment after it.
    /// </summary>
    /// <remarks>
    /// One booking can be decided more than once. A move sends a confirmed booking back for approval,
    /// so it can be approved twice; and a declined change leaves the booking standing at the slot it
    /// had, so it can be declined - or run out of time - twice as well. The first message keeps its key
    /// once sent, so a later one is named for when it was made rather than colliding with it.
    /// </remarks>
    internal static async Task<string> DecisionKeyAsync(
        YallaDbContext db,
        Guid reservationId,
        string what,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var key = OutboxMessageTypes.KeyFor("reservation", reservationId, what);

        return await db.OutboxMessages.AnyAsync(m => m.IdempotencyKey == key, cancellationToken)
            ? OutboxMessageTypes.KeyFor(
                "reservation",
                reservationId,
                what + ":" + nowUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture))
            : key;
    }

    /// <summary>
    /// Tells the diner a booking that was waiting has been decided - the push and the feed entry
    /// beside it - in the caller's unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One copy for the two places a waiting booking is decided: staff (<see cref="ReservationDecisions"/>),
    /// and the sweep that declines a short-notice request nobody answered (<see cref="ApprovalExpirySweep"/>).
    /// The push payload says which with <c>expired</c>, so the diner reads an apology - the venue did not
    /// look in time - rather than the venue's "no".
    /// </para>
    /// <para>
    /// A booking whose diner account is gone has nobody to tell. Nothing is written for it, rather
    /// than a push that could never be addressed and would only be retried into the dead letters.
    /// </para>
    /// </remarks>
    /// <param name="db">The caller's unit of work, for the feed entry.</param>
    /// <param name="outbox">The caller's outbox, for the push.</param>
    /// <param name="reservation">The booking as it now stands.</param>
    /// <param name="branch">Its branch, with the venue, for the names.</param>
    /// <param name="approved">Whether it was a yes.</param>
    /// <param name="expired">Whether it was the clock: nobody answered in time.</param>
    /// <param name="idempotencyKey">The push's key - see <see cref="DecisionKeyAsync"/>.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="change">
    /// For a declined change of a confirmed booking: whether the booking went back to the slot it had,
    /// and the slot that was asked for. Null for every other decision.
    /// </param>
    /// <param name="because">Why a decline happened, carried as <c>declinedBecause</c>; null for an approval.</param>
    internal static void TellDinerDecided(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        Branch branch,
        bool approved,
        bool expired,
        string idempotencyKey,
        DateTime nowUtc,
        DeclinedChange? change = null,
        DeclineReason? because = null)
    {
        if (reservation.DinerUserId is null)
        {
            return;
        }

        var venueName = branch.Venue?.Name ?? branch.Name;

        outbox.Enqueue(
            OutboxMessageTypes.ReservationDecided,
            new
            {
                reservationId = reservation.Id,
                dinerUserId = reservation.DinerUserId,
                venueName,
                branchName = branch.Name,
                tableLabel = string.Empty,
                localStartTime = reservation.LocalStartTime,
                reservationCode = reservation.Code,
                graceExtensionMinutes = 0,
                approved,
                expired,
                wasChange = change is not null,
                originalKept = change?.OriginalKept ?? false,
                declinedBecause = approved ? null : because,
            },
            nowUtc,
            idempotencyKey);

        // The same fact into the diner's feed, beside its push and in the same save (K12).
        DinerNotices.BookingDecided(
            db, reservation, venueName, branch.Name, approved, expired, nowUtc, change, approved ? null : because);
    }

    /// <summary>
    /// Tells a booking's account that it was changed, or cancelled, from its manage link - the push and the
    /// feed entry beside it - in the caller's unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> The link is a bearer capability that goes round group chats and into a calendar file, so
    /// whoever acted may not be the diner. A booking moved without their knowing is a no-show on their
    /// account; one cancelled without their knowing is an evening out that is not there. The app's own
    /// change and cancel, made by the account holder, tell nobody anything new and write nothing.
    /// </para>
    /// <para>
    /// The push is keyed under the booking (<c>reservation:{id}:changed-by-link:{instant}</c>,
    /// <c>…:cancelled-by-link</c>), so the next change or cancel - which cancels the booking's unsent
    /// messages - replaces one not yet sent: the diner hears where it stands, once. The feed keeps each.
    /// A booking whose account is gone has nobody to tell, and nothing is written.
    /// </para>
    /// </remarks>
    internal static void TellDinerDoneFromLink(
        YallaDbContext db,
        IOutbox outbox,
        Reservation reservation,
        Branch branch,
        bool changed,
        DateTime nowUtc)
    {
        if (reservation.DinerUserId is not { } dinerUserId)
        {
            return;
        }

        var venueName = branch.Venue?.Name ?? branch.Name;

        outbox.Enqueue(
            changed ? OutboxMessageTypes.ReservationChangedByLink : OutboxMessageTypes.ReservationCancelledByLink,
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
                changed
                    ? "changed-by-link:" + nowUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                    : "cancelled-by-link"));

        if (changed)
        {
            DinerNotices.BookingChangedByLink(db, reservation, venueName, branch.Name, nowUtc);
        }
        else
        {
            DinerNotices.BookingCancelledByLink(db, reservation, venueName, branch.Name, nowUtc);
        }
    }

    /// <summary>"You can book again" - the push and the feed entry, in the caller's unit of work.</summary>
    internal static void TellDinerUnblocked(YallaDbContext db, IOutbox outbox, BookingBlock block, DateTime nowUtc)
    {
        outbox.Enqueue(
            OutboxMessageTypes.DinerBookingsUnblocked,
            new { dinerUserId = block.DinerUserId, liftedBecause = block.LiftedBecause },
            nowUtc,
            OutboxMessageTypes.KeyFor("booking-block", block.Id, "lifted"));

        DinerNotices.BookingsUnblocked(db, block.DinerUserId, block.LiftedBecause!.Value, nowUtc);
    }
}
