using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The manager's Today on the staff phone: what it counts, and which branches each caller may read it for.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class StaffTodayTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task A_branch_managers_phone_reads_its_branchs_day_with_the_overviews_own_counts()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        var now = factory.Clock.UtcNow;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db, tableCount: 3);
        }

        // A walk-in party of two seated by the waiter, and one booking waiting for the floor tomorrow.
        await using (var db = fixture.CreateContext(factory.Clock))
        {
            await AuthTestData.CreateOpenTabAsync(db, branch, branch.FirstTableId, now);

            var policy = await db.Branches.AsNoTracking().Where(b => b.Id == branch.BranchId).Select(b => b.ReservationPolicy).FirstAsync();
            db.Reservations.Add(Reservation.Create(
                branch.BranchId, branch.TableIds[1], now.AddDays(1), DateOnly.FromDateTime(now.AddDays(1)), new TimeOnly(19, 0),
                4, "Lilit M.", "+37411000001", Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), policy,
                initialStatus: ReservationStatus.PendingApproval));

            var zone = BranchZone.For("Asia/Yerevan");
            var started = zone.ToLocal(now).AddHours(-1);
            var start = new TimeOnly(started.Hour, started.Minute);
            db.StaffShifts.Add(new StaffShift(branch.WaiterId, branch.BranchId, DateOnly.FromDateTime(started), start, start.AddHours(4)));
            await db.SaveChangesAsync();
        }

        // The waiter is signed in on a tablet; the manager reads Today on their phone.
        _ = await StaffAuthTests.SignInWaiterAsync(factory, branch);
        using var phone = await ManagerPhoneAsync(factory, branch);

        var response = await phone.GetAsync("/api/staff/today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var today = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(branch.BranchId, today.GetProperty("branchId").GetGuid());
        Assert.Equal(branch.BranchId, Assert.Single(today.GetProperty("branches").EnumerateArray()).GetProperty("branchId").GetGuid());

        var room = today.GetProperty("room");
        Assert.Equal(1, room.GetProperty("occupiedTables").GetInt32());
        Assert.Equal(3, room.GetProperty("tables").GetInt32());
        Assert.Equal(2, room.GetProperty("people").GetInt32());

        var guests = today.GetProperty("guests");
        Assert.Equal(2, guests.GetProperty("walkIn").GetInt32());
        Assert.Equal(0, guests.GetProperty("withBooking").GetInt32());

        var revenue = today.GetProperty("revenue");
        Assert.Equal(revenue.GetProperty("hallAmd").GetInt64() + revenue.GetProperty("takeawayAmd").GetInt64(), revenue.GetProperty("totalAmd").GetInt64());

        Assert.Equal(1, today.GetProperty("needsYou").GetProperty("pendingApprovals").GetInt32());
        Assert.Equal(0, today.GetProperty("needsYou").GetProperty("unansweredMessages").GetInt32());

        var shift = Assert.Single(today.GetProperty("shifts").EnumerateArray());
        Assert.Equal(branch.WaiterId, shift.GetProperty("staffMemberId").GetGuid());
        Assert.True(shift.GetProperty("onShiftNow").GetBoolean());
        Assert.True(shift.GetProperty("signedIn").GetBoolean());

        // The same counts the console's Overview gives for the branch.
        using var console = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));
        var overview = await (await console.GetAsync($"/api/branches/{branch.BranchId}/overview")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(overview.GetProperty("revenue").GetProperty("todayAmd").GetInt64(), revenue.GetProperty("hallAmd").GetInt64());
        Assert.Equal(overview.GetProperty("bookings").GetProperty("total").GetInt32(), today.GetProperty("bookings").GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task Owners_and_general_managers_switch_branches_on_their_phone_but_nobody_does_on_a_tablet()
    {
        Skip.If(!fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        AuthBranch branch;
        Guid sibling;
        PanelAccount gm;
        const string gmPin = "6150";

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            sibling = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan", tableCount: 1);
            gm = await AuthTestData.SeedGeneralManagerAsync(db, branch.VenueId, pin: gmPin);
        }

        using var branchManagerPhone = await ManagerPhoneAsync(factory, branch);
        using var waiter = factory.CreateClientWithToken(await StaffAuthTests.SignInWaiterAsync(factory, branch));

        // The general manager's own phone, paired at this branch.
        using var gmPhone = await PhoneAsync(factory, branch, gm.StaffMemberId, gmPin, await AuthTestData.SignInAsync(factory, gm));

        var atSibling = await gmPhone.GetAsync($"/api/staff/today?branchId={sibling}");
        Assert.Equal(HttpStatusCode.OK, atSibling.StatusCode);

        var body = await atSibling.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sibling, body.GetProperty("branchId").GetGuid());
        Assert.Equal(2, body.GetProperty("branches").GetArrayLength());

        // The branch manager reaches their own branch only, and a waiter reads no Today at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await branchManagerPhone.GetAsync($"/api/staff/today?branchId={sibling}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.GetAsync("/api/staff/today")).StatusCode);

        // The same general manager on the shared tablet: the tablet's branch only.
        var tabletDevice = await StaffAuthTests.EnrolDeviceAsync(factory, branch);
        using var tablet = factory.CreateClientWithToken(tabletDevice);
        var signedIn = await tablet.PostAsJsonAsync("/api/auth/staff/pin", new { staffMemberId = gm.StaffMemberId, pin = gmPin });
        signedIn.EnsureSuccessStatusCode();

        using var gmOnTablet = factory.CreateClientWithToken(
            (await signedIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!);

        Assert.Equal(HttpStatusCode.Forbidden, (await gmOnTablet.GetAsync($"/api/staff/today?branchId={sibling}")).StatusCode);

        var here = await (await gmOnTablet.GetAsync("/api/staff/today")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(branch.BranchId, here.GetProperty("branchId").GetGuid());
        Assert.Equal(1, here.GetProperty("branches").GetArrayLength());

        // And from the console, the general manager reads any branch.
        using var console = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, gm));
        Assert.Equal(HttpStatusCode.OK, (await console.GetAsync($"/api/staff/today?branchId={sibling}")).StatusCode);
    }

    private static async Task<HttpClient> ManagerPhoneAsync(YallaApiFactory factory, AuthBranch branch) =>
        await PhoneAsync(factory, branch, branch.ManagerId, "9182", await StaffAuthTests.SignInManagerAsync(factory, branch));

    /// <summary>Pairs <paramref name="staffMemberId"/>'s phone with a code <paramref name="issuerToken"/>'s holder issues.</summary>
    private static async Task<HttpClient> PhoneAsync(
        YallaApiFactory factory, AuthBranch branch, Guid staffMemberId, string pin, string issuerToken)
    {
        using var manager = factory.CreateClientWithToken(issuerToken);
        using var anonymous = factory.CreateClient();

        var paired = await StaffPhoneTests.PairAsync(anonymous, await StaffPhoneTests.IssueAsync(manager, branch, staffMemberId), pin);
        paired.EnsureSuccessStatusCode();

        return factory.CreateClientWithToken(
            (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session").GetProperty("accessToken").GetString()!);
    }

    private YallaApiFactory NewFactory() => new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
