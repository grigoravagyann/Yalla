using Microsoft.EntityFrameworkCore;
using Yalla.Application.Auth;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The three reads the authorisation policies need, each a single indexed lookup.
/// </summary>
internal sealed class AuthorizationQueries(YallaDbContext db) : IAuthorizationQueries
{
    public Task<TabParticipantAccess?> GetTabParticipantAccessAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        db.TabParticipants
            .AsNoTracking()
            .Where(p => p.Id == participantId && p.TabId == tabId)
            .Select(p => new TabParticipantAccess(
                p.TabId,
                p.Tab.BranchId,
                p.Tab.Status,
                p.Tab.ClosedAtUtc,
                p.Status,
                p.CanOrder))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> BranchBelongsToVenueAsync(
        Guid branchId,
        Guid venueId,
        CancellationToken cancellationToken = default) =>
        db.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == branchId && b.VenueId == venueId, cancellationToken);

    public Task<bool> IsStaffDeviceActiveAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default) =>
        db.StaffDevices
            .AsNoTracking()
            .AnyAsync(d => d.Id == deviceId && d.RevokedAtUtc == null, cancellationToken);
}
