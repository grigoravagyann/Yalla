using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// K5: moving a branch - its address or map pin - is the owner's call, and it is audited.
/// </summary>
/// <remarks>
/// A manager saves the listing form every day, and the form carries the location. So the rule is
/// about what changes, not what is sent: the stored location sent back is fine for anyone, and a
/// different one is refused for everyone but an owner or a platform admin signed in to the panel -
/// with the rest of the form refused too, so a half-saved listing never hides the refusal.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ListingRelocationTests(SqlServerFixture fixture)
{
    private const string NewAddress = "5 Test Street, Yerevan";

    [SkippableFact]
    public async Task A_manager_cannot_move_the_branch_and_nothing_on_the_form_is_saved()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, seeded.Branch));
        var url = $"/api/branches/{seeded.Branch.BranchId}/listing";
        var stored = await manager.GetFromJsonAsync<JsonElement>(url);
        var address = stored.GetProperty("address").GetString();
        var latitude = stored.GetProperty("latitude").GetDouble();
        var longitude = stored.GetProperty("longitude").GetDouble();

        var moved = await manager.PutAsJsonAsync(url, new
        {
            cuisine = "Should not land",
            address,
            latitude = latitude + 0.01,
            longitude,
        });

        Assert.Equal(HttpStatusCode.Forbidden, moved.StatusCode);
        Assert.Equal("relocation-not-allowed", await CodeAsync(moved));

        // A different street with the same pin is a move too.
        var renamed = await manager.PutAsJsonAsync(url, new
        {
            cuisine = "Should not land",
            address = NewAddress,
            latitude,
            longitude,
        });

        Assert.Equal(HttpStatusCode.Forbidden, renamed.StatusCode);
        Assert.Equal("relocation-not-allowed", await CodeAsync(renamed));

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            var branch = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == seeded.Branch.BranchId);
            Assert.Equal(latitude, branch.Latitude);
            Assert.Equal(address, branch.Address);
            Assert.Null(branch.Cuisine);
            Assert.False(await AuditRowsAsync(db, seeded.Branch.BranchId).AnyAsync());
        }

        // The stored location sent back unchanged - what the console does on every save - is fine.
        var repeated = await manager.PutAsJsonAsync(url, new
        {
            cuisine = "Lands",
            address,
            latitude,
            longitude,
        });

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            Assert.Equal("Lands", (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == seeded.Branch.BranchId)).Cuisine);
            Assert.False(await AuditRowsAsync(db, seeded.Branch.BranchId).AnyAsync());
        }
    }

    [SkippableFact]
    public async Task A_manager_on_a_tablet_PIN_session_cannot_move_the_branch_either()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);

        using var tablet = factory.CreateClientWithToken(await StaffAuthTests.EnrolDeviceAsync(factory, seeded.Branch));
        var pin = await tablet.PostAsJsonAsync(
            "/api/auth/staff/pin", new { staffMemberId = seeded.Branch.ManagerId, pin = "9182" });
        pin.EnsureSuccessStatusCode();

        using var session = factory.CreateClientWithToken(
            (await pin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        var url = $"/api/branches/{seeded.Branch.BranchId}/listing";
        var stored = await session.GetFromJsonAsync<JsonElement>(url);

        var moved = await session.PutAsJsonAsync(url, new
        {
            address = NewAddress,
            latitude = stored.GetProperty("latitude").GetDouble() + 0.02,
            longitude = stored.GetProperty("longitude").GetDouble(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, moved.StatusCode);
        Assert.Equal("relocation-not-allowed", await CodeAsync(moved));
    }

    [SkippableFact]
    public async Task The_owner_moves_the_branch_and_one_audit_row_records_the_old_and_new_location()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);

        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, seeded.Owner));
        var url = $"/api/branches/{seeded.Branch.BranchId}/listing";
        var stored = await owner.GetFromJsonAsync<JsonElement>(url);

        var moved = await owner.PutAsJsonAsync(url, new
        {
            cuisine = "Moved",
            address = NewAddress,
            latitude = 40.2001,
            longitude = 44.4902,
        });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var body = await moved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(NewAddress, body.GetProperty("address").GetString());
        Assert.Equal(40.2001, body.GetProperty("latitude").GetDouble());

        // The same location again is not a move, and writes no second row.
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PutAsJsonAsync(url, new { cuisine = "Moved", address = NewAddress, latitude = 40.2001, longitude = 44.4902 })).StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == seeded.Branch.BranchId);
        Assert.Equal(NewAddress, branch.Address);
        Assert.Equal(44.4902, branch.Longitude);

        var audit = await AuditRowsAsync(db, seeded.Branch.BranchId).SingleAsync();
        Assert.Equal(seeded.Owner.StaffMemberId, audit.ActorStaffMemberId);
        Assert.Equal("Branch", audit.TargetType);

        using var changes = JsonDocument.Parse(audit.ChangesJson);
        var old = changes.RootElement.GetProperty("old");
        var now = changes.RootElement.GetProperty("new");

        Assert.Equal(stored.GetProperty("address").GetString(), old.GetProperty("address").GetString());
        Assert.Equal(stored.GetProperty("latitude").GetDouble(), old.GetProperty("latitude").GetDouble());
        Assert.Equal(stored.GetProperty("longitude").GetDouble(), old.GetProperty("longitude").GetDouble());
        Assert.Equal(NewAddress, now.GetProperty("address").GetString());
        Assert.Equal(40.2001, now.GetProperty("latitude").GetDouble());
        Assert.Equal(44.4902, now.GetProperty("longitude").GetDouble());
    }

    [SkippableFact]
    public async Task A_pin_without_an_address_or_an_address_without_a_pin_is_refused_naming_the_missing_field()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);

        using var owner = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, seeded.Owner));
        var url = $"/api/branches/{seeded.Branch.BranchId}/listing";

        var noAddress = await owner.PutAsJsonAsync(url, new { cuisine = "No", latitude = 40.3, longitude = 44.6 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noAddress.StatusCode);
        Assert.True(await NamesAsync(noAddress, "address", "required"), "coordinates without an address must name address");

        var noPin = await owner.PutAsJsonAsync(url, new { cuisine = "No", address = NewAddress });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noPin.StatusCode);
        Assert.True(await NamesAsync(noPin, "latitude", "required"), "an address without coordinates must name latitude");

        var tooLong = await owner.PutAsJsonAsync(url, new
        {
            cuisine = "No",
            address = new string('a', 401),
            latitude = 40.3,
            longitude = 44.6,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.True(await NamesAsync(tooLong, "address", "max"), "an address over 400 characters must name address");

        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == seeded.Branch.BranchId);
        Assert.Null(branch.Cuisine);
        Assert.NotEqual(NewAddress, branch.Address);
        Assert.False(await AuditRowsAsync(db, seeded.Branch.BranchId).AnyAsync());
    }

    [SkippableFact]
    public async Task A_platform_admin_moves_the_branch()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var seeded = await SeedAsync(factory);

        using var platform = factory.CreateClientWithToken(
            await PlatformEndpointTests.SignInPlatformAdminAsync(factory, seeded.Admin));

        var moved = await platform.PutAsJsonAsync($"/api/branches/{seeded.Branch.BranchId}/listing", new
        {
            address = NewAddress,
            latitude = 40.1111,
            longitude = 44.2222,
        });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        await using var db = fixture.CreateContext(factory.Clock);
        var audit = await AuditRowsAsync(db, seeded.Branch.BranchId).SingleAsync();
        Assert.Equal(seeded.Admin.StaffMemberId, audit.ActorStaffMemberId);
    }

    // ------------------------------------------------------------ helpers

    private sealed record Seeded(AuthBranch Branch, PanelAccount Owner, PlatformAdminAccount Admin);

    private async Task<Seeded> SeedAsync(YallaApiFactory factory)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var branch = await AuthTestData.CreateBranchAsync(db);

        return new Seeded(
            branch,
            await AuthTestData.SeedOwnerAsync(db, branch.VenueId),
            await AuthTestData.CreatePlatformAdminAsync(db));
    }

    private static IQueryable<Yalla.Domain.Audit.PlatformAuditLog> AuditRowsAsync(
        Yalla.Infrastructure.Persistence.YallaDbContext db, Guid branchId) =>
        db.PlatformAuditLogs.AsNoTracking()
            .Where(l => l.TargetId == branchId && l.Action == BranchListingService.RelocateAuditAction);

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task<bool> NamesAsync(HttpResponseMessage response, string field, string bound)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        return problem.GetProperty("code").GetString() == "validation-failed"
               && problem.GetProperty("context").GetProperty("fields").EnumerateArray().Any(f =>
                   f.GetProperty("field").GetString() == field && f.GetProperty("bound").GetString() == bound);
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
