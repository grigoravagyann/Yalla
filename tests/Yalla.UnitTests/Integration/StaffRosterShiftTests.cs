using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// <c>GET /api/auth/staff/roster</c> says who is on shift: the PIN gate lists them first, and everybody else the
/// roster allows stays one tap away. Shifts never narrow who is listed or who may sign in.
/// </summary>
/// <remarks>
/// Each test builds its own branch in Asia/Yerevan (UTC+4 all year) unless it says otherwise, and reads the roster
/// at noon on Monday 19 October 2026 there unless it says otherwise. Shifts are seeded as rows, not through the
/// console, because the console refuses to plan the past and some of these shifts started yesterday.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class StaffRosterShiftTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Monday 19 October 2026 in Yerevan.</summary>
    private static readonly DateTime Noon = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 19);

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task Somebody_on_shift_now_is_marked_with_the_shift_in_wall_clock_and_as_instants()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);

        await ShiftsAsync(factory, new StaffShift(branch.WaiterId, branch.BranchId, Today, T(10), T(18)));

        var roster = await RosterAsync(factory, branch, Noon);
        var waiter = roster[branch.WaiterId];

        Assert.True(waiter.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal("10:00:00", waiter.GetProperty("startsAt").GetString());
        Assert.Equal("18:00:00", waiter.GetProperty("endsAt").GetString());
        Assert.False(waiter.GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(Utc(19, 6), waiter.GetProperty("startsAtUtc").GetDateTime());
        Assert.Equal(Utc(19, 14), waiter.GetProperty("endsAtUtc").GetDateTime());

        // Nobody planned the manager: listed as before, off shift, no times.
        AssertNoShift(roster[branch.ManagerId]);
    }

    [SkippableFact]
    public async Task Last_nights_shift_still_running_past_midnight_is_on_shift_and_one_that_ended_at_midnight_is_not()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);

        await ShiftsAsync(
            factory,
            new StaffShift(branch.WaiterId, branch.BranchId, Today, T(18), T(2)),
            new StaffShift(branch.ManagerId, branch.BranchId, Today, T(16), T(0)));

        // 01:00 on Tuesday the 20th in Yerevan: the shifts are dated Monday.
        var roster = await RosterAsync(factory, branch, Utc(19, 21));
        var waiter = roster[branch.WaiterId];

        Assert.True(waiter.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal("18:00:00", waiter.GetProperty("startsAt").GetString());
        Assert.Equal("02:00:00", waiter.GetProperty("endsAt").GetString());
        Assert.True(waiter.GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(Utc(19, 14), waiter.GetProperty("startsAtUtc").GetDateTime());
        Assert.Equal(Utc(19, 22), waiter.GetProperty("endsAtUtc").GetDateTime());

        AssertNoShift(roster[branch.ManagerId]);
    }

    [SkippableFact]
    public async Task A_shift_later_today_is_not_on_shift_now_but_says_when_it_starts()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);

        await ShiftsAsync(factory, new StaffShift(branch.WaiterId, branch.BranchId, Today, T(16), T(23)));

        var waiter = (await RosterAsync(factory, branch, Noon))[branch.WaiterId];

        Assert.False(waiter.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal("16:00:00", waiter.GetProperty("startsAt").GetString());
        Assert.Equal("23:00:00", waiter.GetProperty("endsAt").GetString());
        Assert.Equal(Utc(19, 12), waiter.GetProperty("startsAtUtc").GetDateTime());
    }

    [SkippableFact]
    public async Task A_shift_that_ended_earlier_today_is_neither_on_shift_nor_reported_and_the_end_is_exclusive()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);

        await ShiftsAsync(
            factory,
            new StaffShift(branch.WaiterId, branch.BranchId, Today, T(6), T(11)),
            new StaffShift(branch.ManagerId, branch.BranchId, Today, T(8), T(12)));

        var roster = await RosterAsync(factory, branch, Noon);

        AssertNoShift(roster[branch.WaiterId]);

        // Ends at 12:00 and it is 12:00: over.
        AssertNoShift(roster[branch.ManagerId]);
    }

    [SkippableFact]
    public async Task Of_several_shifts_in_a_day_the_current_one_is_reported_else_the_next_one_to_start()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);

        await ShiftsAsync(
            factory,
            new StaffShift(branch.WaiterId, branch.BranchId, Today, T(7), T(10)),
            new StaffShift(branch.WaiterId, branch.BranchId, Today, T(19), T(22)),
            new StaffShift(branch.WaiterId, branch.BranchId, Today, T(15), T(18)),
            new StaffShift(branch.ManagerId, branch.BranchId, Today, T(9), T(11)),
            new StaffShift(branch.ManagerId, branch.BranchId, Today, T(17), T(22)),
            new StaffShift(branch.ManagerId, branch.BranchId, Today, T(11, 30), T(13)));

        var roster = await RosterAsync(factory, branch, Noon);

        var waiter = roster[branch.WaiterId];
        Assert.False(waiter.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal(("15:00:00", "18:00:00"), (waiter.GetProperty("startsAt").GetString(), waiter.GetProperty("endsAt").GetString()));

        var manager = roster[branch.ManagerId];
        Assert.True(manager.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal(("11:30:00", "13:00:00"), (manager.GetProperty("startsAt").GetString(), manager.GetProperty("endsAt").GetString()));
    }

    [SkippableFact]
    public async Task Shifts_on_other_days_and_a_day_off_today_are_not_today()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);
        var anna = await PersonAsync(factory, branch.VenueId, "Anna Venue-Wide", branchId: null);

        await ShiftsAsync(
            factory,
            new StaffShift(branch.WaiterId, branch.BranchId, Today.AddDays(1), T(10), T(18)),
            new StaffShift(branch.ManagerId, branch.BranchId, Today.AddDays(-1), T(10), T(18)),
            new StaffShift(anna, branch.BranchId, Today, null, null));

        var roster = await RosterAsync(factory, branch, Noon);

        AssertNoShift(roster[branch.WaiterId]);
        AssertNoShift(roster[branch.ManagerId]);
        AssertNoShift(roster[anna]);
    }

    [SkippableFact]
    public async Task A_shift_at_another_branch_of_the_venue_does_not_count_on_this_tablet()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);
        Guid siblingId;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            siblingId = await AuthTestData.AddBranchAsync(db, branch.VenueId, "Asia/Yerevan");
        }

        // Works the whole venue, so this tablet lists her - but today she is at the sibling branch.
        var anna = await PersonAsync(factory, branch.VenueId, "Anna Venue-Wide", branchId: null);

        await ShiftsAsync(factory, new StaffShift(anna, siblingId, Today, T(10), T(18)));

        var roster = await RosterAsync(factory, branch, Noon);

        AssertNoShift(roster[anna]);
    }

    /// <summary>
    /// The branch's zone, not the server's and not UTC. The zone is one neither this host nor CI is in, so a reader
    /// that took the host's or UTC's wall clock is caught here whichever machine runs it.
    /// </summary>
    [SkippableFact]
    public async Task Shifts_are_read_in_the_branchs_time_zone_not_the_servers_or_utcs()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var zoneId = new[] { "Asia/Tokyo", "Pacific/Honolulu" }.First(id =>
            TimeZoneInfo.FindSystemTimeZoneById(id).BaseUtcOffset != TimeZoneInfo.Local.BaseUtcOffset);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory, zoneId);
        var host = await PersonAsync(factory, branch.VenueId, "Host Clock", branch.BranchId);

        // 05:30 on Tuesday the 20th at the branch.
        var branchDate = new DateOnly(2026, 10, 20);
        var nowUtc = TimeZoneInfo.ConvertTimeToUtc(branchDate.ToDateTime(T(5, 30)), zone);
        var hostNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);

        await ShiftsAsync(
            factory,
            new StaffShift(branch.WaiterId, branch.BranchId, branchDate, T(5), T(13)),

            // Running now by the UTC wall clock, and by this host's: at the branch it is some other hour.
            new StaffShift(
                branch.ManagerId, branch.BranchId, DateOnly.FromDateTime(nowUtc), T(nowUtc.Hour), T((nowUtc.Hour + 1) % 24)),
            new StaffShift(
                host, branch.BranchId, DateOnly.FromDateTime(hostNow), T(hostNow.Hour), T((hostNow.Hour + 1) % 24)));

        var roster = await RosterAsync(factory, branch, nowUtc);
        var waiter = roster[branch.WaiterId];

        Assert.True(waiter.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal("05:00:00", waiter.GetProperty("startsAt").GetString());
        Assert.Equal(
            TimeZoneInfo.ConvertTimeToUtc(branchDate.ToDateTime(T(5)), zone),
            waiter.GetProperty("startsAtUtc").GetDateTime());

        Assert.False(roster[branch.ManagerId].GetProperty("onShiftNow").GetBoolean());
        Assert.False(roster[host].GetProperty("onShiftNow").GetBoolean());
    }

    /// <summary>
    /// Shifts add to the roster and take nothing away: the deactivated stay off it whatever their shifts say, and
    /// somebody with no shift is still listed and still signs in.
    /// </summary>
    [SkippableFact]
    public async Task Shifts_never_change_who_is_listed_or_who_may_sign_in()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);
        var deactivated = await PersonAsync(factory, branch.VenueId, "Abel Deactivated", branch.BranchId, active: false);

        await ShiftsAsync(
            factory,
            new StaffShift(deactivated, branch.BranchId, Today, T(10), T(18)),
            new StaffShift(branch.ManagerId, branch.BranchId, Today, T(10), T(18)));

        var deviceToken = await StaffAuthTests.EnrolDeviceAsync(factory, branch);
        factory.Clock.UtcNow = Noon;

        using var tablet = factory.CreateClientWithToken(deviceToken);
        var roster = await ReadRosterAsync(tablet);

        // The same two people, by name, as before shifts existed.
        Assert.Equal([branch.ManagerId, branch.WaiterId], roster.Keys.ToArray());
        Assert.DoesNotContain(deactivated, roster.Keys);
        Assert.True(roster[branch.ManagerId].GetProperty("onShiftNow").GetBoolean());
        AssertNoShift(roster[branch.WaiterId]);

        // Off shift, and the PIN still opens the tablet.
        var signedIn = await tablet.PostAsJsonAsync(
            "/api/auth/staff/pin", new { staffMemberId = branch.WaiterId, pin = branch.WaiterPin });

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertNoShift(JsonElement person)
    {
        Assert.False(person.GetProperty("onShiftNow").GetBoolean());
        Assert.False(person.GetProperty("endsNextDay").GetBoolean());

        foreach (var field in new[] { "startsAt", "endsAt", "startsAtUtc", "endsAtUtc" })
        {
            Assert.False(person.TryGetProperty(field, out _), $"'{field}' was sent for somebody with no shift to report.");
        }
    }

    /// <summary>Enrols a tablet on the real clock, so its token is good, then reads the roster at <paramref name="nowUtc"/>.</summary>
    private static async Task<Dictionary<Guid, JsonElement>> RosterAsync(
        YallaApiFactory factory, AuthBranch branch, DateTime nowUtc)
    {
        var deviceToken = await StaffAuthTests.EnrolDeviceAsync(factory, branch);
        factory.Clock.UtcNow = nowUtc;

        using var tablet = factory.CreateClientWithToken(deviceToken);

        return await ReadRosterAsync(tablet);
    }

    private static async Task<Dictionary<Guid, JsonElement>> ReadRosterAsync(HttpClient tablet)
    {
        var response = await tablet.GetAsync("/api/auth/staff/roster");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray()
            .ToDictionary(p => p.GetProperty("staffMemberId").GetGuid());
    }

    private async Task<AuthBranch> BranchAsync(YallaApiFactory factory, string? timeZoneId = null)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        var branch = await AuthTestData.CreateBranchAsync(db);

        if (timeZoneId is not null)
        {
            var row = await db.Branches.FirstAsync(b => b.Id == branch.BranchId);
            row.SetTimeZone(timeZoneId);
            await db.SaveChangesAsync();
        }

        return branch;
    }

    private async Task<Guid> PersonAsync(
        YallaApiFactory factory, Guid venueId, string name, Guid? branchId, bool active = true)
    {
        await using var db = fixture.CreateContext(factory.Clock);

        var person = new StaffMember(
            venueId, name, $"+374{Random.Shared.Next(10_000_000, 99_999_999)}", StaffRole.Waiter, "hash", branchId);
        person.SetActive(active);

        db.StaffMembers.Add(person);
        await db.SaveChangesAsync();

        return person.Id;
    }

    private async Task ShiftsAsync(YallaApiFactory factory, params StaffShift[] shifts)
    {
        await using var db = fixture.CreateContext(factory.Clock);
        db.StaffShifts.AddRange(shifts);
        await db.SaveChangesAsync();
    }

    private YallaApiFactory NewFactory() =>
        new YallaApiFactory().WithDatabase(fixture.ConnectionString);
}
