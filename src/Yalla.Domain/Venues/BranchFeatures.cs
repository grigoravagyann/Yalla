using Yalla.Domain.Enums;

namespace Yalla.Domain.Venues;

/// <summary>
/// The sets of <see cref="BranchFeature"/> a branch may be allowed, the presets, and the bit arithmetic of
/// <c>Branches.AllowedFeatures</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stored as one int of bits, one per feature</b> (<c>1 &lt;&lt; (value - 1)</c>), not as a table of rows. The set
/// is read with the branch on every gated request (a tab read, a booking, a sign-in), always whole and never on its
/// own; one column keeps those reads a column on a row they already load, a change is one row's update under the
/// branch's own concurrency, and the migration from the tier is a single <c>UPDATE</c>. A query asks for one feature
/// with <c>(b.AllowedFeatureBits &amp; BranchFeatures.Bit(f)) != 0</c>, which EF translates to SQL.
/// </para>
/// <para>
/// On the wire it is never a number: every DTO carries <c>features</c> as a list of <see cref="BranchFeature"/>
/// values, so a client never does bit arithmetic.
/// </para>
/// </remarks>
public static class BranchFeatures
{
    /// <summary>Every feature, in numeric order.</summary>
    public static readonly IReadOnlyList<BranchFeature> All = Enum.GetValues<BranchFeature>().Order().ToArray();

    /// <summary>
    /// The "Basic" preset: everything except <see cref="BranchFeature.TableService"/>. What a Free branch was, and
    /// what a new branch starts with.
    /// </summary>
    public static readonly IReadOnlyList<BranchFeature> Basic = All.Where(f => f != BranchFeature.TableService).ToArray();

    /// <summary>The "Full" preset: all ten. What a Paid branch was.</summary>
    public static readonly IReadOnlyList<BranchFeature> Full = All;

    /// <summary>The bits of <see cref="Full"/>: 1023. The migration from the tier writes this for Paid.</summary>
    public const int FullBits = (1 << 10) - 1;

    /// <summary>The bits of <see cref="Basic"/>: 1007, every bit but TableService's (16). The migration writes this for Free.</summary>
    public const int BasicBits = FullBits & ~(1 << ((int)BranchFeature.TableService - 1));

    /// <summary>The one bit <paramref name="feature"/> owns in <c>Branches.AllowedFeatures</c>.</summary>
    public static int Bit(BranchFeature feature) => 1 << ((int)Defined(feature) - 1);

    /// <summary>Whether <paramref name="bits"/> includes <paramref name="feature"/>.</summary>
    public static bool Has(int bits, BranchFeature feature) => (bits & Bit(feature)) != 0;

    /// <summary>
    /// Refuses a new action that needs <paramref name="feature"/> at a branch whose allowed bits do not include it. Work
    /// already in flight is never asked: see <c>docs/branch-features.md</c> for what each feature gates and what runs on.
    /// </summary>
    /// <param name="allowedBits">The branch's <c>AllowedFeatureBits</c>.</param>
    /// <param name="feature">What the action needs.</param>
    /// <param name="branchId">The branch, for the problem's context.</param>
    /// <exception cref="FeatureNotEnabledException">The feature is blocked at the branch.</exception>
    public static void Require(int allowedBits, BranchFeature feature, Guid branchId)
    {
        if (!Has(allowedBits, feature))
        {
            throw new FeatureNotEnabledException(feature, branchId);
        }
    }

    /// <summary>The bits of a set. Repeats are harmless.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value that is not a <see cref="BranchFeature"/>; the parameter is <c>features</c>.</exception>
    public static int BitsOf(IEnumerable<BranchFeature> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        return features.Aggregate(0, (bits, feature) => bits | Bit(feature));
    }

    /// <summary>The set the bits hold, in numeric order. Bits no feature owns are ignored.</summary>
    public static IReadOnlyList<BranchFeature> FromBits(int bits) => [.. All.Where(f => Has(bits, f))];

