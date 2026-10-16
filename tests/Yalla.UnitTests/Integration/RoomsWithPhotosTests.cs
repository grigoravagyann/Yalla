using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yalla.Application.BranchSettings;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Media;
using Yalla.Infrastructure.Services;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Rooms with photos: what the rooms editor refuses, what it takes off on its own, what the orphan
/// sweep must leave alone, and that a diner is only offered a table with a pin.
/// </summary>
/// <remarks>
/// The builder's branch has one room, Windows, with one photo and every table pinned on it. Each
/// test changes that through the same whole-plan save the editor uses.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class RoomsWithPhotosTests(SqlServerFixture fixture)
{
    private static readonly DateTime Now = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------ what the editor refuses

    [SkippableFact]
    public async Task A_pin_on_another_rooms_photo_is_refused_naming_the_table_and_nothing_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var terracePhoto = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);

        var areas = Areas(before);
        areas.Add(new FloorAreaInput(null, "Terrace", 1, [terracePhoto]));

        // Table 1 stays in Windows but is pinned on the terrace's picture.
        var tables = Tables(before);
        tables[0] = tables[0] with { PhotoId = terracePhoto };

        var refused = await Assert.ThrowsAsync<FieldValidationException>(
            () => service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand(areas, tables, before.Version)));

        var violation = Assert.Single(refused.Violations);
        Assert.Equal("tables[0].photoId", violation.Field);
        Assert.Equal(FieldBounds.Conflict, violation.Bound);
        Assert.Contains("Terrace", violation.Message);

        await AssertUnchangedAsync(clock, branch, before);
    }

    [SkippableFact]
    public async Task A_photo_uploaded_for_another_branch_is_not_found_and_nothing_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var other = await TestBranchBuilder.CreateAsync(db);
        var foreignPhoto = await TestMenuBuilder.AddPhotoAsync(db, other.BranchId);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);

        var areas = Areas(before);
        areas[0] = areas[0] with { PhotoIds = [.. areas[0].PhotoIds!, foreignPhoto] };

        // The gallery's answer: simply not found here.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.ReplaceFloorPlanAsync(
                branch.BranchId, new ReplaceFloorPlanCommand(areas, Tables(before), before.Version)));

        await AssertUnchangedAsync(clock, branch, before);
    }

    [SkippableFact]
    public async Task A_photo_in_two_rooms_is_refused_naming_the_second()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);

        var areas = Areas(before);
        areas.Add(new FloorAreaInput(null, "Terrace", 1, [.. areas[0].PhotoIds!]));

        var refused = await Assert.ThrowsAsync<FieldValidationException>(
            () => service.ReplaceFloorPlanAsync(
                branch.BranchId, new ReplaceFloorPlanCommand(areas, Tables(before), before.Version)));

        var violation = Assert.Single(refused.Violations);
        Assert.Equal("areas[1].photoIds[0]", violation.Field);
        Assert.Equal(FieldBounds.Conflict, violation.Bound);
        Assert.Contains("Windows", violation.Message);

        await AssertUnchangedAsync(clock, branch, before);
    }

    [SkippableFact]
    public async Task A_room_takes_ten_photos_in_the_order_sent_and_refuses_an_eleventh()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);

        var more = new List<Guid>();

        for (var i = 0; i < FloorAreaPhoto.MaxPhotos; i++)
        {
            more.Add(await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId));
        }

        var existing = Assert.Single(before.Areas[0].Photos).PhotoId;

        // Eleven: refused, naming the list and the bound.
        var eleven = Areas(before);
        eleven[0] = eleven[0] with { PhotoIds = [existing, .. more] };

        var refused = await Assert.ThrowsAsync<FieldValidationException>(
            () => service.ReplaceFloorPlanAsync(
                branch.BranchId, new ReplaceFloorPlanCommand(eleven, Tables(before), before.Version)));

        var violation = Assert.Single(refused.Violations);
        Assert.Equal("areas[0].photoIds", violation.Field);
        Assert.Equal(FieldBounds.Max, violation.Bound);
        Assert.Equal(FloorAreaPhoto.MaxPhotos, violation.Max);

        await AssertUnchangedAsync(clock, branch, before);

        // Ten, the pinned photo last: kept, in exactly that order.
        List<Guid> ten = [.. more.Take(FloorAreaPhoto.MaxPhotos - 1).Reverse(), existing];
        var areas = Areas(before);
        areas[0] = areas[0] with { PhotoIds = ten };

        var saved = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand(areas, Tables(before), before.Version));

        Assert.Equal(ten, saved.Plan.Areas[0].Photos.Select(p => p.PhotoId));
        Assert.All(saved.Plan.Tables, t => Assert.Equal(existing, t.PhotoId));
    }

    // ------------------------------------------------------------ what the editor takes off on its own

    [SkippableFact]
    public async Task Removing_a_photo_from_a_room_takes_the_tables_pinned_on_it_off_and_says_so()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var second = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var loaded = await service.GetFloorPlanAsync(branch.BranchId);
        var first = Assert.Single(loaded.Areas[0].Photos).PhotoId;

        // Two photos; table 3 moves onto the second.
        var areas = Areas(loaded);
        areas[0] = areas[0] with { PhotoIds = [first, second] };
        var tables = Tables(loaded);
        tables[2] = tables[2] with { PhotoId = second, PhotoX = 0.6, PhotoY = 0.4 };

        var both = await service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand(areas, tables, loaded.Version));
        var moved = both.Plan.Tables[2];
        Assert.Equal(second, moved.PhotoId);

        // The second photo leaves the room. The editor still sends the pin it loaded.
        var current = Areas(both.Plan);
        current[0] = current[0] with { PhotoIds = [first] };

        var result = await service.ReplaceFloorPlanAsync(
            branch.BranchId, new ReplaceFloorPlanCommand(current, Tables(both.Plan), both.Plan.Version));

        var unpinned = result.Plan.Tables.Single(t => t.Id == moved.Id);
        Assert.Null(unpinned.PhotoId);
        Assert.Null(unpinned.PhotoX);
        Assert.Null(unpinned.PhotoY);
        Assert.All(result.Plan.Tables.Where(t => t.Id != moved.Id), t => Assert.Equal(first, t.PhotoId));
        Assert.Contains(result.Warnings, w => w.Contains(moved.Label) && w.Contains("taken off"));

        await using var verify = fixture.CreateContext(clock);
        Assert.False(await verify.FloorAreaPhotos.AnyAsync(p => p.PhotoId == second));
    }

    [SkippableFact]
    public async Task Deleting_a_room_takes_its_tables_off_its_photos_and_keeps_them()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);
        var room = Assert.Single(before.Areas);
        Assert.All(before.Tables, t => Assert.NotNull(t.PhotoId));

        await service.DeleteFloorAreaAsync(branch.BranchId, room.Id);

        await using var verify = fixture.CreateContext(clock);

        var tables = await verify.DiningTables.AsNoTracking().Where(t => t.BranchId == branch.BranchId).ToListAsync();
        Assert.Equal(before.Tables.Count, tables.Count);
        Assert.All(tables, t =>
        {
            Assert.True(t.IsActive);
            Assert.Null(t.FloorAreaId);
            Assert.Null(t.PhotoId);
            Assert.Null(t.PhotoX);
            Assert.Null(t.PhotoY);
        });

        Assert.False(await verify.FloorAreaPhotos.AnyAsync(p => p.FloorAreaId == room.Id));
    }

    // ------------------------------------------------------------ the orphan sweep

    /// <summary>
    /// A room's photo, and a photo only an inactive table's pin still names, are in use: the sweep
    /// must not count them as orphans. The foreign keys would refuse the delete anyway - which is
    /// exactly why the sweep must not try, and log a race that never happened, on every run.
    /// </summary>
    [SkippableFact]
    public async Task The_orphan_sweep_leaves_room_photos_and_pinned_table_photos_alone()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        var roomPhoto = await db.FloorAreaPhotos
            .Where(p => p.FloorArea.BranchId == branch.BranchId)
            .Select(p => p.PhotoId)
            .SingleAsync();

        // A photo no room shows any more, which an inactive table is still pinned on.
        var pinOnly = TestBranchBuilder.RoomPhoto(branch.BranchId);
        db.Photos.Add(pinOnly);
        await db.SaveChangesAsync();

        var retired = await db.DiningTables.SingleAsync(t => t.Id == branch.TableIds[2]);
        retired.PlaceOnPhoto(pinOnly.Id, 0.5, 0.5);
        retired.SetActive(false);

        // And a real orphan, so the sweep is seen to run.
        var orphan = TestBranchBuilder.RoomPhoto(branch.BranchId);
        db.Photos.Add(orphan);
        await db.SaveChangesAsync();

        // All three were "uploaded" on 1 January, far past the grace period.
        var log = new CapturingLogger();
        var photos = new PhotoService(
            db,
            new LocalDiskPhotoStorage(
                new PhotoStorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "yalla-rooms-" + Guid.NewGuid().ToString("N")) },
                NullLogger<LocalDiskPhotoStorage>.Instance),
            clock,
            fixture.CreateBranchGuard(db, TestActor.Manager(branch.ManagerId)),
            log);

        await photos.SweepOrphansAsync();

        await using var verify = fixture.CreateContext(clock);
        Assert.True(await verify.Photos.AnyAsync(p => p.Id == roomPhoto));
        Assert.True(await verify.Photos.AnyAsync(p => p.Id == pinOnly.Id));
        Assert.False(await verify.Photos.AnyAsync(p => p.Id == orphan.Id));

        Assert.DoesNotContain(log.Lines, l => l.Contains(roomPhoto.ToString()) || l.Contains(pinOnly.Id.ToString()));
    }

    // ------------------------------------------------------------ what a diner is offered

    [SkippableFact]
    public async Task A_bookable_table_with_no_pin_is_not_offered_to_diners_but_staff_still_see_it()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var unpinned = await TestBranchBuilder.AddTableAsync(db, branch, "U1", seats: 4, pinned: false);

        // Availability, the screen a diner picks from: the pinned tables, and not this one.
        var availability = await fixture.CreateAvailabilityQuery(db, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, new DateOnly(2026, 9, 13), new TimeOnly(18, 0)));

        Assert.NotNull(availability);
        Assert.Contains(availability.Tables, t => t.TableId == branch.FirstTableId);
        Assert.DoesNotContain(availability.Tables, t => t.TableId == unpinned.Id);

        // The public rooms read: not on any photo.
        var rooms = await new PublicListingQuery(db, new MemoryCache(new MemoryCacheOptions()), clock)
            .GetRoomsAsync(branch.BranchId);

        var pins = rooms.Rooms.SelectMany(r => r.Photos).SelectMany(p => p.Tables).Select(t => t.TableId).ToList();
        Assert.Contains(branch.FirstTableId, pins);
        Assert.DoesNotContain(unpinned.Id, pins);

        // A booking that names it anyway is refused the way a walk-in-only table is.
        var reservations = fixture.CreateReservationService(db, clock, TestActor.Diner());

        await Assert.ThrowsAsync<TableNotBookableException>(() => reservations.CreateAsync(new CreateReservationCommand(
            branch.BranchId,
            unpinned.Id,
            new DateOnly(2026, 9, 13),
            new TimeOnly(18, 0),
            2,
            GuestName: "Ani Test",
            GuestPhone: "+37411223344",
            ClientCommandId: Guid.CreateVersion7())));

        // Staff seat it like any other: it is on their floor.
        var floor = await fixture.CreateFloorQuery(db, clock).GetFloorStateAsync(branch.BranchId, Now);
        Assert.NotNull(floor);
        Assert.Contains(floor.Tables, t => t.TableId == unpinned.Id && t.PhotoId == null);
    }

    /// <summary>
    /// A pin on a photo that is not one of the table's own room's is a pin nobody can tap: the rooms
    /// read draws each table on its room's photos. Availability, the booking and the public page must
    /// agree, or a diner is offered a table the room view never shows.
    /// </summary>
    [SkippableFact]
    public async Task A_table_pinned_on_another_rooms_photo_is_not_offered_to_diners()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);

        // Terrace and its photo, and table 1 - still in Windows - pinned on it. The editor refuses
        // this; a row written before that rule, or by hand, is what it guards against.
        var terrace = new FloorArea(branch.BranchId, "Terrace", 1);
        var terracePhoto = TestBranchBuilder.RoomPhoto(branch.BranchId);
        db.FloorAreas.Add(terrace);
        db.Photos.Add(terracePhoto);
        db.FloorAreaPhotos.Add(new FloorAreaPhoto(terrace.Id, terracePhoto.Id, 0));
        await db.SaveChangesAsync();

        var stray = await db.DiningTables.SingleAsync(t => t.Id == branch.FirstTableId);
        stray.PlaceOnPhoto(terracePhoto.Id, 0.5, 0.5);
        await db.SaveChangesAsync();

        var availability = await fixture.CreateAvailabilityQuery(db, clock)
            .GetAvailabilityAsync(new AvailabilityRequest(branch.BranchId, 2, new DateOnly(2026, 9, 13), new TimeOnly(18, 0)));

        Assert.NotNull(availability);
        Assert.Contains(availability.Tables, t => t.TableId == branch.TableIds[1]);
        Assert.DoesNotContain(availability.Tables, t => t.TableId == stray.Id);

        var rooms = await new PublicListingQuery(db, new MemoryCache(new MemoryCacheOptions()), clock)
            .GetRoomsAsync(branch.BranchId);

        Assert.DoesNotContain(stray.Id, rooms.Rooms.SelectMany(r => r.Photos).SelectMany(p => p.Tables).Select(t => t.TableId));

        var slugs = await db.Branches.Where(b => b.Id == branch.BranchId).Select(b => new { Venue = b.Venue.Slug, b.Slug }).SingleAsync();
        var page = await SqlServerFixture.CreatePublicQuery(db, clock).GetBranchAsync(slugs.Venue, slugs.Slug);

        Assert.False(page.FloorPlan.Tables.Single(t => t.Label == stray.Label).IsBookable);

        await Assert.ThrowsAsync<TableNotBookableException>(() => fixture.CreateReservationService(db, clock, TestActor.Diner())
            .CreateAsync(new CreateReservationCommand(
                branch.BranchId,
                stray.Id,
                new DateOnly(2026, 9, 13),
                new TimeOnly(18, 0),
                2,
                GuestName: "Ani Test",
                GuestPhone: "+37411223344",
                ClientCommandId: Guid.CreateVersion7())));
    }

    // ------------------------------------------------------------ matching rooms

    /// <summary>
    /// The editor renames Windows to Lounge and adds a new room called Windows in one save, the new
    /// one listed first. The stored room is Lounge's by its id; a name match must not take it too.
    /// </summary>
    [SkippableFact]
    public async Task A_room_renamed_while_a_new_room_takes_its_old_name_becomes_two_rooms()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var second = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);
        var windows = Assert.Single(before.Areas);
        var first = Assert.Single(windows.Photos).PhotoId;

        List<FloorAreaInput> areas =
        [
            new(null, "Windows", 0, [second]),
            new(windows.Id, "Lounge", 1, [first]),
        ];

        // Every table stays in (the new) Windows, pinned on its photo.
        var tables = Tables(before).Select(t => t with { FloorAreaName = "Windows", PhotoId = second }).ToList();

        var saved = await service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand(areas, tables, before.Version));

        var lounge = Assert.Single(saved.Plan.Areas, a => a.Name == "Lounge");
        var newWindows = Assert.Single(saved.Plan.Areas, a => a.Name == "Windows");
        Assert.Equal(windows.Id, lounge.Id);
        Assert.NotEqual(windows.Id, newWindows.Id);
        Assert.Equal([first], lounge.Photos.Select(p => p.PhotoId));
        Assert.Equal([second], newWindows.Photos.Select(p => p.PhotoId));
        Assert.All(saved.Plan.Tables, t =>
        {
            Assert.Equal(newWindows.Id, t.FloorAreaId);
            Assert.Equal(second, t.PhotoId);
        });
    }

    [SkippableFact]
    public async Task A_room_claimed_by_two_entries_is_refused_naming_it_and_nothing_changes()
    {
        Skip.IfNot(fixture.IsAvailable, fixture.SkipReason);

        var clock = new TestClock(Now);
        await using var db = fixture.CreateContext(clock);
        var branch = await TestBranchBuilder.CreateAsync(db);
        var service = fixture.CreateBranchSettingsService(db, clock, TestActor.Manager(branch.ManagerId));
        var before = await service.GetFloorPlanAsync(branch.BranchId);
        var windows = Assert.Single(before.Areas);

        List<FloorAreaInput> areas =
        [
            new(windows.Id, "Windows", 0, [.. windows.Photos.Select(p => p.PhotoId)]),
            new(windows.Id, "Lounge", 1, []),
        ];

        var refused = await Assert.ThrowsAsync<FloorPlanInvalidException>(
            () => service.ReplaceFloorPlanAsync(branch.BranchId, new ReplaceFloorPlanCommand(areas, Tables(before), before.Version)));

        Assert.Contains(refused.Errors, e => e.Contains("Windows") && e.Contains("Lounge") && e.Contains("more than one"));

        await AssertUnchangedAsync(clock, branch, before);
    }

    // ------------------------------------------------------------ a photo moving between rooms

    // ------------------------------------------------------------ the version and the room routes

    // ------------------------------------------------------------ the sweep racing a save

    // ------------------------------------------------------------ helpers

    /// <summary>The plan as the database holds it is still the plan the test loaded.</summary>
    private async Task AssertUnchangedAsync(TestClock clock, TestBranch branch, FloorPlanView before)
    {
        await using var verify = fixture.CreateContext(clock);
        var service = fixture.CreateBranchSettingsService(verify, clock, TestActor.Manager(branch.ManagerId));
        var after = await service.GetFloorPlanAsync(branch.BranchId);

        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Areas.Select(a => a.Name), after.Areas.Select(a => a.Name));
        Assert.Equal(
            before.Areas.SelectMany(a => a.Photos).Select(p => p.PhotoId),
            after.Areas.SelectMany(a => a.Photos).Select(p => p.PhotoId));
        Assert.Equal(
            before.Tables.Select(t => (t.Id, t.PhotoId, t.PhotoX, t.PhotoY)),
            after.Tables.Select(t => (t.Id, t.PhotoId, t.PhotoX, t.PhotoY)));
    }

    /// <summary>The rooms as the editor would send them back, each with the photos it was read with.</summary>
    private static List<FloorAreaInput> Areas(FloorPlanView plan) =>
        [.. plan.Areas.Select(a => new FloorAreaInput(a.Id, a.Name, a.DisplayOrder, [.. a.Photos.Select(p => p.PhotoId)]))];

    /// <summary>The tables as the editor would send them back: in their rooms, with their pins.</summary>
    private static List<FloorTableInput> Tables(FloorPlanView plan) =>
    [
        .. plan.Tables.Select(t => new FloorTableInput(
            t.Id,
            t.Label,
            t.Seats,
            plan.Areas.FirstOrDefault(a => a.Id == t.FloorAreaId)?.Name,
            t.IsBookable,
            t.PhotoId,
            t.PhotoX,
            t.PhotoY)),
    ];

    private sealed class CapturingLogger : ILogger<PhotoService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
