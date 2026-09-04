using Yalla.Domain.Common;

namespace Yalla.Domain.Tabs;

/// <summary>
/// One participant's stake in one shared order line.
/// </summary>
/// <remarks>
/// These rows are a <b>snapshot of who was at the table when the item was ordered</b>, not a live
/// list of everyone on the tab. Someone who joins for dessert must not be charged a share of the
/// starters they never saw, and someone who leaves early must not stop owing for what they ate.
/// Splitting off the current participant list instead of this snapshot gets both cases wrong.
/// </remarks>
public sealed class TabOrderLineShare : Entity
{
    public Guid TabOrderLineId { get; private set; }

    public TabOrderLine TabOrderLine { get; private set; } = null!;

    public Guid TabParticipantId { get; private set; }

    public TabParticipant TabParticipant { get; private set; } = null!;

    private TabOrderLineShare()
    {
    }

    internal TabOrderLineShare(Guid tabOrderLineId, Guid tabParticipantId)
        : base(Guid.CreateVersion7())
    {
        TabOrderLineId = Guard.NotEmpty(tabOrderLineId, nameof(tabOrderLineId));
        TabParticipantId = Guard.NotEmpty(tabParticipantId, nameof(tabParticipantId));
    }
}
