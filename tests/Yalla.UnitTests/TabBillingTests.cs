using Yalla.Domain.Billing;
using Yalla.Domain.Common;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests;

/// <summary>
/// The bill arithmetic, at its edges and over a few thousand random tabs.
/// </summary>
/// <remarks>
/// Pure functions, so these run in milliseconds and need no database. That is the point of
/// <see cref="TabBilling"/> being a pure function: the bug worth finding here is a dram lost to
/// rounding, which is invisible in any example a person writes by hand and appears the first time
/// three people split a bill with an odd service charge.
/// </remarks>
public sealed class TabBillingTests
{
    // Fixed rather than generated, so a worked example that goes into docs/billing.md produces the
    // same numbers every run. Real participants are UUIDv7 and their relative order is join order.
    private static readonly Guid Host = new("00000000-0000-0000-0000-000000000001");

    private static readonly Guid GuestA = new("00000000-0000-0000-0000-000000000002");

    private static readonly Guid GuestB = new("00000000-0000-0000-0000-000000000003");

    // ------------------------------------------------------------ the worked example from docs/billing.md

    /// <summary>
    /// The three-person example in <c>docs/billing.md</c>, to the dram. If this test and that
    /// document ever disagree, one of them is lying to a venue.
    /// </summary>
    [Fact]
    public void The_documented_three_person_example_comes_out_to_the_dram()
    {
        var wine = Guid.CreateVersion7();

        var lines = new List<BillingLine>
        {
            Line(owner: Host, price: 3_200, quantity: 2),                       // 6,400
            Line(owner: GuestA, price: 4_500, quantity: 1),                     // 4,500
            Line(owner: GuestB, price: 2_800, quantity: 1),                     // 2,800
            Shared(wine, price: 9_500, sharers: [Host, GuestA, GuestB]),        // 9,500
        };

        var bill = TabBilling.Compute(lines, [], Everyone(), serviceChargePercent: 10m, paidAmd: 0L);

        Assert.Equal(23_200L, bill.SubtotalAmd);
        Assert.Equal(2_320L, bill.ServiceChargeAmd);
        Assert.Equal(25_520L, bill.TotalAmd);

        // 9,500 three ways is 3,167 + 3,167 + 3,166 - the odd dram to the host, who is first.
        var host = bill.Shares.Single(s => s.ParticipantId == Host);
        var a = bill.Shares.Single(s => s.ParticipantId == GuestA);
        var b = bill.Shares.Single(s => s.ParticipantId == GuestB);

        Assert.Equal(6_400L, host.OwnItemsAmd);
        Assert.Equal(3_168L, host.SharedItemsAmd);
        Assert.Equal(3_166L, a.SharedItemsAmd);
        Assert.Equal(3_166L, b.SharedItemsAmd);

        Assert.Equal(9_568L, host.PersonalAmd);
        Assert.Equal(7_666L, a.PersonalAmd);
        Assert.Equal(5_966L, b.PersonalAmd);

        Assert.Equal(957L, host.ServiceChargeAmd);
        Assert.Equal(767L, a.ServiceChargeAmd);
        Assert.Equal(596L, b.ServiceChargeAmd);

        Assert.Equal(10_525L, host.ShareAmd);
        Assert.Equal(8_433L, a.ShareAmd);
        Assert.Equal(6_562L, b.ShareAmd);

        Assert.Equal(bill.TotalAmd, bill.Shares.Sum(s => s.ShareAmd));
    }

    // ------------------------------------------------------------ 8. a comp reduces the service charge with it

    [Fact]
    public void A_comp_reduces_the_service_charge_proportionally()
    {
        var soup = Guid.CreateVersion7();

        var lines = new List<BillingLine>
        {
            Line(owner: Host, price: 10_000, quantity: 1),
            Line(lineId: soup, owner: GuestA, price: 5_000, quantity: 1),
        };

        var before = TabBilling.Compute(lines, [], Everyone(), 10m, 0L);

        Assert.Equal(15_000L, before.SubtotalAmd);
        Assert.Equal(1_500L, before.ServiceChargeAmd);

        // The soup was cold. The manager comps it - and its service charge goes with it, because
        // charging service on a dish you have just apologised for is the opposite of an apology.
        var after = TabBilling.Compute(
            lines, [new BillingAdjustment(soup, null, 5_000L)], Everyone(), 10m, 0L);

        Assert.Equal(10_000L, after.SubtotalAmd);
        Assert.Equal(1_000L, after.ServiceChargeAmd);
        Assert.Equal(11_000L, after.TotalAmd);

        // And the guest whose dish was comped owes nothing for it.
        Assert.Equal(0L, after.Shares.Single(s => s.ParticipantId == GuestA).ShareAmd);
    }

