using Yalla.Domain.Common;
using Yalla.Domain.Enums;

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
/// <para>
/// <b>The names are copied, not joined.</b> <see cref="ActorDisplay"/> and <see cref="TargetDisplay"/>
/// are what the people and things were called when it happened, which is what a history should say:
/// a venue renamed last week still reads "suspended Garden Bistro" for the suspension before it. They
/// are null for a row written before the console's activity feed existed and not backfilled, and for
/// a row whose actor is not staff (a diner deleting their own account names nobody).
/// </para>
/// </remarks>
public sealed class PlatformAuditLog : Entity
{
    /// <summary>The staff member who acted. Usually a platform admin; a manager for the actions venue staff may also take, such as regenerating a QR code.</summary>
    public Guid ActorStaffMemberId { get; private set; }

    /// <summary>
    /// The actor's role when they acted, from their stored row. Null when the actor is not a staff
    /// member - the diner account deletion writes the diner's own id as the actor.
    /// </summary>
    public StaffRole? ActorRole { get; private set; }

    /// <summary>The actor's name when they acted. Null when the actor is not staff.</summary>
    public string? ActorDisplay { get; private set; }

    /// <summary>What was done, as a stable slug: <c>venue.create</c>, <c>venue.suspend</c>, <c>branch.update</c>, <c>table.regenerate-qr</c>.</summary>
    public string Action { get; private set; } = null!;

    /// <summary>The entity type acted on: <c>Venue</c>, <c>Branch</c>, <c>DiningTable</c>.</summary>
    public string TargetType { get; private set; } = null!;

    public Guid TargetId { get; private set; }

    /// <summary>
    /// What the target was called: a venue's or branch's name, a person's name, a table's label. For a
    /// review, the branch it is about - the reviewer is a diner and is not copied into this log.
    /// </summary>
    public string? TargetDisplay { get; private set; }

    /// <summary>The venue the action concerns, when there is one. No foreign key: the log outlives what it names.</summary>
    public Guid? VenueId { get; private set; }

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
        DateTime atUtc,
        StaffRole? actorRole = null,
        string? actorDisplay = null,
        string? targetDisplay = null,
        Guid? venueId = null)
        : base(Guid.CreateVersion7())
    {
        ActorStaffMemberId = Guard.NotEmpty(actorStaffMemberId, nameof(actorStaffMemberId));
        Action = Guard.NotBlank(action, nameof(action), FieldLengths.AuditAction);
        TargetType = Guard.NotBlank(targetType, nameof(targetType), FieldLengths.AuditTargetType);
        TargetId = Guard.NotEmpty(targetId, nameof(targetId));
        ChangesJson = string.IsNullOrWhiteSpace(changesJson) ? "{}" : changesJson;
        AtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        ActorRole = actorRole;
        ActorDisplay = Clip(actorDisplay, FieldLengths.PersonName);
        TargetDisplay = Clip(targetDisplay, FieldLengths.AuditTargetDisplay);
        VenueId = venueId == Guid.Empty ? null : venueId;
        StampCreatedAt(AtUtc);
    }

    /// <summary>
    /// A name, trimmed and cut to the column. A log entry is never refused for the length of a name:
    /// the change it records has already passed the entity's own guard, and losing the entry over a
    /// label would lose the record of a change that did happen.
    /// </summary>
    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
