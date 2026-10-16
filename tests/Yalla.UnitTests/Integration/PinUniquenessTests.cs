using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Staff;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// A PIN is unique among the people who sign in on the same tablet: a branch's own people plus the
/// venue-wide ones, and for a venue-wide person the whole venue. Checked when a PIN is set.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PinUniquenessTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task A_pin_somebody_on_the_same_tablet_uses_is_refused_and_one_from_another_branch_is_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var home = await TestBranchBuilder.CreateAsync(db);
        var sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan");
        var owner = await AuthTestData.SeedOwnerAsync(db, home.VenueId);

        var siblingWaiter = Person(db, home.VenueId, sibling, "2468");
        var venueWideCook = Person(db, home.VenueId, null, "1357", StaffRole.Kitchen);
        var leaver = Person(db, home.VenueId, home.BranchId, "9999");
        leaver.SetActive(false);
        await db.SaveChangesAsync();

        var service = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.StaffMemberId));

        // Somebody at this branch takes a PIN.
        var first = await service.CreateAsync(home.VenueId, Staff("First", home.BranchId, "4321"));

        // The same PIN for somebody else at this branch: refused, on creation and on a reset.
        var clash = await Assert.ThrowsAsync<PinTakenException>(
            () => service.CreateAsync(home.VenueId, Staff("Second", home.BranchId, "4321")));
        Assert.Equal("pin", clash.Field);
        Assert.Equal("Somebody who signs in on this tablet already uses that PIN. Choose another.", clash.Message);

        await Assert.ThrowsAsync<PinTakenException>(() => service.SetPinAsync(home.VenueId, home.WaiterId, "4321"));

        // A venue-wide person is on this tablet too, so their PIN is taken here as well.
        await Assert.ThrowsAsync<PinTakenException>(
            () => service.CreateAsync(home.VenueId, Staff("Third", home.BranchId, "1357")));

        // Another branch's PIN is another tablet's business, and a leaver's PIN is nobody's.
        var fromSibling = await service.CreateAsync(home.VenueId, Staff("Fourth", home.BranchId, "2468"));
        var fromLeaver = await service.CreateAsync(home.VenueId, Staff("Fifth", home.BranchId, "9999"));

        // Resetting your own PIN to what it already is clashes with nobody.
        await service.SetPinAsync(home.VenueId, first.Id, "4321");

        // Nothing refused was written.
        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(0, await verify.StaffMembers.CountAsync(s => s.VenueId == home.VenueId && s.FullName == "Second"));
        var hasher = new SecretHasher();
        var waiter = await verify.StaffMembers.AsNoTracking().SingleAsync(s => s.Id == home.WaiterId);
        Assert.False(hasher.Verify(waiter.PinHash, "4321").Matches);

        Assert.NotEqual(fromSibling.Id, fromLeaver.Id);
        _ = siblingWaiter;
    }

    /// <summary>
    /// A venue-wide person signs in on every tablet, so their PIN has to be free on all of them: every
    /// branch's people and the other venue-wide ones.
    /// </summary>
    [SkippableFact]
    public async Task A_venue_wide_persons_pin_is_checked_against_every_branch_of_the_venue()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var home = await TestBranchBuilder.CreateAsync(db);
        var sibling = await AuthTestData.AddBranchAsync(db, home.VenueId, "Asia/Yerevan");
        var elsewhere = await TestBranchBuilder.CreateAsync(db);
        var owner = await AuthTestData.SeedOwnerAsync(db, home.VenueId);

        Person(db, home.VenueId, sibling, "2468");
        Person(db, elsewhere.VenueId, elsewhere.BranchId, "8642");
        await db.SaveChangesAsync();

        var generalManager = await AuthTestData.SeedGeneralManagerAsync(db, home.VenueId, canSignIn: false, pin: "3690");

        var service = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(owner.StaffMemberId));

        // A sibling branch's PIN, for somebody on every tablet: refused.
        await Assert.ThrowsAsync<PinTakenException>(
            () => service.CreateAsync(home.VenueId, Staff("Venue GM", null, "2468", StaffRole.GeneralManager)));
        await Assert.ThrowsAsync<PinTakenException>(
            () => service.SetPinAsync(home.VenueId, generalManager.StaffMemberId, "2468"));

        // Another venue-wide person's PIN: refused for them as well.
        await Assert.ThrowsAsync<PinTakenException>(
            () => service.CreateAsync(home.VenueId, Staff("Venue Cook", null, "3690", StaffRole.Kitchen)));

        // Another venue's PIN is no concern of this one.
        var created = await service.CreateAsync(home.VenueId, Staff("Venue Cook", null, "8642", StaffRole.Kitchen));
        Assert.Null(created.BranchId);
    }

    /// <summary>The refusal over HTTP: 409 <c>pin-taken</c>, <c>context.field = "pin"</c>, and the sentence.</summary>
    [SkippableFact]
    public async Task A_taken_pin_is_a_409_pin_taken_on_pin_over_http()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        var route = $"/api/venues/{branch.VenueId}/staff";

        foreach (var response in new[]
        {
            await manager.PostAsJsonAsync(route, new
            {
                fullName = "Copycat", phone = "+37455500200", role = StaffRole.Waiter, pin = AuthTestData.WaiterPin,
                branchId = branch.BranchId,
            }),
            await manager.PostAsJsonAsync($"{route}/{branch.WaiterId}/pin", new { pin = "9182" }),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pin-taken", problem.GetProperty("code").GetString());
            Assert.Equal("pin", problem.GetProperty("context").GetProperty("field").GetString());
            Assert.Equal(
                "Somebody who signs in on this tablet already uses that PIN. Choose another.",
                problem.GetProperty("detail").GetString());
        }
    }

    private static StaffMember Person(
        Yalla.Infrastructure.Persistence.YallaDbContext db, Guid venueId, Guid? branchId, string pin, StaffRole role = StaffRole.Waiter)
    {
        var person = new StaffMember(
            venueId, $"Person {pin}", $"+374{Random.Shared.Next(10_000_000, 99_999_999)}", role,
            new SecretHasher().Hash(pin), branchId);

        db.StaffMembers.Add(person);

        return person;
    }

    private static CreateStaffCommand Staff(string name, Guid? branchId, string pin, StaffRole role = StaffRole.Waiter) => new(
        FullName: name,
        Phone: $"+374{Random.Shared.Next(10_000_000, 99_999_999)}",
        Role: role,
        Pin: pin,
        BranchId: branchId);
}
