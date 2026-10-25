using Microsoft.EntityFrameworkCore;
using Yalla.Application.Abstractions;
using Yalla.Application.Tabs;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// What every tab command starts from and answers with: who may act, the tab and its sitting, and the
/// result a phone or a tablet is handed back.
/// </summary>
internal sealed class TabLookups(
    YallaDbContext db,
    ICurrentActor actor,
    ITabQuery query,
    TabParticipantTokens tokens)
{
    // ---------------------------------------------------------------- permissions

    internal void RequireStaff(string operation)
    {
        if (actor.Type != ActorType.Staff || actor.StaffMemberId is null)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }
    }

    internal Guid RequireStaffId(string operation)
    {
        RequireStaff(operation);

        return actor.StaffMemberId!.Value;
    }

    /// <summary>
    /// What every opening must carry. Named <c>command</c>, which the API reports as a refusal of the
    /// request as a whole rather than of a field - the filter in front has already refused a missing
    /// command id with its own sentence, so this is the backstop for any other caller.
    /// </summary>
    internal static void RequireOpening(Guid clientCommandId, int? partySize)
    {
        if (clientCommandId == Guid.Empty)
        {
            throw new ArgumentException("A clientCommandId is required.", "command");
        }

        if (partySize is { } size && size <= 0)
        {
            throw new ArgumentOutOfRangeException("command", size, "Party size must be greater than zero.");
        }
    }

    internal static string RequireDevice(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId)
            ? throw new ArgumentException("A device identifier is required.", nameof(deviceId))
            : deviceId.Trim();

    // ---------------------------------------------------------------- loading and shaping

    internal async Task<Tab> LoadTabAsync(Guid tabId, CancellationToken cancellationToken, bool includeJoinTokens = false)
    {
        IQueryable<Tab> tabs = db.Tabs
            .Include(t => t.Participants)
            .Include(t => t.DiningTable)
            .Include(t => t.Branch)
                .ThenInclude(b => b.Venue);

        if (includeJoinTokens)
        {
            tabs = tabs.Include(t => t.JoinTokens);
        }

        var tab = await tabs.FirstOrDefaultAsync(t => t.Id == tabId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Tab {tabId} was not found.");

        RequirePaid(tab.Branch);

        return tab;
    }

    /// <summary>
    /// Tabs and ordering are a paid feature, per branch. Checked in the service so it holds for
    /// every caller; the answer is "not enabled for this branch", not a 403 - the caller is
    /// allowed to be here, the branch has not paid for what they asked.
    /// </summary>
    internal static void RequirePaid(Branch branch)
    {
        if (!branch.IsPaid)
        {
            throw new FeatureNotEnabledException("Tabs and ordering", branch.Id, branch.SubscriptionTier);
        }
    }

    /// <summary>
    /// The sitting's tab - never one a waiter turned away, which gave the sitting's tab slot back so the
    /// party really there can open their own.
    /// </summary>
    internal Task<Tab?> LoadTabBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        db.Tabs
            .Include(t => t.Participants)
            .FirstOrDefaultAsync(
                t => t.TableSessionId == sessionId && t.Admission != TabAdmission.Rejected, cancellationToken);

    internal async Task<TabAccessResult> ResultAsync(
        Tab tab,
        TabParticipant participant,
        TabOpenOutcome outcome,
        bool wasReplay,
        CancellationToken cancellationToken)
    {
        var token = tokens.Issue(tab, participant);

        var view = await query.GetForParticipantAsync(tab.Id, participant.Id, cancellationToken)
                   ?? throw new InvalidOperationException(
                       $"Participant {participant.Id} was just written to tab {tab.Id} but cannot be read back.");

        return new TabAccessResult(token, view, outcome, wasReplay);
    }

    internal async Task<TabStaffView> StaffViewAsync(Guid tabId, CancellationToken cancellationToken) =>
        await query.GetForStaffAsync(tabId, cancellationToken)
        ?? throw new InvalidOperationException($"Tab {tabId} was just written but cannot be read back.");

    internal static string NameOrDefault(string? displayName, int ordinal) =>
        string.IsNullOrWhiteSpace(displayName) ? $"Guest {ordinal}" : displayName.Trim();
}
