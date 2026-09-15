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
/// A branch ships with a default policy nobody at the venue chose. The web channel's rule - the switch
/// - is unchanged, and the channels that are not the app are not gated at all.
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
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);

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

        // The page books as it did.
        Assert.Equal(
            HttpStatusCode.Created,
            (await diner.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.Web))).StatusCode);

        // A client that does not say where it booked from is not the app, and is not gated.
        Assert.Equal(
            HttpStatusCode.Created,
            (await diner.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[1], ReservationChannel.Unknown))).StatusCode);

        // Somebody saves the policy.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await ReviewTestData.SavePolicyAsync(db, branch.BranchId, factory.Clock.UtcNow);
        }

        var saved = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branch.BranchId}");
        Assert.True(saved.GetProperty("acceptsAppBookings").GetBoolean());
        Assert.True((await anyone.GetFromJsonAsync<JsonElement>(pageUrl)).GetProperty("acceptsAppBookings").GetBoolean());

        var booked = await diner.PostAsJsonAsync(
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
            branch = await AuthTestData.CreateBranchAsync(db);

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
            await diner.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.Web)),
            HttpStatusCode.Conflict,
            "web-bookings-not-accepted");

        await using var verify = fixture.CreateContext(factory.Clock);
        Assert.False(await verify.Reservations.AnyAsync(r => r.BranchId == branch.BranchId));
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