    [Fact]
    public void A_percentage_off_the_whole_tab_is_taken_before_the_service_charge()
    {
        var lines = new List<BillingLine> { Line(owner: Host, price: 20_000, quantity: 1) };

        var bill = TabBilling.Compute(
            lines, [new BillingAdjustment(null, 10m, null)], Everyone(), 10m, 0L);

        Assert.Equal(18_000L, bill.SubtotalAmd);
        Assert.Equal(1_800L, bill.ServiceChargeAmd);
        Assert.Equal(19_800L, bill.TotalAmd);
    }

    [Fact]
    public void An_adjustment_larger_than_what_it_applies_to_cannot_drive_the_bill_negative()
    {
        var dish = Guid.CreateVersion7();
        var lines = new List<BillingLine> { Line(lineId: dish, owner: Host, price: 1_500, quantity: 1) };

        var bill = TabBilling.Compute(
            lines, [new BillingAdjustment(dish, null, 5_000L)], Everyone(), 10m, 0L);

        Assert.Equal(0L, bill.SubtotalAmd);
        Assert.Equal(0L, bill.ServiceChargeAmd);
        Assert.Equal(0L, bill.TotalAmd);
        Assert.All(bill.Shares, s => Assert.Equal(0L, s.ShareAmd));
    }

    [Fact]
    public void A_voided_line_counts_zero_and_leaves_the_rest_of_the_bill_intact()
    {
        var lines = new List<BillingLine>
        {
            Line(owner: Host, price: 4_000, quantity: 1),
            Line(owner: GuestA, price: 6_000, quantity: 1) with { IsVoided = true },
        };

        var bill = TabBilling.Compute(lines, [], Everyone(), 10m, 0L);

        Assert.Equal(4_000L, bill.SubtotalAmd);
        Assert.Equal(400L, bill.ServiceChargeAmd);
        Assert.Equal(0L, bill.Shares.Single(s => s.ParticipantId == GuestA).ShareAmd);
    }

    // ------------------------------------------------------------ 12. removed participants

    [Fact]
    public void A_removed_participants_items_fall_to_the_host_and_stay_on_the_bill()
    {
        var lines = new List<BillingLine>
        {
            Line(owner: Host, price: 3_000, quantity: 1),
            Line(owner: GuestA, price: 7_000, quantity: 1),
        };

        var participants = new List<BillingParticipant>
        {
            new(Host, IsHost: true, ParticipantStatus.Approved),
            new(GuestA, IsHost: false, ParticipantStatus.Removed),
        };

        var bill = TabBilling.Compute(lines, [], participants, 10m, 0L);

        // Still on the bill: the food was eaten.
        Assert.Equal(10_000L, bill.SubtotalAmd);

        var host = bill.Shares.Single(s => s.ParticipantId == Host);

        Assert.Equal(7_000L, host.AbsorbedFromRemovedAmd);
        Assert.Equal(7_000L, bill.AbsorbedFromRemovedAmd);
        Assert.Equal(11_000L, host.ShareAmd);
        Assert.Equal(0L, bill.Shares.Single(s => s.ParticipantId == GuestA).ShareAmd);
        Assert.Equal(bill.TotalAmd, bill.Shares.Sum(s => s.ShareAmd));
    }

    [Fact]
    public void A_removed_participants_slice_of_a_shared_line_falls_to_the_host_too()
    {
        var bottle = Guid.CreateVersion7();

        var lines = new List<BillingLine> { Shared(bottle, 9_000, [Host, GuestA, GuestB]) };

        var participants = new List<BillingParticipant>
        {
            new(Host, IsHost: true, ParticipantStatus.Approved),
            new(GuestA, IsHost: false, ParticipantStatus.Approved),
            new(GuestB, IsHost: false, ParticipantStatus.Removed),
        };

        var bill = TabBilling.Compute(lines, [], participants, 0m, 0L);

        var host = bill.Shares.Single(s => s.ParticipantId == Host);

        Assert.Equal(3_000L, host.SharedItemsAmd);
        Assert.Equal(3_000L, host.AbsorbedFromRemovedAmd);
        Assert.Equal(6_000L, host.ShareAmd);
        Assert.Equal(3_000L, bill.Shares.Single(s => s.ParticipantId == GuestA).ShareAmd);
        Assert.Equal(bill.TotalAmd, bill.Shares.Sum(s => s.ShareAmd));
    }

