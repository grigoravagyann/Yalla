using Microsoft.EntityFrameworkCore;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Takes away the phones a staff member can no longer use, when the staff screen changes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deactivating somebody unpairs their phones.</b> A tablet stops taking their PIN the moment they are
/// deactivated, and the session they had renews no more; a phone is theirs alone, so a deactivated person's phone
/// is a device nobody can ever open again - and a live device token for nobody is exactly what the device list
/// should not show. Revoking it also ends any session open on it on the next request, not at the next renewal.
/// </para>
/// <para>
/// The same for somebody moved to the kitchen (a phone is not for the kitchen) and somebody moved to another
/// branch (their phone at the old branch no longer takes their PIN). Unused pairing codes for them die with it.
/// </para>
/// </remarks>
internal static class StaffPhones
{
    /// <summary>
    /// Stages the revocation of every live phone <paramref name="staff"/> can no longer use, with their sessions.
    /// </summary>
    /// <returns>The revoked devices, for the caller to evict from the authority cache after its save.</returns>
    public static async Task<IReadOnlyList<Guid>> RevokeUnusableAsync(
        YallaDbContext db,
        StaffMember staff,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var finished = !staff.IsActive || staff.Role == StaffRole.Kitchen;

        if (finished)
        {
            var codes = await db.StaffEnrolmentCodes
                .Where(c => c.ForStaffMemberId == staff.Id && c.RedeemedAtUtc == null && c.SupersededAtUtc == null)
                .ToListAsync(cancellationToken);

            foreach (var code in codes)
            {
                code.Supersede(nowUtc);
            }
        }

        var phones = await db.StaffDevices
            .Where(d => d.BoundStaffMemberId == staff.Id && d.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        var dead = phones
            .Where(d => finished || (staff.BranchId is { } home && d.BranchId != home))
            .ToList();

        if (dead.Count == 0)
        {
            return [];
        }

        var ids = dead.Select(d => d.Id).ToList();

        foreach (var phone in dead)
        {
            phone.Revoke(nowUtc);
        }

        var sessions = await db.StaffSessions
            .Where(s => ids.Contains(s.StaffDeviceId) && s.EndedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.End(nowUtc);
        }

        await ForgetPushSubscriptionsAsync(db, ids, cancellationToken);

        return ids;
    }

    /// <summary>
    /// Stages the History's entry for a device that stopped working, in the caller's save: <c>device.revoked</c> for a
    /// tablet, <c>device.phone-unpaired</c> for somebody's phone, its summary's <c>cause</c> one of
    /// <see cref="PhoneUnpairedBy"/>. See <c>docs/activity.md</c>.
    /// </summary>
    public static async Task RecordGoneAsync(
        YallaDbContext db,
        IActivityLog activity,
        StaffDevice device,
        string cause,
        CancellationToken cancellationToken,
        ActivityProvedActor? provedBy = null)
    {
        if (device.Kind != StaffDeviceKind.Phone)
        {
            await activity.RecordAsync(
                new ActivityRecord(ActivityCategory.Device, ActivityActions.DeviceRevoked, ActivitySubjects.Device, device.Id)
                {
                    BranchId = device.BranchId,
                    Summary = new { name = device.Name },
                },
                cancellationToken);

            return;
        }

        var personId = device.BoundStaffMemberId;
        var staffName = db.StaffMembers.Local.FirstOrDefault(s => s.Id == personId)?.FullName
                        ?? await db.StaffMembers
                            .AsNoTracking()
                            .Where(s => s.Id == personId)
                            .Select(s => s.FullName)
                            .FirstOrDefaultAsync(cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Device, ActivityActions.DevicePhoneUnpaired, ActivitySubjects.Device, device.Id)
            {
                BranchId = device.BranchId,
                ProvedBy = provedBy,
                Summary = new { name = device.Name, staffMemberId = personId, staffName, cause },
            },
            cancellationToken);
    }

    /// <summary>
    /// Stages the deletion of the push subscriptions of devices being revoked, in the caller's save.
    /// </summary>
    /// <remarks>
    /// The fan-out already skips a revoked device; this is so the endpoint - a capability to push to that browser -
    /// is not kept for a device nobody can open again. A browser paired again subscribes again.
    /// </remarks>
    public static async Task ForgetPushSubscriptionsAsync(
        YallaDbContext db,
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken)
    {
        if (deviceIds.Count == 0)
        {
            return;
        }

        var subscriptions = await db.StaffPushSubscriptions
            .Where(s => deviceIds.Contains(s.StaffDeviceId))
            .ToListAsync(cancellationToken);

        db.StaffPushSubscriptions.RemoveRange(subscriptions);
    }
}

/// <summary>Why a phone was unpaired - the <c>cause</c> of a <c>device.phone-unpaired</c> entry. Stable wire words.</summary>
internal static class PhoneUnpairedBy
{
    /// <summary>A manager unpaired it from the console's phones list.</summary>
    public const string Console = "console";

    /// <summary>Its own person unpaired it, from the phone.</summary>
    public const string ItsOwner = "its-owner";

    /// <summary>The same browser was paired again - to somebody else here, or to its person at another branch.</summary>
    public const string Replaced = "replaced";

    /// <summary>Its person was deactivated.</summary>
    public const string Deactivated = "deactivated";

    /// <summary>Its person was moved to the kitchen, which works on its tablet.</summary>
    public const string Kitchen = "kitchen";

    /// <summary>Its person was moved to another branch.</summary>
    public const string OtherBranch = "other-branch";
}
