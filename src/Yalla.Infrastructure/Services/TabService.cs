using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Tabs: opening one by scanning, inviting others, and the permission model between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opening is convergent.</b> Whatever two phones at the same table do at the same moment, the
/// answer is one tab, one host and one pending guest. The service does not achieve that with a
/// lock. It relies on two unique indexes the database already enforces - one open session per
/// table, one tab per session - and on the table's <c>RowVersion</c>, and treats losing any of
/// those races as new information: drop the failed attempt, look at the table again, and take
/// whichever of the four table-state cases now applies. See <c>docs/tabs.md</c>.
/// </para>
/// <para>
/// <b>Seating goes through the state machine.</b> A free table is occupied by
/// <see cref="ITableStateService.SeatQrScanAsync"/>, which opens the session and writes the audit
/// row in one commit like every other transition. The tab and its host are a second commit
/// against that session. A crash between the two leaves an occupied session with no tab, which is
/// precisely the "seated party, no tab yet" case the next scan already handles - so the gap
/// self-heals rather than needing a sweeper.
/// </para>
/// <para>
/// <b>Who may do what is decided here.</b> The host check and the staff check are in the service,
/// not in an endpoint, so they hold for every caller.
/// </para>
/// <para>
/// <b>Where each command lives.</b> This is the one <see cref="ITabService"/> the endpoints see. Each
/// group of commands is a class of its own over the same unit of work - <see cref="TabOpener"/>,
/// <see cref="TabJoining"/>, <see cref="TabRoster"/>, <see cref="TabStaffActions"/> and
/// <see cref="TabAdmissionCommands"/> - beside the part they share, <see cref="TabLookups"/>. They are
/// composed here rather than registered one by one, so this constructor stays the service's whole list
/// of dependencies.
/// </para>
/// <para>
/// <b>What a command tells the rest of the system is not here.</b> The tab raises what happened - opened,
/// let in, turned away, somebody asking to join, let on, taken off, the host moved, the bill presented -
/// and the handlers in <c>DomainEvents/Tabs</c> write the activity entry, the tab's stream, the branch's
/// change stream and the pushes in the same save (<c>docs/domain-events.md</c>).
/// </para>
/// </remarks>
internal sealed class TabService : ITabService
{
    private readonly TabOpener opener;
    private readonly TabJoining joining;
    private readonly TabRoster roster;
    private readonly TabStaffActions staff;
    private readonly TabAdmissionCommands admission;

    public TabService(
        YallaDbContext db,
        IClock clock,
        ICurrentActor actor,
        ITableStateService tableState,
        ITabQuery query,
        ITokenAuthorityCheck authority,
        TabParticipantTokens tokens,
        IOptions<TabOptions> options,
        TabLedger ledger,
        StaffBranchGuard branchGuard,
        ILogger<TabService> logger)
    {
        var lookups = new TabLookups(db, actor, query, tokens);

        joining = new TabJoining(db, clock, actor, authority, ledger, logger, lookups);
        opener = new TabOpener(db, clock, actor, tableState, ledger, logger, lookups, joining);
        roster = new TabRoster(db, clock, query, authority, options, ledger, logger, lookups);
        staff = new TabStaffActions(db, clock, actor, authority, ledger, logger, lookups);
        admission = new TabAdmissionCommands(db, clock, tableState, authority, ledger, branchGuard, logger, lookups);
    }

    // ---------------------------------------------------------------- opening and joining

    public Task<TabAccessResult> OpenAsync(
        OpenTabCommand command,
        CancellationToken cancellationToken = default) =>
        opener.OpenAsync(command, cancellationToken);

    public Task<TabAccessResult> OpenByBookingAsync(
        OpenTabByBookingCommand command,
        CancellationToken cancellationToken = default) =>
        opener.OpenByBookingAsync(command, cancellationToken);

    public Task<TabAccessResult> JoinAsync(
        JoinTabCommand command,
        CancellationToken cancellationToken = default) =>
        joining.JoinAsync(command, cancellationToken);

    // ---------------------------------------------------------------- invitations and participants

    public Task<TabJoinTokenResult> CreateJoinTokenAsync(
        Guid tabId,
        Guid actingParticipantId,
        CancellationToken cancellationToken = default) =>
        roster.CreateJoinTokenAsync(tabId, actingParticipantId, cancellationToken);

    public Task<TabParticipantView> ApproveParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        roster.ApproveParticipantAsync(tabId, actingParticipantId, participantId, cancellationToken);

    public Task<TabParticipantView> RejectParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        roster.RejectParticipantAsync(tabId, actingParticipantId, participantId, cancellationToken);

    public Task<TabParticipantView> RemoveParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        roster.RemoveParticipantAsync(tabId, actingParticipantId, participantId, cancellationToken);

    public Task<TabParticipantView> LeaveAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        roster.LeaveAsync(tabId, participantId, cancellationToken);

    public Task<TabParticipantView> SetPermissionsAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        SetParticipantPermissionsCommand permissions,
        CancellationToken cancellationToken = default) =>
        roster.SetPermissionsAsync(tabId, actingParticipantId, participantId, permissions, cancellationToken);

    public Task<TabParticipantView> SetDisplayNameAsync(
        Guid tabId,
        Guid participantId,
        string displayName,
        CancellationToken cancellationToken = default) =>
        roster.SetDisplayNameAsync(tabId, participantId, displayName, cancellationToken);

    public Task<TabView> SetSettlementModeAsync(
        Guid tabId,
        Guid actingParticipantId,
        SettlementMode settlementMode,
        CancellationToken cancellationToken = default) =>
        roster.SetSettlementModeAsync(tabId, actingParticipantId, settlementMode, cancellationToken);

    // ---------------------------------------------------------------- staff

    public Task<TabStaffView> OpenForStaffAsync(
        OpenTabForStaffCommand command,
        CancellationToken cancellationToken = default) =>
        staff.OpenForStaffAsync(command, cancellationToken);

    public Task<TabStaffView> ReassignHostAsync(
        Guid tabId,
        Guid newHostParticipantId,
        CancellationToken cancellationToken = default) =>
        staff.ReassignHostAsync(tabId, newHostParticipantId, cancellationToken);

    public Task<TabStaffView> BeginClosingAsync(Guid tabId, CancellationToken cancellationToken = default) =>
        staff.BeginClosingAsync(tabId, cancellationToken);

    // ---------------------------------------------------------------- admission

    public Task<TabStaffView> AdmitAsync(AdmitTabCommand command, CancellationToken cancellationToken = default) =>
        admission.AdmitAsync(command, cancellationToken);

    public Task<TabStaffView> RejectAdmissionAsync(RejectTabCommand command, CancellationToken cancellationToken = default) =>
        admission.RejectAdmissionAsync(command, cancellationToken);

    public Task<IReadOnlyList<TabAwaitingAdmissionView>> ListAwaitingAdmissionAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        admission.ListAwaitingAdmissionAsync(branchId, cancellationToken);
}
