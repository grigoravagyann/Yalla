using Microsoft.EntityFrameworkCore;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Reads one tab for the participant looking at it.
/// </summary>
/// <remarks>
/// This query does not check that the caller belongs to the tab: the <c>TabParticipant</c> policy
/// has already refused anyone who does not, and repeating the check here would be a second place
/// for the rule to live and drift.
/// </remarks>
internal sealed class TabQuery(YallaDbContext db) : ITabQuery
{
    public async Task<TabView?> GetForParticipantAsync(
        Guid tabId,
        Guid participantId,
        CancellationToken cancellationToken = default)
    {
        var tab = await db.Tabs
            .AsNoTracking()
            .Where(t => t.Id == tabId)
            .Select(t => new
            {
                t.Id,
                t.BranchId,
                TableLabel = t.DiningTable.Label,
                t.Status,
                t.SettlementMode,
                t.OpenedAtUtc,
                t.ClosedAtUtc,
                t.TotalAmd,
                t.RemainingAmd,
                t.HideTotalFromGuests,
                Participants = t.Participants
                    .Where(p => p.Status != ParticipantStatus.Removed)
                    .OrderBy(p => p.JoinedAtUtc)
                    .Select(p => new TabParticipantView(
                        p.Id, p.DisplayName, p.Role, p.Status, p.CanOrder, p.CanPay))
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (tab is null)
        {
            return null;
        }

        var me = tab.Participants.FirstOrDefault(p => p.ParticipantId == participantId);

        if (me is null)
        {
            return null;
        }

        // Hiding the total is a host's decision recorded on the tab, so it is applied here rather
        // than left to three clients to remember. A guest who may not see the total gets no
        // money at all, not a zero.
        var showMoney = !tab.HideTotalFromGuests || me.Role == ParticipantRole.Host;

        return new TabView(
            tab.Id,
            tab.BranchId,
            tab.TableLabel,
            tab.Status,
            tab.SettlementMode,
            tab.OpenedAtUtc,
            tab.ClosedAtUtc,
            showMoney ? tab.TotalAmd : null,
            showMoney ? tab.RemainingAmd : null,
            me,
            tab.Participants);
    }
}
