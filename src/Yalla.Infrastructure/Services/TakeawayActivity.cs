using System.Globalization;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Takeaway;

namespace Yalla.Infrastructure.Services;

/// <summary>A takeaway order's entries in the activity log, one shape for the counter and the sweep. See <c>docs/activity.md</c>.</summary>
internal static class TakeawayActivity
{
    public static Task RecordAsync(
        IActivityLog activity,
        TakeawayOrder order,
        string action,
        CancellationToken cancellationToken,
        bool bySystem = false,
        bool byGuest = false,
        string? reason = null)
    {
        var collected = action == ActivityActions.TakeawayCollected;

        return activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Takeaway, action, ActivitySubjects.Takeaway, order.Id)
            {
                BranchId = order.BranchId,
                SubjectCode = order.Code,
                GuestName = order.GuestName,
                ItemNames = [.. order.Lines.Select(l => l.NameSnapshot)],
                AmountAmd = collected ? order.PaidAmountAmd : order.TotalAmd,
                IsMoney = collected,
                Reason = reason,
                BySystem = bySystem,
                ByGuest = byGuest,
                Summary = new
                {
                    code = order.Code,
                    items = order.Lines.Sum(l => l.Quantity),
                    totalAmd = order.TotalAmd,
                    status = (int)order.Status,
                    readyAtUtc = order.ReadyAtUtc,
                    pickupLocal = order.RequestedPickupLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    paymentMethod = (int?)order.PaymentMethod,
                    paidAmd = order.PaidAmountAmd,
                },
            },
            cancellationToken);
    }
}
