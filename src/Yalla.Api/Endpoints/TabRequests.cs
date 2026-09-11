using System.ComponentModel.DataAnnotations;
using Yalla.Domain.Enums;

namespace Yalla.Api.Endpoints;

/// <summary>Body of <c>POST /api/tabs/open</c>.</summary>
/// <remarks>
/// No account is involved and none is asked for. This is the flow the product lives on: someone
/// scans the code on table 7 and orders a coffee, and if that asked them to register they would
/// put the phone down.
/// </remarks>
/// <param name="QrToken">The token printed in the QR code on the table.</param>
/// <param name="DeviceId">
/// A stable identifier the app generates once per install. Not an account and not a login - it is
/// what lets someone who re-scans after their phone locked land back on the same participant
/// instead of appearing twice on the bill.
/// </param>
/// <param name="ClientCommandId">
/// The caller's own id for this scan. <b>Required.</b> A double scan, or a retry on flaky wifi,
/// with the same id returns the same tab instead of opening a second one.
/// </param>
/// <param name="DisplayName">Optional. What the host sees; defaults to a numbered guest.</param>
/// <param name="PartySize">Optional. How many sat down, when the app asks. Defaults to one.</param>
/// <param name="SettlementMode">
/// Optional. How the bill will be split, if the host chooses now: 1 HostPaysEverything,
/// 2 EveryonePaysOwnItems, 3 AnyonePaysAnyAmount (the default). Only used when this scan opens the tab.
/// </param>
/// <param name="HideTotalFromGuests">
/// Optional. The table default for whether guests see the total. Only used when this scan opens the tab.
/// </param>
public sealed record OpenTabRequest(
    [Required] string QrToken,
    [Required] string DeviceId,
    [Required] Guid ClientCommandId,
    string? DisplayName = null,
    [Range(1, 100)] int? PartySize = null,
    SettlementMode? SettlementMode = null,
    bool? HideTotalFromGuests = null) : IClientCommandRequest;

/// <summary>Body of <c>POST /api/tabs/open-by-booking</c>.</summary>
/// <remarks>
/// The scan's body with the booking code in place of the QR token. The code finds the caller's own
/// booking, the booking names the table, and from there it is the scan: the same host, the same
/// approval, the same replay, the same answer.
/// </remarks>
/// <param name="BookingCode">
/// The code on the diner's booking - "DFJFQY" - typed however they typed it. Case, spaces and dashes
/// do not matter.
/// </param>
/// <param name="DeviceId">See <see cref="OpenTabRequest.DeviceId"/>.</param>
/// <param name="ClientCommandId">
/// The caller's own id for this attempt. <b>Required.</b> A retry with the same id returns the same
/// tab instead of opening a second one.
/// </param>
/// <param name="DisplayName">Optional. What the host sees; defaults to a numbered guest.</param>
/// <param name="PartySize">
/// Optional. How many sat down. Only used when this seats the table; defaults to the booked party size.
/// </param>
/// <param name="SettlementMode">
/// Optional. As on <c>/api/tabs/open</c>. Only used when this opens the tab.
/// </param>
/// <param name="HideTotalFromGuests">
/// Optional. As on <c>/api/tabs/open</c>. Only used when this opens the tab.
/// </param>
public sealed record OpenTabByBookingRequest(
    [Required] string BookingCode,
    [Required] string DeviceId,
    [Required] Guid ClientCommandId,
    string? DisplayName = null,
    [Range(1, 100)] int? PartySize = null,
    SettlementMode? SettlementMode = null,
    bool? HideTotalFromGuests = null) : IClientCommandRequest;

/// <summary>Body of <c>POST /api/tabs/join</c>.</summary>
/// <param name="JoinToken">The invitation from the host's QR or share link. Thirty-minute lifetime.</param>
/// <param name="DeviceId">See <see cref="OpenTabRequest.DeviceId"/>.</param>
/// <param name="DisplayName">Optional display name.</param>
public sealed record JoinTabRequest(
    [Required] string JoinToken,
    [Required] string DeviceId,
    string? DisplayName = null);

/// <summary>Body of <c>POST /api/tabs/{tabId}/display-name</c>.</summary>
/// <param name="DisplayName">What the host should see instead of "Guest 3".</param>
public sealed record SetDisplayNameRequest(
    [Required][StringLength(100, MinimumLength = 1)] string DisplayName);

/// <summary>Body of <c>POST /api/tabs/{tabId}/participants/{participantId}/permissions</c>.</summary>
/// <remarks>
/// All three at once, because they are not independent. <c>canPay: true</c> with
/// <c>canSeeTableTotal: false</c> is refused with 400 rather than corrected: nobody puts money toward
/// a total they are not allowed to see, and a host who tapped one thing and got another has been
/// surprised.
/// </remarks>
/// <param name="CanOrder">Whether they may add items.</param>
/// <param name="CanSeeTableTotal">Whether they may see the table total and other people's items.</param>
/// <param name="CanPay">Whether they may settle against the tab. Requires <paramref name="CanSeeTableTotal"/>.</param>
public sealed record SetParticipantPermissionsRequest(
    bool CanOrder,
    bool CanSeeTableTotal,
    bool CanPay);

/// <summary>Body of <c>POST /api/tabs/{tabId}/settlement-mode</c>.</summary>
/// <param name="SettlementMode">1 HostPaysEverything, 2 EveryonePaysOwnItems, 3 AnyonePaysAnyAmount.</param>
public sealed record SetSettlementModeRequest(
    [Required] SettlementMode SettlementMode);

/// <summary>Body of <c>POST /api/tabs/{tabId}/reassign-host</c>.</summary>
/// <param name="NewHostParticipantId">The approved participant who takes over.</param>
public sealed record ReassignHostRequest(
    [Required] Guid NewHostParticipantId);
