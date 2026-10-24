using Microsoft.EntityFrameworkCore;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The History's entries for the console's device admin - an enrolment code made, a device revoked, a PIN lockout
/// cleared - written right after the device service's own save.
/// </summary>
/// <remarks>
/// <b>The one place the log is a second save rather than the change's own.</b> Those three changes live in the staff
/// auth service, which the personal-phone work (<c>feat/staff-phone</c>) is rewriting; recording them from the
/// endpoint keeps that service untouched so the two branches merge cleanly. The cost is that an entry can be missing
/// if the process dies between the two saves - never an entry for a change that did not happen, because this runs
/// only after the change returned. Fold it into the service once the phone work has landed. See <c>docs/activity.md</c>.
/// </remarks>
public interface IDeviceAdminActivity
{
    Task EnrolmentCodeCreatedAsync(Guid branchId, DateTime expiresAtUtc, CancellationToken cancellationToken = default);

    Task DeviceRevokedAsync(Guid branchId, Guid deviceId, CancellationToken cancellationToken = default);

    Task PinLockoutClearedAsync(Guid branchId, Guid staffMemberId, CancellationToken cancellationToken = default);
}

internal sealed class DeviceAdminActivity(YallaDbContext db, IActivityLog activity) : IDeviceAdminActivity
{
    public async Task EnrolmentCodeCreatedAsync(Guid branchId, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
    {
        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Device, ActivityActions.DeviceEnrolmentCodeCreated, ActivitySubjects.Branch, branchId)
            {
                BranchId = branchId,
                Summary = new { expiresAtUtc },
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeviceRevokedAsync(Guid branchId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        var name = await db.StaffDevices
            .AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => d.Name)
            .FirstOrDefaultAsync(cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Device, ActivityActions.DeviceRevoked, ActivitySubjects.Device, deviceId)
            {
                BranchId = branchId,
                Summary = new { name },
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PinLockoutClearedAsync(Guid branchId, Guid staffMemberId, CancellationToken cancellationToken = default)
    {
        var name = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == staffMemberId)
            .Select(s => s.FullName)
            .FirstOrDefaultAsync(cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(
                ActivityCategory.Staff, ActivityActions.StaffPinLockoutCleared, ActivitySubjects.StaffMember, staffMemberId)
            {
                BranchId = branchId,
                Summary = new { name },
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }
}
