using Yalla.Domain.Enums;

namespace Yalla.Application.Tabs;

/// <summary>
/// The permission rules for a participant on a tab, each written once.
/// </summary>
/// <remarks>
/// <para>
/// Two things read these: <see cref="TabProjection"/>, which decides what a participant is shown,
/// and the <c>TabParticipant</c> authorisation handler, which decides what a participant may do.
/// If the projection said "you may order" while the policy said no, the app would draw a button
/// that returns 403; if the policy said yes while the projection hid the total, a client would
/// have to guess. One function per rule, called from both, is what keeps the two in step.
/// </para>
/// <para>
/// The stored flags on <c>TabParticipant</c> are what the host chose. These functions apply them
/// to the moment - is the person approved yet, is the tab still open - which is a different
/// question and one the flags alone cannot answer.
/// </para>
/// </remarks>
public static class TabPermissions
{
    /// <summary>
    /// Whether a participant may still read the tab at all. Anyone not removed: a pending joiner
    /// may see their own state, and a participant on a closed tab may look at the receipt.
    /// </summary>
    public static bool MayReadTab(ParticipantStatus status) => status != ParticipantStatus.Removed;

    /// <summary>
    /// Whether a participant may add items right now: approved by the host, allowed to order,
    /// and the tab still open. After staff mark the tab closing, nobody orders - someone who
    /// already paid their share must not get dessert added after they have left.
    /// </summary>
    public static bool MayOrder(ParticipantStatus status, bool canOrder, TabStatus tabStatus) =>
        status == ParticipantStatus.Approved
        && canOrder
        && tabStatus == TabStatus.Open;

    /// <summary>
    /// Whether a participant may see the table aggregate and other people's items. Requires
    /// approval as well as the flag: a pending joiner with the default flag set has not been let
    /// on yet, and the total is the one thing the next table must not read.
    /// </summary>
    public static bool MaySeeTableTotal(ParticipantStatus status, bool canSeeTableTotal) =>
        status == ParticipantStatus.Approved && canSeeTableTotal;

    /// <summary>
    /// Whether a participant may see who else is at the table. Approved participants only; a
    /// pending joiner sees their own row and nothing else.
    /// </summary>
    public static bool MaySeeRoster(ParticipantStatus status) => status == ParticipantStatus.Approved;

    /// <summary>
    /// Whether a participant may settle against the tab right now. The stored flag, applied to
    /// an approved participant on a live tab. <c>CanPay</c> implying <c>CanSeeTableTotal</c> is
    /// enforced on the entity, so it is not re-checked here.
    /// </summary>
    public static bool MayPay(ParticipantStatus status, bool canPay, TabStatus tabStatus) =>
        status == ParticipantStatus.Approved
        && canPay
        && tabStatus is TabStatus.Open or TabStatus.Closing;

    /// <summary>Whether anyone new may be put on the tab. Only while it is open.</summary>
    public static bool AcceptsNewParticipants(TabStatus tabStatus) => tabStatus == TabStatus.Open;
}
