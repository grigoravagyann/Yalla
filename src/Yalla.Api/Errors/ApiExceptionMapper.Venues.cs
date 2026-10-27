using Yalla.Domain.Venues;

namespace Yalla.Api.Errors;

internal static partial class ApiExceptionMapper
{
    /// <summary>Branch settings saved over somebody else's, and a review without a visit.</summary>
    private static MappedError? MapBranchSettings(Exception exception) => exception switch
    {
        // The listing form moves the branch and the caller is not the owner (K5). Its own code rather
        // than the generic 403, because the console has something specific to say: the rest of the
        // form was fine, and only the owner can move the pin.
        Yalla.Application.BranchSettings.RelocationNotAllowedException e => Refuse(
            ErrorCodes.RelocationNotAllowed,
            e.Message,
            context: new Dictionary<string, object?> { ["branchId"] = e.BranchId }),

        // Somebody saved the floor plan since this editor loaded it (K6). The current version rides
        // along so the editor can reload against it.
        Yalla.Application.BranchSettings.FloorPlanChangedException e => Refuse(
            ErrorCodes.FloorPlanChanged,
            e.Message,
            context: new Dictionary<string, object?> { ["currentVersion"] = e.CurrentVersion }),

        // Somebody saved the special days since this editor loaded them. The list is replaced whole, so the
        // later save is refused rather than allowed to undo the other; the current version rides along.
        Yalla.Application.BranchSettings.SpecialOpeningDaysChangedException e => Refuse(
            ErrorCodes.ConcurrentUpdate,
            e.Message,
            context: new Dictionary<string, object?> { ["currentVersion"] = e.CurrentVersion }),

        // A table that is booked and not yet sat at cannot go: deactivating it would strand the
        // booking. The tables and the booking codes ride along so the console can say what to move.
        Yalla.Application.BranchSettings.TableHasFutureBookingsException e => Refuse(
            ErrorCodes.TableHasFutureBookings,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["tables"] = e.Tables,
                ["bookings"] = e.Bookings,
            }),

        // A first review from a diner with no visit in the window (K8). Its own code rather than the
        // generic 403, because the app has something to say - "you can review a place after you have
        // been" - and the window rides along so it can say how long for.
        ReviewNeedsVisitException e => Refuse(
            ErrorCodes.ReviewNeedsVisit,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["branchId"] = e.BranchId,
                ["windowDays"] = e.WindowDays,
            }),

        _ => null,
    };

    /// <summary>A branch that is shut, unfinished, unpaid for, or not taking this channel's bookings.</summary>
    private static MappedError? MapVenueAvailability(Exception exception) => exception switch
    {
        // The venue stopped paying, or was deleted, and this is a new booking or a new tab. Not a
        // 403 and not a 404: the branch is real and the caller is welcome, it is simply shut.
        // Must stay ABOVE the general DomainStateException arm it derives from.
        BranchUnavailableException e => Refuse(
            ErrorCodes.BranchUnavailable,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["branchId"] = e.BranchId,
                ["reason"] = e.Reason,
            }),

        // The menu is not finished, so the branch cannot start taking diners. Must stay ABOVE the
        // general DomainStateException arm it derives from. The count is the point: "eleven dishes
        // still need a photo" is something a manager can act on this afternoon, and "cannot
        // upgrade" sends them to support.
        BranchNotReadyForDinersException e => Refuse(
            ErrorCodes.BranchNotReady,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["branchId"] = e.BranchId,
                ["incompleteMenuItemCount"] = e.IncompleteMenuItemCount,
            }),

        // Both derive from DomainStateException and must stay ABOVE it.
        //
        // Not a 403. The caller is allowed to be here; the platform has not allowed what they
        // asked at this branch. The code is what the apps branch on to hide or lock the feature.
        FeatureNotEnabledException e => Refuse(
            ErrorCodes.FeatureNotEnabled,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["feature"] = e.Feature,
                ["branchId"] = e.BranchId,
            }),

        VenueDeletionBlockedException e => Refuse(
            ErrorCodes.VenueDeletionBlocked,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["openTabs"] = e.OpenTabs,
                ["futureReservations"] = e.FutureReservations,
            }),

        // The venue has not switched its public page on for bookings. Its own code so the page
        // can say "call the venue" rather than showing a generic conflict. Must stay ABOVE
        // DomainStateException, which it derives from.
        WebBookingsNotAcceptedException e => Refuse(
            ErrorCodes.WebBookingsNotAccepted,
            e.Message,
            context: new Dictionary<string, object?> { ["branchId"] = e.BranchId }),

        // The app channel's own gate (K9): bookings switched off, or the policy never saved. Its own
        // code so the app can say "book with the venue directly" rather than show a generic conflict.
        // Must stay ABOVE DomainStateException, which it derives from.
        AppBookingsNotAcceptedException e => Refuse(
            ErrorCodes.BookingsNotAccepted,
            e.Message,
            context: new Dictionary<string, object?> { ["branchId"] = e.BranchId }),

        _ => null,
    };
}
