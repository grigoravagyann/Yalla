using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
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
        SubscriptionTier subscriptionTier = SubscriptionTier.Paid,
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
            floorWidth: 1000,
            floorHeight: 700,
            reservationPolicy: policy,
            subscriptionTier: subscriptionTier);

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

        var tables = Enumerable.Range(1, tableCount)
            .Select(i => new DiningTable(
                branch.Id,
                label: i.ToString(),
                seats: seatsPerTable,
                x: 50 * i,
                y: 100,
                width: 90,
                height: 90,
                shape: TableShape.Round,
                floorAreaId: area.Id))
            .ToList();

        db.DiningTables.AddRange(tables);

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
        CancellationToken cancellationToken = default)
    {
        var table = new DiningTable(
            branch.BranchId,
            label: label,
            seats: seats,
            x: 500,
            y: 400,
            width: 120,
            height: 120,
            shape: TableShape.Rectangle,
            isBookable: isBookable);

        db.DiningTables.Add(table);
        await db.SaveChangesAsync(cancellationToken);

        return table;
    }

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
            serviceChargePercentSnapshot: 10m);

        // Server-computed totals: the only way money gets onto a tab.
        tab.ApplyComputedTotals(subtotalAmd: outstandingAmd, serviceChargeAmd: 0L, paidAmd: 0L);

        db.Tabs.Add(tab);

        var session = await db.TableSessions.FirstAsync(s => s.Id == tableSessionId, cancellationToken);
        session.AttachTab(tab.Id);

        await db.SaveChangesAsync(cancellationToken);

        return tab;
    }
}
