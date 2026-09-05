using System.Text.Json;
using System.Text.Json.Serialization;
using Yalla.Application.Abstractions;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Adds a <see cref="PlatformAuditLog"/> row to the current unit of work.
/// </summary>
/// <remarks>
/// It only <i>adds</i>. The caller's <c>SaveChanges</c> commits the row together with the change
/// it records, which is the whole guarantee: no change lands without its entry, and no entry
/// survives a change that rolled back.
/// </remarks>
internal static class PlatformAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Record(
        YallaDbContext db,
        ICurrentActor actor,
        IClock clock,
        string action,
        string targetType,
        Guid targetId,
        object changes)
    {
        var actorId = actor.StaffMemberId
                      ?? throw new StaffPermissionException(action, actor.Role, StaffRole.PlatformAdmin);

        db.PlatformAuditLogs.Add(new PlatformAuditLog(
            actorId,
            action,
            targetType,
            targetId,
            JsonSerializer.Serialize(changes, Json),
            clock.UtcNow));
    }
}
