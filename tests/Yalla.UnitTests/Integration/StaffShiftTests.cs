using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yalla.Application.Staff;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// The Staff screen's shifts: planned by whoever may manage the person, one person in one place at a time, read on
/// the branch's service day with the "on shift today" count.
/// </summary>
/// <remarks>
/// Each test builds its own venue in Asia/Yerevan (UTC+4 all year), open 10:00-23:00 every day. Now is noon on
/// Monday 19 October 2026 unless a test says otherwise.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class StaffShiftTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Monday 19 October 2026 in Yerevan.</summary>
    private static readonly DateTime Noon = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 10, 19);

    private static readonly DateOnly Tomorrow = Today.AddDays(1);

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    private static TimeOnly T(int hour, int minute = 0) => new(hour, minute);

    // ---------------------------------------------------------------- planning and reading

    [SkippableFact]
    public async Task Shifts_are_listed_from_the_service_day_with_today_counted_and_the_inactive_left_out()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Noon);
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db);
        var shifts = fixture.CreateStaffShiftService(db, clock, TestActor.Owner(team.OwnerId));

        var waiter = await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.WaiterId, Today, T(16), T(0)));
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.ManagerId, Today, T(10), T(19)));
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.GeneralManagerId, Today, null, null, DayOff: true));
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.CookId, Tomorrow, T(11), T(19)));
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.SecondWaiterId, Today, T(12), T(20)));

        // At the other branch: the general manager's working day there is not this branch's count.
        await shifts.CreateAsync(team.SiblingBranchId, new CreateShiftCommand(team.OwnerId, Today, T(9), T(17)));

        // An overnight shift ends on the next date, at midnight - 20:00 UTC.
        Assert.Equal((T(16), T(0), true, false), (waiter.StartsAt, waiter.EndsAt, waiter.EndsNextDay, waiter.DayOff));
        Assert.Equal(new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc), waiter.StartsAtUtc);
        Assert.Equal(new DateTime(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc), waiter.EndsAtUtc);

        var listed = await shifts.ListAsync(team.BranchId);

        Assert.Equal(Today, listed.ServiceDate);
        Assert.Equal((Today, Today.AddDays(13)), (listed.From, listed.To));
        Assert.Equal(3, listed.OnShiftToday);

        // By date, a day off first, then by start.
        Assert.Equal(
            [
                (team.GeneralManagerId, Today, true),
                (team.ManagerId, Today, false),
                (team.SecondWaiterId, Today, false),
                (team.WaiterId, Today, false),
                (team.CookId, Tomorrow, false),
            ],
            listed.Shifts.Select(s => (s.StaffMemberId, s.Date, s.DayOff)));

        var dayOff = listed.Shifts[0];
        Assert.Null(dayOff.StartsAt);
        Assert.Null(dayOff.StartsAtUtc);
        Assert.False(dayOff.EndsNextDay);

        // Deactivated: hidden and not counted, but kept.
        await DeactivateAsync(db, team.SecondWaiterId);

        var after = await shifts.ListAsync(team.BranchId);

        Assert.Equal(2, after.OnShiftToday);
        Assert.DoesNotContain(team.SecondWaiterId, after.Shifts.Select(s => s.StaffMemberId));
        Assert.Equal(1, await db.StaffShifts.CountAsync(s => s.StaffMemberId == team.SecondWaiterId));

        // A window of its own; tomorrow only.
        var tomorrow = await shifts.ListAsync(team.BranchId, Tomorrow, Tomorrow);

        Assert.Equal(team.CookId, Assert.Single(tomorrow.Shifts).StaffMemberId);
        Assert.Equal(2, tomorrow.OnShiftToday);
    }

    [SkippableFact]
    public async Task After_midnight_last_nights_service_is_still_today()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // 00:30 on Tuesday in Yerevan, with the branch open 10:00-01:00: still Monday's service.
        var clock = new TestClock(new DateTime(2026, 10, 19, 20, 30, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db, TestBranchBuilder.LateNightEveryDay);
        var shifts = fixture.CreateStaffShiftService(db, clock, TestActor.Owner(team.OwnerId));

        // Monday is not past yet, so it can still be planned, and it is what "today" counts.
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.WaiterId, Today, T(18), T(2)));
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.ManagerId, Tomorrow, T(10), T(18)));

        var listed = await shifts.ListAsync(team.BranchId);

        Assert.Equal(Today, listed.ServiceDate);
        Assert.Equal(Today, listed.From);
        Assert.Equal(1, listed.OnShiftToday);
        Assert.Equal(2, listed.Shifts.Count);

        // Sunday is history.
        var past = await Assert.ThrowsAsync<FieldValidationException>(() => shifts.CreateAsync(
            team.BranchId, new CreateShiftCommand(team.CookId, Today.AddDays(-1), T(10), T(18))));

        Assert.Equal(("date", FieldBounds.Min), (past.Field, past.Violations[0].Bound));
    }

    // ---------------------------------------------------------------- one person, one place

    [SkippableFact]
    public async Task One_person_works_one_shift_at_a_time_across_the_venues_branches()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Noon);
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db);
        var shifts = fixture.CreateStaffShiftService(db, clock, TestActor.Owner(team.OwnerId));

        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.GeneralManagerId, Today, T(10), T(16)));

        // The same general manager at the other branch, overlapping: refused by name.
        var overlap = await Assert.ThrowsAsync<FieldValidationException>(() => shifts.CreateAsync(
            team.SiblingBranchId, new CreateShiftCommand(team.GeneralManagerId, Today, T(15), T(20))));

        Assert.Equal(("startsAt", FieldBounds.Conflict), (overlap.Field, overlap.Violations[0].Bound));

        // Touching is a handover, not a clash.
        var later = await shifts.CreateAsync(
            team.SiblingBranchId, new CreateShiftCommand(team.GeneralManagerId, Today, T(16), T(20)));

        // A day off on a date with work on it, either way round.
        var dayOff = await Assert.ThrowsAsync<FieldValidationException>(() => shifts.CreateAsync(
            team.BranchId, new CreateShiftCommand(team.GeneralManagerId, Today, null, null, DayOff: true)));

        Assert.Equal(("date", FieldBounds.Conflict), (dayOff.Field, dayOff.Violations[0].Bound));

        // A change is checked against the others but never against itself.
        await shifts.UpdateAsync(team.SiblingBranchId, later.Id, new UpdateShiftCommand(Today, T(16), T(21)));

        await Assert.ThrowsAsync<FieldValidationException>(() => shifts.UpdateAsync(
            team.SiblingBranchId, later.Id, new UpdateShiftCommand(Today, T(12), T(21))));

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            [(T(10), T(16)), (T(16), T(21))],
            (await verify.StaffShifts.AsNoTracking()
                .Where(s => s.StaffMemberId == team.GeneralManagerId)
                .OrderBy(s => s.StartsAt)
                .Select(s => new { s.StartsAt, s.EndsAt })
                .ToListAsync())
            .Select(s => (s.StartsAt!.Value, s.EndsAt!.Value)));
    }

    [SkippableFact]
    public async Task Two_overlapping_shifts_saved_at_once_cannot_both_land()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Noon);
        TeamIds team;

        await using (var setup = fixture.CreateContext(clock))
        {
            team = await TeamAsync(setup);
        }

        Task? secondSave = null;

        // The co-owner's save, for the general manager at the other branch, starts just before the owner's inserts.
        // It cannot finish there - it waits on the person's lock the owner's save holds - so it is given LockWait,
        // the owner's commits, and the co-owner's is awaited after: it must then see the clash.
        var race = new BeforeFirstInsertInto("StaffShifts", async () =>
        {
            secondSave = Task.Run(async () =>
            {
                await using var otherDb = fixture.CreateContext(clock);

                await fixture.CreateStaffShiftService(otherDb, clock, TestActor.Owner(team.CoOwnerId))
                    .CreateAsync(team.SiblingBranchId, new CreateShiftCommand(team.GeneralManagerId, Today, T(12), T(18)));
            });

            await Task.WhenAny(secondSave, Task.Delay(LockWait));
        });

        await using var racingDb = fixture.CreateContext(clock, race);

        await fixture.CreateStaffShiftService(racingDb, clock, TestActor.Owner(team.OwnerId))
            .CreateAsync(team.BranchId, new CreateShiftCommand(team.GeneralManagerId, Today, T(10), T(16)));

        Assert.True(race.Fired, "The race never happened, so this proves nothing.");

        var refused = await Assert.ThrowsAsync<FieldValidationException>(() => secondSave!);

        Assert.Equal("startsAt", refused.Field);

        await using var verify = fixture.CreateContext(clock);
        var landed = Assert.Single(await verify.StaffShifts.AsNoTracking()
            .Where(s => s.StaffMemberId == team.GeneralManagerId)
            .ToListAsync());

        Assert.Equal(team.BranchId, landed.BranchId);
    }

    // ---------------------------------------------------------------- who

    [SkippableFact]
    public async Task A_branch_manager_plans_their_own_people_and_themselves_and_nobody_above_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Noon);
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db);
        var shifts = fixture.CreateStaffShiftService(db, clock, TestActor.Manager(team.ManagerId));

        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.WaiterId, Today, T(16), T(23)));
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.CookId, Today, T(9), T(17)));
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.ManagerId, Today, T(10), T(19)));

        // Above, or level: the general manager, the owner, another branch manager of the same branch.
        foreach (var above in new[] { team.GeneralManagerId, team.OwnerId, team.SecondManagerId })
        {
            await Assert.ThrowsAsync<StaffPermissionException>(() => shifts.CreateAsync(
                team.BranchId, new CreateShiftCommand(above, Tomorrow, T(10), T(18))));
        }

        // The other branch is out of reach altogether, and its people do not work here.
        await Assert.ThrowsAsync<StaffPermissionException>(() => shifts.ListAsync(team.SiblingBranchId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => shifts.CreateAsync(
            team.BranchId, new CreateShiftCommand(team.SiblingWaiterId, Today, T(10), T(18))));

        // A shift somebody above planned for the general manager is theirs to change, not the manager's.
        var owners = fixture.CreateStaffShiftService(db, clock, TestActor.Owner(team.OwnerId));
        var gmShift = await owners.CreateAsync(
            team.BranchId, new CreateShiftCommand(team.GeneralManagerId, Tomorrow, T(10), T(18)));

        await Assert.ThrowsAsync<StaffPermissionException>(() => shifts.DeleteAsync(team.BranchId, gmShift.Id));
        await Assert.ThrowsAsync<StaffPermissionException>(() => shifts.UpdateAsync(
            team.BranchId, gmShift.Id, new UpdateShiftCommand(Tomorrow, T(11), T(18))));

        // A deactivated waiter is not planned.
        await DeactivateAsync(db, team.SecondWaiterId);

        await Assert.ThrowsAsync<DomainStateException>(() => shifts.CreateAsync(
            team.BranchId, new CreateShiftCommand(team.SecondWaiterId, Tomorrow, T(10), T(18))));
    }

    [SkippableFact]
    public async Task A_shift_is_moved_turned_into_a_day_off_and_removed()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Noon);
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db);
        var shifts = fixture.CreateStaffShiftService(db, clock, TestActor.Owner(team.OwnerId));

        var shift = await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.WaiterId, Today, T(10), T(18)));

        var moved = await shifts.UpdateAsync(team.BranchId, shift.Id, new UpdateShiftCommand(Tomorrow, T(18), T(2)));

        Assert.Equal((shift.Id, Tomorrow, true), (moved.Id, moved.Date, moved.EndsNextDay));

        var off = await shifts.UpdateAsync(team.BranchId, shift.Id, new UpdateShiftCommand(Tomorrow, null, null, DayOff: true));

        Assert.True(off.DayOff);
        Assert.Null(off.EndsAt);

        // Through another branch's route it is not there.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => shifts.DeleteAsync(team.SiblingBranchId, shift.Id));

        await shifts.DeleteAsync(team.BranchId, shift.Id);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => shifts.DeleteAsync(team.BranchId, shift.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => shifts.UpdateAsync(
            team.BranchId, shift.Id, new UpdateShiftCommand(Tomorrow, T(10), T(18))));

        Assert.Empty((await shifts.ListAsync(team.BranchId)).Shifts);
    }

    // ---------------------------------------------------------------- review round 1

    [SkippableFact]
    public async Task Moving_a_person_takes_their_future_shifts_off_the_branches_they_leave()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Noon);
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db);
        var shifts = fixture.CreateStaffShiftService(db, clock, TestActor.Owner(team.OwnerId));
        var staff = fixture.CreateStaffManagementService(db, clock, TestActor.Owner(team.OwnerId));

        // The waiter's history at the branch, and two shifts still to come there.
        db.StaffShifts.Add(new StaffShift(team.WaiterId, team.BranchId, Today.AddDays(-1), T(10), T(18)));
        await db.SaveChangesAsync();
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.WaiterId, Today, T(16), T(23)));
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.WaiterId, Tomorrow, T(16), T(23)));

        // The general manager works both branches tomorrow.
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.GeneralManagerId, Tomorrow, T(9), T(13)));
        await shifts.CreateAsync(team.SiblingBranchId, new CreateShiftCommand(team.GeneralManagerId, Tomorrow, T(14), T(20)));

        // The waiter moves to the sibling branch; the general manager becomes its branch manager.
        await staff.UpdateAsync(team.VenueId, team.WaiterId, new UpdateStaffCommand(SetBranch: true, BranchId: team.SiblingBranchId));
        await staff.UpdateAsync(
            team.VenueId,
            team.GeneralManagerId,
            new UpdateStaffCommand(Role: StaffRole.Manager, SetBranch: true, BranchId: team.SiblingBranchId));

        await using (var verify = fixture.CreateContext(clock))
        {
            // Today on at the branch they left is gone; history stays, and the sibling's shift stays.
            Assert.Equal(
                [(team.BranchId, Today.AddDays(-1))],
                (await verify.StaffShifts.AsNoTracking().Where(s => s.StaffMemberId == team.WaiterId)
                    .Select(s => new { s.BranchId, s.Date }).ToListAsync())
                .Select(s => (s.BranchId, s.Date)));

            Assert.Equal(
                [team.SiblingBranchId],
                await verify.StaffShifts.AsNoTracking().Where(s => s.StaffMemberId == team.GeneralManagerId)
                    .Select(s => s.BranchId).ToListAsync());
        }

        // A shift orphaned some other way - the row moved without the service - is not listed, counted or clashed
        // with at the branch the person left, and can still be taken off.
        var stale = await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.CookId, Today, T(9), T(17)));

        var cook = await db.StaffMembers.FirstAsync(s => s.Id == team.CookId);
        cook.Reassign(StaffRole.Kitchen, team.SiblingBranchId);
        await db.SaveChangesAsync();

        var listed = await shifts.ListAsync(team.BranchId, Today.AddDays(-1), Tomorrow);

        Assert.Empty(listed.Shifts);
        Assert.Equal(0, listed.OnShiftToday);

        await shifts.CreateAsync(team.SiblingBranchId, new CreateShiftCommand(team.CookId, Today, T(10), T(16)));
        await shifts.DeleteAsync(team.BranchId, stale.Id);

        Assert.Equal(
            [team.SiblingBranchId],
            await db.StaffShifts.AsNoTracking().Where(s => s.StaffMemberId == team.CookId).Select(s => s.BranchId).ToListAsync());
    }

    [SkippableFact]
    public async Task A_branch_manager_cannot_plan_somebody_who_works_the_whole_venue()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Noon);
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db);

        // A waiter with no branch is on every branch's tablets - further than a branch manager reaches.
        await Assert.ThrowsAsync<StaffPermissionException>(() => fixture
            .CreateStaffShiftService(db, clock, TestActor.Manager(team.ManagerId))
            .CreateAsync(team.BranchId, new CreateShiftCommand(team.VenueWideWaiterId, Today, T(10), T(18))));

        var planned = await fixture.CreateStaffShiftService(db, clock, TestActor.GeneralManager(team.GeneralManagerId))
            .CreateAsync(team.BranchId, new CreateShiftCommand(team.VenueWideWaiterId, Today, T(10), T(18)));

        Assert.Equal(team.VenueWideWaiterId, planned.StaffMemberId);
    }

    [SkippableFact]
    public async Task A_past_shift_is_history_unless_it_is_still_running()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        // 00:30 on Tuesday in Yerevan; the branch shut at 23:00, so Tuesday is the service day.
        var clock = new TestClock(new DateTime(2026, 10, 19, 20, 30, 0, DateTimeKind.Utc));
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db);
        var shifts = fixture.CreateStaffShiftService(db, clock, TestActor.Owner(team.OwnerId));

        var ended = new StaffShift(team.CookId, team.BranchId, Today, T(10), T(18));
        var running = new StaffShift(team.WaiterId, team.BranchId, Today, T(18), T(2));
        db.StaffShifts.AddRange(ended, running);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainStateException>(() => shifts.UpdateAsync(
            team.BranchId, ended.Id, new UpdateShiftCommand(Today, T(10), T(19))));
        await Assert.ThrowsAsync<DomainStateException>(() => shifts.DeleteAsync(team.BranchId, ended.Id));

        // Still running at 00:30: its end may be corrected on its own date, not moved earlier.
        var corrected = await shifts.UpdateAsync(team.BranchId, running.Id, new UpdateShiftCommand(Today, T(18), T(3)));

        Assert.Equal((Today, T(3)), (corrected.Date, corrected.EndsAt!.Value));

        var earlier = await Assert.ThrowsAsync<FieldValidationException>(() => shifts.UpdateAsync(
            team.BranchId, running.Id, new UpdateShiftCommand(Today.AddDays(-1), T(18), T(3))));

        Assert.Equal(("date", FieldBounds.Min), (earlier.Field, earlier.Violations[0].Bound));

        await shifts.DeleteAsync(team.BranchId, running.Id);
    }

    [SkippableFact]
    public async Task Shifts_at_branches_in_two_zones_clash_by_the_clock_on_the_wall_of_neither()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Noon);
        await using var db = fixture.CreateContext(clock);
        var team = await TeamAsync(db, siblingTimeZoneId: "Europe/London");
        var shifts = fixture.CreateStaffShiftService(db, clock, TestActor.Owner(team.OwnerId));

        // Tomorrow, in Yerevan: 10:00-13:00 is 06:00-09:00 UTC.
        await shifts.CreateAsync(team.BranchId, new CreateShiftCommand(team.GeneralManagerId, Tomorrow, T(10), T(13)));

        // London on summer time: 07:00-09:00 is 06:00-08:00 UTC - the same hours, whatever the wall clocks say.
        await Assert.ThrowsAsync<FieldValidationException>(() => shifts.CreateAsync(
            team.SiblingBranchId, new CreateShiftCommand(team.GeneralManagerId, Tomorrow, T(7), T(9))));

        // 11:00-12:00 in London is 10:00-11:00 UTC: after the Yerevan shift, though the wall clocks overlap.
        await shifts.CreateAsync(team.SiblingBranchId, new CreateShiftCommand(team.GeneralManagerId, Tomorrow, T(11), T(12)));
    }

    // ---------------------------------------------------------------- over HTTP

    [SkippableFact]
    public async Task The_console_plans_reads_changes_and_removes_shifts_over_http()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);
        AuthBranch branch;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
        }

        using var manager = factory.CreateClientWithToken(await StaffAuthTests.SignInManagerAsync(factory, branch));

        // Signed in on the real clock, so the token is good; the branch's day is then noon on the Monday.
        factory.Clock.UtcNow = Noon;
        var route = $"/api/branches/{branch.BranchId}/shifts";

        var created = await manager.PostAsJsonAsync(
            route, new { staffMemberId = branch.WaiterId, date = "2026-10-19", startsAt = "16:00", endsAt = "00:00" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var shift = await created.Content.ReadFromJsonAsync<JsonElement>();
        var shiftId = shift.GetProperty("id").GetGuid();

        Assert.Equal($"{route}/{shiftId}", created.Headers.Location?.OriginalString);
        Assert.Equal("16:00:00", shift.GetProperty("startsAt").GetString());
        Assert.Equal("00:00:00", shift.GetProperty("endsAt").GetString());
        Assert.True(shift.GetProperty("endsNextDay").GetBoolean());
        Assert.False(shift.GetProperty("dayOff").GetBoolean());

        // A day off: no times on the wire at all.
        var off = await (await manager.PostAsJsonAsync(
                route, new { staffMemberId = branch.ManagerId, date = "2026-10-20", dayOff = true }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(off.GetProperty("dayOff").GetBoolean());
        Assert.False(off.TryGetProperty("startsAt", out _));

        // Every refusal names its field.
        var noDate = await manager.PostAsJsonAsync(route, new { staffMemberId = branch.WaiterId, startsAt = "10:00", endsAt = "12:00" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noDate.StatusCode);
        Assert.Equal("date", (await noDate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("field").GetString());

        var overlap = await manager.PostAsJsonAsync(
            route, new { staffMemberId = branch.WaiterId, date = "2026-10-19", startsAt = "22:00", endsAt = "23:30" });
        var overlapBody = await overlap.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, overlap.StatusCode);
        Assert.Equal("validation-failed", overlapBody.GetProperty("code").GetString());
        Assert.Equal("startsAt", overlapBody.GetProperty("context").GetProperty("field").GetString());

        var read = await (await manager.GetAsync(route)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("2026-10-19", read.GetProperty("serviceDate").GetString());
        Assert.Equal(1, read.GetProperty("onShiftToday").GetInt32());
        Assert.Equal(2, read.GetProperty("shifts").GetArrayLength());

        var tooLong = await manager.GetAsync($"{route}?from=2026-10-19&to=2027-01-19");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal("to", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("context").GetProperty("field").GetString());

        var changed = await manager.PutAsJsonAsync(
            $"{route}/{shiftId}", new { date = "2026-10-19", startsAt = "17:00", endsAt = "23:00" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.False((await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("endsNextDay").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"{route}/{shiftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.DeleteAsync($"{route}/{shiftId}")).StatusCode);
    }

    // ---------------------------------------------------------------- the team

    /// <summary>
    /// Ids of one venue's people: an owner and a co-owner, a general manager, and at the branch its manager, a second
    /// manager, two waiters and a cook; a sibling branch with a waiter of its own.
    /// </summary>
    private sealed record TeamIds(
        Guid VenueId,
        Guid BranchId,
        Guid SiblingBranchId,
        Guid OwnerId,
        Guid CoOwnerId,
        Guid GeneralManagerId,
        Guid ManagerId,
        Guid SecondManagerId,
        Guid WaiterId,
        Guid SecondWaiterId,
        Guid CookId,
        Guid SiblingWaiterId,
        Guid VenueWideWaiterId);

    private static async Task<TeamIds> TeamAsync(
        YallaDbContext db,
        IReadOnlyList<(TimeOnly Opens, TimeOnly Closes, bool ClosesNextDay)>? openingHours = null,
        string? siblingTimeZoneId = null)
    {
        var branch = await TestBranchBuilder.CreateAsync(db, openingHours: openingHours);
        var sibling = await AuthTestData.AddBranchAsync(db, branch.VenueId, siblingTimeZoneId ?? branch.TimeZoneId);

        StaffMember Person(string name, StaffRole role, Guid? branchId) =>
            new(branch.VenueId, name, $"+3749{Guid.NewGuid():N}"[..12], role, "hash", branchId);

        var owner = Person("Aram Owner", StaffRole.Owner, null);
        var coOwner = Person("Ani Co-owner", StaffRole.Owner, null);
        var generalManager = Person("Lilit General", StaffRole.GeneralManager, null);
        var secondManager = Person("Mariam Manager", StaffRole.Manager, branch.BranchId);
        var secondWaiter = Person("Hayk Waiter", StaffRole.Waiter, branch.BranchId);
        var cook = Person("Sona Cook", StaffRole.Kitchen, branch.BranchId);
        var siblingWaiter = Person("Tigran Waiter", StaffRole.Waiter, sibling);
        var venueWideWaiter = Person("Gor Floater", StaffRole.Waiter, null);

        db.StaffMembers.AddRange(owner, coOwner, generalManager, secondManager, secondWaiter, cook, siblingWaiter, venueWideWaiter);
        await db.SaveChangesAsync();

        return new TeamIds(
            branch.VenueId,
            branch.BranchId,
            sibling,
            owner.Id,
            coOwner.Id,
            generalManager.Id,
            branch.ManagerId,
            secondManager.Id,
            branch.WaiterId,
            secondWaiter.Id,
            cook.Id,
            siblingWaiter.Id,
            venueWideWaiter.Id);
    }

    private static async Task DeactivateAsync(YallaDbContext db, Guid staffMemberId)
    {
        var person = await db.StaffMembers.FirstAsync(s => s.Id == staffMemberId);
        person.SetActive(false);
        await db.SaveChangesAsync();
    }
}
