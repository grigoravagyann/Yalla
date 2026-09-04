using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.Domain.Occupancy;

/// <summary>
/// Every rule that decides whether a slot may be booked, as pure functions over the branch's own
/// <see cref="ReservationPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure and shared on purpose. Booking a table and <i>showing</i> whether a table is bookable are
/// the same question asked twice, and a second copy of these rules inside the availability query
/// would drift from this one within a release - the app would offer a table that the creation
/// endpoint then refuses, which reads to a diner as the product being broken.
/// </para>
/// <para>
/// Nothing here throws. Each function answers with a <see cref="ReservationRejectionReason"/> or
/// null, so the availability read model can label a table while the creation service turns the
/// same value into its own named exception.
/// </para>
/// <para>
/// Not one number in this file. Every threshold arrives on the policy, because a lunch cafe and a
/// dinner restaurant answer all of these differently.
/// </para>
/// </remarks>
public static class ReservationRules
{
    /// <summary>
    /// The two rules about <i>when</i> the request is: too soon to be useful, or too far out to be
    /// honoured.
    /// </summary>
    /// <param name="startUtc">The requested start.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="localDate">The diner's local calendar date, as they picked it.</param>
    /// <param name="localToday">Today at the branch, in the branch's own zone.</param>
    /// <param name="policy">The branch's policy. Supplies both thresholds.</param>
    public static ReservationRejectionReason? CheckTiming(
        DateTime startUtc,
        DateTime nowUtc,
        DateOnly localDate,
        DateOnly localToday,
        ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Nobody books a table they are standing next to. Strictly earlier than the threshold is a
        // rejection; exactly on it is fine, so a client that computed the same boundary and
        // offered the slot is not contradicted a second later.
        if (startUtc < nowUtc.AddMinutes(policy.MinLeadMinutes))
        {
            return ReservationRejectionReason.LeadTimeTooShort;
        }

        return localDate > localToday.AddDays(policy.BookingWindowDays)
            ? ReservationRejectionReason.OutsideBookingWindow
            : null;
    }

    /// <summary>
    /// Whether the whole interval falls inside one opening block for the branch's local day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>whole</b> interval, not just the start: a booking the venue cannot see out is not a
    /// booking. And the day an interval belongs to is not always the day it starts on. A branch
    /// open 10:00-01:00 has a Friday block running into Saturday morning, so a 22:30 Friday
    /// sitting ending 00:30 is inside <i>Friday's</i> hours - which is why the previous local day
    /// is examined as well as the current one.
    /// </para>
    /// <para>
    /// A branch may open twice in a day - lunch, then dinner - so blocks are not unique per day and
    /// the interval has to fit inside a single one of them. Straddling the afternoon closure is
    /// not "open for both halves".
    /// </para>
    /// </remarks>
    /// <param name="openingHours">Every opening block the branch has, for all days.</param>
    /// <param name="localStart">Wall-clock start at the branch.</param>
    /// <param name="localEnd">Wall-clock end at the branch.</param>
    public static ReservationRejectionReason? CheckOpeningHours(
        IEnumerable<OpeningHours> openingHours,
        DateTime localStart,
        DateTime localEnd)
    {
        ArgumentNullException.ThrowIfNull(openingHours);

        var blocks = openingHours as IReadOnlyCollection<OpeningHours> ?? openingHours.ToList();
        var startDate = DateOnly.FromDateTime(localStart);

        foreach (var day in new[] { startDate.AddDays(-1), startDate })
        {
            foreach (var block in blocks)
            {
                if (block.Day != day.DayOfWeek)
                {
                    continue;
                }

                var opensAt = day.ToDateTime(block.OpensAt);
                var closesAt = (block.ClosesNextDay ? day.AddDays(1) : day).ToDateTime(block.ClosesAt);

                if (localStart >= opensAt && localEnd <= closesAt)
                {
                    return null;
                }
            }
        }

        return ReservationRejectionReason.OutsideOpeningHours;
    }

    /// <summary>
    /// The rules about <i>this table</i>: whether it takes bookings at all, whether the party
    /// fits, and whether it is wastefully large for them.
    /// </summary>
    /// <remarks>
    /// Seat overhang stops a couple taking the eight-seater on a Friday. It is checked only when
    /// the branch sets a limit; null means the owner does not care, which is the right answer for
    /// a quiet weekday cafe and the wrong one for a Friday dinner service - hence a setting.
    /// </remarks>
    public static ReservationRejectionReason? CheckTable(
        TableStatus status,
        bool isBookable,
        int seats,
        int partySize,
        ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!isBookable)
        {
            return ReservationRejectionReason.TableNotBookable;
        }

        if (status == TableStatus.OutOfService)
        {
            return ReservationRejectionReason.TableOutOfService;
        }

        if (partySize > seats)
        {
            return ReservationRejectionReason.PartyExceedsCapacity;
        }

        return policy.MaxSeatOverhang is { } maxOverhang && seats - partySize > maxOverhang
            ? ReservationRejectionReason.SeatOverhangExceeded
            : null;
    }

    /// <summary>
    /// Whether the branch wants a human to look at this booking before it is promised.
    /// </summary>
    /// <remarks>
    /// A large party is not a rejection. Twelve people is the most valuable booking of the night
    /// and the one most likely to need tables moved, so it goes to staff as
    /// <see cref="ReservationStatus.PendingApproval"/> rather than being refused.
    /// </remarks>
    public static bool NeedsApproval(int partySize, ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.ApprovalRequiredAbovePartySize is { } threshold && partySize > threshold;
    }

    /// <summary>
    /// Whether a cancellation at <paramref name="nowUtc"/> is past the branch's free-cancellation
    /// deadline.
    /// </summary>
    /// <remarks>
    /// Never blocks the cancellation. A late cancellation is far better than a no-show, so the
    /// answer only decides whether the booking is <i>recorded</i> as a late one.
    /// </remarks>
    public static bool IsLateCancellation(DateTime startUtc, DateTime nowUtc, ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return nowUtc > startUtc.AddMinutes(-policy.CancellationDeadlineMinutes);
    }
}
