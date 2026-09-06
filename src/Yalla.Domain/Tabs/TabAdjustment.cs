using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.Domain.Tabs;

/// <summary>
/// Money taken off a bill by a manager: a discount, or a dish comped because something went wrong.
/// </summary>
/// <remarks>
/// <para>
/// A separate entity rather than a nullable discount column on the tab, because both of the
/// everyday cases need more than one number. A manager comps <i>one dish</i> and discounts the
/// <i>whole tab</i> in the same evening, and each one needs its own reason attached to its own
/// target. A single column cannot hold two of them, and the moment it has to, somebody edits the
/// line's price instead - which breaks the snapshot the whole bill rests on.
/// </para>
/// <para>
/// Adjustments are <b>visible to the diner with their reason</b>, exactly like voids. A number that
/// silently drops off a bill somebody is watching on their phone reads as a mistake; "-1,500 AMD,
/// the soup was cold" reads as the venue behaving well.
/// </para>
/// <para>
/// Reversal is <see cref="Void"/>, never deletion. A comp applied to the wrong table is itself part
/// of what happened, and reporting that cannot see it cannot answer why the evening's takings are
/// short.
/// </para>
/// </remarks>
public sealed class TabAdjustment : Entity
{
    public Guid TabId { get; private set; }

    public Tab Tab { get; private set; } = null!;

    /// <summary>The line this applies to, or null for the whole tab.</summary>
    public Guid? TabOrderLineId { get; private set; }

    public TabOrderLine? TabOrderLine { get; private set; }

    public AdjustmentKind Kind { get; private set; }

    /// <summary>Percentage off, 0-100. Exactly one of this and <see cref="AmountAmd"/> is set.</summary>
    public decimal? Percent { get; private set; }

    /// <summary>A flat amount off, in whole dram. Exactly one of this and <see cref="Percent"/> is set.</summary>
    public long? AmountAmd { get; private set; }

    /// <summary>Why. Shown to the diner, so it is written for them and not for the back office.</summary>
    public string Reason { get; private set; } = null!;

    public Guid CreatedByStaffId { get; private set; }

    public StaffMember CreatedByStaff { get; private set; } = null!;

    /// <summary>Set when the adjustment was reversed. A reversed adjustment stops counting but stays.</summary>
    public DateTime? VoidedAtUtc { get; private set; }

    public Guid? VoidedByStaffId { get; private set; }

    public bool IsActive => VoidedAtUtc is null;

    private TabAdjustment()
    {
    }

    private TabAdjustment(
        Guid tabId,
        Guid? tabOrderLineId,
        AdjustmentKind kind,
        decimal? percent,
        long? amountAmd,
        string reason,
        Guid createdByStaffId,
        DateTime createdAtUtc)
        : base(Guid.CreateVersion7())
    {
        if (percent is null == (amountAmd is null))
        {
            throw new ArgumentException(
                "An adjustment is either a percentage or a flat amount, never both and never neither.",
                nameof(percent));
        }

        if (percent is { } p && p is <= 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percent), p, "A percentage adjustment must be above 0 and at most 100.");
        }

        if (amountAmd is { } a && a <= 0L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amountAmd), a, "A flat adjustment must be for a positive amount.");
        }

        TabId = Guard.NotEmpty(tabId, nameof(tabId));
        TabOrderLineId = tabOrderLineId;
        Kind = Guard.Defined(kind, nameof(kind));
        Percent = percent is { } value ? decimal.Round(value, 2) : null;
        AmountAmd = amountAmd;
        Reason = Guard.NotBlank(reason, nameof(reason), FieldLengths.Reason);
        CreatedByStaffId = Guard.NotEmpty(createdByStaffId, nameof(createdByStaffId));
        StampCreatedAt(createdAtUtc);
    }

    /// <summary>A percentage off one line, or off the whole tab when the line is null.</summary>
    public static TabAdjustment ByPercent(
        Guid tabId,
        Guid? tabOrderLineId,
        AdjustmentKind kind,
        decimal percent,
        string reason,
        Guid createdByStaffId,
        DateTime createdAtUtc) =>
        new(tabId, tabOrderLineId, kind, percent, null, reason, createdByStaffId, createdAtUtc);

    /// <summary>A flat amount off one line, or off the whole tab when the line is null.</summary>
    public static TabAdjustment ByAmount(
        Guid tabId,
        Guid? tabOrderLineId,
        AdjustmentKind kind,
        long amountAmd,
        string reason,
        Guid createdByStaffId,
        DateTime createdAtUtc) =>
        new(tabId, tabOrderLineId, kind, null, amountAmd, reason, createdByStaffId, createdAtUtc);

    /// <summary>
    /// What this takes off, given what it applies to.
    /// </summary>
    /// <remarks>
    /// Clamped to <paramref name="baseAmd"/>: a 2,000 AMD comp on a 1,500 AMD dish takes off 1,500,
    /// not 2,000. Without the clamp a generous manager could drive a subtotal negative, and every
    /// number downstream - the service charge, the shares, the remaining balance - would then be
    /// arithmetic nobody can explain to a diner.
    /// </remarks>
    public long ReductionOn(long baseAmd) =>
        IsActive ? ReductionFor(Percent, AmountAmd, baseAmd) : 0L;

    /// <summary>
    /// The same arithmetic over loose values, for a read model that never materialises the entity.
    /// </summary>
    /// <remarks>
    /// <see cref="ReductionOn"/> delegates to this rather than repeating it. The diner's bill shows
    /// each adjustment's reduction beside a total computed by <c>TabBilling</c>, and two
    /// implementations of the rounding and the clamp would put a discount on screen that does not
    /// add up to the number under it.
    /// </remarks>
    public static long ReductionFor(decimal? percent, long? amountAmd, long baseAmd)
    {
        if (baseAmd <= 0L)
        {
            return 0L;
        }

        var raw = percent is { } value
            ? Money.PercentOf(baseAmd, value)
            : amountAmd ?? 0L;

        return Math.Min(raw, baseAmd);
    }

    public void Void(DateTime voidedAtUtc, Guid voidedByStaffId)
    {
        if (!IsActive)
        {
            throw new DomainStateException("This adjustment has already been reversed.");
        }

        VoidedAtUtc = Guard.NotLocalTime(voidedAtUtc, nameof(voidedAtUtc));
        VoidedByStaffId = Guard.NotEmpty(voidedByStaffId, nameof(voidedByStaffId));
    }
}
