using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>GET /api/auth/staff/roster</c>: who may tap a PIN on this tablet, so the first sign-in is a
/// name to tap rather than a staff member id to type.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class StaffRosterTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task An_enrolled_device_lists_its_branch_and_the_venue_wide_staff_sorted_by_name()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedRosterAsync(factory);
        var deviceToken = await StaffAuthTests.EnrolDeviceAsync(factory, seeded.Branch);

        using var tablet = factory.CreateClientWithToken(deviceToken);
        var response = await tablet.GetAsync("/api/auth/staff/roster");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var roster = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        // "Anna" works across the venue, so she is here; the two seeded branch staff follow by name.
        Assert.Equal(
            [seeded.VenueWideId, seeded.Branch.ManagerId, seeded.Branch.WaiterId],
            roster.Select(p => p.GetProperty("staffMemberId").GetGuid()).ToArray());

        Assert.Equal(
            new[] { "Anna Venue-Wide", "Test Manager", "Test Waiter" },
            roster.Select(p => p.GetProperty("fullName").GetString()!).ToArray());

        Assert.Equal(
            [(int)StaffRole.Waiter, (int)StaffRole.Manager, (int)StaffRole.Waiter],
            roster.Select(p => p.GetProperty("role").GetInt32()).ToArray());

        // Exactly three fields. No phone, email or PIN state leaves on a counter tablet.
        foreach (var person in roster)
        {
            Assert.Equal(
                ["fullName", "role", "staffMemberId"],
                person.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        }
    }

    [SkippableFact]
    public async Task The_roster_leaves_out_other_branches_other_venues_and_deactivated_staff()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedRosterAsync(factory);
        var deviceToken = await StaffAuthTests.EnrolDeviceAsync(factory, seeded.Branch);

        using var tablet = factory.CreateClientWithToken(deviceToken);
        var response = await tablet.GetAsync("/api/auth/staff/roster");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray()
            .Select(p => p.GetProperty("staffMemberId").GetGuid())
            .ToList();

        Assert.DoesNotContain(seeded.OtherBranchId, ids);
        Assert.DoesNotContain(seeded.OtherVenueWaiterId, ids);
        Assert.DoesNotContain(seeded.OtherVenueWideId, ids);
        Assert.DoesNotContain(seeded.DeactivatedId, ids);
        Assert.Equal(3, ids.Count);
    }

    [SkippableFact]
    public async Task The_roster_refuses_a_caller_with_no_device_token_like_the_device_route()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        using var anonymous = factory.CreateClient();

        var device = await anonymous.GetAsync("/api/auth/staff/device");
        var roster = await anonymous.GetAsync("/api/auth/staff/roster");

        Assert.Equal(HttpStatusCode.Unauthorized, device.StatusCode);
        Assert.Equal(device.StatusCode, roster.StatusCode);
        Assert.Contains(roster.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [SkippableFact]
    public async Task The_roster_refuses_a_revoked_device_like_the_device_route()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedRosterAsync(factory);
        var deviceToken = await StaffAuthTests.EnrolDeviceAsync(factory, seeded.Branch);

        using var tablet = factory.CreateClientWithToken(deviceToken);

        // Works before, so the refusal afterwards means something.
        Assert.Equal(HttpStatusCode.OK, (await tablet.GetAsync("/api/auth/staff/roster")).StatusCode);

        Guid deviceId;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            deviceId = await db.StaffDevices
                .Where(d => d.BranchId == seeded.Branch.BranchId)
                .Select(d => d.Id)
                .SingleAsync();
        }

        using var manager = factory.CreateClientWithToken(
            await StaffAuthTests.SignInManagerAsync(factory, seeded.Branch));

        var revoked = await manager.PostAsJsonAsync(
            $"/api/branches/{seeded.Branch.BranchId}/devices/{deviceId}/revoke", new { });

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        var device = await tablet.GetAsync("/api/auth/staff/device");
        var roster = await tablet.GetAsync("/api/auth/staff/roster");

        Assert.Equal(HttpStatusCode.Unauthorized, device.StatusCode);
        Assert.Equal(device.StatusCode, roster.StatusCode);

        // The same refusal, not merely the same status: the pipeline's authority check turns a
        // revoked device away before either handler runs, and both must get that one answer.
        Assert.Equal(await device.Content.ReadAsStringAsync(), await roster.Content.ReadAsStringAsync());
        Assert.Equal(
            device.Headers.WwwAuthenticate.ToString(), roster.Headers.WwwAuthenticate.ToString());
    }

    private sealed record SeededRoster(
        AuthBranch Branch,
        Guid VenueWideId,
        Guid OtherBranchId,
        Guid OtherVenueWaiterId,
        Guid OtherVenueWideId,
        Guid DeactivatedId);

    /// <summary>
    /// One branch's staff plus everyone who must not appear on its tablet: somebody at a sibling
    /// branch, a whole other venue's staff, and a deactivated waiter at this very branch.
    /// </summary>
    private async Task<SeededRoster> SeedRosterAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db);
        var otherVenue = await AuthTestData.CreateBranchAsync(db);
        var siblingBranchId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan");

        var venueWide = Staff(db, branch.VenueId, "Anna Venue-Wide", null);
        var sibling = Staff(db, branch.VenueId, "Aaron Sibling-Branch", siblingBranchId);
        var otherVenueWide = Staff(db, otherVenue.VenueId, "Aa Other-Venue", null);
        var deactivated = Staff(db, branch.VenueId, "Abel Deactivated", branch.BranchId);
        deactivated.SetActive(false);

        await db.SaveChangesAsync();

        return new SeededRoster(
            branch, venueWide.Id, sibling.Id, otherVenue.WaiterId, otherVenueWide.Id, deactivated.Id);
    }

    private static StaffMember Staff(YallaDbContext db, Guid venueId, string name, Guid? branchId)
    {
        var person = new StaffMember(
            venueId, name, $"+374{Random.Shared.Next(10_000_000, 99_999_999)}", StaffRole.Waiter, "hash", branchId);

        db.StaffMembers.Add(person);

        return person;
    }

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
