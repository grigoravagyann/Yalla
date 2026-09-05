namespace Yalla.Domain.Venues;

/// <summary>
/// The sane bounds for a <see cref="ReservationPolicy"/> as edited from the admin panel.
/// </summary>
/// <remarks>
/// <para>
/// The policy's own constructor accepts any positive turn time, because the domain must not
/// refuse a value that a migration or a test has a reason to write. These bounds are the
/// <i>editing</i> rules: a five-minute turn time is a typo and a twelve-hour one is a table nobody
/// else can book all day, and both are refused with a message that says so rather than silently
/// clamped to something the owner did not choose.
/// </para>
/// <para>
/// Zero is allowed for the paddings - buffer, grace, lead time - because "no buffer" is a real
/// setting for a counter-service cafe. Turn time and booking window must be positive: a booking
/// with no duration and a window of no days are not settings, they are switching reservations off.
/// </para>
/// </remarks>
public static class ReservationPolicyLimits
{
    public const int MinTurnTimeMinutes = 15;
    public const int MaxTurnTimeMinutes = 6 * 60;

    public const int MaxBufferMinutes = 3 * 60;
    public const int MaxGraceMinutes = 3 * 60;
    public const int MaxLateNudgeAfterMinutes = 3 * 60;
    public const int MaxGraceExtensionMinutes = 3 * 60;

    public const int MaxMinLeadMinutes = 7 * 24 * 60;

    public const int MinBookingWindowDays = 1;
    public const int MaxBookingWindowDays = 365;

    public const int MaxCancellationDeadlineMinutes = 14 * 24 * 60;

    /// <summary>Beyond a few hours the warning fires on every seating and stops being read.</summary>
    public const int MaxWalkInHoldbackMinutes = 4 * 60;

    /// <summary>Refuses a policy outside the bounds, naming the field and the value given.</summary>
    /// <exception cref="ArgumentOutOfRangeException">One field is outside its bounds.</exception>
    public static void Validate(ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        Between(policy.TurnTimeMinutes, MinTurnTimeMinutes, MaxTurnTimeMinutes, "Turn time", "minutes");
        Between(policy.BufferMinutes, 0, MaxBufferMinutes, "Buffer", "minutes");
        Between(policy.GraceMinutes, 0, MaxGraceMinutes, "Grace period", "minutes");
        Between(policy.LateNudgeAfterMinutes, 0, MaxLateNudgeAfterMinutes, "Late nudge delay", "minutes");
        Between(policy.GraceExtensionMinutes, 0, MaxGraceExtensionMinutes, "Grace extension", "minutes");
        Between(policy.MinLeadMinutes, 0, MaxMinLeadMinutes, "Minimum lead time", "minutes");
        Between(policy.BookingWindowDays, MinBookingWindowDays, MaxBookingWindowDays, "Booking window", "days");
        Between(policy.CancellationDeadlineMinutes, 0, MaxCancellationDeadlineMinutes, "Cancellation deadline", "minutes");
        Between(policy.WalkInHoldbackMinutes, 0, MaxWalkInHoldbackMinutes, "Walk-in holdback", "minutes");

        // Service charge 0..100 is already enforced by the policy constructor.
    }

    private static void Between(int value, int min, int max, string field, string unit)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(
                field,
                value,
                $"{field} must be between {min} and {max} {unit}; {value} {unit} was given.");
        }
    }
}
