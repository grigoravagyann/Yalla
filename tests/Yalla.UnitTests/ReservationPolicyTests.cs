using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests;

public class ReservationPolicyTests
{
    [Theory]
    [InlineData(VenueType.Restaurant, 90)]
    [InlineData(VenueType.Cafe, 120)]
    public void DefaultFor_uses_the_shipped_turn_time_for_the_venue_type(VenueType venueType, int expectedTurnTime)
    {
        var policy = ReservationPolicy.DefaultFor(venueType);

        Assert.Equal(expectedTurnTime, policy.TurnTimeMinutes);
    }

    [Fact]
    public void DefaultFor_ships_the_documented_defaults()
    {
        var policy = ReservationPolicy.DefaultFor(VenueType.Restaurant);

        Assert.Equal(15, policy.BufferMinutes);
        Assert.Equal(15, policy.GraceMinutes);
        Assert.Equal(10, policy.LateNudgeAfterMinutes);
        Assert.Equal(10, policy.GraceExtensionMinutes);
        Assert.Equal(30, policy.MinLeadMinutes);
        Assert.Equal(14, policy.BookingWindowDays);
        Assert.Equal(120, policy.CancellationDeadlineMinutes);
        Assert.True(policy.AutoConfirm);
        Assert.Equal(10m, policy.ServiceChargePercent);
        Assert.True(policy.PricesIncludeVat);
        Assert.Equal(2, policy.MaxSeatOverhang);
        Assert.Equal(8, policy.ApprovalRequiredAbovePartySize);
    }

    [Fact]
    public void A_service_charge_over_one_hundred_percent_is_rejected()
    {
        var construct = () => new ReservationPolicy(
            turnTimeMinutes: 90,
            bufferMinutes: 15,
            graceMinutes: 15,
            lateNudgeAfterMinutes: 10,
            graceExtensionMinutes: 10,
            minLeadMinutes: 30,
            bookingWindowDays: 14,
            cancellationDeadlineMinutes: 120,
            autoConfirm: true,
            serviceChargePercent: 101m,
            pricesIncludeVat: true,
            maxSeatOverhang: 2,
            approvalRequiredAbovePartySize: 8);

        Assert.Throws<ArgumentOutOfRangeException>(construct);
    }
}
