namespace Yalla.Domain.Common;

/// <summary>
/// Whole-dram arithmetic, and the only place rounding happens.
/// </summary>
/// <remarks>
/// <para>
/// Every amount in Yalla is a <see cref="long"/> count of whole Armenian dram. The dram has no
/// subunit in practice, and a decimal or a float invites drift that shows up as a bill three
/// people cannot settle. The only decimals in the system are the stored service-charge and
/// discount <i>percentages</i>, and both are turned into whole dram here.
/// </para>
/// <para>
/// <b>Rounding is half-up, and happens once.</b> Half-up because it is what a person does on paper
/// and what a diner checking the arithmetic expects; .NET's default is banker's rounding, which is
/// correct for statistics and surprising on a receipt. Once, because rounding per item and then
/// summing gives a different answer from summing and then rounding, and only one of those can
/// match what the guest is shown.
/// </para>
/// </remarks>
public static class Money
{
    /// <summary>
    /// <paramref name="percent"/> of <paramref name="amountAmd"/>, rounded half-up to whole dram.
    /// </summary>
    public static long PercentOf(long amountAmd, decimal percent)
    {
        if (amountAmd == 0L || percent == 0m)
        {
            return 0L;
        }

        var exact = amountAmd * percent / 100m;

        return (long)decimal.Round(exact, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// <paramref name="numerator"/>/<paramref name="denominator"/> of <paramref name="amountAmd"/>,
    /// rounded half-up. Used for a participant's pro-rata slice of the service charge.
    /// </summary>
    public static long ProRata(long amountAmd, long numerator, long denominator)
    {
        if (denominator <= 0L || numerator <= 0L || amountAmd == 0L)
        {
            return 0L;
        }

        var exact = (decimal)amountAmd * numerator / denominator;

        return (long)decimal.Round(exact, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Splits <paramref name="amountAmd"/> into <paramref name="ways"/> whole-dram parts that sum
    /// back to it exactly, giving the remainder to the first part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1,000 AMD three ways is 334 + 333 + 333, not 333 three times. The naive version loses a dram
    /// and the table cannot close: the shares add up to less than the total, the last person's card
    /// is declined for a dram, and a waiter fixes it by hand while the guests watch.
    /// </para>
    /// <para>
    /// The extra dram goes to the <b>first</b> part, and callers order the participants so that the
    /// first is the host. Somebody has to carry it, and the host is the one person on the tab who
    /// has agreed to be responsible for it.
    /// </para>
    /// </remarks>
    public static long[] SplitEvenly(long amountAmd, int ways)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ways, 1);

        var parts = new long[ways];

        if (amountAmd == 0L)
        {
            return parts;
        }

        var each = amountAmd / ways;
        var remainder = amountAmd - (each * ways);

        for (var i = 0; i < ways; i++)
        {
            parts[i] = each;
        }

        parts[0] += remainder;

        return parts;
    }
}
