using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Yalla.Application.BranchSettings;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Special opening days - holidays, closures, different hours on one date - saved through the console and
/// honoured by every reader of opening hours: availability, the day's slots, booking and changing a booking, the
/// Overview, "open now" and the public pages.
/// </summary>
/// <remarks>
/// Each test builds its own branch in Asia/Yerevan (UTC+4 all year), open 10:00-23:00 every day. Now is noon on
/// Wednesday 30 December 2026. The Eve, Thursday 31 December, is open 18:00-03:00; New Year's Day, Friday
/// 1 January 2027, is shut. Weekly hours alone would have the branch open at noon on both and shut at 23:30 on the
/// Eve - every reader is asked about exactly those moments.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class SpecialOpeningDayTests(SqlServerFixture fixture)
{
    /// <summary>12:00 on Wednesday 30 December 2026 in Yerevan.</summary>
    private static readonly DateTime Now = new(2026, 12, 30, 8, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly Today = new(2026, 12, 30);

    private static readonly DateOnly Eve = new(2026, 12, 31);

    private static readonly DateOnly NewYear = new(2027, 1, 1);

    private static readonly TimeOnly Noon = new(12, 0);

    // ---------------------------------------------------------------- saving them

    [SkippableFact]
    public async Task Special_days_are_saved_from_today_on_and_read_back_earliest_first_with_the_past_kept()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        // History: a day already past at the branch, which no save touches and no list names.
        db.SpecialOpeningDays.Add(new SpecialOpeningDay(branch.BranchId, Today.AddDays(-1), "Inventory", []));
        await db.SaveChangesAsync();

        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        var saved = await service.ReplaceSpecialOpeningDaysAsync(branch.BranchId, Holidays());

        Assert.Equal([Eve, NewYear], saved.Days.Select(d => d.Date));
        Assert.Equal("New Year's Eve", saved.Days[0].Note);
        Assert.False(saved.Days[0].Closed);
        Assert.Equal(new OpeningBlockView(new TimeOnly(18, 0), new TimeOnly(3, 0), true), saved.Days[0].Blocks.Single());
        Assert.True(saved.Days[1].Closed);
        Assert.Empty(saved.Days[1].Blocks);
        Assert.Equal(0, saved.AffectedExistingReservations);

        static IEnumerable<string> Flat(IEnumerable<SpecialOpeningDayView> days) =>
            days.Select(d => $"{d.Date:O} {d.Note} {d.Closed} {string.Join(',', d.Blocks)}");

        Assert.Equal(Flat(saved.Days), Flat(await service.GetSpecialOpeningDaysAsync(branch.BranchId)));

        // Saving again replaces the list whole; the past day is still there.
        var cleared = await service.ReplaceSpecialOpeningDaysAsync(branch.BranchId, []);

        Assert.Empty(cleared.Days);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            [Today.AddDays(-1)],
            await verify.SpecialOpeningDays.Where(d => d.BranchId == branch.BranchId).Select(d => d.Date).ToListAsync());
    }

    [SkippableFact]
    public async Task A_bad_list_is_refused_field_by_field_and_nothing_is_saved()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        await service.ReplaceSpecialOpeningDaysAsync(branch.BranchId, Holidays());

        var refused = await Assert.ThrowsAsync<FieldValidationException>(() => service.ReplaceSpecialOpeningDaysAsync(
            branch.BranchId,
            [
                Day(Today.AddDays(-1), null),
                Day(NewYear, null),
                Day(NewYear, null, ("12:00", "16:00"), ("15:00", "20:00")),
            ]));

        Assert.Equal(
            ["[0].date", "[2].blocks[1].opensAt", "[2].date"],
            refused.Violations.Select(v => v.Field).Order(StringComparer.Ordinal));

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            2,
            await verify.SpecialOpeningDays.CountAsync(d => d.BranchId == branch.BranchId));
    }

    [SkippableFact]
    public async Task Saving_reports_the_live_bookings_the_new_hours_leave_out_and_changes_none()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        // Booked on New Year's Day before anybody said it was a holiday, and on the Eve at 19:00, which the Eve's
        // own hours still have room for.
        var diner = fixture.CreateReservationService(db, clock, TestActor.Diner());
        var lunch = await diner.CreateAsync(Booking(branch, branch.TableIds[0], NewYear, Noon));
        var evening = await diner.CreateAsync(Booking(branch, branch.TableIds[1], Eve, new TimeOnly(19, 0)));

        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var saved = await service.ReplaceSpecialOpeningDaysAsync(branch.BranchId, Holidays());

        Assert.Equal(1, saved.AffectedExistingReservations);
        Assert.Equal([lunch.Id], saved.AffectedReservationIds);

        await using var verify = fixture.CreateContext(clock);
        Assert.Equal(
            ReservationStatus.Confirmed,
            await verify.Reservations.Where(r => r.Id == lunch.Id).Select(r => r.Status).SingleAsync());
        Assert.Equal(
            ReservationStatus.Confirmed,
            await verify.Reservations.Where(r => r.Id == evening.Id).Select(r => r.Status).SingleAsync());

        // Taking the holiday off again affects nothing: lunch fits the Friday's hours.
        var undone = await service.ReplaceSpecialOpeningDaysAsync(branch.BranchId, []);
        Assert.Equal(0, undone.AffectedExistingReservations);
    }

    [SkippableFact]
    public async Task A_manager_of_another_branch_can_neither_read_nor_save_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var mine = await TestBranchBuilder.CreateAsync(db);
        var theirs = await TestBranchBuilder.CreateAsync(db);

        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(mine.ManagerId));

        await Assert.ThrowsAsync<StaffPermissionException>(() => service.GetSpecialOpeningDaysAsync(theirs.BranchId));
        await Assert.ThrowsAsync<StaffPermissionException>(
            () => service.ReplaceSpecialOpeningDaysAsync(theirs.BranchId, Holidays()));

        Assert.False(await db.SpecialOpeningDays.AnyAsync(d => d.BranchId == theirs.BranchId));
    }

    // ---------------------------------------------------------------- availability and the day's slots

    [SkippableFact]
    public async Task Availability_follows_the_special_day_and_not_the_weekday()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await HolidayBranchAsync(db, clock);
        var query = fixture.CreateAvailabilityQuery(db, clock);

        var newYearLunch = await query.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, NewYear, Noon));
        Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, newYearLunch!.UnavailableReason);

        var eveLunch = await query.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, Eve, Noon));
        Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, eveLunch!.UnavailableReason);

        // 23:30 to 01:00 on the Eve: past a Thursday's 23:00, inside the Eve's 03:00.
        var eveLate = await query.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, Eve, new TimeOnly(23, 30)));
        Assert.Null(eveLate!.UnavailableReason);
        Assert.Contains(eveLate.Tables, t => t.IsAvailable);

        // The day after the holiday is an ordinary Saturday again.
        var saturday = await query.GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, NewYear.AddDays(1), Noon));
        Assert.Null(saturday!.UnavailableReason);
    }

    [SkippableFact]
    public async Task The_days_slots_follow_the_special_day_and_say_which_one_it_is()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await HolidayBranchAsync(db, clock);
        var query = fixture.CreateAvailabilityQuery(db, clock);

        // The Eve: from 18:00 to the last start whose 90 minutes end by 03:00 - after midnight those are the
        // next date's times - and nothing at the weekday's noon.
        var eve = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, Eve, 2));

        Assert.NotNull(eve);
        Assert.Equal("18:00", eve.Slots[0].Time);
        Assert.Equal("23:45", eve.Slots[^1].Time);
        Assert.Null(eve.UnavailableReason);
        Assert.Equal("New Year's Eve", eve.SpecialDay!.Note);
        Assert.False(eve.SpecialDay.Closed);

        // New Year's Day lists only the Eve's small hours, and nothing of its own.
        var newYear = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, NewYear, 2));

        Assert.NotNull(newYear);
        Assert.Equal("00:00", newYear.Slots[0].Time);
        Assert.Equal("01:30", newYear.Slots[^1].Time);
        Assert.True(newYear.SpecialDay!.Closed);

        // A day shut outright, with no evening before running into it, has no slots at all and says so once.
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        await service.ReplaceSpecialOpeningDaysAsync(branch.BranchId, [.. Holidays(), Day(NewYear.AddDays(3), "Stocktake")]);

        var shut = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, NewYear.AddDays(3), 2));

        Assert.Empty(shut!.Slots);
        Assert.Equal(ReservationRejectionReason.OutsideOpeningHours, shut.UnavailableReason);
        Assert.Equal("Stocktake", shut.SpecialDay!.Note);

        // An ordinary day carries no special day.
        var ordinary = await query.GetDaySlotsAsync(new DaySlotsRequest(branch.BranchId, NewYear.AddDays(1), 2));
        Assert.Null(ordinary!.SpecialDay);
        Assert.Equal("10:00", ordinary.Slots[0].Time);
    }

    // ---------------------------------------------------------------- booking and changing a booking

    [SkippableFact]
    public async Task Booking_and_moving_a_booking_are_refused_outside_the_special_days_hours()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await HolidayBranchAsync(db, clock);
        var service = fixture.CreateReservationService(db, clock, TestActor.Diner());

        await Assert.ThrowsAsync<OutsideOpeningHoursException>(
            () => service.CreateAsync(Booking(branch, branch.TableIds[0], NewYear, Noon)));

        // Late on the Eve is bookable, though Thursdays shut at 23:00.
        var late = await service.CreateAsync(Booking(branch, branch.TableIds[0], Eve, new TimeOnly(23, 30)));
        Assert.Equal(Eve, late.LocalDate);

        // A booking for an ordinary day cannot be moved onto the holiday.
        var saturday = await service.CreateAsync(Booking(branch, branch.TableIds[1], NewYear.AddDays(1), Noon));

        await Assert.ThrowsAsync<OutsideOpeningHoursException>(() => service.RescheduleAsync(
            new RescheduleReservationCommand(saturday.Id, branch.TableIds[1], NewYear, Noon, 2, Guid.CreateVersion7())));
    }

    // ---------------------------------------------------------------- the Overview

    [SkippableFact]
    public async Task The_overview_reads_its_service_day_and_hours_from_the_special_days()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await HolidayBranchAsync(db, clock);
        var query = fixture.CreateBranchOverviewQuery(db, clock, TestActor.Manager(branch.ManagerId));

        // Noon on the Eve: shut until 18:00, the chart spanning the night to the 02:00 hour, takings from 18:00.
        clock.UtcNow = Local(Eve, 12, 0);
        var eveNoon = await query.GetAsync(branch.BranchId);

        Assert.Equal(Eve, eveNoon.ServiceDate);
        Assert.False(eveNoon.Hours.IsOpen);
        Assert.Equal(new TimeOnly(18, 0), eveNoon.Hours.OpensAt);
        Assert.Equal("New Year's Eve", eveNoon.Hours.SpecialDay!.Note);
        Assert.Equal(Enumerable.Range(18, 9), eveNoon.Load.Hours.Select(h => h.Hour));
        Assert.Equal(Local(Eve, 18, 0), eveNoon.Revenue.SinceUtc);

        // 01:00 on New Year's Day: still the Eve's service, open until 03:00 today.
        clock.UtcNow = Local(NewYear, 1, 0);
        var night = await query.GetAsync(branch.BranchId);

        Assert.Equal(Eve, night.ServiceDate);
        Assert.True(night.Hours.IsOpen);
        Assert.Equal(new TimeOnly(3, 0), night.Hours.ClosesAt);
        Assert.False(night.Hours.ClosesNextDay);

        // Noon on New Year's Day: its own service day, shut, opening no more today.
        clock.UtcNow = Local(NewYear, 12, 0);
        var holiday = await query.GetAsync(branch.BranchId);

        Assert.Equal(NewYear, holiday.ServiceDate);
        Assert.False(holiday.Hours.IsOpen);
        Assert.Null(holiday.Hours.OpensAt);
        Assert.True(holiday.Hours.SpecialDay!.Closed);
        Assert.Empty(holiday.Load.Hours);
        Assert.Equal(Local(NewYear, 0, 0), holiday.Revenue.SinceUtc);

        // An ordinary Saturday: no special day.
        clock.UtcNow = Local(NewYear.AddDays(1), 12, 0);
        var saturday = await query.GetAsync(branch.BranchId);

        Assert.True(saturday.Hours.IsOpen);
        Assert.Null(saturday.Hours.SpecialDay);
    }

    // ---------------------------------------------------------------- open now and the public pages

    [SkippableFact]
    public async Task Open_now_on_the_public_page_and_the_listing_follows_the_special_days()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await HolidayBranchAsync(db, clock);
        var slugs = await db.Branches
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { Venue = b.Venue.Slug, b.Slug })
            .SingleAsync();

        async Task<(bool Page, bool Listing)> OpenAt(DateTime utc)
        {
            clock.UtcNow = utc;

            // Fresh caches: "open now" is cached for seconds, and each question here is another moment.
            var page = await SqlServerFixture.CreatePublicQuery(db, clock).GetBranchAsync(slugs.Venue, slugs.Slug);
            var detail = await new PublicListingQuery(db, new MemoryCache(new MemoryCacheOptions()), clock)
                .GetDetailAsync(branch.BranchId, null, null);

            return (page.IsOpenNow, detail.Listing.IsOpenNow);
        }

        Assert.Equal((false, false), await OpenAt(Local(NewYear, 12, 0)));
        Assert.Equal((true, true), await OpenAt(Local(Eve, 23, 30)));
        Assert.Equal((true, true), await OpenAt(Local(NewYear, 2, 0)));
        Assert.Equal((false, false), await OpenAt(Local(Eve, 12, 0)));
        Assert.Equal((true, true), await OpenAt(Local(NewYear.AddDays(1), 12, 0)));
    }

    [SkippableFact]
    public async Task The_public_page_and_the_listing_detail_name_the_upcoming_special_days()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));

        // Beyond the sixty days the pages look ahead.
        await service.ReplaceSpecialOpeningDaysAsync(
            branch.BranchId, [.. Holidays(), Day(Today.AddDays(61), "Far off")]);

        var slugs = await db.Branches
            .Where(b => b.Id == branch.BranchId)
            .Select(b => new { Venue = b.Venue.Slug, b.Slug })
            .SingleAsync();

        var page = await SqlServerFixture.CreatePublicQuery(db, clock).GetBranchAsync(slugs.Venue, slugs.Slug);
        var detail = await new PublicListingQuery(db, new MemoryCache(new MemoryCacheOptions()), clock)
            .GetDetailAsync(branch.BranchId, null, null);

        foreach (var days in new[] { page.SpecialDays, detail.SpecialDays })
        {
            Assert.Equal([Eve, NewYear], days.Select(d => d.Date));
            Assert.Equal("New Year's Eve", days[0].Note);
            Assert.True(days[1].Closed);
        }

        // The weekly hours stay the week.
        Assert.Equal(7, page.OpeningHours.Count);
    }

    // ---------------------------------------------------------------- the wire

    [SkippableFact]
    public async Task The_console_saves_and_reads_them_over_http_in_the_documented_shape()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString);

        // The real clock: a token signed at a made-up instant is not valid now. The dates hang off it.
        var today = ConsoleTestData.YerevanDate(factory.Clock.UtcNow);
        var closed = today.AddDays(30);
        var late = today.AddDays(29);

        AuthBranch branch;
        PanelAccount owner;

        await using (var db = fixture.CreateContext(factory.Clock))
        {
            branch = await AuthTestData.CreateBranchAsync(db);
            owner = await AuthTestData.SeedOwnerAsync(db, branch.VenueId);
        }

        using var client = factory.CreateClientWithToken(await AuthTestData.SignInAsync(factory, owner));
        var route = $"/api/branches/{branch.BranchId}/opening-hours/exceptions";

        var put = await client.PutAsJsonAsync(
            route,
            new object[]
            {
                new { date = Iso(closed), note = "New Year", blocks = Array.Empty<object>() },
                new { date = Iso(late), blocks = new[] { new { opensAt = "18:00", closesAt = "03:00" } } },
            });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var saved = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, saved.GetProperty("affectedExistingReservations").GetInt32());
        Assert.Empty(saved.GetProperty("affectedReservationIds").EnumerateArray());

        var days = saved.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(Iso(late), days[0].GetProperty("date").GetString());

        // A null is left out, like everywhere in this API.
        Assert.False(days[0].TryGetProperty("note", out _));
        Assert.False(days[0].GetProperty("closed").GetBoolean());

        var block = days[0].GetProperty("blocks").EnumerateArray().Single();
        Assert.Equal("18:00:00", block.GetProperty("opensAt").GetString());
        Assert.Equal("03:00:00", block.GetProperty("closesAt").GetString());
        Assert.True(block.GetProperty("closesNextDay").GetBoolean());
        Assert.Equal("New Year", days[1].GetProperty("note").GetString());
        Assert.True(days[1].GetProperty("closed").GetBoolean());

        var read = await client.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal(2, read.GetArrayLength());

        var refused = await client.PutAsJsonAsync(
            route, new object[] { new { date = Iso(today.AddDays(-1)), blocks = Array.Empty<object>() } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var field = (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("context").GetProperty("fields").EnumerateArray().Single();
        Assert.Equal("[0].date", field.GetProperty("field").GetString());
        Assert.Equal("min", field.GetProperty("bound").GetString());
    }

    /// <summary>
    /// The published schema the console's and the apps' types are generated from: the new operations, and which
    /// of the new fields are always there - the frontend's gateway check reads those <c>required</c> lists.
    /// </summary>
    [Fact]
    public async Task The_published_schema_names_the_new_operations_and_marks_what_is_always_there()
    {
        await using var factory = new YallaApiFactory().WithSwagger(enabled: true);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        var paths = document.RootElement.GetProperty("paths");

        string OperationId(string path, string verb) =>
            paths.GetProperty(path).GetProperty(verb).GetProperty("operationId").GetString()!;

        Assert.Equal("getOpeningHoursExceptions", OperationId("/api/branches/{branchId}/opening-hours/exceptions", "get"));
        Assert.Equal("putOpeningHoursExceptions", OperationId("/api/branches/{branchId}/opening-hours/exceptions", "put"));
        Assert.Equal("reorderMenuCategories", OperationId("/api/branches/{branchId}/menu/categories/order", "put"));
        Assert.Equal(
            "reorderMenuItems", OperationId("/api/branches/{branchId}/menu/categories/{categoryId}/items/order", "put"));

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        HashSet<string> Required(string name)
        {
            var schema = schemas.EnumerateObject()
                .Single(s => s.Name == name || s.Name.EndsWith("." + name, StringComparison.Ordinal))
                .Value;

            return schema.TryGetProperty("required", out var required)
                ? [.. required.EnumerateArray().Select(r => r.GetString()!)]
                : [];
        }

        Assert.Superset(new HashSet<string> { "date", "closed", "blocks" }, Required(nameof(SpecialOpeningDayView)));
        Assert.DoesNotContain("note", Required(nameof(SpecialOpeningDayView)));
        Assert.Superset(new HashSet<string> { "opensAt", "closesAt", "closesNextDay" }, Required(nameof(OpeningBlockView)));
        Assert.Superset(new HashSet<string> { "date", "blocks" }, Required(nameof(SpecialOpeningDayInput)));
        Assert.DoesNotContain("note", Required(nameof(SpecialOpeningDayInput)));
        Assert.Superset(
            new HashSet<string> { "days", "affectedExistingReservations", "affectedReservationIds" },
            Required(nameof(SpecialOpeningDaysChangeResult)));

        Assert.Contains("specialDays", Required("PublicBranchPage"));
        Assert.Contains("specialDays", Required("PublicBranchDetail"));
        Assert.DoesNotContain("specialDay", Required(nameof(BranchDaySlots)));
        Assert.DoesNotContain("specialDay", Required("OverviewHours"));

        Assert.Contains("missingFields", Required("MenuItemView"));
        Assert.Contains("categoryIds", Required("ReorderMenuCategoriesCommand"));
        Assert.Contains("itemIds", Required("ReorderMenuItemsCommand"));
        Assert.DoesNotContain("displayOrder", Required("CreateMenuCategoryCommand"));
    }

    // ---------------------------------------------------------------- fixtures

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The Eve open 18:00-03:00, New Year's Day shut.</summary>
    private static List<SpecialOpeningDayInput> Holidays() =>
    [
        Day(NewYear, "New Year"),
        Day(Eve, "New Year's Eve", ("18:00", "03:00")),
    ];

    private static SpecialOpeningDayInput Day(DateOnly date, string? note, params (string Opens, string Closes)[] blocks) =>
        new(date, note, [.. blocks.Select(b => new OpeningBlockInput(TimeOnly.Parse(b.Opens), TimeOnly.Parse(b.Closes)))]);

    /// <summary>A branch open 10:00-23:00 every day, with the two holidays saved through the console.</summary>
    private async Task<TestBranch> HolidayBranchAsync(YallaDbContext db, TestClock clock)
    {
        var branch = await TestBranchBuilder.CreateAsync(db);

        await fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId))
            .ReplaceSpecialOpeningDaysAsync(branch.BranchId, Holidays());

        return branch;
    }

    private static CreateReservationCommand Booking(TestBranch branch, Guid tableId, DateOnly date, TimeOnly time) =>
        new(
            branch.BranchId,
            tableId,
            date,
            time,
            2,
            GuestName: "Ani Holiday",
            GuestPhone: "+37411223355",
            ClientCommandId: Guid.CreateVersion7());

    /// <summary>The instant that is <paramref name="hour"/>:<paramref name="minute"/> in Yerevan on that date.</summary>
    private static DateTime Local(DateOnly date, int hour, int minute) =>
        DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Utc).AddHours(-4);
}
