using Yalla.Domain.Takeaway;

namespace Yalla.Api.Errors;

internal static partial class ApiExceptionMapper
{
    /// <summary>Takeaway orders.</summary>
    private static MappedError? MapTakeaway(Exception exception) => exception switch
    {
        // Takeaway. Each its own code, so the app and the tablet say what is true. Must stay ABOVE DomainStateException.
        TakeawayNotAcceptedException e => Refuse(
            ErrorCodes.TakeawayNotAccepted,
            e.Message,
            context: new Dictionary<string, object?> { ["branchId"] = e.BranchId }),

        TakeawayClosedException e => Refuse(
            ErrorCodes.TakeawayClosed,
            e.Message,
            context: e.OpensAtUtc is { } opensAtUtc
                ? new Dictionary<string, object?>
                {
                    ["branchId"] = e.BranchId,
                    ["opensAtUtc"] = opensAtUtc,
                    ["opensAt"] = e.OpensAtLocal,
                }
                : new Dictionary<string, object?> { ["branchId"] = e.BranchId }),

        TakeawayBusyException e => Refuse(
            ErrorCodes.TakeawayBusy,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["branchId"] = e.BranchId,
                ["maxInProgress"] = e.MaxInProgress,
            }),

        TakeawaySlotUnavailableException e => Refuse(
            ErrorCodes.TakeawaySlotUnavailable,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["branchId"] = e.BranchId,
                ["at"] = e.At,
                ["slots"] = e.Slots,
            }),

        TakeawayStatusConflictException e => Refuse(
            ErrorCodes.TakeawayStatusConflict,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["orderId"] = e.OrderId,
                ["status"] = (int)e.Status,
            }),

        TakeawayNoShowTooEarlyException e => Refuse(
            ErrorCodes.TakeawayNoShowTooEarly,
            e.Message,
            context: new Dictionary<string, object?>
            {
                ["orderId"] = e.OrderId,
                ["noShowFromUtc"] = e.NoShowFromUtc,
            }),

        _ => null,
    };
}
