using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Tabs;

/// <summary>
/// One thing that happened on a tab, in order, so a phone that missed it can catch up.
/// </summary>
/// <remarks>
/// <para>
/// The live bill is the feature this exists for. Four phones at one table are all showing the same
/// running total, and one of them was in a lift when the wine was ordered. Without an ordered log
/// its only options are to re-fetch the whole tab on every reconnect - which is what makes a bill
/// flicker and disagree with itself - or to miss the change entirely.
/// </para>
/// <para>
/// <b><see cref="Sequence"/> is a database IDENTITY, not a timestamp.</b> Two events in the same
/// millisecond are common on a busy tab, and a client asking for "everything after what I have"
/// needs a total order it can state exactly. It is scoped by query rather than per tab, so the
/// numbers have gaps within one tab; the contract is that they increase, never that they are
/// contiguous.
/// </para>
/// <para>
/// Written in the same <c>SaveChanges</c> as the change it describes, so the stream can never
/// disagree with the tab. This is the seam SignalR plugs into later - adding it now costs one table
/// and adding it afterwards is a migration on the hottest rows in the product.
/// </para>
/// <para>
/// <b>Known reliance.</b> When one <c>SaveChanges</c> writes several events - a payment that settles
/// the bill writes <c>PaymentRecorded</c> and then <c>TabClosed</c> - their relative order comes from
/// EF Core inserting same-type entities in the order they were tracked, which is reliable in
/// practice and not contractual. The events are logically simultaneous, so a client that treats them
/// as a set for one instant is correct either way; a client that reads only the last one is not.
/// If this ever needs to be a guarantee rather than an observation, the fix is an
/// application-assigned sequence per tab with a unique index behind it, not a second SaveChanges.
/// </para>
/// </remarks>
public sealed class TabEvent : Entity
{
    public Guid TabId { get; private set; }

    public Tab Tab { get; private set; } = null!;

    /// <summary>
    /// Database-assigned running number. Increasing, not contiguous - see the type's remarks.
    /// </summary>
    public long Sequence { get; private set; }

    public TabEventType Type { get; private set; }

    /// <summary>
    /// What changed, as JSON, shaped per <see cref="Type"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately opaque to the database. A client applies a payload it recognises and skips one
    /// it does not, which is what lets an old app build stay on a tab that used a new event type
    /// instead of failing on it.
    /// </remarks>
    public string PayloadJson { get; private set; } = null!;

    public ActorType ActorType { get; private set; }

    /// <summary>The participant or staff member who caused it. Null for the system.</summary>
    public Guid? ActorId { get; private set; }

    public DateTime AtUtc { get; private set; }

    private TabEvent()
    {
    }

    public TabEvent(
        Guid tabId,
        TabEventType type,
        string payloadJson,
        ActorType actorType,
        Guid? actorId,
        DateTime atUtc)
        : base(Guid.CreateVersion7())
    {
        TabId = Guard.NotEmpty(tabId, nameof(tabId));
        Type = Guard.Defined(type, nameof(type));
        PayloadJson = Guard.NotBlank(payloadJson, nameof(payloadJson), int.MaxValue);
        ActorType = Guard.Defined(actorType, nameof(actorType));
        ActorId = actorId;
        AtUtc = Guard.NotLocalTime(atUtc, nameof(atUtc));
        StampCreatedAt(atUtc);
    }
}
