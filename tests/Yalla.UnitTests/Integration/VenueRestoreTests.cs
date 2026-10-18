using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Platform;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Restoring a soft-deleted venue: it comes back as it was before - switched on, switched off or
/// suspended - with everything under it, accepts changes again, is audited, and an undo that arrives
/// twice writes nothing. The web address stays reserved while it is deleted, so it is never found taken.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class VenueRestoreTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_deleted_venue_comes_back_as_it_was_and_takes_changes_again()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);

        await platform.DeleteVenueAsync(branch.VenueId);
        await Assert.ThrowsAsync<DomainStateException>(() => platform.SuspendVenueAsync(branch.VenueId));

        clock.Advance(TimeSpan.FromSeconds(5));
        var restored = await platform.RestoreVenueAsync(branch.VenueId);

        Assert.Equal(VenueStatus.Active, restored.Venue.Status);
        Assert.False(restored.Venue.IsDeleted);
        Assert.Null(restored.Venue.DeletedAtUtc);
        Assert.True(restored.Venue.IsActive);
        Assert.Equal(3, Assert.Single(restored.Branches).TableCount);

        // Back in the list without asking for deleted ones, and it takes changes again.
        Assert.Equal(
            branch.VenueId,
            Assert.Single((await platform.ListVenuesAsync(new VenueListQuery(Search: restored.Venue.Name))).Items).VenueId);
        await platform.UpdateVenueAsync(branch.VenueId, new UpdateVenueCommand(Name: $"Back {Guid.NewGuid():N}"[..20]));

        await using var verify = fixture.CreateContext(clock);
        var audit = await verify.PlatformAuditLogs.AsNoTracking()
            .SingleAsync(l => l.TargetId == branch.VenueId && l.Action == "venue.restore");
        Assert.Equal(StaffRole.PlatformAdmin, audit.ActorRole);
        Assert.Equal(branch.VenueId, audit.VenueId);
        Assert.Contains("wasDeletedAtUtc", audit.ChangesJson, StringComparison.Ordinal);

        var row = await verify.Venues.AsNoTracking().SingleAsync(v => v.Id == branch.VenueId);
        Assert.Null(row.ActiveBeforeDeletion);
    }

    [SkippableFact]
    public async Task A_venue_suspended_or_switched_off_before_it_was_deleted_comes_back_that_way()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));

        var suspended = await TestBranchBuilder.CreateAsync(db);
        var suspendedAt = (await platform.SuspendVenueAsync(suspended.VenueId)).Venue.SuspendedAtUtc;
        await platform.DeleteVenueAsync(suspended.VenueId);

        var off = await TestBranchBuilder.CreateAsync(db);
        await platform.UpdateVenueAsync(off.VenueId, new UpdateVenueCommand(IsActive: false));
        await platform.DeleteVenueAsync(off.VenueId);

        var backSuspended = (await platform.RestoreVenueAsync(suspended.VenueId)).Venue;
        Assert.Equal(VenueStatus.Suspended, backSuspended.Status);
        Assert.True(backSuspended.IsActive);
        Assert.Equal(suspendedAt, backSuspended.SuspendedAtUtc);

        var backOff = (await platform.RestoreVenueAsync(off.VenueId)).Venue;
        Assert.Equal(VenueStatus.Inactive, backOff.Status);
        Assert.False(backOff.IsActive);
    }

    [SkippableFact]
    public async Task Restoring_a_venue_that_is_not_deleted_writes_nothing()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);

        await platform.DeleteVenueAsync(branch.VenueId);
        await platform.RestoreVenueAsync(branch.VenueId);
        var again = await platform.RestoreVenueAsync(branch.VenueId);

        Assert.Equal(VenueStatus.Active, again.Venue.Status);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(1, await verify.PlatformAuditLogs.CountAsync(l => l.TargetId == branch.VenueId && l.Action == "venue.restore"));
    }

    [SkippableFact]
    public async Task A_deleted_venue_keeps_its_web_address_so_a_restore_never_finds_it_taken()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(DateTime.UtcNow);
        await using var db = fixture.CreateContext(clock);
        var admin = await AuthTestData.CreatePlatformAdminAsync(db);
        var platform = fixture.CreatePlatformService(db, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
        var branch = await TestBranchBuilder.CreateAsync(db);
        var slug = await db.Venues.Where(v => v.Id == branch.VenueId).Select(v => v.Slug).FirstAsync();

        await platform.DeleteVenueAsync(branch.VenueId);

        // Nobody can take the address while the venue is deleted.
        await using (var other = fixture.CreateContext(clock))
        {
            var taking = fixture.CreatePlatformService(other, clock, TestActor.PlatformAdmin(admin.StaffMemberId));
            var refused = await Assert.ThrowsAsync<DomainStateException>(() => taking.CreateVenueAsync(new CreateVenueCommand(
                "Usurper", VenueType.Cafe, slug,
                new CreateBranchCommand("Main", $"main-{Guid.NewGuid():N}"[..20], "1 Test Street", 40.18, 44.51, "Asia/Yerevan"))));
            Assert.Contains(slug, refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(slug, (await platform.RestoreVenueAsync(branch.VenueId)).Venue.Slug);
    }

    [SkippableFact]
    public async Task Only_a_platform_admin_restores_and_an_unknown_venue_is_not_found()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        AuthBranch branch;
        PlatformAdminAccount admin;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            admin = await AuthTestData.CreatePlatformAdminAsync(db);
        }

        using var platform = factory.CreateClientWithToken(await PlatformEndpointTests.SignInPlatformAdminAsync(factory, admin));
        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        Assert.Equal(HttpStatusCode.OK, (await platform.DeleteAsync($"/api/platform/venues/{branch.VenueId}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await manager.PostAsJsonAsync($"/api/platform/venues/{branch.VenueId}/restore", new { })).StatusCode);

        var restored = await platform.PostAsJsonAsync($"/api/platform/venues/{branch.VenueId}/restore", new { });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        var venue = (await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("venue");
        Assert.False(venue.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(1, venue.GetProperty("status").GetInt32());

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await platform.PostAsJsonAsync($"/api/platform/venues/{Guid.NewGuid()}/restore", new { })).StatusCode);
    }
}
