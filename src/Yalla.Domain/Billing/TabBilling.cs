using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.Domain.Billing;

/// <summary>One order line, reduced to what the arithmetic needs.</summary>
/// <param name="LineId">The line, so an adjustment can name it.</param>
/// <param name="OwnerParticipantId">
/// Who ordered it. Null when the line belongs to the table - a waiter keyed in a spoken order and
/// could not say who asked for it.
/// </param>
/// <param name="UnitPriceAmd">The price snapshotted when it was ordered.</param>
/// <param name="Quantity">How many.</param>
/// <param name="IsVoided">Voided lines stay on the bill and count zero.</param>
/// <param name="IsSplitAcrossParticipants">True for a shared bottle or a table-attributed line.</param>
/// <param name="ShareParticipantIds">
/// Who was at the table when it was ordered. A snapshot: the guest who arrived for dessert is not
/// on the starters, and the guest who left early still owes for them.
/// </param>
public sealed record BillingLine(
    Guid LineId,
    Guid? OwnerParticipantId,
    long UnitPriceAmd,
    int Quantity,
    bool IsVoided,
    bool IsSplitAcrossParticipants,
    IReadOnlyList<Guid> ShareParticipantIds)
{
    /// <summary>What the line comes to before any adjustment. Zero once voided.</summary>
    public long GrossAmd => IsVoided ? 0L : UnitPriceAmd * Quantity;
}

/// <summary>A discount or a comp, reduced to what the arithmetic needs.</summary>
/// <param name="LineId">The line it applies to, or null for the whole tab.</param>
/// <param name="Percent">Percentage off. Exactly one of this and <paramref name="AmountAmd"/> is set.</param>
/// <param name="AmountAmd">Flat amount off, in whole dram.</param>
public sealed record BillingAdjustment(Guid? LineId, decimal? Percent, long? AmountAmd)
{
    /// <summary>What this takes off a base amount, never more than the base itself.</summary>
    public long ReductionOn(long baseAmd)
    {
        if (baseAmd <= 0L)
        {
            return 0L;
        }

        var raw = Percent is { } percent ? Money.PercentOf(baseAmd, percent) : AmountAmd ?? 0L;

        return Math.Min(raw, baseAmd);
    }
}

/// <summary>One person on the tab, for splitting purposes.</summary>
/// <param name="ParticipantId">The participant row.</param>
/// <param name="IsHost">Whether they host. The host carries every rounding remainder.</param>
/// <param name="Status">Approved, pending, or removed.</param>
/// <param name="PaidAmd">What they have settled so far. Reported, never netted off the share.</param>
public sealed record BillingParticipant(
    Guid ParticipantId,
    bool IsHost,
    ParticipantStatus Status,
    long PaidAmd = 0L);

/// <summary>What one person owes of the bill.</summary>
/// <param name="ParticipantId">The participant.</param>
/// <param name="OwnItemsAmd">Their own unshared lines, after any adjustment on those lines.</param>
/// <param name="SharedItemsAmd">Their slice of shared and table-attributed lines.</param>
/// <param name="AbsorbedFromRemovedAmd">
/// What fell to them because somebody who was removed from the tab cannot pay for what they ate.
/// Non-zero only for the host, and reported rather than folded in silently.
/// </param>
/// <param name="PersonalAmd">Their part of the subtotal: own plus shared plus absorbed, less their share of any tab-wide discount.</param>
/// <param name="ServiceChargeAmd">Their pro-rata slice of the service charge.</param>
/// <param name="ShareAmd">What they owe in total. These sum to the tab total exactly.</param>
/// <param name="PaidAmd">What they have already settled.</param>
public sealed record ParticipantShare(
    Guid ParticipantId,
    long OwnItemsAmd,
    long SharedItemsAmd,
    long AbsorbedFromRemovedAmd,
    long PersonalAmd,
    long ServiceChargeAmd,
    long ShareAmd,
    long PaidAmd);

