using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Media;
using Yalla.Application.Messaging;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Who is on a tab and what they may do: the host's invitation, letting people on, turning them away and
/// removing them, permissions and names, leaving - and the host's choice of how the bill is split.
/// </summary>
internal sealed class TabRoster(
    YallaDbContext db,
    IClock clock,
    ITabQuery query,
    ITokenAuthorityCheck authority,
    IOptions<TabOptions> options,
    TabLedger ledger,
    IOutbox outbox,
    ILogger<TabService> logger,
    TabLookups lookups)
{
    // ---------------------------------------------------------------- invitations

    public async Task<TabJoinTokenResult> CreateJoinTokenAsync(
        Guid tabId,
        Guid actingParticipantId,
        CancellationToken cancellationToken = default)
    {
        var tab = await lookups.LoadTabAsync(tabId, cancellationToken, includeJoinTokens: true);
        var host = RequireHost(tab, actingParticipantId, "Inviting others");

        if (!tab.AcceptsNewParticipants)
        {
            throw new DomainStateException("This tab is being settled; nobody new can join it.");
        }

        var nowUtc = clock.UtcNow;

        // One live invitation at a time. Asking for another is how the host refreshes an expired
        // one, and it also kills any earlier token that is still circulating - a refresh is a
        // statement that the old one should stop working.
        foreach (var live in tab.JoinTokens.Where(t => t.IsUsableAt(nowUtc)))
        {
            live.Revoke(nowUtc);
        }

        var token = new TabJoinToken(tab.Id, host.Id, nowUtc);
        db.TabJoinTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        return new TabJoinTokenResult(
            token.Token,
            options.Value.JoinUrlTemplate.Replace("{token}", Uri.EscapeDataString(token.Token), StringComparison.Ordinal),
            token.ExpiresAtUtc,
            tab.Id);
    }

    // ---------------------------------------------------------------- participants

    public Task<TabParticipantView> ApproveParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Approving a participant",
            TabEventType.ParticipantApproved,
            (participant, nowUtc) => participant.Approve(nowUtc), cancellationToken,
            alreadyAnswered: participant => participant.IsApproved);

    public Task<TabParticipantView> RejectParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Rejecting a participant",
            TabEventType.ParticipantRejected,
            (participant, nowUtc) => participant.Reject(nowUtc), cancellationToken,
            alreadyAnswered: participant => participant.WasTurnedAway);

    /// <summary>
    /// A status change, never a delete. The participant's order lines and payments reference the
    /// row and must stay answerable after they have gone.
    /// </summary>
    public Task<TabParticipantView> RemoveParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Removing a participant",
            TabEventType.ParticipantRemoved,
            (participant, nowUtc) => participant.Remove(nowUtc), cancellationToken);

    /// <summary>
    /// A participant takes themself off the tab - the same status change a host's remove makes, so
    /// their lines and payments stay answerable and they stop being put on shared lines.
    /// </summary>
    /// <remarks>
    /// The app used to fake this: it cleared the tab on the phone while the server kept the guest
    /// approved, on every shared bottle ordered after they walked out and in the split. A host hands
    /// over to the longest-standing approved guest, which is what the app already tells them; a host
    /// with nobody approved to hand to is refused, rather than leaving a tab nobody can let anyone
    /// onto.
    /// </remarks>
    public async Task<TabParticipantView> LeaveAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        var tab = await lookups.LoadTabAsync(tabId, cancellationToken);

        var leaving = tab.Participants.FirstOrDefault(p => p.Id == participantId && !p.IsRemoved)
                      ?? throw new TabPermissionException("Leaving this tab", "a participant on this tab");

        TabParticipant? successor = null;

        if (leaving.IsHost && tab.HostParticipantId == leaving.Id)
        {
            // Approved only: somebody still waiting to be let on cannot let anybody else on. Join
            // order, then approval order for two phones that scanned in the same instant.
            successor = tab.Participants
                .Where(p => p.Id != leaving.Id && p.Status == ParticipantStatus.Approved)
                .OrderBy(p => p.JoinedAtUtc)
                .ThenBy(p => p.ApprovedAtUtc)
                .FirstOrDefault()
                ?? throw new DomainStateException(
                    "You are the only one on this tab, so there is nobody to hand it to. Ask a waiter "
                    + "to take it over or close it.");

            // The order ReassignHostAsync keeps: the tab refuses a closed bill before either row is
            // touched, and the new host must be approved before they can take the role.
            tab.ReassignHost(successor.Id);
            successor.BecomeHost();
            leaving.BecomeGuest();

            ledger.Append(tab.Id, TabEventType.HostReassigned, new
            {
                fromParticipantId = leaving.Id,
                toParticipantId = successor.Id,
            });
        }

        leaving.Remove(clock.UtcNow);

        // The roster event every phone at the table already handles, marked as their own doing.
        ledger.Append(tab.Id, TabEventType.ParticipantRemoved, new
        {
            participantId = leaving.Id,
            displayName = leaving.DisplayName,
            status = (int)leaving.Status,
            canOrder = leaving.CanOrder,
            canSeeTableTotal = leaving.CanSeeTableTotal,
            canPay = leaving.CanPay,
            left = true,
        });

        await ledger.SaveAppendedAsync(cancellationToken);

        // Their token has to stop working now rather than when the cached standing lapses, and the
        // new host's standing changed with the role.
        authority.InvalidateParticipant(tab.Id, leaving.Id);

        if (successor is not null)
        {
            authority.InvalidateParticipant(tab.Id, successor.Id);
        }

        logger.LogInformation(
            "Participant {ParticipantId} left tab {TabId}; the host is now {HostParticipantId}.",
            leaving.Id, tab.Id, tab.HostParticipantId);

        return await ToViewAsync(leaving, tab.Status, tab.Admission, cancellationToken);
    }

    /// <summary>
    /// The entity refuses <c>CanPay</c> without <c>CanSeeTableTotal</c>; nothing here corrects the
    /// request silently, so the refusal reaches the host as a 400 that says why.
    /// </summary>
    public Task<TabParticipantView> SetPermissionsAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        SetParticipantPermissionsCommand permissions,
        CancellationToken cancellationToken = default) =>
        ChangeParticipantAsync(
            tabId, actingParticipantId, participantId, "Changing permissions",
            TabEventType.ParticipantPermissionsChanged,
            (participant, _) => participant.SetPermissions(
                permissions.CanOrder, permissions.CanSeeTableTotal, permissions.CanPay),
            cancellationToken);

    public async Task<TabParticipantView> SetDisplayNameAsync(
        Guid tabId,
        Guid participantId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var tab = await lookups.LoadTabAsync(tabId, cancellationToken);

        var participant = tab.Participants.FirstOrDefault(p => p.Id == participantId && !p.IsRemoved)
                          ?? throw new KeyNotFoundException("No such participant on that tab.");

        participant.SetDisplayName(displayName);

        ledger.Append(tab.Id, TabEventType.ParticipantRenamed, new
        {
            participantId = participant.Id,
            displayName = participant.DisplayName,
        });

        await ledger.SaveAppendedAsync(cancellationToken);

        return await ToViewAsync(participant, tab.Status, tab.Admission, cancellationToken);
    }

    private async Task<TabParticipantView> ChangeParticipantAsync(
        Guid tabId,
        Guid actingParticipantId,
        Guid participantId,
        string operation,
        TabEventType eventType,
        Action<TabParticipant, DateTime> change,
        CancellationToken cancellationToken,
        Func<TabParticipant, bool>? alreadyAnswered = null)
    {
        var tab = await lookups.LoadTabAsync(tabId, cancellationToken);
        RequireHost(tab, actingParticipantId, operation);

        var target = tab.Participants.FirstOrDefault(p => p.Id == participantId)
                     ?? throw new KeyNotFoundException("No such participant on that tab.");

        // Answered already: the banner and the People screen offer the same tap, and a retry on
        // flaky wifi sends it twice. The answer is the one the first tap got. A second event would
        // tell every phone it happened twice, and a second approval push collides with the first on
        // its idempotency key.
        if (alreadyAnswered?.Invoke(target) == true)
        {
            return await ToViewAsync(target, tab.Status, tab.Admission, cancellationToken);
        }

        change(target, clock.UtcNow);

        // The one message worth pushing on this path: the guest is sitting at the table with the
        // phone in their pocket, on a screen that says "waiting for the host". Only reaches somebody
        // whose phone was carrying a diner token when it scanned - see TabParticipant.UserId.
        if (eventType == TabEventType.ParticipantApproved && target.UserId is { } dinerUserId)
        {
            outbox.Enqueue(
                OutboxMessageTypes.ParticipantApproved,
                new
                {
                    tabId = tab.Id,
                    participantId = target.Id,
                    dinerUserId,
                    tableLabel = tab.DiningTable?.Label ?? string.Empty,
                },
                clock.UtcNow,
                OutboxMessageTypes.KeyFor("participant", target.Id, "approved"));
        }

        // The other phones at the table are showing this roster. Written in the same SaveChanges as
        // the change, so the stream can never disagree with the tab.
        ledger.Append(tab.Id, eventType, new
        {
            participantId = target.Id,
            displayName = target.DisplayName,
            status = (int)target.Status,
            canOrder = target.CanOrder,
            canSeeTableTotal = target.CanSeeTableTotal,
            canPay = target.CanPay,
        });

        try
        {
            await ledger.SaveAppendedAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.OutboxIdempotency))
        {
            // Two approvals at the same instant both read the joiner as pending, and the other one
            // committed first: its push took the key this one was about to write. The joiner is
            // approved either way, so this answers with the approval that won, as a second tap
            // after it would have been answered.
            logger.LogInformation(
                "{Operation} on tab {TabId} lost the race for participant {ParticipantId}; answering with the one that won.",
                operation, tabId, participantId);

            db.ChangeTracker.Clear();
            ledger.DiscardPending();
            authority.InvalidateParticipant(tabId, participantId);

            var current = await lookups.LoadTabAsync(tabId, cancellationToken);

            return await ToViewAsync(
                current.Participants.First(p => p.Id == participantId), current.Status, current.Admission, cancellationToken);
        }

        // Their standing just changed, so the cached answer about it is wrong. A participant the
        // host has just removed must be refused on their next call, not on the one after the cache
        // lapses - the approval flow is the whole reason the token is not enough on its own.
        authority.InvalidateParticipant(tab.Id, target.Id);

        logger.LogInformation(
            "{Operation} on tab {TabId}: participant {ParticipantId} is now {Status}.",
            operation, tab.Id, target.Id, target.Status);

        return await ToViewAsync(target, tab.Status, tab.Admission, cancellationToken);
    }

    // ---------------------------------------------------------------- settlement mode

    public async Task<TabView> SetSettlementModeAsync(
        Guid tabId,
        Guid actingParticipantId,
        SettlementMode settlementMode,
        CancellationToken cancellationToken = default)
    {
        var tab = await lookups.LoadTabAsync(tabId, cancellationToken);
        var host = RequireHost(tab, actingParticipantId, "Changing the settlement mode");

        // The lock is stamped the first time a change is attempted after money has landed. A
        // reserved payment counts: the hold was computed under the current split, and changing
        // the split underneath it is exactly what the lock exists to stop.
        if (tab.SettlementModeLockedAtUtc is null
            && await db.Payments.AnyAsync(
                p => p.TabId == tab.Id
                     && (p.Status == PaymentStatus.Reserved || p.Status == PaymentStatus.Succeeded),
                cancellationToken))
        {
            tab.LockSettlementMode(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Throws DomainStateException when locked.
        tab.SetSettlementMode(settlementMode);

        ledger.Append(tab.Id, TabEventType.SettlementModeChanged, new
        {
            settlementMode = (int)settlementMode,
            lockedAtUtc = tab.SettlementModeLockedAtUtc,
        });

        await ledger.SaveAppendedAsync(cancellationToken);

        return await query.GetForParticipantAsync(tab.Id, host.Id, cancellationToken)
               ?? throw new InvalidOperationException($"Tab {tab.Id} vanished while its settlement mode was being set.");
    }

    // ---------------------------------------------------------------- shaping

    /// <summary>
    /// One participant as the command that just changed them answers, picture included.
    /// </summary>
    /// <remarks>
    /// The same face the tab read shows - the profile photo of a live diner account the phone
    /// scanned with - so a client that updates its roster from this answer does not blank an avatar
    /// the next read would draw. One indexed read, on a path a host taps a few times an evening.
    /// </remarks>
    private async Task<TabParticipantView> ToViewAsync(
        TabParticipant participant,
        TabStatus tabStatus,
        TabAdmission admission,
        CancellationToken cancellationToken)
    {
        var photo = participant.UserId is { } userId
            ? await db.DinerUsers
                .AsNoTracking()
                .Where(d => d.Id == userId && d.DeletedAtUtc == null && d.PhotoId != null)
                .Select(d => d.Photo!)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return TabProjection.ToParticipantView(
            new TabParticipantSnapshot(
                participant.Id,
                participant.DisplayName,
                participant.Role,
                participant.Status,
                participant.CanOrder,
                participant.CanSeeTableTotal,
                participant.CanPay,
                participant.JoinedAtUtc,
                photo is null ? null : PhotoView.From(photo)),
            tabStatus,
            admission);
    }

    // ---------------------------------------------------------------- permissions

    /// <summary>
    /// The acting participant must be on the tab and be its host. Decided here, not in an
    /// endpoint, so it holds for every caller.
    /// </summary>
    private static TabParticipant RequireHost(Tab tab, Guid actingParticipantId, string operation)
    {
        var acting = tab.Participants.FirstOrDefault(p => p.Id == actingParticipantId && !p.IsRemoved)
                     ?? throw new TabPermissionException(operation, "a participant on this tab");

        if (!acting.IsHost || tab.HostParticipantId != acting.Id)
        {
            throw new TabPermissionException(operation, "the host of this tab");
        }

        return acting;
    }
}
