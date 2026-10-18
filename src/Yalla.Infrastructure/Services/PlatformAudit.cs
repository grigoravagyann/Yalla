using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>What an audit row is about: the entity, what it was called, and the venue it belongs to.</summary>
/// <param name="Type">The entity type - <c>Venue</c>, <c>Branch</c>, <c>StaffMember</c>, <c>BranchReview</c>, <c>DiningTable</c>.</param>
/// <param name="Id">The entity.</param>
/// <param name="Display">
/// What it was called: a name, a table's label. For a review, the branch it is about - the reviewer is
/// a diner and is never copied into the log.
/// </param>
/// <param name="VenueId">The venue it belongs to; null for the platform admin's own account.</param>
internal sealed record AuditTarget(string Type, Guid Id, string? Display, Guid? VenueId);

/// <summary>
/// Adds a <see cref="PlatformAuditLog"/> row to the current unit of work.
/// </summary>
/// <remarks>
/// <para>
/// It only <i>adds</i>. The caller's <c>SaveChanges</c> commits the row together with the change
/// it records, which is the whole guarantee: no change lands without its entry, and no entry
/// survives a change that rolled back.
/// </para>
/// <para>
/// <b>The actor's name and role are read from their stored row</b>, not from the token, and copied
/// into the entry: the console's activity feed reads "Grigor Avagyan suspended Garden Bistro" from the
/// row alone, as it was when it happened. The one read this costs is skipped when the context is
/// already tracking the actor's row.
/// </para>
/// </remarks>
internal static class PlatformAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task RecordAsync(
        YallaDbContext db,
        ICurrentActor actor,
        IClock clock,
        string action,
        AuditTarget target,
        object changes,
        CancellationToken cancellationToken)
    {
        var actorId = actor.StaffMemberId
                      ?? throw new StaffPermissionException(action, actor.Role, StaffRole.PlatformAdmin);

        var (role, name) = await ActorAsync(db, actorId, cancellationToken);

        db.PlatformAuditLogs.Add(new PlatformAuditLog(
            actorId,
            action,
            target.Type,
            target.Id,
            JsonSerializer.Serialize(changes, Json),
            clock.UtcNow,
            actorRole: role ?? actor.Role,
            actorDisplay: name,
            targetDisplay: target.Display,
            venueId: target.VenueId));
    }

    private static async Task<(StaffRole? Role, string? Name)> ActorAsync(
        YallaDbContext db,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var tracked = db.StaffMembers.Local.FirstOrDefault(s => s.Id == actorId);

        if (tracked is not null)
        {
            return (tracked.Role, tracked.FullName);
        }

        var stored = await db.StaffMembers
            .AsNoTracking()
            .Where(s => s.Id == actorId)
            .Select(s => new { s.Role, s.FullName })
            .FirstOrDefaultAsync(cancellationToken);

        return stored is null ? (null, null) : (stored.Role, stored.FullName);
    }
}
