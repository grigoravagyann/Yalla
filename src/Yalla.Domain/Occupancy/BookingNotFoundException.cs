namespace Yalla.Domain.Occupancy;

/// <summary>
/// None of the caller's bookings carries that code - whether or not somebody else's does.
/// </summary>
/// <remarks>
/// <para>
/// One answer for both, word for word. A booking code is six characters read out at the door; it is
/// not a secret (see <see cref="ReservationCode"/>), so knowing one proves nothing, and a different
/// answer for "exists, but not yours" would let anybody with an account find out which codes are
/// live tonight.
/// </para>
/// <para>
/// A <see cref="KeyNotFoundException"/>, so it is a 404 wherever it lands; the API gives it its own
/// code so the app can say "check the code" rather than a generic not-found.
/// </para>
/// </remarks>
public sealed class BookingNotFoundException()
    : KeyNotFoundException(
        "None of your bookings has that code. Check it against the booking in the app, or scan the "
        + "code on the table.");
