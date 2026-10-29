using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Media;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>Ids of the fixture data one test works against.</summary>
internal sealed record TestBranch(
    Guid VenueId,
    Guid BranchId,
    Guid WaiterId,
    Guid ManagerId,
    IReadOnlyList<Guid> TableIds,
    string TimeZoneId)
{
    public Guid FirstTableId => TableIds[0];
}

/// <summary>
/// Builds an isolated branch per test.
/// </summary>
/// <remarks>
/// Every test gets its own venue and branch with a unique slug, so the shared database needs no
/// cleanup between tests and they can run in any order. Cheaper and far less brittle than
/// truncating tables around each one.
/// </remarks>
internal static class TestBranchBuilder
{
    /// <summary>
    /// Opening hours a booking test can rely on: open every day, wide enough for any sitting the
    /// default policies produce.
    /// </summary>
    public static readonly IReadOnlyList<(TimeOnly Opens, TimeOnly Closes, bool ClosesNextDay)> AllDayEveryDay =
        [(new TimeOnly(10, 0), new TimeOnly(23, 0), false)];

    /// <summary>A venue open past midnight, for the <c>ClosesNextDay</c> cases.</summary>
    public static readonly IReadOnlyList<(TimeOnly Opens, TimeOnly Closes, bool ClosesNextDay)> LateNightEveryDay =
        [(new TimeOnly(10, 0), new TimeOnly(1, 0), true)];

