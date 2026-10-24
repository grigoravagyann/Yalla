using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
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

        return ids;
    }
}
