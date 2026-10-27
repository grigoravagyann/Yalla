using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K9: the diner app books only where online bookings are switched on <b>and</b> somebody at the
/// venue saved the reservation policy - and the details screen and the branch page say so.
/// </summary>
/// <remarks>
/// A branch ships with a default policy nobody at the venue chose. The web channel's rule - the switch - is unchanged.
/// Since 2026-10-27 the route is the channel: <c>POST /api/reservations</c> is the app's whatever a body names, and the
/// page books through <c>POST /api/public/branches/{id}/reservations</c>.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class AppBookingGateTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_branch_whose_policy_nobody_saved_takes_no_app_bookings_while_the_web_rule_is_unchanged()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        string pageUrl;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3, bookable: false);

            // Switched on, and the policy never saved.
            var row = await db.Branches.Include(b => b.Venue).FirstAsync(b => b.Id == branch.BranchId);
            row.SetAcceptsWebBookings(true);
            await db.SaveChangesAsync();

            Assert.Null(row.ReservationPolicyReviewedAtUtc);
            pageUrl = $"/api/public/branches/{row.Venue.Slug}/{row.Slug}";
        }

        using var anyone = factory.CreateClient();

        var detail = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branch.BranchId}");
        Assert.True(detail.GetProperty("acceptsWebBookings").GetBoolean());
        Assert.False(detail.GetProperty("acceptsAppBookings").GetBoolean());
        Assert.False((await anyone.GetFromJsonAsync<JsonElement>(pageUrl)).GetProperty("acceptsAppBookings").GetBoolean());

        using var diner = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

        var fromApp = await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.App)),
            HttpStatusCode.Conflict,
            "bookings-not-accepted");

        Assert.Equal(branch.BranchId, fromApp.GetProperty("context").GetProperty("branchId").GetGuid());

        // The page books through its own route, as it did - the policy is not its gate.
        Assert.Equal(
            HttpStatusCode.Created,
            (await diner.PostAsJsonAsync(
                $"/api/public/branches/{branch.BranchId}/reservations", PageBooking(factory, branch, branch.TableIds[0]))).StatusCode);

        // The app's route is the app whatever a body names (2026-10-27): naming the page, or nothing, does not get round
        // K9. (Another diner: one diner holds one table at a branch at a time.)
        using var second = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

        foreach (var named in new[] { ReservationChannel.Web, ReservationChannel.Unknown })
        {
            await ReviewIntegrityTests.AssertProblemAsync(
                await second.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[1], named)),
                HttpStatusCode.Conflict,
                "bookings-not-accepted");
        }

        // Somebody saves the policy.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow);
        }

        var saved = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branch.BranchId}");
        Assert.True(saved.GetProperty("acceptsAppBookings").GetBoolean());
        Assert.True((await anyone.GetFromJsonAsync<JsonElement>(pageUrl)).GetProperty("acceptsAppBookings").GetBoolean());

        using var third = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

        var booked = await third.PostAsJsonAsync(
            "/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[2], ReservationChannel.App));

        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
    }

    [SkippableFact]
    public async Task Switching_online_bookings_off_closes_the_app_as_well_as_the_page()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, bookable: false);

            // The policy saved, and online bookings off.
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow, acceptsOnlineBookings: false);
        }

        using var anyone = factory.CreateClient();
        Assert.False((await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branch.BranchId}"))
            .GetProperty("acceptsAppBookings").GetBoolean());

        using var diner = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

        await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.App)),
            HttpStatusCode.Conflict,
            "bookings-not-accepted");

        await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PostAsJsonAsync(
                $"/api/public/branches/{branch.BranchId}/reservations", PageBooking(factory, branch, branch.TableIds[0])),
            HttpStatusCode.Conflict,
            "web-bookings-not-accepted");

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.False(await verify.Reservations.AnyAsync(r => r.BranchId == branch.BranchId));
    }

    /// <summary>A body for the public page's own route: two days out at 18:00, party of two, no branch, no channel.</summary>
    private static object PageBooking(YallaApiFactory factory, AuthBranch branch, Guid tableId) => new
    {
        tableId,
        date = BookingPerNumberTests.LocalDate(factory, 2),
        time = "18:00",
        partySize = 2,
        guestName = "Ani Test",
        guestPhone = "+37499000999",
        clientCommandId = Guid.CreateVersion7(),
    };

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
