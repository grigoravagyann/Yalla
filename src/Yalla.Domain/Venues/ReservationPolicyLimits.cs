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
/// <para>
/// <b>Every refusal names its field, and a request that breaks six bounds reports six.</b> The
/// refusal used to be an <see cref="ArgumentOutOfRangeException"/> whose <c>ParamName</c> was an
/// English label - "Turn time" - which left the console maintaining a label-to-input lookup table
/// keyed on server prose while its own labels were translated. <see cref="Check"/> returns the wire
/// names instead, and returns all of them, because a form that surfaces one error at a time makes
/// an owner submit six times.
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

    /// <summary>A party of one still needs staff approval to be a meaningful threshold.</summary>
    public const int MinApprovalRequiredAbovePartySize = 1;

    /// <summary>
    /// The wire names of the policy's fields, as the OpenAPI schema spells them.
    /// </summary>
    /// <remarks>
    /// Constants rather than <c>nameof</c> on the command, because the command lives in the
    /// application layer and these rules do not depend on it - and because the contract a client
    /// keys on should be visible as a string, not inferred from a C# identifier that could be
    /// renamed without anyone noticing the wire changed.
    /// </remarks>
    public static class Fields
    {
        public const string TurnTimeMinutes = "turnTimeMinutes";
        public const string BufferMinutes = "bufferMinutes";
        public const string GraceMinutes = "graceMinutes";
        public const string LateNudgeAfterMinutes = "lateNudgeAfterMinutes";
        public const string GraceExtensionMinutes = "graceExtensionMinutes";
        public const string MinLeadMinutes = "minLeadMinutes";
        public const string BookingWindowDays = "bookingWindowDays";
        public const string CancellationDeadlineMinutes = "cancellationDeadlineMinutes";
        public const string ServiceChargePercent = "serviceChargePercent";
        public const string MaxSeatOverhang = "maxSeatOverhang";
        public const string ApprovalRequiredAbovePartySize = "approvalRequiredAbovePartySize";
        public const string WalkInHoldbackMinutes = "walkInHoldbackMinutes";
    }

    /// <summary>
    /// Every bound the supplied values break, named. Empty when the policy is acceptable.
    /// </summary>
    /// <remarks>
    /// Checked against the raw values rather than a constructed <see cref="ReservationPolicy"/>,
    /// deliberately: the constructor throws on the first bad number it meets, so validating after
    /// it would only ever be able to report one problem out of six.
    /// </remarks>
    public static IReadOnlyList<FieldViolation> Check(
        int turnTimeMinutes,
        int bufferMinutes,
        int graceMinutes,
        int lateNudgeAfterMinutes,
        int graceExtensionMinutes,
        int minLeadMinutes,
        int bookingWindowDays,
        int cancellationDeadlineMinutes,
        decimal serviceChargePercent,
        int? maxSeatOverhang,
        int? approvalRequiredAbovePartySize,
        int walkInHoldbackMinutes)
    {
        var violations = new List<FieldViolation>();

        Between(violations, Fields.TurnTimeMinutes, turnTimeMinutes, MinTurnTimeMinutes, MaxTurnTimeMinutes, "Turn time", "minutes");
        Between(violations, Fields.BufferMinutes, bufferMinutes, 0, MaxBufferMinutes, "Buffer", "minutes");
        Between(violations, Fields.GraceMinutes, graceMinutes, 0, MaxGraceMinutes, "Grace period", "minutes");
        Between(violations, Fields.LateNudgeAfterMinutes, lateNudgeAfterMinutes, 0, MaxLateNudgeAfterMinutes, "Late nudge delay", "minutes");
        Between(violations, Fields.GraceExtensionMinutes, graceExtensionMinutes, 0, MaxGraceExtensionMinutes, "Grace extension", "minutes");
        Between(violations, Fields.MinLeadMinutes, minLeadMinutes, 0, MaxMinLeadMinutes, "Minimum lead time", "minutes");
        Between(violations, Fields.BookingWindowDays, bookingWindowDays, MinBookingWindowDays, MaxBookingWindowDays, "Booking window", "days");
        Between(violations, Fields.CancellationDeadlineMinutes, cancellationDeadlineMinutes, 0, MaxCancellationDeadlineMinutes, "Cancellation deadline", "minutes");
        Between(violations, Fields.WalkInHoldbackMinutes, walkInHoldbackMinutes, 0, MaxWalkInHoldbackMinutes, "Walk-in holdback", "minutes");

        // Checked here rather than left to the policy constructor, so it lands in the same list as
        // everything else instead of being the one field that answers in a different shape.
        if (serviceChargePercent < 0m || serviceChargePercent > 100m)
        {
            violations.Add(new FieldViolation(
                Fields.ServiceChargePercent,
                $"Service charge must be between 0 and 100 percent; {serviceChargePercent} was given.",
                FieldBounds.Range,
                Min: 0m,
                Max: 100m,
                Value: serviceChargePercent));
        }

        if (maxSeatOverhang is { } overhang && overhang < 0)
        {
            violations.Add(new FieldViolation(
                Fields.MaxSeatOverhang,
                $"Seat overhang must not be negative; {overhang} was given.",
                FieldBounds.Min,
                Min: 0,
                Value: overhang));
        }

        if (approvalRequiredAbovePartySize is { } threshold && threshold < MinApprovalRequiredAbovePartySize)
        {
            violations.Add(new FieldViolation(
                Fields.ApprovalRequiredAbovePartySize,
                $"The approval threshold must be at least {MinApprovalRequiredAbovePartySize} guest; {threshold} was given.",
                FieldBounds.Min,
                Min: MinApprovalRequiredAbovePartySize,
                Value: threshold));
        }

        return violations;
    }

    /// <summary>Refuses values outside the bounds, naming every field that broke one.</summary>
    /// <exception cref="FieldValidationException">One or more fields are outside their bounds.</exception>
    public static void Validate(
        int turnTimeMinutes,
        int bufferMinutes,
        int graceMinutes,
        int lateNudgeAfterMinutes,
        int graceExtensionMinutes,
        int minLeadMinutes,
        int bookingWindowDays,
        int cancellationDeadlineMinutes,
        decimal serviceChargePercent,
        int? maxSeatOverhang,
        int? approvalRequiredAbovePartySize,
        int walkInHoldbackMinutes)
    {
        var violations = Check(
            turnTimeMinutes, bufferMinutes, graceMinutes, lateNudgeAfterMinutes, graceExtensionMinutes,
            minLeadMinutes, bookingWindowDays, cancellationDeadlineMinutes, serviceChargePercent,
            maxSeatOverhang, approvalRequiredAbovePartySize, walkInHoldbackMinutes);

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }
    }

    /// <summary>The same check over a policy that already exists, for a caller holding one.</summary>
    /// <exception cref="FieldValidationException">One or more fields are outside their bounds.</exception>
    public static void Validate(ReservationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        Validate(
            policy.TurnTimeMinutes,
            policy.BufferMinutes,
            policy.GraceMinutes,
            policy.LateNudgeAfterMinutes,
            policy.GraceExtensionMinutes,
            policy.MinLeadMinutes,
            policy.BookingWindowDays,
            policy.CancellationDeadlineMinutes,
            policy.ServiceChargePercent,
            policy.MaxSeatOverhang,
            policy.ApprovalRequiredAbovePartySize,
            policy.WalkInHoldbackMinutes);
    }

    private static void Between(
        List<FieldViolation> violations,
        string field,
        int value,
        int min,
        int max,
        string label,
        string unit)
    {
        if (value >= min && value <= max)
        {
            return;
        }

        violations.Add(new FieldViolation(
            field,
            $"{label} must be between {min} and {max} {unit}; {value} {unit} was given.",
            value < min ? FieldBounds.Min : FieldBounds.Max,
            Min: min,
            Max: max,
            Value: value));
    }
}
