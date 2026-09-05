using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Domain;
using Yalla.Domain.Tabs;

namespace Yalla.Infrastructure.Identity;

/// <summary>
/// Mints the tab-scoped token for a participant, with an expiry that tracks the tab rather than
/// the clock.
/// </summary>
/// <remarks>
/// <para>
/// The token names a participant, a tab and a branch, and nothing else. There is no user row
/// behind it. That is not a shortcut - it is the requirement: walk-ins are most of the traffic in
/// a cafe, and a stranger will not create an account to order a coffee.
/// </para>
/// <para>
/// A tab that is still open gets the hard cap, because there is no way to know when it will
/// close. A tab that has already closed gets its close plus the receipt grace period, so someone
/// can still look at what they paid. The <c>TabParticipant</c> policy re-checks the tab on every
/// request, so a tab closing mid-token shortens it too - the <c>exp</c> claim is the ceiling, not
/// the whole rule.
/// </para>
/// </remarks>
internal sealed class TabParticipantTokens(
    TokenIssuer tokens,
    IClock clock,
    IOptions<JwtOptions> jwtOptions)
{
    private readonly JwtOptions _jwt = jwtOptions.Value;

    public TabParticipantTokenResult Issue(Tab tab, TabParticipant participant)
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
