using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Auth;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

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

    private static DateTime At(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

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
    public async Task Shifts_beyond_the_twelve_hour_look_ahead_over_ones_and_a_day_off_are_not_reported()
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
    /// The next shift is looked for twelve hours ahead, not to midnight: at 23:50, tomorrow's 00:10 is the next one.
    /// </summary>
    [SkippableFact]
    public async Task The_next_shift_is_reported_up_to_twelve_hours_ahead_across_midnight()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory);
        var anna = await PersonAsync(factory, branch.VenueId, "Anna Venue-Wide", branchId: null);
        var tomorrow = Today.AddDays(1);

        await ShiftsAsync(
            factory,
            new StaffShift(branch.WaiterId, branch.BranchId, tomorrow, T(0, 10), T(8)),
            new StaffShift(anna, branch.BranchId, tomorrow, T(11, 50), T(18)),
            new StaffShift(branch.ManagerId, branch.BranchId, tomorrow, T(12), T(20)));

        // 23:50 on Monday in Yerevan.
        var roster = await RosterAsync(factory, branch, Utc(19, 19, 50));

        var waiter = roster[branch.WaiterId];
        Assert.False(waiter.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal("00:10:00", waiter.GetProperty("startsAt").GetString());
        Assert.Equal(Utc(19, 20, 10), waiter.GetProperty("startsAtUtc").GetDateTime());

        // Exactly twelve hours ahead is in; ten minutes more is not.
        Assert.Equal("11:50:00", roster[anna].GetProperty("startsAt").GetString());
        AssertNoShift(roster[branch.ManagerId]);
    }

    // ---------------------------------------------------------------- clock changes

    // Berlin's clocks go back from 03:00 summer time to 02:00 winter time at 01:00Z on Sunday 25 October 2026, so
    // 02:00-03:00 happens twice - 00:00Z-01:00Z, then 01:00Z-02:00Z - and forward from 02:00 to 03:00 at 01:00Z on
    // Sunday 29 March 2026, so 02:00-03:00 never happens that night.
    private const string Berlin = "Europe/Berlin";

    private static readonly DateOnly FallBack = new(2026, 10, 25);

    private static readonly DateOnly SpringForward = new(2026, 3, 29);

    /// <summary>The review's probe: the wall clock put a shift back on in the repeated hour after it had ended.</summary>
    [SkippableFact]
    public async Task A_shift_ending_in_the_repeated_hour_is_over_on_its_second_pass()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory, Berlin);

        await ShiftsAsync(factory, new StaffShift(branch.WaiterId, branch.BranchId, FallBack, T(1), T(2, 30)));

        var tablet = await TabletAsync(factory, branch);

        // 02:15 summer time: running, and over at the first 02:30.
        var first = (await RosterAtAsync(factory, tablet, At(10, 25, 0, 15)))[branch.WaiterId];
        Assert.True(first.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal(At(10, 24, 23, 0), first.GetProperty("startsAtUtc").GetDateTime());
        Assert.Equal(At(10, 25, 0, 30), first.GetProperty("endsAtUtc").GetDateTime());

        // 02:45 summer time, then 02:15 winter time: over both times.
        AssertNoShift((await RosterAtAsync(factory, tablet, At(10, 25, 0, 45)))[branch.WaiterId]);
        AssertNoShift((await RosterAtAsync(factory, tablet, At(10, 25, 1, 15)))[branch.WaiterId]);
    }

    [SkippableFact]
    public async Task A_shift_starting_in_the_repeated_hour_starts_at_its_first_pass_and_stays_on()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory, Berlin);

        await ShiftsAsync(factory, new StaffShift(branch.WaiterId, branch.BranchId, FallBack, T(2, 30), T(8)));

        var tablet = await TabletAsync(factory, branch);

        // 02:15 summer time: not yet, and it says when - the first 02:30.
        var before = (await RosterAtAsync(factory, tablet, At(10, 25, 0, 15)))[branch.WaiterId];
        Assert.False(before.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal(At(10, 25, 0, 30), before.GetProperty("startsAtUtc").GetDateTime());

        // 02:45 summer time, then 02:15 winter time - before 02:30 on the clock, but long since started.
        foreach (var now in new[] { At(10, 25, 0, 45), At(10, 25, 1, 15) })
        {
            var during = (await RosterAtAsync(factory, tablet, now))[branch.WaiterId];

            Assert.True(during.GetProperty("onShiftNow").GetBoolean(), $"Not on shift at {now:HH:mm}Z.");
            Assert.Equal(At(10, 25, 0, 30), during.GetProperty("startsAtUtc").GetDateTime());
            Assert.Equal(At(10, 25, 7, 0), during.GetProperty("endsAtUtc").GetDateTime());
        }
    }

    /// <summary>
    /// 02:30 does not exist that night. The shift starts when a watch not yet put forward reads 02:30 - 03:30 summer
    /// time, 01:30Z - and the roster says so, so a screen re-checking the instants agrees with it.
    /// </summary>
    [SkippableFact]
    public async Task A_shift_starting_in_the_skipped_hour_starts_on_the_clock_from_before_the_change_and_has_its_instants()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = NewFactory();
        var branch = await BranchAsync(factory, Berlin);

        await ShiftsAsync(factory, new StaffShift(branch.WaiterId, branch.BranchId, SpringForward, T(2, 30), T(10)));

        var tablet = await TabletAsync(factory, branch);

        // 03:15 summer time: after 02:30 on the clock, but not yet.
        var before = (await RosterAtAsync(factory, tablet, At(3, 29, 1, 15)))[branch.WaiterId];
        Assert.False(before.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal("02:30:00", before.GetProperty("startsAt").GetString());
        Assert.Equal(At(3, 29, 1, 30), before.GetProperty("startsAtUtc").GetDateTime());
        Assert.Equal(At(3, 29, 8, 0), before.GetProperty("endsAtUtc").GetDateTime());

        // 03:45 summer time: on.
        var during = (await RosterAtAsync(factory, tablet, At(3, 29, 1, 45)))[branch.WaiterId];
        Assert.True(during.GetProperty("onShiftNow").GetBoolean());
        Assert.Equal(At(3, 29, 1, 30), during.GetProperty("startsAtUtc").GetDateTime());
    }

    // ---------------------------------------------------------------- cost

    /// <summary>
    /// The roster is three statements - the device, the people, the branch's shifts - however many people and shifts
    /// there are: no read per person.
    /// </summary>
    [SkippableFact]
    public async Task The_roster_reads_in_three_statements_however_many_people_and_shifts()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var counter = new CommandCountingInterceptor();
        await using var factory = NewFactory()
            .WithServices(services => services.ConfigureDbContext<YallaDbContext>(o => o.AddInterceptors(counter)));

        var small = await BranchAsync(factory);
        var large = await BranchAsync(factory);
        var people = new List<Guid> { large.WaiterId, large.ManagerId };

        for (var i = 0; i < 4; i++)
        {
            people.Add(await PersonAsync(factory, large.VenueId, $"Extra Person {i}", large.BranchId));
        }

        await ShiftsAsync(
            factory,
            [
                .. people.Select(id => new StaffShift(id, large.BranchId, Today, T(10), T(14))),
                .. people.Select(id => new StaffShift(id, large.BranchId, Today, T(18), T(23))),
            ]);

        await StaffAuthTests.EnrolDeviceAsync(factory, small);
        await StaffAuthTests.EnrolDeviceAsync(factory, large);
        factory.Clock.UtcNow = Noon;

        var statements = new Dictionary<string, (int Count, int People)>();

        foreach (var (name, branch) in new[] { ("small", small), ("large", large) })
        {
            Guid deviceId;

            await using (var db = fixture.CreateContext(factory.Clock))
            {
                deviceId = await db.StaffDevices.Where(d => d.BranchId == branch.BranchId).Select(d => d.Id).SingleAsync();
            }

            using var scope = factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IStaffAuthService>();

            var before = counter.Count;
            var roster = await service.GetRosterAsync(deviceId);

            statements[name] = (counter.Count - before, roster.Count);
        }

        Assert.Equal((3, 2), statements["small"]);
        Assert.Equal((3, 6), statements["large"]);
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

    /// <summary>Enrols a tablet on the real clock, so its token is good.</summary>
    private static async Task<string> TabletAsync(YallaApiFactory factory, AuthBranch branch) =>
        await StaffAuthTests.EnrolDeviceAsync(factory, branch);

    /// <summary>The roster on an enrolled tablet at <paramref name="nowUtc"/>.</summary>
    private static async Task<Dictionary<Guid, JsonElement>> RosterAtAsync(
        YallaApiFactory factory, string deviceToken, DateTime nowUtc)
    {
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
        var branch = await AuthTestData.CreateBranchAsync(db, enrolTablet: false);

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
