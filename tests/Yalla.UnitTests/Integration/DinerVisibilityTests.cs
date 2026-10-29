using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.BranchSettings;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A branch missing a required checklist line - a bookable table, opening hours, staff, a device to
/// see the booking arrive - is not shown to diners and takes no booking (2026-10-29).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DinerVisibilityTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_branch_with_no_tablet_is_hidden_and_refuses_a_booking_until_one_is_enrolled()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 2, enrolTablet: false);
        }

        using var anyone = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync($"/api/public/branches/{branch.BranchId}")).StatusCode);

        using var diner = factory.CreateClientWithToken((await ReviewIntegrityTests.SignInDinerAsync(factory)).AccessToken);

        var refused = await ReviewIntegrityTests.AssertProblemAsync(
            await diner.PostAsJsonAsync("/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.App)),
            HttpStatusCode.Conflict,
            "branch-unavailable");
        Assert.Equal(branch.BranchId, refused.GetProperty("context").GetProperty("branchId").GetGuid());

        // A tablet on the counter, and the branch is there at once: nothing about it is cached.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            db.StaffDevices.Add(new StaffDevice(branch.VenueId, branch.BranchId, "Counter", $"late-{Guid.NewGuid():N}", factory.Clock.UtcNow));
            await db.SaveChangesAsync();
        }

        var detail = await anyone.GetFromJsonAsync<JsonElement>($"/api/public/branches/{branch.BranchId}");
        Assert.Equal(branch.BranchId, detail.GetProperty("listing").GetProperty("branchId").GetGuid());

        Assert.Equal(
            HttpStatusCode.Created,
            (await diner.PostAsJsonAsync(
                "/api/reservations", ReviewTestData.Booking(factory, branch, branch.TableIds[0], ReservationChannel.App))).StatusCode);
    }

    [SkippableFact]
    public async Task The_checklist_says_taking_guests_exactly_when_diners_can_see_the_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var readiness = SqlServerFixture.CreateReadinessQuery(db);

        var full = await TestBranchBuilder.CreateAsync(db);
        var noTablet = await TestBranchBuilder.CreateAsync(db, enrolTablet: false);
        var noHours = await TestBranchBuilder.CreateAsync(db, openingHours: []);
        var unpinned = await TestBranchBuilder.CreateAsync(db, pinTables: false);
        var noTables = await TestBranchBuilder.CreateAsync(db, tableCount: 0);
        var noStaff = await TestBranchBuilder.CreateAsync(db);
        await db.Database.ExecuteSqlAsync($"UPDATE dbo.StaffMembers SET IsActive = 0 WHERE BranchId = {noStaff.BranchId}");

        var cases = new (TestBranch Branch, string[] Missing)[]
        {
            (full, []),
            (noTablet, [ReadinessIssueCodes.NoDevice]),
            (noHours, [ReadinessIssueCodes.NoHours]),
            (unpinned, [ReadinessIssueCodes.NoBookableTable]),
            (noTables, [ReadinessIssueCodes.NoTables]),
            (noStaff, [ReadinessIssueCodes.NoStaff]),
        };

        foreach (var (branch, missing) in cases)
        {
            var view = await readiness.GetAsync(branch.BranchId);

            Assert.Equal(missing, view.MissingForGuests);
            Assert.Equal(missing.Length == 0, view.TakingGuests);
            Assert.Equal(view.TakingGuests, await DinerVisibility.IsVisibleAsync(db, branch.BranchId, CancellationToken.None));
        }

        // The menu and the policy are advice: the fresh builder branch has neither and still takes guests.
        var advice = await readiness.GetAsync(full.BranchId);
        Assert.False(advice.IsReadyForDiners);
        Assert.True(advice.TakingGuests);
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
