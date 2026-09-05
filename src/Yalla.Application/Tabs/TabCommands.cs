using Yalla.Domain.Enums;

namespace Yalla.Application.Tabs;

/// <summary>
/// Open a tab by scanning the table's QR code - or land on the one already open there.
/// </summary>
/// <remarks>
/// No account is involved and none is asked for. <see cref="DeviceId"/> is a stable per-install
/// identifier the app generates; it is what lets the same phone re-scan and land on the same
/// participant rather than appear twice on the bill.
/// </remarks>
/// <param name="QrToken">The token printed in the QR code on the table.</param>
/// <param name="DeviceId">The scanning phone. Not an account.</param>
/// <param name="ClientCommandId">
/// Caller-generated. A double scan or a retry on flaky wifi with the same id returns the same tab
/// instead of opening a second one.
/// </param>
/// <param name="DisplayName">Optional. What the host sees; defaults to a numbered guest.</param>
/// <param name="PartySize">
/// How many sat down, when the scanner is asked. Recorded on the session for turnover reporting.
/// Defaults to one, because a person scanning alone for a coffee should not be asked.
/// </param>
/// <param name="SettlementMode">
/// How the bill will be split, if the host chooses now. Only used when this scan opens the tab;
/// defaults to anyone paying any amount.
/// </param>
/// <param name="HideTotalFromGuests">
/// The table default for guests' sight of the total. Only used when this scan opens the tab.
/// </param>
public sealed record OpenTabCommand(
    string QrToken,
    string DeviceId,
    Guid ClientCommandId,
    string? DisplayName = null,
    int? PartySize = null,
    SettlementMode? SettlementMode = null,
    bool? HideTotalFromGuests = null);

/// <summary>Join an open tab with the invitation the host passed round.</summary>
/// <param name="JoinToken">The token from the host's QR or share link. Thirty-minute lifetime.</param>
/// <param name="DeviceId">The joining phone. Not an account.</param>
/// <param name="DisplayName">Optional. What the host sees.</param>
public sealed record JoinTabCommand(
    string JoinToken,
    string DeviceId,
    string? DisplayName = null);

/// <summary>
/// The three flags, set together because they are not independent: <c>CanPay</c> implies
/// <c>CanSeeTableTotal</c>, and the entity refuses the combination that breaks it.
/// </summary>
public sealed record SetParticipantPermissionsCommand(
    bool CanOrder,
    bool CanSeeTableTotal,
    bool CanPay);
