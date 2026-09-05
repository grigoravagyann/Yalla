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
/// <b><see cref="Sequence"/> is a per-tab counter, not a timestamp and not a database identity.</b>
/// Two events in the same millisecond are ordinary on a busy tab, so a client asking for
/// "everything after what I have" needs a total order the clock cannot give it.
/// </para>
/// <para>
/// It was an <c>IDENTITY</c> column first, and that was wrong in a way that took an intermittent
/// test failure to surface: when one <c>SaveChanges</c> writes several events - a payment that
/// settles the bill writes <c>PaymentRecorded</c> and then <c>TabClosed</c> - the order the database
/// assigns identities in is the order EF happens to insert them, which is not guaranteed and does
/// vary. The stream then read <c>124:TabClosed, 125:PaymentRecorded</c>, telling a catching-up phone
/// that the tab closed before the payment that closed it. Assigned by
/// <c>TabLedger</c> against a unique index on <c>(TabId, Sequence)</c>, the order is the order the
/// events were appended in, which is the only order that means anything.
/// </para>
/// <para>
/// Written in the same <c>SaveChanges</c> as the change it describes, so the stream can never
/// disagree with the tab. This is the seam SignalR plugs into later - adding it now costs one table
/// and adding it afterwards is a migration on the hottest rows in the product.
/// </para>
/// <para>
/// Sequences are contiguous from 1 within a tab. A client keeps the highest it has seen and asks for
/// everything after it; it should still ignore a <see cref="Type"/> it does not recognise rather than
/// stopping, because new types will be added and an old app build must not break on a tab that used
/// one.
/// </para>
/// </remarks>
public sealed class TabEvent : Entity
{
    public Guid TabId { get; private set; }

    public Tab Tab { get; private set; } = null!;

    /// <summary>
    /// This event's position on its tab, from 1. Assigned by the ledger - see the type's remarks.
    /// </summary>
    public long Sequence { get; private set; }

    /// <summary>Puts this event in its place on the tab, immediately before it is saved.</summary>
    /// <remarks>
    /// Set here rather than in the constructor because the number depends on what is already stored,
    /// which is not known when the caller appends. A unique index on <c>(TabId, Sequence)</c> is what
    /// makes two concurrent writers safe: the loser sees a violation and is renumbered.
    /// </remarks>
    public void PlaceAt(long sequence)
    {
        if (sequence <= 0L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence), sequence, "A tab event's sequence starts at 1.");
        }

        Sequence = sequence;
    }

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
