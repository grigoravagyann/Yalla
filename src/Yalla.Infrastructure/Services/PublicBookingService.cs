using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain.Occupancy;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reading, cancelling and moving one booking with nothing but the manage link.
/// </summary>
/// <remarks>
/// <para>
/// <b>The narrowest surface in the API.</b> One row, reached by one unguessable token, projected
/// into a record that cannot carry anything sensitive - see <see cref="PublicBookingView"/>. There
/// is no list, no search and no second row reachable from here, so a stolen link leaks exactly the
/// booking it is for and nothing beside it.
/// </para>
/// <para>
/// Cancelling is <c>IReservationService.CancelByManageTokenAsync</c> rather than a write of its
/// own. The deadline rule, the lateness record and cancelling the reminder are the same code the
/// app's cancel runs; a second path here is how a web cancel would eventually stop cancelling the
/// reminder, and the diner would get a push about a table they had already given back.
/// </para>
/// <para>
/// Moving is <c>IReservationService.RescheduleByManageTokenAsync</c> for the same reason: the rules,
/// the status decided again, the table's lock, the replaced reminders and the idempotency record are
/// the app's move. Both writes answer by re-reading through the one projection below, so no write can
/// come to show more than the read does.
/// </para>
/// </remarks>
internal sealed class PublicBookingService(
    YallaDbContext db,
    IClock clock,
    IReservationService reservations) : IPublicBookingService
{
    public async Task<PublicBookingView> GetAsync(
        string manageToken,
        CancellationToken cancellationToken = default)
    {
        // A blank token is answered like any other bad one - see ManageBookingFailure.
        if (string.IsNullOrWhiteSpace(manageToken))
        {
            throw ManageBookingFailure.Raise();
        }

        var hash = Secrets.Hash(manageToken);

        // Projected in the query rather than loaded and mapped, so the fields this route does not
        // publish are never read out of the database at all.
        var row = await db.Reservations
            .AsNoTracking()
            .Where(r => r.ManageTokenHash == hash)
            .Select(r => new Row(
                r.Branch.Venue.Name,
                r.Branch.Name,
                r.Branch.Venue.Slug,
                r.Branch.Slug,
                r.Branch.Address,
                r.Branch.TimeZoneId,
                r.DiningTable.Label,
                r.LocalDate,
                r.LocalStartTime,
                r.PartySize,
                r.Status,
                r.Code,
                r.Id,
                r.DiningTableId,
                r.StartUtc,
                r.EndUtc,
                r.CancelledAfterDeadline,
                r.Branch.ReservationPolicy.CancellationDeadlineMinutes,
                r.Note,
                r.DiningTable.FloorArea == null ? null : r.DiningTable.FloorArea.Name,
                r.ApprovalExpiresAtUtc,
                r.DeclineReason,
                r.CancelledBecause,

                // Waiting on the guest before it to answer in the app, beside the row in the same statement.
                db.TableShareRequests.Any(q => q.LaterReservationId == r.Id
                                               && q.Status == Domain.Enums.TableShareRequestStatus.Pending
                                               && q.AsksEarlierGuest),
                r.PhoneUnverified && !db.DinerUsers.Any(d => d.Id == r.DinerUserId && d.PhoneVerifiedAtUtc != null),

                // Whether the link may move the booking: the account it lands on is live with a proved
                // number - the reschedule's own gate, asked ahead so the page hides a button that would 403.
                r.DinerUserId != null
                && db.DinerUsers.Any(d => d.Id == r.DinerUserId
                                          && d.PhoneVerifiedAtUtc != null
                                          && d.IsActive
                                          && d.DeletedAtUtc == null),
                r.GuestConfirmedOnWayAtUtc,
                r.Branch.AcceptsPreOrders,
                r.GuestRunningLateMinutes,
                r.StaffAskedOnMyWayAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        // Unknown and expired, answered identically. The expiry is recomputed here from the same
        // rule the entity uses rather than trusted from the row, and it raises the same exception
        // as no row at all, so nothing on the wire tells the two apart.
        if (row is null || clock.UtcNow > row.EndUtc.AddDays(Reservation.ManageTokenGraceDays))
        {
            throw ManageBookingFailure.Raise();
        }

        var nowUtc = clock.UtcNow;
        var preOrder = await PreOrderReads.ForReservationAsync(db, row.ReservationId, nowUtc, cancellationToken);

        // The branch's menu, for pre-ordering, is the branch page's: reached from the slug pair the link already carries,
        // so the link publishes no id of its own beyond the two changing the booking needs.
        return Project(row, nowUtc) with
        {
            PreOrder = preOrder,
            PreOrderEditableUntilUtc = row.AcceptsPreOrders
                                       && PreOrderRules.BookingTakesEdits(row.Status)
                                       && row.StartUtc > nowUtc
                ? PreOrderRules.LockedAtUtc(row.StartUtc)
                : null,
            GuestConfirmedOnWayAtUtc = row.GuestConfirmedOnWayAtUtc,
            GuestRunningLateMinutes = row.GuestRunningLateMinutes,
            StaffAskedOnMyWayAtUtc = row.StaffAskedOnMyWayAtUtc,
            OnMyWayOpensAtUtc = PreOrderRules.LockedAtUtc(row.StartUtc),

            // The question the push and the feed ask, here for a link holder with no app: of every booking, at 30
            // minutes, or 45 while a pre-order waits for the kitchen; not once the kitchen has it or the party was
            // marked not coming.
            OnYourWayAsked = row.Status == Domain.Enums.ReservationStatus.Confirmed
                             && row.GuestConfirmedOnWayAtUtc is null
                             && row.GuestRunningLateMinutes is null
                             && nowUtc < row.EndUtc
                             && (row.StaffAskedOnMyWayAtUtc is not null
                                 || (preOrder is null
                                     ? nowUtc >= PreOrderRules.OnYourWayAtUtc(row.StartUtc, preOrderWaiting: false)
                                     : preOrder is { State: PreOrderState.Locked or PreOrderState.Editable }
                                       && nowUtc >= PreOrderRules.OnYourWayAtUtc(row.StartUtc, preOrderWaiting: true))),
        };
    }

    public async Task<PublicBookingView> CancelAsync(
        string manageToken,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // The one cancellation path, shared with the app's. Everything this route adds is how the
        // caller proved they may: they are holding the token.
        await reservations.CancelByManageTokenAsync(manageToken, reason, cancellationToken);

        // Re-read through the same projection the GET uses, so the two routes cannot come to
        // disagree about what a manage link is allowed to show.
        return await GetAsync(manageToken, cancellationToken);
    }

    public async Task<PublicBookingView> RescheduleAsync(
        string manageToken,
        RescheduleByManageTokenCommand command,
        CancellationToken cancellationToken = default)
    {
        // The one move, shared with the app's. Everything this route adds is how the caller proved
        // they may: they are holding the token.
        await reservations.RescheduleByManageTokenAsync(manageToken, command, cancellationToken);

        // Re-read through the projection the GET uses - a replay included, which answers with the
        // booking as it stands - so the page renders the answer exactly as it renders the link.
        return await GetAsync(manageToken, cancellationToken);
    }

    private static PublicBookingView Project(Row row, DateTime nowUtc) => new(
        row.VenueName,
        row.BranchName,

        // The branch page's address: the reminder's link is /booking/{token}, which names no branch.
        row.VenueSlug,
        row.BranchSlug,
        row.BranchAddress,
        row.TimeZoneId,
        row.TableLabel,
        row.LocalDate,
        row.LocalStartTime,
        row.PartySize,
        row.Status,
        row.Code,

        // The two ids changing the booking from the link needs: its own, to leave it out of the
        // availability reads, and its table's - no secret, those reads publish every pinned table's.
        ReservationId: row.ReservationId,
        TableId: row.TableId,

        // Absolute, computed from the interval the diner booked and the deadline in force. The page
        // does not reimplement this arithmetic and a later policy edit cannot move a deadline the
        // diner has already been shown, because the start it is measured from never changes.
        CancellationDeadlineUtc: row.StartUtc.AddMinutes(-row.CancellationDeadlineMinutes),

        // Whether cancelling would do anything - not whether it would be free. Cancelling past the
        // deadline is allowed and merely recorded, which is the whole point of the deadline being
        // a record rather than a block.
        CanCancel: ReservationCancellation.CanStillCancel(row.Status),
        CancelledAfterDeadline: row.CancelledAfterDeadline,

        // Whose answer a waiting booking waits for: the guest before it, in the app, or the venue.
        AwaitsEarlierGuestAnswer: row.AsksEarlierGuest && row.Status == Domain.Enums.ReservationStatus.PendingApproval,

        // The diner's own words to the venue (K9): nothing the link holder did not write.
        Note: row.Note,

        // Where the table is, which is about the venue rather than about anybody else.
        RoomName: row.RoomName,

        // The countdown a waiting booking waits under - short notice, or the guest before it - and why a
        // declined one was: the same
        // facts the app shows, about nothing but the link holder's own booking. The column is cleared
        // once the booking stops waiting, so it is only ever a live deadline.
        ApprovalExpiresAtUtc: row.ApprovalExpiresAtUtc,
        DeclinedBecause: row.Status == Domain.Enums.ReservationStatus.CancelledByVenue ? row.DeclineReason : null,

        // Made without a proved number: the page says the venue confirms it, and that changing it means
        // cancelling and booking again.
        PhoneUnverified: row.PhoneUnverified,

        // Whether "change" would do anything: still to come, and an account with a proved number to land
        // on. False for a booking made without an account - those are cancelled and booked again.
        CanChange: row.AccountCanMove
                   && row.Status is Domain.Enums.ReservationStatus.Confirmed or Domain.Enums.ReservationStatus.PendingApproval
                   && row.StartUtc > nowUtc,

        // Cancelled by the platform, not the venue: the guest was blocked for no-shows.
        CancelledBecause: row.CancelledBecause);

    private sealed record Row(
        string VenueName,
        string BranchName,
        string VenueSlug,
        string BranchSlug,
        string BranchAddress,
        string TimeZoneId,
        string TableLabel,
        DateOnly LocalDate,
        TimeOnly LocalStartTime,
        int PartySize,
        Domain.Enums.ReservationStatus Status,
        string Code,
        Guid ReservationId,
        Guid TableId,
        DateTime StartUtc,
        DateTime EndUtc,
        bool CancelledAfterDeadline,
        int CancellationDeadlineMinutes,
        string? Note,
        string? RoomName,
        DateTime? ApprovalExpiresAtUtc,
        Domain.Enums.DeclineReason? DeclineReason,
        Domain.Occupancy.CancellationCause? CancelledBecause,
        bool AsksEarlierGuest,
        bool PhoneUnverified,
        bool AccountCanMove,
        DateTime? GuestConfirmedOnWayAtUtc,
        bool AcceptsPreOrders,
        int? GuestRunningLateMinutes,
        DateTime? StaffAskedOnMyWayAtUtc);
}
