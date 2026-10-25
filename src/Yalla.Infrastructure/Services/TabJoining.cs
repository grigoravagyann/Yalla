using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Messaging;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Getting onto a tab that is already open: by its invitation, or by landing on it from a scan or a
/// booking - and telling the host somebody is waiting.
/// </summary>
internal sealed class TabJoining(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    ITokenAuthorityCheck authority,
    TabLedger ledger,
    IOutbox outbox,
    ILogger<TabService> logger,
    TabLookups lookups,
    TabSignals signals)
{
    // ---------------------------------------------------------------- joining

    public async Task<TabAccessResult> JoinAsync(
        JoinTabCommand command,
        CancellationToken cancellationToken = default)
    {
        var device = TabLookups.RequireDevice(command.DeviceId);
        var nowUtc = clock.UtcNow;
        var value = (command.JoinToken ?? string.Empty).Trim();

        var invitation = await db.TabJoinTokens
            .FirstOrDefaultAsync(t => t.Token == value, cancellationToken);

        if (invitation is null || !invitation.IsUsableAt(nowUtc))
        {
            // Unknown, revoked and expired all answer the same way. A screenshot from last Tuesday
            // must not learn anything about the tab it once pointed at.
            throw new AuthenticationFailedException(
                "join-token-invalid", "That invitation is no longer valid. Ask the host for a new one.");
        }

        var tab = await lookups.LoadTabAsync(invitation.TabId, cancellationToken);

        // Nobody new joins at a venue that is no longer open for business. The people already on
        // the tab keep reading and settling it - LoadTabAsync deliberately does not gate on this.
        VenueGate.RequireOpenForBusiness(tab.Branch);

        var participant = await JoinExistingAsync(tab, device, command.DisplayName, cancellationToken);

        return await lookups.ResultAsync(tab, participant, TabOpenOutcome.JoinedExistingTab, wasReplay: false, cancellationToken);
    }

    /// <summary>
    /// Puts a device on an existing tab, or recognises it if it is already there.
    /// </summary>
    /// <remarks>
    /// Re-scanning after the phone locked, or reopening the app, must not put a second person on
    /// the bill: the device is what recognises someone with no account. Somebody new lands pending
    /// - the host taps approve - and only while the tab is still open, so a person who has paid
    /// and left cannot find a stranger's dessert added afterwards.
    /// </remarks>
    internal async Task<TabParticipant> JoinExistingAsync(
        Tab tab,
        string device,
        string? displayName,
        CancellationToken cancellationToken,
        bool asBooker = false)
    {
        var nowUtc = clock.UtcNow;

        // The booker is recognised by their account as well as the phone: a second phone of theirs,
        // or the app reinstalled, is still the person whose booking this sitting is.
        var bookerId = asBooker ? actor.DinerUserId : null;

        var existing = tab.Participants.FirstOrDefault(p => p.DeviceId == device && !p.IsRemoved)
                       ?? (bookerId is { } id
                           ? tab.Participants.FirstOrDefault(p => p.UserId == id && !p.IsRemoved)
                           : null);

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                existing.SetDisplayName(displayName);
            }

            // The booker is here: their booking's sitting is theirs, so a tab a friend's unverified scan
            // opened on it is admitted - the booking is the proof a waiter would otherwise give.
            if (asBooker)
            {
                signals.AdmitInContext(tab, TabAdmissionReason.Booker, staffId: null, nowUtc);
            }

            if (asBooker && !existing.IsApproved)
            {
                await ApproveBookerAsync(tab, existing, nowUtc, cancellationToken);
            }
            else
            {
                await ledger.SaveAppendedAsync(cancellationToken);
            }

            return existing;
        }

        if (!tab.AcceptsNewParticipants)
        {
            throw new DomainStateException(tab.Status == TabStatus.Closing
                ? "This tab is being settled and no longer takes new people. Ask a member of staff."
                : "That tab is closed. Scan the table's QR code to open a new one.");
        }

        // A tab staff opened has nobody on it. Somebody landing on it pending would wait for a
        // host who does not exist, so the first person to arrive hosts it - exactly as if they
        // had scanned the table first. The tab row changes, so two phones racing for the role
        // meet on its row version and the loser comes back round as an ordinary guest.
        if (tab.HostParticipantId is null)
        {
            var host = TabParticipant.Host(
                tab.Id,
                TabLookups.NameOrDefault(displayName, tab.Participants.Count + 1),
                device,
                nowUtc,
                actor.DinerUserId);

            tab.SetHostParticipant(host.Id);
            db.TabParticipants.Add(host);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Participant {ParticipantId} became host of tab {TabId}, which staff opened with nobody on it.",
                host.Id, tab.Id);

            return host;
        }

        var guest = TabParticipant.Guest(
            tab.Id,
            TabLookups.NameOrDefault(displayName, tab.Participants.Count + 1),
            device,
            nowUtc,
            tab.HideTotalFromGuests,
            actor.DinerUserId);

        db.TabParticipants.Add(guest);

        // It is their booking: the party at the table is theirs, so nobody has to let them on. The
        // host - the friend who scanned first - stays host, and the tab may order from now on.
        if (asBooker)
        {
            signals.AdmitInContext(tab, TabAdmissionReason.Booker, staffId: null, nowUtc);

            await ApproveBookerAsync(tab, guest, nowUtc, cancellationToken);

            logger.LogInformation(
                "Booker {ParticipantId} joined tab {TabId} on their own booking's sitting, approved.", guest.Id, tab.Id);

            return guest;
        }

        AnnouncePendingJoin(tab, guest, displayName);

        await ledger.SaveAppendedAsync(cancellationToken);

        logger.LogInformation(
            "Participant {ParticipantId} joined tab {TabId} pending the host's approval.", guest.Id, tab.Id);

        return guest;
    }

    /// <summary>
    /// Tells the host somebody is waiting to be let on: on the tab's stream, which every phone at the
    /// table polls, and on the host's lock screen when the host scanned with an account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only for a join that lands pending. The first phone onto a tab staff opened hosts it, and the
    /// booker on their own booking's sitting is approved on the spot and announced by
    /// <c>ParticipantApproved</c>. Neither is waiting for anybody, and a second event for the same
    /// moment would only make every phone refetch twice.
    /// </para>
    /// <para>
    /// The push has one address, the host's account - see <see cref="TabParticipant.UserId"/>. Most
    /// hosts scanned without one, and then the stream is all there is. The host's own account on a
    /// second phone lands pending like anybody else, and is not told that it wants to join itself.
    /// </para>
    /// <para>
    /// Somebody the host already turned away on this tab - the same phone, or the same account on
    /// another - is on the stream again but not pushed again. The host sees them on the bill and can
    /// still let them on; they are not woken for every retry of a question they answered.
    /// </para>
    /// </remarks>
    private void AnnouncePendingJoin(Tab tab, TabParticipant joiner, string? givenName)
    {
        // The shape of every other roster event, so the redactor's participant rule already decides
        // it: a pending reader sees who it is about and nothing that says who they are.
        ledger.Append(tab.Id, TabEventType.ParticipantJoined, new
        {
            participantId = joiner.Id,
            displayName = joiner.DisplayName,
            status = (int)joiner.Status,
            canOrder = joiner.CanOrder,
            canSeeTableTotal = joiner.CanSeeTableTotal,
            canPay = joiner.CanPay,
        });

        var hostUserId = tab.Participants.FirstOrDefault(p => p.Id == tab.HostParticipantId)?.UserId;

        if (hostUserId is not { } dinerUserId || joiner.UserId == dinerUserId)
        {
            return;
        }

        var askedBefore = tab.Participants.Any(p =>
            p.WasTurnedAway
            && (p.DeviceId == joiner.DeviceId || (joiner.UserId is { } account && p.UserId == account)));

        if (askedBefore)
        {
            logger.LogInformation(
                "Participant {ParticipantId} is back on tab {TabId} after the host turned them away; not pushing again.",
                joiner.Id, tab.Id);

            return;
        }

        outbox.Enqueue(
            OutboxMessageTypes.JoinRequested,
            new
            {
                tabId = tab.Id,
                participantId = joiner.Id,
                dinerUserId,
                tableLabel = tab.DiningTable?.Label ?? string.Empty,
                // The name they gave, or none. "Guest 3" is the roster's placeholder in English; the
                // push says "a guest" in the host's own language instead.
                guestName = string.IsNullOrWhiteSpace(givenName) ? null : joiner.DisplayName,
            },
            clock.UtcNow,
            OutboxMessageTypes.KeyFor("participant", joiner.Id, "join-requested"));
    }

    /// <summary>
    /// Lets the booker onto their own booking's tab without the host's tap, recorded on the ledger as
    /// an approval so the other phones' rosters move as if the host had approved them.
    /// </summary>
    private async Task ApproveBookerAsync(
        Tab tab,
        TabParticipant participant,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        participant.Approve(nowUtc);

        ledger.Append(tab.Id, TabEventType.ParticipantApproved, new
        {
            participantId = participant.Id,
            displayName = participant.DisplayName,
            status = (int)participant.Status,
            canOrder = participant.CanOrder,
            canSeeTableTotal = participant.CanSeeTableTotal,
            canPay = participant.CanPay,
        });

        await ledger.SaveAppendedAsync(cancellationToken);

        authority.InvalidateParticipant(tab.Id, participant.Id);
    }
}