    /// <summary>
    /// What a diner and the public page see as on: the allowed set, narrowed by the owner's own switch wherever one
    /// exists. Turning a feature off at the platform never clears the owner's switch, so the two are kept apart and
    /// combined here.
    /// </summary>
    /// <param name="allowedBits">The branch's <c>AllowedFeatureBits</c>.</param>
    /// <param name="switches">The owner's switches.</param>
    /// <returns>
    /// <see cref="BranchFeature.AppBookings"/> when allowed, bookings switched on and the reservation policy saved by
    /// somebody (K9); <see cref="BranchFeature.WebBookings"/> when allowed and bookings switched on;
    /// <see cref="BranchFeature.PreOrders"/> when allowed and accepted;
    /// <see cref="BranchFeature.Takeaway"/> when allowed and enabled; <see cref="BranchFeature.GuestMessages"/> when
    /// allowed and guests may write; every other feature when allowed.
    /// </returns>
    public static IReadOnlyList<BranchFeature> Effective(int allowedBits, OwnerSwitches switches)
    {
        var app = Has(allowedBits, BranchFeature.AppBookings) && switches.AcceptsWebBookings && switches.ReservationPolicyReviewed;
        var web = Has(allowedBits, BranchFeature.WebBookings) && switches.AcceptsWebBookings;

        return [.. All.Where(f => Has(allowedBits, f) && f switch
        {
            BranchFeature.AppBookings => app,
            BranchFeature.WebBookings => web,
            BranchFeature.PreOrders => switches.AcceptsPreOrders,
            BranchFeature.Takeaway => switches.TakeawayEnabled,
            BranchFeature.GuestMessages => switches.GuestsCanMessage,
            _ => true,
        })];
    }

    /// <summary>
    /// Checks a set the platform admin asks for: every value defined, and <see cref="BranchFeature.PreOrders"/> only
    /// with a booking channel to pre-order on.
    /// </summary>
    /// <returns>The set's bits.</returns>
    /// <exception cref="FieldValidationException">On <c>features</c>: an undefined value, or pre-orders with no booking channel.</exception>
    public static int Validate(IEnumerable<BranchFeature>? features)
    {
        if (features is null)
        {
            throw new FieldValidationException(
                [new FieldViolation(FieldName, "The list of features is required; send [] to block every one.", FieldBounds.Required)]);
        }

        var list = features.ToList();

        if (list.Where(f => !Enum.IsDefined(f)).Select(f => (int?)f).FirstOrDefault() is { } undefined)
        {
            throw new FieldValidationException(
                [new FieldViolation(FieldName, $"{undefined} is not a feature; they are 1 to {All.Count}.", FieldBounds.Range, 1, All.Count, undefined)]);
        }

        var bits = BitsOf(list);

        if (Has(bits, BranchFeature.PreOrders)
            && !Has(bits, BranchFeature.AppBookings)
            && !Has(bits, BranchFeature.WebBookings))
        {
            throw new FieldValidationException(
                [new FieldViolation(
                    FieldName,
                    "Pre-orders ride on bookings: allow app bookings or web bookings with them.",
                    FieldBounds.Conflict,
                    Value: (int)BranchFeature.PreOrders)]);
        }

        return bits;
    }

    /// <summary>The wire name every refusal of a set is keyed on.</summary>
    public const string FieldName = "features";

    private static BranchFeature Defined(BranchFeature feature) =>
        Enum.IsDefined(feature)
            ? feature
            : throw new ArgumentOutOfRangeException(FieldName, feature, $"Not a defined {nameof(BranchFeature)} value.");

    /// <summary>The owner's switches that narrow an allowed feature. Read off the branch row.</summary>
    /// <param name="AcceptsWebBookings">Online bookings switched on.</param>
    /// <param name="ReservationPolicyReviewed">Somebody saved the reservation policy (K9: the app books only then).</param>
    /// <param name="AcceptsPreOrders">Pre-orders accepted.</param>
    /// <param name="TakeawayEnabled">Takeaway switched on.</param>
    /// <param name="GuestsCanMessage">Guests may write to the venue.</param>
    public sealed record OwnerSwitches(
        bool AcceptsWebBookings,
        bool ReservationPolicyReviewed,
        bool AcceptsPreOrders,
        bool TakeawayEnabled,
        bool GuestsCanMessage);
}
