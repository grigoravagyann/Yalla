using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Tokens for people at a table who have no account and are never going to be asked for one.
/// </summary>
/// <remarks>
/// <para>
/// The token this issues names a participant, a tab and a branch, and nothing else. There is no
/// user row behind it. That is not a shortcut - it is the requirement: walk-ins are most of the
/// traffic in a cafe, and a stranger will not create an account to order a coffee.
/// </para>
/// <para>
/// Because the token names one tab, a participant is structurally incapable of addressing another
/// one. Enforcement lives in the <c>TabParticipant</c> authorisation policy, which compares the
/// route's tab id to the claim; no handler checks it, so no handler can forget to.
/// </para>
/// </remarks>
internal sealed class TabParticipantAuthService(
    YallaDbContext db,
    IClock clock,
    TokenIssuer tokens,
    IOptions<JwtOptions> jwtOptions,
    ILogger<TabParticipantAuthService> logger) : ITabParticipantAuthService
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<TabParticipantTokenResult> JoinByTableQrAsync(
        string qrToken,
        string deviceId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var token = (qrToken ?? string.Empty).Trim();

        var table = await db.DiningTables
            .Where(t => t.QrToken == token && t.IsActive)
            .Select(t => new { t.Id, t.BranchId, t.Label, t.CurrentSessionId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("That QR code does not belong to a table in service.");

        var tab = table.CurrentSessionId is { } sessionId
            ? await db.Tabs
                .Include(t => t.Participants)
                .FirstOrDefaultAsync(t => t.TableSessionId == sessionId, cancellationToken)
            : null;

        if (tab is null)
        {
            // Seating is a staff transition in this system - it opens a TableSession, writes an
            // audit row and takes part in the table state machine's concurrency handling. Letting
            // a QR scan open a tab on an unseated table would be a second, unaudited way to
            // occupy a table, which is exactly what that state machine exists to prevent.
            throw new DomainStateException(
                $"Nobody is seated at table {table.Label} yet, so there is no tab to join. "
                + "A member of staff seats the party first.");
        }

        return await JoinAsync(tab, deviceId, displayName, ParticipantRole.Guest, cancellationToken);
    }

    public async Task<TabParticipantTokenResult> RedeemJoinTokenAsync(
        string joinToken,
        string deviceId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;
        var value = (joinToken ?? string.Empty).Trim();

        var invitation = await db.TabJoinTokens
            .FirstOrDefaultAsync(t => t.Token == value, cancellationToken);

        if (invitation is null || !invitation.IsUsableAt(nowUtc))
        {
            throw new AuthenticationFailedException(
                "join-token-invalid", "That invitation is no longer valid. Ask for a new one.");
        }

        var tab = await db.Tabs
            .Include(t => t.Participants)
            .FirstOrDefaultAsync(t => t.Id == invitation.TabId, cancellationToken)
            ?? throw new KeyNotFoundException("That invitation points at a tab that no longer exists.");

        return await JoinAsync(tab, deviceId, displayName, ParticipantRole.Guest, cancellationToken);
    }

    public async Task<TabParticipantTokenResult> SetDisplayNameAsync(
        Guid tabId,
        Guid participantId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var participant = await db.TabParticipants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.TabId == tabId, cancellationToken)
            ?? throw new KeyNotFoundException("No such participant on that tab.");

        participant.SetDisplayName(displayName);
        await db.SaveChangesAsync(cancellationToken);

        var tab = await db.Tabs.FirstAsync(t => t.Id == tabId, cancellationToken);

        return Issue(tab, participant);
    }

    private async Task<TabParticipantTokenResult> JoinAsync(
        Tab tab,
        string deviceId,
        string? displayName,
        ParticipantRole role,
        CancellationToken cancellationToken)
    {
        if (tab.Status is not (TabStatus.Open or TabStatus.Closing))
        {
            throw new DomainStateException("That tab is closed. Ask a member of staff to open a new one.");
        }

        var device = string.IsNullOrWhiteSpace(deviceId)
            ? throw new ArgumentException("A device identifier is required.", nameof(deviceId))
            : deviceId.Trim();

        var nowUtc = clock.UtcNow;

        // Re-scanning after the phone locked, or reopening the app, must not put a second person
        // on the bill. The device is what recognises someone with no account.
        var existing = tab.Participants.FirstOrDefault(
            p => p.DeviceId == device && p.Status != ParticipantStatus.Removed);

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                existing.SetDisplayName(displayName);
            }

            await db.SaveChangesAsync(cancellationToken);

            return Issue(tab, existing);
        }

        var name = string.IsNullOrWhiteSpace(displayName)
            ? $"Guest {tab.Participants.Count + 1}"
            : displayName.Trim();

        var participant = new TabParticipant(
            tab.Id,
            name,
            device,
            role,
            ParticipantStatus.Approved,
            nowUtc,
            canOrder: true,
            canSeeTableTotal: !tab.HideTotalFromGuests,
            canPay: false);

        db.TabParticipants.Add(participant);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Participant {ParticipantId} joined tab {TabId} with no account.", participant.Id, tab.Id);

        return Issue(tab, participant);
    }

    /// <summary>
    /// Mints the token, with an expiry that tracks the tab rather than the clock.
    /// </summary>
    /// <remarks>
    /// A tab that is still open gets the hard cap, because there is no way to know when it will
    /// close. A tab that has already closed gets its close plus the receipt grace period, so
    /// someone can still look at what they paid. The policy re-checks the tab on every request,
    /// so a tab closing mid-token shortens it too - the <c>exp</c> claim is the ceiling, not the
    /// whole rule.
    /// </remarks>
    private TabParticipantTokenResult Issue(Tab tab, TabParticipant participant)
    {
        var nowUtc = clock.UtcNow;
        var cap = nowUtc.AddHours(_jwt.ParticipantTokenMaxHours);

        var expiresAtUtc = tab.ClosedAtUtc is { } closedAtUtc
            ? Min(closedAtUtc.AddMinutes(_jwt.ParticipantReceiptGraceMinutes), cap)
            : cap;

        // A tab that closed longer ago than the grace period would otherwise mint an expiry in
        // the past, which the token handler rejects with a confusing message. Refuse plainly.
        if (expiresAtUtc <= nowUtc)
        {
            throw new DomainStateException("That tab closed some time ago and can no longer be joined.");
        }

        var (token, expires) = tokens.IssueTabParticipantToken(
            participant.Id, tab.Id, tab.BranchId, expiresAtUtc);

        return new TabParticipantTokenResult(
            token,
            expires,
            participant.Id,
            tab.Id,
            tab.BranchId,
            participant.DisplayName,
            participant.CanOrder);
    }

    private static DateTime Min(DateTime left, DateTime right) => left < right ? left : right;
}