    [Fact]
    public void A_table_attributed_line_splits_across_everyone_snapshotted_on_it()
    {
        var mystery = Guid.CreateVersion7();

        var lines = new List<BillingLine> { Shared(mystery, 1_000, [Host, GuestA, GuestB]) };

        var bill = TabBilling.Compute(lines, [], Everyone(), 0m, 0L);

        // 1,000 three ways: the odd dram to the host.
        Assert.Equal(334L, bill.Shares.Single(s => s.ParticipantId == Host).ShareAmd);
        Assert.Equal(333L, bill.Shares.Single(s => s.ParticipantId == GuestA).ShareAmd);
        Assert.Equal(333L, bill.Shares.Single(s => s.ParticipantId == GuestB).ShareAmd);
        Assert.Equal(1_000L, bill.Shares.Sum(s => s.ShareAmd));
    }

    [Fact]
    public void Nobody_is_ever_handed_a_negative_share()
    {
        // The case the naive "residue to the host" rule got wrong: a host who ordered almost
        // nothing while everybody else rounded up.
        var lines = new List<BillingLine>
        {
            Line(owner: Host, price: 1, quantity: 1),
            Line(owner: GuestA, price: 3_333, quantity: 1),
            Line(owner: GuestB, price: 3_333, quantity: 1),
        };

        var bill = TabBilling.Compute(lines, [], Everyone(), 7.5m, 0L);

        Assert.All(bill.Shares, s => Assert.True(s.ShareAmd >= 0L, $"{s.ParticipantId} owes {s.ShareAmd}."));
        Assert.Equal(bill.TotalAmd, bill.Shares.Sum(s => s.ShareAmd));
    }

    // ------------------------------------------------------------ 9. the property test

    /// <summary>
    /// <b>Test 9.</b> Over randomised tabs - any number of participants, shared and unshared lines,
    /// adjustments, any service-charge percent - the shares always sum to the total exactly.
    /// </summary>
    /// <remarks>
    /// This is the test that finds the rounding bug before a venue does. An off-by-one dram means
    /// the last person to pay is short, the tab will not close, and a waiter fixes it by hand while
    /// the table watches. Seeded, so a failure is reproducible: the seed is printed in the message.
    /// </remarks>
    [Fact]
    public void Shares_always_sum_exactly_to_the_total()
    {
        for (var seed = 0; seed < 3_000; seed++)
        {
            var random = new Random(seed);
            var (lines, adjustments, participants, percent) = RandomTab(random);

            var bill = TabBilling.Compute(lines, adjustments, participants, percent, paidAmd: 0L);

            var summed = bill.Shares.Sum(s => s.ShareAmd);

            Assert.True(
                summed == bill.TotalAmd,
                $"Seed {seed}: shares sum to {summed} but the total is {bill.TotalAmd} "
                + $"({participants.Count} participants, {lines.Count} lines, {percent}% service).");

            Assert.True(
                bill.Shares.All(s => s.ShareAmd >= 0L),
                $"Seed {seed}: somebody was handed a negative share.");

            Assert.True(
                bill.SubtotalAmd >= 0L && bill.ServiceChargeAmd >= 0L,
                $"Seed {seed}: the bill went negative.");

            // And the parts of each share agree with the whole.
            foreach (var share in bill.Shares)
            {
                Assert.True(
                    share.ShareAmd == share.PersonalAmd + share.ServiceChargeAmd,
                    $"Seed {seed}: {share.ParticipantId} has parts that do not add up.");
            }
        }
    }

