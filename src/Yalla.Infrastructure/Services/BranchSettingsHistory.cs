using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The settings' History lines (<c>settings.*</c>, docs/activity.md): one path per setting, recorded by the part
/// that saves it, in the same unit of work.
/// </summary>
/// <remarks>
/// The one settings class that takes <see cref="IActivityLog"/> (<c>HandWrittenSideEffectTests.Allowed</c>): the
/// policy and the hours record through it rather than holding the log themselves.
/// </remarks>
internal sealed class BranchSettingsHistory(IActivityLog activity)
{
    /// <summary>A setting changed, for the History (docs/activity.md), in this unit of work.</summary>
    public Task RecordAsync(
        Branch branch, string action, object before, object after, object summary, CancellationToken cancellationToken) =>
        activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Settings, action, ActivitySubjects.Branch, branch.Id)
            {
                BranchId = branch.Id,
                VenueId = branch.VenueId,
                Before = before,
                After = after,
                Summary = summary,
            },
            cancellationToken);
}
