namespace Yalla.Domain.Occupancy;

/// <summary>
/// A move from a booking's manage link refused because the booking's account cannot take it: deactivated,
/// deleted, or without a proved number - found by the phone gate, or by the account's lock inside the
/// move.
/// </summary>
/// <remarks>
/// <para>
/// The <b>403 <c>phone-not-verified</c></b> the app's diner gets, with words a visitor holding only the
/// link can act on. The app's sentence - "verify your number with a code first" - is addressed to an
/// account holder who is signed in; the link holder has no account to verify, and may not be the diner
/// at all. What they can do is call the venue, or cancel - which the link still allows - and book again.
/// </para>
/// <para>
/// It also stands in for the account lock's <c>session-revoked</c> (401) when the account stops being
/// live between the gate and the lock: an anonymous route has no session to end, and a web client that
/// treats a 401 as "sign in again" would do the wrong thing.
/// </para>
/// <para>No context and a constant message, as for the manage link's other refusals.</para>
/// </remarks>
public sealed class ManageLinkAccountUnavailableException()
    : Exception("This booking can no longer be changed online. Call the venue, or cancel it and book again.");