/// <summary>The bill, computed.</summary>
/// <param name="SubtotalAmd">Lines less adjustments.</param>
/// <param name="ServiceChargeAmd">Charged on the post-discount subtotal.</param>
/// <param name="TotalAmd">Subtotal plus service charge.</param>
/// <param name="PaidAmd">Settled so far. Tips are excluded.</param>
/// <param name="RemainingAmd">Still owed.</param>
/// <param name="Shares">One per participant still relevant to the split, host first.</param>
/// <param name="AbsorbedFromRemovedAmd">Total that fell to the host from removed participants.</param>
public sealed record TabBill(
    long SubtotalAmd,
    long ServiceChargeAmd,
    long TotalAmd,
    long PaidAmd,
    long RemainingAmd,
    IReadOnlyList<ParticipantShare> Shares,
    long AbsorbedFromRemovedAmd);

/// <summary>
/// The whole of Yalla's money arithmetic, as one pure function.
/// </summary>
/// <remarks>
/// <para>
/// <b>No database, no clock, no services.</b> Everything here is a function of its arguments, which
/// is what makes the property test possible: generate a few thousand random tabs and assert the
/// shares always sum to the total. That test is worth more than any amount of review, because the
/// bug it looks for - a dram lost to rounding - is invisible in every example anyone writes by hand
/// and appears the first time three people split a bill with an odd service charge.
/// </para>
/// <para>
/// The order of operations is the part that gets decided by accident elsewhere, so it is stated
/// here:
/// </para>
/// <list type="number">
/// <item>Each line's gross, with voided lines counting zero.</item>
/// <item>Line-level adjustments come off their own line.</item>
/// <item>Tab-level adjustments come off the sum.</item>
/// <item><b>Service charge is computed on what is left</b> - a comped dish comps its service charge
/// with it, which is what a manager means by comping a dish.</item>
/// <item>Rounding is half-up and happens <b>once</b>, at the service-charge line. Never per item:
/// rounding each item and summing gives a different answer from summing and rounding, and only one
/// of the two can match what the guest is shown.</item>
/// </list>
/// <para>
/// VAT is not a line. Armenian menu prices are VAT-inclusive, so the displayed price is the price -
/// see <c>docs/billing.md</c> for why the fiscal module will still need the decomposition.
/// </para>
/// </remarks>
public static class TabBilling
{
    /// <summary>
    /// Computes the bill and every participant's share of it.
    /// </summary>
    /// <param name="lines">Every line on the tab, voided ones included.</param>
    /// <param name="adjustments">Active discounts and comps only; reversed ones are filtered out by the caller.</param>
    /// <param name="participants">Everyone on the tab, in join order.</param>
    /// <param name="serviceChargePercent">The branch percentage snapshotted when the tab opened.</param>
    /// <param name="paidAmd">What has been settled, tips excluded.</param>
    public static TabBill Compute(
        IReadOnlyList<BillingLine> lines,
        IReadOnlyList<BillingAdjustment> adjustments,
        IReadOnlyList<BillingParticipant> participants,
        decimal serviceChargePercent,
        long paidAmd)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(adjustments);
        ArgumentNullException.ThrowIfNull(participants);

        // ---------------------------------------------------------- 1 and 2. lines, net of their own adjustments
        var netByLine = new Dictionary<Guid, long>(lines.Count);

        foreach (var line in lines)
        {
            var net = line.GrossAmd;

            foreach (var adjustment in adjustments.Where(a => a.LineId == line.LineId))
            {
                net -= adjustment.ReductionOn(net);
            }

            netByLine[line.LineId] = net;
        }

        var grossSubtotal = netByLine.Values.Sum();

        // ---------------------------------------------------------- 3. tab-wide adjustments
        var tabReduction = 0L;
        var reducible = grossSubtotal;

        foreach (var adjustment in adjustments.Where(a => a.LineId is null))
        {
            var off = adjustment.ReductionOn(reducible);
            tabReduction += off;
            reducible -= off;
        }

        var subtotal = grossSubtotal - tabReduction;

        // ---------------------------------------------------------- 4 and 5. the service charge, rounded once
        var serviceCharge = Money.PercentOf(subtotal, serviceChargePercent);
        var total = subtotal + serviceCharge;

