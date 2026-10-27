using Yalla.Application.BranchSettings;
using Yalla.Application.Platform;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests;

/// <summary>
/// The arithmetic the platform console's numbers share: the venue's one-word state,
/// the week-on-week change and the averages, and which bucket a booking status counts in.
/// </summary>
public class PlatformRollupsTests
{
    [Theory]
    [InlineData(true, false, false, VenueStatus.Active)]
    [InlineData(false, false, false, VenueStatus.Inactive)]
    [InlineData(true, true, false, VenueStatus.Suspended)]
    [InlineData(false, true, false, VenueStatus.Suspended)]
    [InlineData(true, true, true, VenueStatus.Deleted)]
    [InlineData(false, false, true, VenueStatus.Deleted)]
    public void Deleted_outranks_suspended_which_outranks_switched_off(bool active, bool suspended, bool deleted, VenueStatus expected) =>
        Assert.Equal(expected, PlatformRollups.StatusOf(active, suspended, deleted));

    [Fact]
    public void The_week_on_week_change_is_a_percentage_to_one_decimal_and_absent_from_nothing()
    {
        Assert.Equal(12.2, PlatformRollups.ChangePercent(312, 278));
        Assert.Equal(-50.0, PlatformRollups.ChangePercent(5, 10));
        Assert.Equal(0.0, PlatformRollups.ChangePercent(7, 7));
        Assert.Equal(100.0, PlatformRollups.ChangePercent(2, 1));

        // Growth from zero is not a percentage, and a previous week with none is not a "-100 %".
        Assert.Null(PlatformRollups.ChangePercent(9, 0));
        Assert.Null(PlatformRollups.ChangePercent(0, 0));
    }

    [Fact]
    public void An_average_is_to_one_decimal_rounding_half_away_from_zero_and_absent_with_nothing()
    {
        Assert.Equal(3.2, PlatformRollups.Average(16, 5));
        Assert.Equal(2.5, PlatformRollups.Average(5, 2));
        Assert.Equal(3.3, PlatformRollups.Average(10, 3));
        Assert.Null(PlatformRollups.Average(0, 0));
    }

    [Fact]
    public void A_guest_sees_tables_menu_dishes_hours_and_photo_and_nothing_the_venue_does_behind_the_scenes()
    {
        ReadinessIssue[] checklist =
        [
            new(ReadinessIssueCodes.TablesUnlabelled),
            new(ReadinessIssueCodes.MenuEmpty),
            new(ReadinessIssueCodes.NoHours),
            new(ReadinessIssueCodes.PolicyNotReviewed),
            new(ReadinessIssueCodes.NoStaff),
            new(ReadinessIssueCodes.NoDevice),
        ];

        Assert.Equal(
            [
                new ReadinessIssue(ReadinessIssueCodes.NoTables),
                new ReadinessIssue(ReadinessIssueCodes.NoMenu),
                new ReadinessIssue(ReadinessIssueCodes.NoHours),
                new ReadinessIssue(ReadinessIssueCodes.NoPhoto),
            ],
            PlatformRollups.GuestFacingIssues(checklist, hasCover: false));

        // The dish count rides along; a branch with a cover and only back-office gaps has nothing to show.
        Assert.Equal(
            [new ReadinessIssue(ReadinessIssueCodes.IncompleteDishes, 3)],
            PlatformRollups.GuestFacingIssues(
                [new(ReadinessIssueCodes.IncompleteDishes, 3), new(ReadinessIssueCodes.NoDevice)],
                hasCover: true));

        Assert.Empty(PlatformRollups.GuestFacingIssues([new(ReadinessIssueCodes.PolicyNotReviewed)], hasCover: true));
        Assert.Equal([new ReadinessIssue(ReadinessIssueCodes.NoPhoto)], PlatformRollups.GuestFacingIssues([], hasCover: false));
    }

    [Fact]
    public void The_guest_facing_codes_are_the_five_the_console_maps()
    {
        Assert.Equal(
            ["no-tables", "no-menu", "incomplete-dishes", "no-hours", "no-photo"],
            new[]
            {
                ReadinessIssueCodes.NoTables,
                ReadinessIssueCodes.NoMenu,
                ReadinessIssueCodes.IncompleteDishes,
                ReadinessIssueCodes.NoHours,
                ReadinessIssueCodes.NoPhoto,
            });
    }

    [Theory]
    [InlineData(ReservationStatus.PendingApproval, BookingBucket.PendingApproval)]
    [InlineData(ReservationStatus.Confirmed, BookingBucket.Upcoming)]
    [InlineData(ReservationStatus.Seated, BookingBucket.Completed)]
    [InlineData(ReservationStatus.Completed, BookingBucket.Completed)]
    [InlineData(ReservationStatus.CancelledByDiner, BookingBucket.Cancelled)]
    [InlineData(ReservationStatus.CancelledByVenue, BookingBucket.Cancelled)]
    [InlineData(ReservationStatus.NoShow, BookingBucket.NoShow)]
    public void Every_booking_status_counts_in_exactly_one_bucket(ReservationStatus status, BookingBucket bucket) =>
        Assert.Equal(bucket, PlatformRollups.BucketOf(status));

    [Fact]
    public void A_status_nobody_mapped_is_refused_rather_than_counted_somewhere()
    {
        // Every defined status has a bucket; the theory above lists them all. A status added later must
        // land in that list rather than silently in a default bucket.
        foreach (var status in Enum.GetValues<ReservationStatus>())
        {
            _ = PlatformRollups.BucketOf(status);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => PlatformRollups.BucketOf((ReservationStatus)3));
    }
}
