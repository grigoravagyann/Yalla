using Yalla.Domain.Common;

namespace Yalla.Domain.Audit;

/// <summary>
/// One thing a platform admin did: who, what, to which entity, and a JSON snapshot of what changed.
/// </summary>
/// <remarks>
/// <para>
/// This is the tier that can delete a venue and change what a customer pays, so the log is not
/// optional. Every platform action adds one of these to the same unit of work as the change it
/// records and commits them together: a change without its log entry cannot land, and a log
/// entry for a change that rolled back cannot either.
/// </para>
/// <para>
/// Separate from <c>TableStateChange</c> on purpose. That log is about the floor and is read by
/// venue staff; this one is about customers and money and is read by Yalla.
/// </para>
/// </remarks>
public sealed class PlatformAuditLog : Entity
{
    /// <summary>The staff member who acted. Usually a platform admin; a manager for the actions venue staff may also take, such as regenerating a QR code.</summary>
    public Guid ActorStaffMemberId { get; private set; }

    /// <summary>What was done, as a stable slug: <c>venue.create</c>, <c>venue.suspend</c>, <c>branch.update</c>, <c>table.regenerate-qr</c>.</summary>
    public string Action { get; private set; } = null!;

    /// <summary>The entity type acted on: <c>Venue</c>, <c>Branch</c>, <c>DiningTable</c>.</summary>
    public string TargetType { get; private set; } = null!;

    public Guid TargetId { get; private set; }

    /// <summary>What changed, as JSON. Shape varies by action; typically <c>before</c> and <c>after</c>.</summary>
    public string ChangesJson { get; private set; } = null!;

    public DateTime AtUtc { get; private set; }

    private PlatformAuditLog()
    {
    }

    public PlatformAuditLog(
        Guid actorStaffMemberId,
        string action,
        string targetType,
        Guid targetId,
        string changesJson,
        DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        ActorStaffMemberId = Guard.NotEmpty(actorStaffMemberId, nameof(actorStaffMemberId));
        Action = Guard.NotBlank(action, nameof(action), FieldLengths.AuditAction);
        TargetType = Guard.NotBlank(targetType, nameof(targetType), FieldLengths.AuditTargetType);
        TargetId = Guard.NotEmpty(targetId, nameof(targetId));
        ChangesJson = string.IsNullOrWhiteSpace(changesJson) ? "{}" : changesJson;
        AtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        StampCreatedAt(AtUtc);
    }
}
