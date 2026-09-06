using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Ordering;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Who owes what, and what has happened on the tab.
/// </summary>
/// <remarks>
/// Reads only. The arithmetic lives in <c>TabBilling</c> and the recomputation in
/// <see cref="TabLedger"/>; this projects their output through whatever the caller is allowed to
/// see, using the same <see cref="TabPermissions"/> functions the tab projection and the
/// authorisation handler use. One rule, three readers, so a client is never shown a number the
/// policy would refuse or refused a number the client was shown.
/// </remarks>
internal sealed class TabBillingQuery(
    YallaDbContext db,
    TabLedger ledger,
    ILogger<TabBillingQuery> logger) : ITabBillingQuery
{
    /// <summary>The most events one catch-up will return. A client that wants more asks again.</summary>
    private const int MaxEventPageSize = 500;

    public async Task<TabSharesView> GetSharesAsync(
        Guid tabId,
        Guid? actingParticipantId,
        CancellationToken cancellationToken = default)
    {
        var tab = await ledger.LoadForWriteAsync(tabId, cancellationToken);

        // Computed from the lines, which are the truth, and the cache is brought back in line if it
        // disagrees. This is the read half of moving the cache out of the order-insert transaction:
        // a refresh that lost every one of its races leaves stale columns behind, and the bill
        // screen is exactly where somebody would notice. Writes only when the numbers differ.
        var bill = await ledger.EnsureTotalsFreshAsync(tab, cancellationToken);

        var names = tab.Participants.ToDictionary(p => p.Id, p => (p.DisplayName, p.Status));

        var shares = bill.Shares
            .Select(s => new ParticipantShareView(
                s.ParticipantId,
                names.TryGetValue(s.ParticipantId, out var who) ? who.DisplayName : "Guest",
                names.TryGetValue(s.ParticipantId, out var status) ? status.Status : ParticipantStatus.Removed,
                s.OwnItemsAmd,
                s.SharedItemsAmd,
                s.AbsorbedFromRemovedAmd,
                s.PersonalAmd,
                s.ServiceChargeAmd,
                s.ShareAmd,
                s.PaidAmd))
            .ToList();

        if (bill.AbsorbedFromRemovedAmd > 0L)
        {
            // Worth a log line: it means somebody ate and left, and the host is carrying it. A
            // venue asking "why did the host owe more than they ordered" needs this to exist.
            logger.LogInformation(
                "Tab {TabId}: {AbsorbedAmd} AMD from removed participants fell to the host.",
                tabId, bill.AbsorbedFromRemovedAmd);
        }

        // Staff see everything - they are not participants and the host's visibility flags are not
        // about them.
        if (actingParticipantId is null)
        {
            return new TabSharesView(
                tabId, null, true, TabLedger.ToSnapshot(bill), shares, bill.AbsorbedFromRemovedAmd);
        }

        var me = tab.Participants.FirstOrDefault(p => p.Id == actingParticipantId)
                 ?? throw new TabPermissionException("Reading the split", "somebody on this tab");

        var myShare = shares.FirstOrDefault(s => s.ParticipantId == me.Id);

        if (!TabPermissions.MaySeeTableTotal(me.Status, me.CanSeeTableTotal))
        {
            // Their own number, and the table aggregate absent rather than zeroed. A zero reads as
            // "nothing owed"; an absent member beside an explicit flag can only be read as "not
            // shown to you".
            return new TabSharesView(tabId, myShare, false, null, null, 0L);
        }

        return new TabSharesView(
            tabId, myShare, true, TabLedger.ToSnapshot(bill), shares, bill.AbsorbedFromRemovedAmd);
    }

    public async Task<TabEventPage> GetEventsAsync(
        Guid tabId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit <= 0 ? MaxEventPageSize : limit, 1, MaxEventPageSize);

        // One more than asked for, so "is there another page" is answered without a second count.
        var rows = await db.TabEvents
            .AsNoTracking()
            .Where(e => e.TabId == tabId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence)
            .Take(capped + 1)
            .Select(e => new
            {
                e.Sequence,
                e.Type,
                e.PayloadJson,
                e.ActorType,
                e.ActorId,
                e.AtUtc,
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > capped;
        var page = hasMore ? rows.Take(capped).ToList() : rows;

        var maxSequence = await ledger.MaxSequenceAsync(tabId, cancellationToken);

        return new TabEventPage(
            tabId,
            afterSequence,
            maxSequence,
            hasMore,
            [
                .. page.Select(e => new TabEventView(
                    e.Sequence,
                    e.Type,
                    JsonSerializer.Deserialize<JsonElement>(e.PayloadJson),
                    e.ActorType,
                    e.ActorId,
                    e.AtUtc)),
            ]);
    }
}
