namespace Yalla.Application.Abstractions;

/// <summary>
/// The per-network budget for bookings made without a proved phone number, shared by both doors that make
/// them: the public page's (no account) and the app's (an account that never proved its number).
/// </summary>
/// <remarks>
/// <para>
/// In the service rather than as an endpoint policy because the app's door is <c>POST /api/reservations</c>,
/// which a proved diner uses too: only the service knows the booker has no proof. And keyed on the caller's
/// network - the address, an IPv6 one by its /64 - never on a bearer token: a tab participant token from any
/// photographed QR code, or a fresh account, must not open a fresh budget.
/// </para>
/// <para>
/// Ten an hour by default (<c>RateLimiting:PublicUnverifiedBookingPermitLimit</c> /
/// <c>PublicUnverifiedBookingWindowSeconds</c>). Not one: a carrier puts thousands of phones behind one
/// address. The real limits are the number, the browser and the venue's approval.
/// </para>
/// </remarks>
public interface IUnverifiedBookingBudget
{
    /// <summary>Spends one booking from the caller's network budget; false when it is spent already.</summary>
    bool TrySpend();
}

/// <summary>No budget at all: where rate limiting is off, and for code that runs with no request.</summary>
public sealed class UnlimitedUnverifiedBookingBudget : IUnverifiedBookingBudget
{
    public bool TrySpend() => true;
}

/// <summary>
/// The caller's network has made its share of bookings without a proved number this hour. 429
/// <c>rate-limited</c>.
/// </summary>
public sealed class UnverifiedBookingRateLimitedException()
    : Exception("Too many bookings without a verified number from this network. Verify your number, or try again later.");
