using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// What a branch's reservation policy ships with, and the bounds an edit is held to, read from the
/// server rather than copied into the console - the copy had already drifted once.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReservationPolicyDefaultsTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task The_defaults_are_the_branchs_venue_types_and_the_bounds_are_the_editing_rules()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);

        AuthBranch cafe;
        Guid restaurantBranchId;
        PanelAccount owner;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            cafe = await AuthTestData.CreateBranchAsync(db);
            (await db.Venues.SingleAsync(v => v.Id == cafe.VenueId)).SetType(VenueType.Cafe);
            restaurantBranchId = await AuthTestData.AddBranchAsync(db, cafe.VenueId, "Asia/Yerevan");
            await db.SaveChangesAsync();
            owner = await AuthTestData.SeedOwnerAsync(db, cafe.VenueId);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, cafe));

        var body = await manager.GetFromJsonAsync<JsonElement>($"/api/branches/{cafe.BranchId}/reservation-policy/defaults");
        var defaults = body.GetProperty("defaults");
        var expected = ReservationPolicy.DefaultFor(VenueType.Cafe);

        Assert.Equal(expected.TurnTimeMinutes, defaults.GetProperty("turnTimeMinutes").GetInt32());
        Assert.Equal(ReservationPolicy.DefaultBufferMinutes, defaults.GetProperty("bufferMinutes").GetInt32());
        Assert.Equal(ReservationPolicy.DefaultMinLeadMinutes, defaults.GetProperty("minLeadMinutes").GetInt32());
        Assert.Equal(expected.BookingWindowDays, defaults.GetProperty("bookingWindowDays").GetInt32());
        Assert.Equal(expected.CancellationDeadlineMinutes, defaults.GetProperty("cancellationDeadlineMinutes").GetInt32());
        Assert.Equal(expected.GraceMinutes, defaults.GetProperty("graceMinutes").GetInt32());
        Assert.Equal(expected.LateNudgeAfterMinutes, defaults.GetProperty("lateNudgeAfterMinutes").GetInt32());
        Assert.Equal(expected.GraceExtensionMinutes, defaults.GetProperty("graceExtensionMinutes").GetInt32());
        Assert.Equal(expected.WalkInHoldbackMinutes, defaults.GetProperty("walkInHoldbackMinutes").GetInt32());
        Assert.Equal(expected.MaxSeatOverhang, defaults.GetProperty("maxSeatOverhang").GetInt32());
        Assert.Equal(expected.ApprovalRequiredAbovePartySize, defaults.GetProperty("approvalRequiredAbovePartySize").GetInt32());
        Assert.Equal(expected.ServiceChargePercent, defaults.GetProperty("serviceChargePercent").GetDecimal());
        Assert.True(defaults.GetProperty("autoConfirm").GetBoolean());
        Assert.True(defaults.GetProperty("pricesIncludeVat").GetBoolean());

        // Booking without a verified number (2026-10-20): such bookings wait for the venue, six at most.
        Assert.True(defaults.GetProperty("unverifiedBookingsNeedApproval").GetBoolean());
        Assert.Equal(6, defaults.GetProperty("unverifiedMaxPartySize").GetInt32());

        // Every bounded field, by its wire name, with the numbers the save is checked against.
        var bounds = body.GetProperty("bounds").EnumerateArray()
            .ToDictionary(b => b.GetProperty("field").GetString()!, b => b);

        Assert.Equal(13, bounds.Count);
        // 0 is a setting: no bookings without a verified number.
        Assert.Equal(0m, bounds["unverifiedMaxPartySize"].GetProperty("min").GetDecimal());
        Assert.Equal(100m, bounds["unverifiedMaxPartySize"].GetProperty("max").GetDecimal());
        Assert.Equal(ReservationPolicyLimits.MinTurnTimeMinutes, bounds["turnTimeMinutes"].GetProperty("min").GetDecimal());
        Assert.Equal(ReservationPolicyLimits.MaxTurnTimeMinutes, bounds["turnTimeMinutes"].GetProperty("max").GetDecimal());
        Assert.Equal(ReservationPolicyLimits.MaxMinLeadMinutes, bounds["minLeadMinutes"].GetProperty("max").GetDecimal());
        Assert.Equal(100m, bounds["serviceChargePercent"].GetProperty("max").GetDecimal());
        Assert.Equal(1m, bounds["approvalRequiredAbovePartySize"].GetProperty("min").GetDecimal());
        Assert.False(bounds["approvalRequiredAbovePartySize"].TryGetProperty("max", out _));
        Assert.False(bounds["maxSeatOverhang"].TryGetProperty("max", out _));

        // The defaults follow the venue type, which the owner can change.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            (await db.Venues.SingleAsync(v => v.Id == cafe.VenueId)).SetType(VenueType.Restaurant);
            await db.SaveChangesAsync();
        }

        using var ownerClient = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));
        var restaurant = await ownerClient.GetFromJsonAsync<JsonElement>(
            $"/api/branches/{restaurantBranchId}/reservation-policy/defaults");
        Assert.Equal(
            ReservationPolicy.DefaultFor(VenueType.Restaurant).TurnTimeMinutes,
            restaurant.GetProperty("defaults").GetProperty("turnTimeMinutes").GetInt32());

        // Read-only, and scoped like the policy itself: the branch's manager does not reach a sibling.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.GetAsync($"/api/branches/{restaurantBranchId}/reservation-policy/defaults")).StatusCode);
    }
}
