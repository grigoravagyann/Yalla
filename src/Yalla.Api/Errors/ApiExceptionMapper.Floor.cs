using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;

namespace Yalla.Api.Errors;

internal static partial class ApiExceptionMapper
{
    /// <summary>A table's state machine: a lost race, an impossible move, a table taken or booked.</summary>
    private static MappedError? MapTables(Exception exception) => exception switch
    {
        // Somebody else changed the table first. 409 with the table's CURRENT state, so the
        // client can redraw it and tell the user what actually happened rather than just failing.
        // Expected under load - two people really do tap table 7 at the same moment.
        TableStateConflictException e => Refuse(
            ErrorCodes.TableStateConflict,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                // The numeric enum values, matching what the schema declares and what every other
                // response carries. A client comparing this to its generated TableStatus enum
                // should not have to know that this one place spelled it out in English.
                ["attemptedFromStatus"] = (int)e.AttemptedFromStatus,
                ["currentStatus"] = (int)e.CurrentStatus,
                ["currentSessionId"] = e.CurrentSessionId,
            }),

        // An impossible transition, not a race: freeing a table nobody is sitting at, or marking
        // an occupied one broken. 422 - the request was understood and is semantically wrong.
        InvalidTableTransitionException e => Refuse(
            ErrorCodes.InvalidTableTransition,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["fromStatus"] = (int)e.FromStatus,
                ["attemptedToStatus"] = (int)e.ToStatus,
                ["allowedFromHere"] = TableStatusTransitions.From(e.FromStatus)
                    .Select(s => (int)s)
                    .ToArray(),
            }),

        // Somebody else took the table between the diner seeing it free and confirming. 409, not
        // 422: the request was right when it was made and the world moved. The body carries the
        // clashing window AND a fresh availability snapshot, so the app can redraw the floor and
        // show what changed instead of firing a second request into the same contention.
        // Someone is sitting there now. A different sentence from "already booked", because the
        // table may free up early and the diner can act on that.
        TableCurrentlyOccupiedException e => Refuse(
            ErrorCodes.TableCurrentlyOccupied,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["reason"] = (int)e.Reason,
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["requestedStartUtc"] = e.Requested.StartUtc,
                ["requestedEndUtc"] = e.Requested.EndUtc,
                ["seatedAtUtc"] = e.SeatedAtUtc,
                ["projectedFreeAtUtc"] = e.ProjectedFreeAtUtc,
                ["tableSessionId"] = e.TableSessionId,
                ["availability"] = e.Availability,
            }),

        TableAlreadyBookedException e => Refuse(
            ErrorCodes.TableAlreadyBooked,
            e.Message,
            context: BookedContext(e)),

        _ => null,
    };

    /// <summary>Ordering and paying at the table, and the idempotency key a device write carries.</summary>
    private static MappedError? MapOrders(Exception exception) => exception switch
    {
        // Two callers minted the same idempotency key. Not a replay - a replay is the SAME caller
        // sending the same command again - so the honest answer is that the id is taken, and
        // nothing at all about the booking that holds it.
        ClientCommandIdAlreadyUsedException e => Refuse(ErrorCodes.ClientCommandIdInUse, e.Message),

        // A dish sold out. 409 with the item NAMED, because the client has to be able to say which
        // one - "your order could not be placed" sends the diner back to a waiter to find out what
        // went wrong, which is the question the whole feature exists to remove.
        MenuItemUnavailableException e => Refuse(
            ErrorCodes.MenuItemUnavailable,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["menuItemId"] = e.MenuItemId,
                ["itemName"] = e.ItemName,
            }),

        // The bill has been asked for. Its own code rather than a generic refusal, because the
        // client's response differs: show the bill, not the menu.
        TabNotAcceptingOrdersException e => Refuse(
            ErrorCodes.TabNotAcceptingOrders,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tabId"] = e.TabId,
                ["status"] = (int)e.Status,
            }),

        // A guest a waiter named on a staff order who is not on the tab, or not orderable on it. The
        // status and code stay those of the base types (404 not-found, 409 conflicting-state); the
        // context is what lets a tablet tell "that guest" from a deleted dish or another branch's
        // dish, which answer the same. Both must stay ABOVE KeyNotFoundException and
        // DomainStateException, which they derive from.
        NamedGuestNotOnTabException e => Refuse(
            ErrorCodes.NotFound,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["field"] = e.Field,
                ["participantId"] = e.ParticipantId,
            }),

        NamedGuestNotOrderableException e => Refuse(
            ErrorCodes.ConflictingState,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["field"] = e.Field,
                ["participantId"] = e.ParticipantId,
                ["participantStatus"] = (int)e.ParticipantStatus,
            }),

        // A tab hangs off a sitting. Its own code so the tablet can say "seat them first" rather
        // than show a generic conflict.
        TableNotSeatedException e => Refuse(
            ErrorCodes.TableNotSeated,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["status"] = (int)e.Status,
            }),

        // Taking this off would reverse money that has already changed hands, which is a refund.
        LineAlreadyPaidException e => Refuse(
            ErrorCodes.LineAlreadyPaid,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tabId"] = e.TabId,
                ["lineId"] = e.LineId,
            }),

        // The waiter is standing at the table holding notes, so the balance goes in the body. The
        // usual cause is somebody settling in the app while the waiter was typing.
        PaymentExceedsRemainingException e => Refuse(
            ErrorCodes.PaymentExceedsRemaining,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tabId"] = e.TabId,
                ["requestedAmd"] = e.RequestedAmd,
                ["remainingAmd"] = e.RemainingAmd,
            }),

        // 429 rather than 409: this is a rate limit, and a client should back off rather than
        // change what it is asking for.
        ServiceRequestRateLimitedException e => Refuse(
            ErrorCodes.ServiceRequestRateLimited,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tabId"] = e.TabId,
                ["limit"] = e.Limit,
                ["windowMinutes"] = e.WindowMinutes,
            }),

        _ => null,
    };

    /// <summary>A write that lost a race or carries a precondition that no longer holds.</summary>
    private static MappedError? MapStaleWrites(Exception exception) => exception switch
    {
        // Someone else won the race for a table, a tab or a booking. Expected under load.
        DbUpdateConcurrencyException => Refuse(
            ErrorCodes.ConcurrentUpdate,
            "This record changed while you were working on it. Reload and try again."),

        // A free that named a booking whose sitting is no longer the one at the table - the console's
        // stale row. Must stay ABOVE DomainStateException, which it derives from.
        TableSittingChangedException e => Refuse(
            ErrorCodes.TableSittingChanged,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["reservationId"] = e.ReservationId,
                ["tableStatus"] = (int)e.TableStatus,
                ["currentSessionId"] = e.CurrentSessionId,
            }),

        // A queued command that has gone stale. Must stay ABOVE DomainStateException, which it
        // derives from, and distinct from TableStateConflictException: the client shows one
        // immediately and files the other for later.
        CommandPreconditionFailedException e => Refuse(
            ErrorCodes.PreconditionFailed,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tableId"] = e.TableId,
                ["tableLabel"] = e.TableLabel,
                ["expectedFromStatus"] = (int)e.ExpectedFromStatus,
                ["currentStatus"] = (int)e.CurrentStatus,

                // Which half failed. A status mismatch is on the waiter's screen; a version
                // mismatch on a matching status is the one nothing else can show them.
                ["failure"] = (int)e.Failure,
                ["clientCommandId"] = e.ClientCommandId,
            }),

        _ => null,
    };

    /// <summary>A scan the table turned away for now.</summary>
    private static MappedError? MapScan(Exception exception) => exception switch
    {
        ScanTurnedAwayException e => Refuse(
            ErrorCodes.ScanTurnedAway,
            e.Message,
            context: new Dictionary<string, object?> { ["retryAfterUtc"] = e.RetryAfterUtc }),

        _ => null,
    };

    /// <summary>Joining a tab that waits for the waiter.</summary>
    private static MappedError? MapAdmission(Exception exception) => exception switch
    {
        // The phone waits for the waiter; its own code so it shows that, not an error.
        TabAwaitingAdmissionException e => Refuse(
            ErrorCodes.TabAwaitingAdmission,
            e.Message,
            context: new Dictionary<string, object?> { ["tabId"] = e.TabId }),

        TabAdmissionDecidedException e => Refuse(
            ErrorCodes.TabAdmissionDecided,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tabId"] = e.TabId,
                ["admission"] = (int)e.Admission,
            }),

        _ => null,
    };

    /// <summary>Handing a table over, and the History export.</summary>
    private static MappedError? MapTransfers(Exception exception) => exception switch
    {
        // Handing a table over (docs/activity.md). Their own codes, so the tablet says what is true.
        Yalla.Application.Activity.TransferTargetIneligibleException e => Refuse(
            ErrorCodes.TransferTargetIneligible,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["staffMemberId"] = e.StaffMemberId,
                ["reason"] = e.Why,
            }),

        Yalla.Application.Activity.TabNotTransferableException e => Refuse(
            ErrorCodes.TabNotTransferable,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tabId"] = e.TabId,
                ["status"] = (int)e.Status,
            }),

        Yalla.Application.Activity.ActivityExportTooLargeException e => Refuse(
            ErrorCodes.ActivityExportTooLarge,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["rows"] = e.Rows,
                ["maxRows"] = e.MaxRows,
            }),

        _ => null,
    };

    /// <summary>
    /// The context of a lost race for a table.
    /// </summary>
    /// <remarks>
    /// The availability snapshot is added only when there is one. It is best-effort - a floor that
    /// could not be read must not turn a 409 the client can act on into a 500 it cannot - and
    /// <c>DefaultIgnoreCondition</c> does not reach inside a dictionary, so an unconditional entry
    /// would put a bare <c>"availability": null</c> on the wire for a generated client to trip over.
    /// </remarks>
    private static Dictionary<string, object?> BookedContext(TableAlreadyBookedException exception)
    {
        var context = new Dictionary<string, object?>
        {
            // Numeric, like every other enum on the wire and in the schema.
            ["reason"] = (int)exception.Reason,
            ["tableId"] = exception.TableId,
            ["tableLabel"] = exception.TableLabel,
            ["requestedStartUtc"] = exception.Requested.StartUtc,
            ["requestedEndUtc"] = exception.Requested.EndUtc,
            ["conflictingStartUtc"] = exception.Conflicting.StartUtc,
            ["conflictingEndUtc"] = exception.Conflicting.EndUtc,
            ["conflictingReservationId"] = exception.ConflictingReservationId,
        };

        if (exception.Availability is { } availability)
        {
            context["availability"] = availability;
        }

        return context;
    }
}
