using Yalla.Application.Reservations;
using Yalla.Domain.Occupancy;

namespace Yalla.UnitTests;

/// <summary>
/// The door code, and the two local times that are not simply instants.
/// </summary>
public class ReservationCodeTests
{
    [Fact]
    public void A_code_never_contains_a_character_that_is_ambiguous_when_read_aloud()
    {
        // A diner reads this over the phone and a waiter reads it off a tablet, so 0/O and 1/I/L
        // are removed. Generate enough codes that a missed character would have shown up.
        var codes = Enumerable.Range(0, 2_000).Select(_ => ReservationCode.Generate()).ToList();

        Assert.All(codes, code => Assert.DoesNotContain(code, c => "0O1IL".Contains(c)));
        Assert.All(codes, code => Assert.Equal(code.ToUpperInvariant(), code));
        Assert.All(codes, code => Assert.Equal(ReservationCode.DefaultLength, code.Length));
    }

    [Fact]
    public void The_alphabet_itself_excludes_the_ambiguous_characters()
    {
        Assert.All("0O1IL", c => Assert.DoesNotContain(c, ReservationCode.Alphabet));
    }

    [Fact]
    public void Codes_are_spread_across_the_alphabet_rather_than_repeating()
    {
        // Not a uniqueness guarantee - the unique index is that - but a generator that returned
        // the same handful of codes would collide constantly, and this would catch it.
        var codes = Enumerable.Range(0, 5_000).Select(_ => ReservationCode.Generate()).ToHashSet();

        Assert.True(codes.Count > 4_990, $"Only {codes.Count} distinct codes out of 5000.");
    }

    [Fact]
    public void A_code_that_is_too_short_to_be_worth_generating_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReservationCode.Generate(3));
    }
}

/// <summary>
/// Converting the diner's local date and time into the instant everything is stored as.
/// </summary>
/// <remarks>
/// Armenia does not currently observe daylight saving, so none of the awkward cases arise in
/// Yerevan today. The zone is a per-branch setting and the next customer may be somewhere that
/// does, so a zone that <i>does</i> change is used here deliberately.
/// </remarks>
public class BranchZoneTests
{
    /// <summary>Europe/London: clocks go forward 01:00-02:00 on 29 March 2026, back on 25 October.</summary>
    private const string DaylightSavingZone = "Europe/London";

    private const string YerevanZone = "Asia/Yerevan";

    [Fact]
    public void A_Yerevan_evening_converts_to_the_instant_four_hours_earlier()
    {
        var zone = BranchZone.For(YerevanZone);

        var utc = zone.ToUtc(new DateOnly(2026, 9, 10), new TimeOnly(19, 30));

        Assert.Equal(new DateTime(2026, 9, 10, 15, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void The_conversion_round_trips()
    {
        var zone = BranchZone.For(YerevanZone);
        var localDate = new DateOnly(2026, 9, 10);
        var localTime = new TimeOnly(19, 30);

        var utc = zone.ToUtc(localDate, localTime);

        Assert.Equal(localDate, zone.LocalDateAt(utc));
        Assert.Equal(localTime, zone.LocalTimeAt(utc));
    }

    [Fact]
    public void A_local_time_the_clocks_skip_is_refused_by_name_rather_than_throwing_from_the_conversion()
    {
        // 01:30 on 29 March 2026 does not exist in London. Letting TimeZoneInfo's ArgumentException
        // escape would surface as a 500 that tells the diner nothing.
        var zone = BranchZone.For(DaylightSavingZone);

        var exception = Assert.Throws<LocalTimeDoesNotExistException>(
            () => zone.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(1, 30)));

        Assert.Equal(ReservationRejectionReason.LocalTimeDoesNotExist, exception.Reason);
        Assert.Equal(DaylightSavingZone, exception.TimeZoneId);
    }

    [Fact]
    public void The_non_throwing_form_reports_a_skipped_local_time_as_false()
    {
        // What the availability read model uses: a labelled table, not an exception.
        var zone = BranchZone.For(DaylightSavingZone);

        Assert.False(zone.TryToUtc(new DateOnly(2026, 3, 29), new TimeOnly(1, 30), out _));
        Assert.True(zone.TryToUtc(new DateOnly(2026, 3, 29), new TimeOnly(3, 30), out _));
    }

    [Fact]
    public void An_ambiguous_local_time_resolves_to_the_first_of_the_two_instants()
    {
        // 01:30 happens twice on 25 October 2026 in London. Resolving to the earlier one is a
        // decision, not an accident: .NET's default is the later instant, which would seat one
        // party an hour after another was promised the same table.
        var zone = BranchZone.For(DaylightSavingZone);
        var utc = zone.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(1, 30));

        // 01:30 BST (UTC+1) is 00:30Z; 01:30 GMT is 01:30Z. The earlier instant is the BST one.
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void A_branch_time_zone_this_host_cannot_resolve_is_our_bug_and_says_so()
    {
        var exception = Assert.Throws<BranchTimeZoneMisconfiguredException>(
            () => BranchZone.For("Mars/Olympus_Mons"));

        Assert.Equal("Mars/Olympus_Mons", exception.TimeZoneId);
    }
}

/// <summary>
/// The rolling no-show rule: what a history of missed bookings costs, and how to switch it off.
/// </summary>
public class NoShowPolicyTests
{
    [Fact]
    public void A_diner_under_the_threshold_keeps_instant_confirmation()
    {
        var policy = new NoShowPolicy { Threshold = 3 };

        Assert.False(policy.RequiresApproval(0));
        Assert.False(policy.RequiresApproval(3));
    }

    [Fact]
    public void A_diner_above_the_threshold_loses_instant_confirmation_and_nothing_else()
    {
        // The consequence is deliberately mild. Nobody is ever banned - the market is too small
        // for a wrongly-banned regular, and the count cannot tell a serial no-show from somebody
        // whose phone died three times.
        var policy = new NoShowPolicy { Threshold = 3 };

        Assert.True(policy.RequiresApproval(4));
    }

    [Fact]
    public void The_whole_rule_switches_off_with_one_setting()
    {
        // This is a recommendation, not a settled decision, so switching it off has to be trivial.
        var policy = new NoShowPolicy { Enabled = false, Threshold = 3 };

        Assert.False(policy.RequiresApproval(50));
    }

    [Fact]
    public void The_window_is_rolling_rather_than_a_lifetime_tally()
    {
        // A diner who missed three bookings two years ago has served their sentence.
        var policy = new NoShowPolicy { WindowDays = 90 };
        var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddDays(-90), policy.WindowStartUtc(now));
    }

    [Fact]
    public void The_threshold_is_a_setting()
    {
        Assert.True(new NoShowPolicy { Threshold = 1 }.RequiresApproval(2));
        Assert.False(new NoShowPolicy { Threshold = 10 }.RequiresApproval(2));
    }
}