    /// <summary>The same invariant with the service charge at its extremes.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12.5)]
    [InlineData(100)]
    public void Shares_sum_to_the_total_at_every_service_charge(decimal percent)
    {
        for (var seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            var (lines, adjustments, participants, _) = RandomTab(random);

            var bill = TabBilling.Compute(lines, adjustments, participants, percent, 0L);

            Assert.True(
                bill.Shares.Sum(s => s.ShareAmd) == bill.TotalAmd,
                $"Seed {seed} at {percent}%: shares do not sum to the total.");
        }
    }

    // ------------------------------------------------------------ Money, at its edges

    [Theory]
    [InlineData(1000, 3, new long[] { 334, 333, 333 })]
    [InlineData(1000, 1, new long[] { 1000 })]
    [InlineData(0, 4, new long[] { 0, 0, 0, 0 })]
    [InlineData(7, 2, new long[] { 4, 3 })]
    public void An_even_split_always_sums_back_to_what_went_in(long amount, int ways, long[] expected)
    {
        var parts = Money.SplitEvenly(amount, ways);

        Assert.Equal(expected, parts);
        Assert.Equal(amount, parts.Sum());
    }

    /// <summary>Half-up, not banker's. 2,500 at 10% is 250; 50 at 5% is 3, not 2.</summary>
    [Theory]
    [InlineData(2500, 10, 250)]
    [InlineData(50, 5, 3)]
    [InlineData(150, 5, 8)]
    [InlineData(0, 10, 0)]
    [InlineData(1000, 0, 0)]
    public void Percentages_round_half_up(long amount, decimal percent, long expected) =>
        Assert.Equal(expected, Money.PercentOf(amount, percent));

    // ------------------------------------------------------------ helpers

    private static List<BillingParticipant> Everyone() =>
    [
        new(Host, IsHost: true, ParticipantStatus.Approved),
        new(GuestA, IsHost: false, ParticipantStatus.Approved),
        new(GuestB, IsHost: false, ParticipantStatus.Approved),
    ];

    private static BillingLine Line(Guid owner, long price, int quantity, Guid? lineId = null) =>
        new(lineId ?? Guid.CreateVersion7(), owner, price, quantity, false, false, []);

    private static BillingLine Shared(Guid lineId, long price, IReadOnlyList<Guid> sharers) =>
        new(lineId, null, price, 1, false, true, sharers);

    private static (List<BillingLine> Lines,
        List<BillingAdjustment> Adjustments,
        List<BillingParticipant> Participants,
        decimal Percent) RandomTab(Random random)
    {
        var count = random.Next(1, 7);

        var participants = Enumerable.Range(0, count)
            .Select(i => new BillingParticipant(
                Guid.CreateVersion7(),
                IsHost: i == 0,
                Status: random.Next(10) switch
                {
                    0 => ParticipantStatus.Removed,
                    1 => ParticipantStatus.PendingApproval,
                    _ => ParticipantStatus.Approved,
                }))
            .ToList();

        var ids = participants.Select(p => p.ParticipantId).ToList();
        var lines = new List<BillingLine>();

        for (var i = 0; i < random.Next(0, 12); i++)
        {
            var lineId = Guid.CreateVersion7();
            var price = random.Next(1, 25_000);
            var quantity = random.Next(1, 5);
            var voided = random.Next(8) == 0;

            if (random.Next(3) == 0)
            {
                // Shared or table-attributed: a random non-empty subset of the table.
                var sharers = ids.Where(_ => random.Next(2) == 0).ToList();

                if (sharers.Count == 0)
                {
                    sharers.Add(ids[random.Next(ids.Count)]);
                }

                lines.Add(new BillingLine(lineId, null, price, quantity, voided, true, sharers));
            }
            else
            {
                lines.Add(new BillingLine(
                    lineId, ids[random.Next(ids.Count)], price, quantity, voided, false, []));
            }
        }

        var adjustments = new List<BillingAdjustment>();

        for (var i = 0; i < random.Next(0, 4); i++)
        {
            var target = random.Next(2) == 0 && lines.Count > 0
                ? lines[random.Next(lines.Count)].LineId
                : (Guid?)null;

            adjustments.Add(random.Next(2) == 0
                ? new BillingAdjustment(target, Math.Round((decimal)random.NextDouble() * 100m, 2), null)
                : new BillingAdjustment(target, null, random.Next(1, 30_000)));
        }

        var percent = Math.Round((decimal)random.NextDouble() * 20m, 2);

        return (lines, adjustments, participants, percent);
    }
}