        var shares = SplitAcrossParticipants(
            lines, netByLine, participants, grossSubtotal, tabReduction, subtotal, serviceCharge, paidAmd);

        return new TabBill(
            subtotal,
            serviceCharge,
            total,
            paidAmd,
            Math.Max(0L, total - paidAmd),
            shares,
            shares.Sum(s => s.AbsorbedFromRemovedAmd));
    }

    /// <summary>
    /// Who owes what. Every split is integer and every remainder goes to the host, so the shares
    /// sum to the total to the dram.
    /// </summary>
    /// <remarks>
    /// An off-by-one here is not a rounding curiosity: the shares add up to less than the total, the
    /// last person to pay is short a dram, the tab will not close, and a waiter sorts it out by hand
    /// while the table watches. That is the failure this function exists to make impossible.
    /// </remarks>
    private static IReadOnlyList<ParticipantShare> SplitAcrossParticipants(
        IReadOnlyList<BillingLine> lines,
        Dictionary<Guid, long> netByLine,
        IReadOnlyList<BillingParticipant> participants,
        long grossSubtotal,
        long tabReduction,
        long subtotal,
        long serviceCharge,
        long paidAmd)
    {
        // Host first, in every ordering, because the host carries every remainder. Someone has to,
        // and the host is the one person who has agreed to be responsible for the tab.
        //
        // After the host, join order - LINQ's OrderBy is stable, so this is what leaving the second
        // key off does. Sorting by ParticipantId instead would look equivalent and is not: these are
        // UUIDv7 values whose leading bytes are all the same millisecond, so the comparison falls
        // through to random bits and the odd dram lands on a different guest each time the same
        // bill is computed. Whoever joined first is both deterministic and explainable to a table.
        var ordered = participants
            .OrderByDescending(p => p.IsHost)
            .ToList();

        if (ordered.Count == 0)
        {
            return [];
        }

        // Where an unattributable amount lands: the host, or the first approved person when there
        // is no host, or simply the first person on a tab where nobody is approved yet.
        var absorber =
            ordered.FirstOrDefault(p => p.IsHost)
            ?? ordered.FirstOrDefault(p => p.Status == ParticipantStatus.Approved)
            ?? ordered[0];

        var own = ordered.ToDictionary(p => p.ParticipantId, _ => 0L);
        var shared = ordered.ToDictionary(p => p.ParticipantId, _ => 0L);
        var absorbed = ordered.ToDictionary(p => p.ParticipantId, _ => 0L);

        var onTab = ordered.ToDictionary(p => p.ParticipantId, p => p);

        foreach (var line in lines)
        {
            var net = netByLine[line.LineId];

            if (net == 0L)
            {
                continue;
            }

            if (!line.IsSplitAcrossParticipants)
            {
                // One person's item. If they are gone - or were never really on the tab - the food
                // was still eaten and somebody has to owe for it.
                var owner = line.OwnerParticipantId;

                if (owner is { } id && onTab.TryGetValue(id, out var person)
                                    && person.Status != ParticipantStatus.Removed)
                {
                    own[id] += net;
                }
                else
                {
                    absorbed[absorber.ParticipantId] += net;
                }

                continue;
            }

            // A shared bottle, or a line the waiter could not attribute. Split across the people
            // snapshotted on it, host first so the remainder lands in the right place.
            var payers = ordered
                .Where(p => line.ShareParticipantIds.Contains(p.ParticipantId))
                .ToList();

            var present = payers.Where(p => p.Status != ParticipantStatus.Removed).ToList();

            if (present.Count == 0)
            {
                // Everyone who shared this has since been removed. It still has to be paid for.
                absorbed[absorber.ParticipantId] += net;
                continue;
            }

            var parts = Money.SplitEvenly(net, payers.Count);

            for (var i = 0; i < payers.Count; i++)
            {
                var payer = payers[i];

                if (payer.Status == ParticipantStatus.Removed)
                {
                    absorbed[absorber.ParticipantId] += parts[i];
                }
                else
                {
                    shared[payer.ParticipantId] += parts[i];
                }
            }
        }

        // Anything the lines could not place at all - a tab whose only lines are table-attributed
        // with nobody snapshotted on them - still has to add up.
        var placed = own.Values.Sum() + shared.Values.Sum() + absorbed.Values.Sum();

        if (placed != grossSubtotal)
        {
            absorbed[absorber.ParticipantId] += grossSubtotal - placed;
        }

        // ---------------------------------------------------------- the tab-wide discount, pro rata
        var beforeDiscount = ordered
            .ToDictionary(
                p => p.ParticipantId,
                p => own[p.ParticipantId] + shared[p.ParticipantId] + absorbed[p.ParticipantId]);

        var personal = ApportionExactly(ordered, beforeDiscount, subtotal, grossSubtotal);

        // ---------------------------------------------------------- the service charge, pro rata
        var service = ApportionExactly(ordered, personal, serviceCharge, subtotal);

        return
        [
            .. ordered.Select(p => new ParticipantShare(
                p.ParticipantId,
                own[p.ParticipantId],
                shared[p.ParticipantId],
                absorbed[p.ParticipantId],
                personal[p.ParticipantId],
                service[p.ParticipantId],
                personal[p.ParticipantId] + service[p.ParticipantId],
                p.PaidAmd)),
        ];
    }

    /// <summary>
    /// Hands <paramref name="amountToShare"/> out in proportion to <paramref name="weights"/>, so
    /// that the parts sum to it exactly and none of them is negative.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The largest-remainder method: give everybody the whole-dram floor of their exact slice, then
    /// hand the few dram left over to whoever was cut by the most, one each.
    /// </para>
    /// <para>
    /// The obvious alternative - round every slice and give the residue to the host - is wrong in a
    /// way that only shows up on a real bill. Half-up rounding can hand out several dram <i>more</i>
    /// than there are to give, and subtracting that from a host who ordered a coffee produces a
    /// negative share. A diner shown "you owe -3 AMD" has found a bug, whatever the totals say.
    /// </para>
    /// <para>
    /// Ties go to whoever comes first in <paramref name="ordered"/>, which puts the host at the
    /// front. The host still carries the remainder in the case the rule was written for; they just
    /// cannot be driven below zero by it.
    /// </para>
    /// </remarks>
    private static Dictionary<Guid, long> ApportionExactly(
        IReadOnlyList<BillingParticipant> ordered,
        IReadOnlyDictionary<Guid, long> weights,
        long amountToShare,
        long weightTotal)
    {
        var result = ordered.ToDictionary(p => p.ParticipantId, _ => 0L);

        if (amountToShare == 0L || ordered.Count == 0)
        {
            return result;
        }

        if (weightTotal <= 0L)
        {
            // Nothing to weigh by - a fully comped tab that still carries a service charge, say.
            // Split it evenly rather than dropping it, host first.
            var even = Money.SplitEvenly(amountToShare, ordered.Count);

            for (var i = 0; i < ordered.Count; i++)
            {
                result[ordered[i].ParticipantId] = even[i];
            }

            return result;
        }

        var floors = new long[ordered.Count];
        var fractions = new decimal[ordered.Count];
        var allocated = 0L;

        for (var i = 0; i < ordered.Count; i++)
        {
            var exact = (decimal)amountToShare * weights[ordered[i].ParticipantId] / weightTotal;
            var floor = decimal.Floor(exact);

            floors[i] = (long)floor;
            fractions[i] = exact - floor;
            allocated += floors[i];
        }

        var leftOver = amountToShare - allocated;

        // Whoever was cut by the most gets the next dram. Position breaks ties, and the host is
        // first, so a table splitting evenly still sees the odd dram land on the host.
        var byFraction = Enumerable.Range(0, ordered.Count)
            .OrderByDescending(i => fractions[i])
            .ThenBy(i => i)
            .ToList();

        for (var n = 0; n < leftOver; n++)
        {
            floors[byFraction[n % byFraction.Count]]++;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            result[ordered[i].ParticipantId] = floors[i];
        }

        return result;
    }
}
