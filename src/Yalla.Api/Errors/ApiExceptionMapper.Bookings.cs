using Yalla.Application.Reservations;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;

namespace Yalla.Api.Errors;

internal static partial class ApiExceptionMapper
{
    /// <summary>Lock contention (retryable), and the booking rules' family of refusals.</summary>
    private static MappedError? MapContention(Exception exception) => exception switch
    {
        // Contention, not refusal. 503 with retryable set, because the client's correct response
        // is to try again - with the same clientCommandId - whereas a 409 will never succeed no
        // matter how often it is repeated. Collapsing the two would teach clients to retry
        // conflicts, which is how a party ends up with two tables.
        ReservationLockTimeoutException e => Refuse(
            ErrorCodes.ReservationLockTimeout,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["timeoutMilliseconds"] = e.TimeoutMilliseconds,
                ["retryable"] = e.Retryable,
            }),

        // The same contention reached from the floor screen instead of a diner's phone: a waiter
        // seating a walk-in while a booking holds the table. Below the arm above, because the
        // booking-specific type derives from this one and would otherwise never be reached.
        TableLockTimeoutException e => Refuse(
            ErrorCodes.TableLockTimeout,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["timeoutMilliseconds"] = e.TimeoutMilliseconds,
                ["retryable"] = e.Retryable,
            }),

        // One entry for the whole family of booking refusals, each answering with its own code
        // and its own numbers. 422: understood, and semantically wrong. Adding a rule is a new
        // exception type and a new constant - this mapper does not change.
        ReservationRejectedException e => RefuseFamily(
            e.Code,
            StatusCodes.Status422UnprocessableEntity,
            e.Message,
            context: e.Context),

        _ => null,
    };

    /// <summary>A booking's own refusals: holding, freeing, the earlier guest, changes, approvals, the tab.</summary>
    private static MappedError? MapBookings(Exception exception) => exception switch
    {
        // "Keep my table" refused, and which refusal: not started yet or not a confirmed booking,
        // the one extension spent, or none offered. Each its own code, because the app inferred
        // "you already let them know" from a bare 409 - and told a diner at a branch with no
        // extensions exactly that. Must stay ABOVE DomainStateException, which it derives from.
        HoldExtensionRefusedException e => RefuseFamily(
            e.Code,
            StatusCodes.Status409Conflict,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["startUtc"] = e.StartUtc,
            }),

        // A table whose next booking cuts the sitting short, booked without accepting the time to leave
        // by. 422 naming the field, with the time to accept, so the app asks "will you free the table by
        // 21:25?" and sends it again.
        FreeByNotAcceptedException e => Refuse(
            ErrorCodes.ReservationFreeByRequired,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["field"] = FreeByNotAcceptedException.Field,
                ["requiredFreeByUtc"] = e.RequiredFreeByUtc,
                ["requiredFreeByLocal"] = e.RequiredFreeByLocal.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                ["nextBookingStartUtc"] = e.NextBookingStartUtc,
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
            },
            errors: new Dictionary<string, string[]> { [FreeByNotAcceptedException.Field] = [e.Message] }),

        // Staff deciding a booking that waits on the guest before it to answer in the app. Theirs to
        // answer. Must stay ABOVE DomainStateException, which it derives from.
        ReservationAwaitingEarlierGuestException e => Refuse(
            ErrorCodes.ReservationAwaitingEarlierGuest,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["requestId"] = e.RequestId,
                ["expiresAtUtc"] = e.ExpiresAtUtc,
            }),

        // The same refusal on a manage link, which names nothing about the other booking: the link shows
        // one booking, and whoever opened it from a group chat learns no second. No context on purpose.
        // Must stay ABOVE DomainStateException.
        ManageLinkOverlapsOwnException e => Refuse(ErrorCodes.ReservationOverlapsOwn, e.Message),

        // A diner's second table at the same branch while their first booking runs. Its own code so the
        // app can name the booking they already have. Must stay ABOVE DomainStateException.
        ReservationOverlapsOwnException e => Refuse(
            ErrorCodes.ReservationOverlapsOwn,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["code"] = e.Code,
                ["localDate"] = e.LocalDate,
                ["localStartTime"] = e.LocalStartTime,
                ["tableLabel"] = e.TableLabel,
                ["venueName"] = e.VenueName,
                ["branchName"] = e.BranchName,
            }),

        // An answer to a request for a table that no longer waits. Its own code so the app drops the
        // card. Must stay ABOVE DomainStateException, which it derives from.
        TableRequestNotPendingException e => Refuse(
            ErrorCodes.TableRequestNotPending,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["requestId"] = e.RequestId,
                ["status"] = (int)e.Status,
                ["expiresAtUtc"] = e.ExpiresAtUtc,
            }),

        // A change to a booking that is finished with or under way. Its own code so the app can say
        // "this booking can no longer be changed" rather than offer another slot that will be refused
        // the same way. Must stay ABOVE DomainStateException, which it derives from.
        ReservationNotChangeableException e => Refuse(
            ErrorCodes.ReservationNotChangeable,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["status"] = (int)e.Status,
                ["startUtc"] = e.StartUtc,
            }),

        // A no-show before the booking's start: nobody can have failed to come yet. Its own code so the
        // tablet can say "not due until 19:00" rather than a conflict the waiter answers by tapping again.
        // Must stay ABOVE DomainStateException, which it derives from.
        ReservationNotStartedException e => Refuse(
            ErrorCodes.ReservationNotStarted,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["startUtc"] = e.StartUtc,
            }),

        // A yes or no that came too late: the short-notice window closed, the booking was already
        // declined for no answer, or it has started. Its own code so the tablet drops the request and
        // says why, rather than showing a conflict the waiter answers by tapping again. Must stay ABOVE
        // DomainStateException, which it derives from.
        ReservationApprovalExpiredException e => Refuse(
            ErrorCodes.ReservationApprovalExpired,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["status"] = (int)e.Status,
                ["startUtc"] = e.StartUtc,
                ["approvalExpiresAtUtc"] = e.ApprovalExpiresAtUtc,
            }),

        // "I'm at my table" refused, and which refusal: too early, over, or a booking not expecting
        // its party. Each its own code with the booking's facts, so the app can say "from 19:10" or
        // "this booking was cancelled" rather than that something went wrong. Must stay ABOVE
        // DomainStateException, which it derives from.
        BookingTabRefusedException e => RefuseFamily(
            e.Code,
            StatusCodes.Status409Conflict,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["status"] = (int)e.Status,
                ["startUtc"] = e.StartUtc,
                ["endUtc"] = e.EndUtc,
                ["earliestUtc"] = e.EarliestUtc,
                ["tableLabel"] = e.TableLabel,
            }),

        _ => null,
    };

    /// <summary>The active-booking limit per number, and a booking the platform cancelled.</summary>
    private static MappedError? MapBookingLimits(Exception exception) => exception switch
    {
        // At most three active bookings per proved number. The caller's own earliest is named - only to its
        // own account; the limit always. Must stay ABOVE DomainStateException.
        PhoneHasActiveBookingException e => Refuse(
            ErrorCodes.PhoneHasActiveBooking,
            e.Message,
            context: PhoneHasActiveBookingContextOf(e)),

        // The platform cancelled it - a guest's no-show block. Must stay ABOVE DomainStateException.
        ReservationCancelledByBlockException e => Refuse(
            ErrorCodes.ReservationCancelledByBlock,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["cancelledAtUtc"] = e.CancelledAtUtc,
            }),

        _ => null,
    };

    /// <summary>Pre-orders, and "on my way" and its question.</summary>
    private static MappedError? MapPreOrdersAndOnMyWay(Exception exception) => exception switch
    {
        // Pre-orders. Each its own code so the app and the tablet say what is true rather than "conflict". Must stay
        // ABOVE DomainStateException, which they derive from.
        PreOrderLockedException e => Refuse(
            ErrorCodes.PreOrderLocked,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["lockedAtUtc"] = e.LockedAtUtc,
            }),

        PreOrderAlreadySentException e => Refuse(
            ErrorCodes.PreOrderAlreadySent,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["sentAtUtc"] = e.SentAtUtc,
            }),

        PreOrdersNotAcceptedException e => Refuse(
            ErrorCodes.PreOrdersNotAccepted,
            e.Message,
            context: new Dictionary<string, object?> { ["branchId"] = e.BranchId }),

        PreOrderNotSendableException e => Refuse(
            ErrorCodes.PreOrderNotSendable,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["status"] = (int)e.Status,
                ["sendableFromUtc"] = e.SendableFromUtc,
                ["endUtc"] = e.EndUtc,
            }),

        OnMyWayNotOpenException e => Refuse(
            ErrorCodes.OnMyWayNotOpen,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["status"] = (int)e.Status,
                ["opensAtUtc"] = e.OpensAtUtc,
            }),

        // "Ask: are you coming?" refused - each its own code, so the tablet says why. Must stay ABOVE DomainStateException.
        AskOnMyWayNotOpenException e => Refuse(
            ErrorCodes.AskOnMyWayNotOpen,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["status"] = (int)e.Status,
                ["startUtc"] = e.StartUtc,
            }),

        AskOnMyWayTooEarlyException e => Refuse(
            ErrorCodes.AskOnMyWayTooEarly,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["opensAtUtc"] = e.OpensAtUtc,
            }),

        AskOnMyWayAskedRecentlyException e => Refuse(
            ErrorCodes.AskOnMyWayAskedRecently,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reservationId"] = e.ReservationId,
                ["askedAtUtc"] = e.AskedAtUtc,
                ["nextAllowedAtUtc"] = e.NextAllowedAtUtc,
            }),

        GuestAlreadyAnsweredException e => Refuse(
            ErrorCodes.GuestAlreadyAnswered,
            e.Message,
            context: e.RunningLateMinutes is { } late
                ? new Dictionary<string, object?>
                {
                    ["reservationId"] = e.ReservationId,
                    ["answeredAtUtc"] = e.AnsweredAtUtc,
                    ["runningLateMinutes"] = late,
                }
                : new Dictionary<string, object?>
                {
                    ["reservationId"] = e.ReservationId,
                    ["answeredAtUtc"] = e.AnsweredAtUtc,
                }),

        _ => null,
    };

    /// <summary>A no-show that stands, and a guest blocked for no-shows.</summary>
    private static MappedError? MapNoShows(Exception exception) => exception switch
    {
        // The no-show's service day is over: it stands. Must stay ABOVE DomainStateException.
        NoShowUndoWindowClosedException e => Refuse(
            ErrorCodes.NoShowUndoWindowClosed,
            e.Message,
            context: new Dictionary<string, object?> { ["undoableUntilUtc"] = e.UndoableUntilUtc }),

        // Blocked for three no-shows in three days, until the block ends.
        BookingBlockedForNoShowsException e => Refuse(
            ErrorCodes.BookingBlockedNoShows,
            e.Message,
            context: new Dictionary<string, object?> { ["blockedUntilUtc"] = e.BlockedUntilUtc }),

        _ => null,
    };

    /// <summary>
    /// <c>phone-has-active-booking</c>'s context: the limit, and the caller's own earliest active booking when
    /// there is one.
    /// </summary>
    private static Dictionary<string, object?> PhoneHasActiveBookingContextOf(PhoneHasActiveBookingException e)
    {
        var context = new Dictionary<string, object?> { ["maxActiveBookings"] = e.MaxActiveBookings };

        if (e.Existing is { } held)
        {
            context["reservationId"] = held.ReservationId;
            context["code"] = held.Code;
            context["startUtc"] = held.StartUtc;
            context["localDate"] = held.LocalDate;
            context["localStartTime"] = held.LocalStartTime;
            context["venueName"] = held.VenueName;
            context["branchName"] = held.BranchName;
        }

        return context;
    }
}