    public static async Task<TestBranch> CreateAsync(
        YallaDbContext db,
        VenueType venueType = VenueType.Restaurant,
        int tableCount = 3,
        string timeZoneId = "Asia/Yerevan",
        int seatsPerTable = 4,
        ReservationPolicy? policy = null,
        IReadOnlyList<(TimeOnly Opens, TimeOnly Closes, bool ClosesNextDay)>? openingHours = null,
        IReadOnlyList<BranchFeature>? features = null,
        bool pinTables = true,
        bool roomPhoto = true,
        bool enrolTablet = true,
        CancellationToken cancellationToken = default)
    {
        var unique = Guid.NewGuid().ToString("N")[..12];

        var venue = new Venue($"Test Venue {unique}", venueType, $"test-venue-{unique}");
        db.Venues.Add(venue);

        var branch = new Branch(
            venue,
            name: $"Test Branch {unique}",
            slug: $"test-branch-{unique}",
            address: "1 Test Street, Yerevan",
            latitude: 40.18,
            longitude: 44.51,
            timeZoneId: timeZoneId,
            reservationPolicy: policy,
            allowedFeatures: features ?? BranchFeatures.Full);

        db.Branches.Add(branch);

        // Every day of the week, so a test can pick any date without first working out which
        // weekday it lands on.
        foreach (var (opens, closes, closesNextDay) in openingHours ?? AllDayEveryDay)
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                db.OpeningHours.Add(new OpeningHours(branch.Id, day, opens, closes, closesNextDay));
            }
        }

        var waiter = new StaffMember(venue.Id, "Test Waiter", $"+3741{unique[..7]}", StaffRole.Waiter, "hash", branch.Id);
        var manager = new StaffMember(venue.Id, "Test Manager", $"+3742{unique[..7]}", StaffRole.Manager, "hash", branch.Id);
        db.StaffMembers.AddRange(waiter, manager);

        var area = new FloorArea(branch.Id, "Windows", 0);
        db.FloorAreas.Add(area);

        // One photo of the room, every table pinned on it - because a diner is only offered a table
        // with a pin, and most tests book. A test about unpinned tables asks for pinTables: false; a
        // test writing against a schema from before rooms had photos asks for roomPhoto: false.
        var photo = roomPhoto ? RoomPhoto(branch.Id) : null;

        if (photo is not null)
        {
            db.Photos.Add(photo);
            db.FloorAreaPhotos.Add(new FloorAreaPhoto(area.Id, photo.Id, 0));
        }

        var tables = Enumerable.Range(1, tableCount)
            .Select(i => new DiningTable(
                branch.Id,
                label: i.ToString(),
                seats: seatsPerTable,
                floorAreaId: area.Id))
            .ToList();

        if (pinTables && photo is not null)
        {
            for (var i = 0; i < tables.Count; i++)
            {
                tables[i].PlaceOnPhoto(photo.Id, PinX(i), 0.5);
            }
        }

        db.DiningTables.AddRange(tables);

        // A tablet on the counter, because a branch with no device to see a booking arrive is not
        // taking guests (DinerVisibility) and most tests book. A test about that rule, or one that
        // counts the branch's devices, asks for enrolTablet: false.
        if (enrolTablet)
        {
            db.StaffDevices.Add(new StaffDevice(
                venue.Id, branch.Id, "Counter", $"{SeededTabletPrefix}{unique}", DateTime.UtcNow));
        }

        await db.SaveChangesAsync(cancellationToken);

        return new TestBranch(
            venue.Id, branch.Id, waiter.Id, manager.Id, tables.Select(t => t.Id).ToList(), timeZoneId);
    }

    /// <summary>
    /// Adds one table with its own seat count or bookability, for the rules that are about the
    /// table rather than the party - seat overhang, bar stools, tables out of service.
    /// </summary>
    public static async Task<DiningTable> AddTableAsync(
        YallaDbContext db,
        TestBranch branch,
        string label,
        int seats,
        bool isBookable = true,
        bool pinned = true,
        CancellationToken cancellationToken = default)
    {
        // In the branch's first room, pinned on its first photo unless asked otherwise - the same
        // place every built table is, so a diner is offered it.
        var roomPhoto = await db.FloorAreaPhotos
            .Where(p => p.FloorArea.BranchId == branch.BranchId)
            .OrderBy(p => p.FloorArea.DisplayOrder)
            .ThenBy(p => p.Position)
            .Select(p => new { p.FloorAreaId, p.PhotoId })
            .FirstOrDefaultAsync(cancellationToken);

        var table = new DiningTable(
            branch.BranchId,
            label: label,
            seats: seats,
            floorAreaId: roomPhoto?.FloorAreaId,
            isBookable: isBookable);

        if (pinned && roomPhoto is not null)
        {
            table.PlaceOnPhoto(roomPhoto.PhotoId, 0.9, 0.8);
        }

        db.DiningTables.Add(table);
        await db.SaveChangesAsync(cancellationToken);

        return table;
    }

    /// <summary>
    /// A photo row a room can show, without going through an upload. Externally hosted, so it needs
    /// no bytes on disk - these tests are about rooms and pins, not image processing.
    /// </summary>
    public static Photo RoomPhoto(Guid branchId)
    {
        var unique = Guid.NewGuid().ToString("N");

        return Photo.ExternallyHosted(
            branchId,
            $"https://cdn.example.test/room-{unique}.jpg",
            unique + unique,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// Fills in whatever a branch made some other way lacks of the required checklist lines, so
    /// diners see it (DinerVisibility): a pinned bookable table on a room photo, opening hours every
    /// day, somebody on its staff and a tablet. For branches a test built by hand or through the
    /// platform API; one from <see cref="CreateAsync"/> already has all four.
    /// </summary>
    public static async Task TakeGuestsAsync(YallaDbContext db, Guid branchId, CancellationToken cancellationToken = default)
    {
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId, cancellationToken);

        if (!await db.DiningTables.AnyAsync(
                t => t.BranchId == branchId && t.IsActive && t.IsBookable && t.PhotoId != null, cancellationToken))
        {
            var area = new FloorArea(branchId, "Room", 0);
            var photo = RoomPhoto(branchId);
            db.FloorAreas.Add(area);
            db.Photos.Add(photo);
            db.FloorAreaPhotos.Add(new FloorAreaPhoto(area.Id, photo.Id, 0));

            var table = new DiningTable(branchId, label: $"G{Guid.NewGuid():N}"[..8], seats: 4, floorAreaId: area.Id);
            table.PlaceOnPhoto(photo.Id, 0.5, 0.5);
            db.DiningTables.Add(table);
        }

        if (!await db.OpeningHours.AnyAsync(h => h.BranchId == branchId, cancellationToken))
        {
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                db.OpeningHours.Add(new OpeningHours(branchId, day, new TimeOnly(10, 0), new TimeOnly(23, 0), false));
            }
        }

        if (!await db.StaffMembers.AnyAsync(
                s => s.IsActive && s.VenueId == branch.VenueId && (s.BranchId == null || s.BranchId == branchId),
                cancellationToken))
        {
            var unique = Guid.NewGuid().ToString("N")[..7];
            db.StaffMembers.Add(new StaffMember(branch.VenueId, "Test Waiter", $"+3743{unique}", StaffRole.Waiter, "hash", branchId));
        }

        if (!await db.StaffDevices.AnyAsync(d => d.BranchId == branchId && d.RevokedAtUtc == null, cancellationToken))
        {
            db.StaffDevices.Add(new StaffDevice(
                branch.VenueId, branchId, "Counter", $"{SeededTabletPrefix}{Guid.NewGuid():N}", DateTime.UtcNow));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The client id prefix of the tablets this builder enrols, so a test can tell them from its own.</summary>
    public const string SeededTabletPrefix = "test-tablet-";

    /// <summary>Spreads pins across the photo so no two sit on top of each other.</summary>
    private static double PinX(int index) => Math.Round(0.05 + ((index % 10) * 0.09), 2);

    /// <summary>Adds a confirmed booking on a table, so the reservation overlay has something to find.</summary>
    public static async Task<Reservation> AddConfirmedReservationAsync(
        YallaDbContext db,
        TestBranch branch,
        Guid tableId,
        DateTime startUtc,
        int partySize = 4,
        CancellationToken cancellationToken = default)
    {
        // ReservationPolicy is an owned type: EF refuses to project one on its own in a
        // tracking query. We only read it to shape fixture data, so read it untracked.
        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .FirstAsync(cancellationToken);

        var reservation = Reservation.Create(
            branchId: branch.BranchId,
            diningTableId: tableId,
            startUtc: startUtc,
            localDate: DateOnly.FromDateTime(startUtc),
            localStartTime: TimeOnly.FromDateTime(startUtc),
            partySize: partySize,
            guestName: "Ani Test",
            guestPhone: "+37411223344",
            code: Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            policy: policy);

        db.Reservations.Add(reservation);
        await db.SaveChangesAsync(cancellationToken);

        return reservation;
    }

    /// <summary>
    /// Opens a tab on an existing session and sets an outstanding balance, for the
    /// free-a-table-with-money-owed case.
    /// </summary>
    public static async Task<Tab> AddOpenTabAsync(
        YallaDbContext db,
        TestBranch branch,
        Guid tableId,
        Guid tableSessionId,
        long outstandingAmd,
        DateTime openedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var tab = new Tab(
            branchId: branch.BranchId,
            diningTableId: tableId,
            tableSessionId: tableSessionId,
            openedAtUtc: openedAtUtc,
            serviceChargePercentSnapshot: 10m,
            admittedBecause: TabAdmissionReason.OpenedByStaff);

        // Server-computed totals: the only way money gets onto a tab.
        tab.ApplyComputedTotals(subtotalAmd: outstandingAmd, serviceChargeAmd: 0L, paidAmd: 0L);

        db.Tabs.Add(tab);

        var session = await db.TableSessions.FirstAsync(s => s.Id == tableSessionId, cancellationToken);
        session.AttachTab(tab.Id);

        await db.SaveChangesAsync(cancellationToken);

        return tab;
    }
}
