using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;

namespace Yalla.Infrastructure.Persistence;

/// <summary>
/// Creates a minimal branch to work against in development: one venue, one branch, two floor
/// areas, a handful of tables, a waiter and a manager.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the dev actor stub has to report a <i>real</i> staff member -
/// <c>TableSession.SeatedByStaffId</c> is a foreign key, so a made-up id fails on the first
/// seating. It is deliberately tiny and has no menu, no bookings and no tabs: those belong to the
/// tasks that build them.
/// </para>
/// <para>
/// Idempotent, and keyed on the venue slug rather than on hardcoded primary keys, so it can run
/// on every startup and survives the database being dropped and recreated.
/// </para>
/// </remarks>
internal sealed class DevDataSeeder(
    YallaDbContext db,
    DevSeedRegistry registry,
    ILogger<DevDataSeeder> logger)
{
    private const string VenueSlug = "yalla-demo";
    private const string BranchSlug = "yerevan-centre";
    private const string WaiterPhone = "+37410000001";
    private const string ManagerPhone = "+37410000002";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var venue = await db.Venues
            .Include(v => v.Branches)
            .FirstOrDefaultAsync(v => v.Slug == VenueSlug, cancellationToken);

        if (venue is null)
        {
            venue = new Venue("Yalla Demo Cafe", VenueType.Cafe, VenueSlug);
            db.Venues.Add(venue);
            logger.LogInformation("Seeding development venue {Slug}.", VenueSlug);
        }

        var branch = venue.Branches.FirstOrDefault(b => b.Slug == BranchSlug)
                     ?? await db.Branches.FirstOrDefaultAsync(
                         b => b.VenueId == venue.Id && b.Slug == BranchSlug, cancellationToken);

        if (branch is null)
        {
            branch = new Branch(
                venue,
                name: "Yerevan Centre",
                slug: BranchSlug,
                address: "12 Abovyan Street, Yerevan",
                latitude: 40.1830,
                longitude: 44.5150,
                timeZoneId: "Asia/Yerevan",
                floorWidth: 1000,
                floorHeight: 700);

            db.Branches.Add(branch);
            logger.LogInformation("Seeding development branch {Slug}.", BranchSlug);
        }

        var waiter = await EnsureStaffAsync(venue.Id, branch.Id, "Aram Waiter", WaiterPhone, StaffRole.Waiter, cancellationToken);
        var manager = await EnsureStaffAsync(venue.Id, branch.Id, "Nune Manager", ManagerPhone, StaffRole.Manager, cancellationToken);

        await EnsureFloorAsync(branch, cancellationToken);
        await EnsureOpeningHoursAsync(branch, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        registry.Publish(venue.Id, branch.Id, waiter.Id, manager.Id);

        logger.LogInformation(
            "Development data ready. BranchId={BranchId} WaiterId={WaiterId} ManagerId={ManagerId}",
            branch.Id, waiter.Id, manager.Id);
    }

    private async Task<StaffMember> EnsureStaffAsync(
        Guid venueId,
        Guid branchId,
        string fullName,
        string phone,
        StaffRole role,
        CancellationToken cancellationToken)
    {
        var existing = await db.StaffMembers
            .FirstOrDefaultAsync(s => s.VenueId == venueId && s.Phone == phone, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        // Not a real hash and not a real credential - authentication is a later task, and this
        // value is never checked against anything.
        var staff = new StaffMember(venueId, fullName, phone, role, "dev-no-pin-set", branchId);
        db.StaffMembers.Add(staff);

        return staff;
    }

    /// <summary>
    /// Gives the demo branch a week of opening hours.
    /// </summary>
    /// <remarks>
    /// Not decoration. Every booking rule checks that the whole sitting falls inside an opening
    /// block, so a branch with no hours at all refuses <i>every</i> booking with
    /// <c>OutsideOpeningHours</c> - which looks exactly like a broken booking endpoint rather than
    /// like missing seed data.
    /// </remarks>
    private async Task EnsureOpeningHoursAsync(Branch branch, CancellationToken cancellationToken)
    {
        var hasHours = branch.OpeningHours.Count > 0
                       || await db.OpeningHours.AnyAsync(h => h.BranchId == branch.Id, cancellationToken);

        if (hasHours)
        {
            return;
        }

        // 09:00 to midnight on weekdays; Friday and Saturday run to 01:00, which is what makes the
        // ClosesNextDay path reachable from the demo data rather than only from a test.
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var closesNextDay = day is DayOfWeek.Friday or DayOfWeek.Saturday;

            db.OpeningHours.Add(new OpeningHours(
                branch.Id,
                day,
                opensAt: new TimeOnly(9, 0),
                closesAt: closesNextDay ? new TimeOnly(1, 0) : new TimeOnly(23, 59),
                closesNextDay: closesNextDay));
        }

        logger.LogInformation("Seeding development opening hours for branch {Slug}.", BranchSlug);
    }

    private async Task EnsureFloorAsync(Branch branch, CancellationToken cancellationToken)
    {
        var hasTables = branch.DiningTables.Count > 0
                        || await db.DiningTables.AnyAsync(t => t.BranchId == branch.Id, cancellationToken);

        if (hasTables)
        {
            return;
        }

        var windows = new FloorArea(branch.Id, "Windows", displayOrder: 0);
        var terrace = new FloorArea(branch.Id, "Terrace", displayOrder: 1);
        db.FloorAreas.AddRange(windows, terrace);

        // A small, plausible room: four two-seaters along the windows, three fours on the
        // terrace, and one large table that exercises the approval threshold later.
        var tables = new List<DiningTable>();

        for (var i = 0; i < 4; i++)
        {
            tables.Add(new DiningTable(
                branch.Id, label: (i + 1).ToString(), seats: 2,
                x: 80 + (i * 140), y: 90, width: 90, height: 90,
                shape: TableShape.Round, floorAreaId: windows.Id));
        }

        for (var i = 0; i < 3; i++)
        {
            tables.Add(new DiningTable(
                branch.Id, label: (i + 5).ToString(), seats: 4,
                x: 100 + (i * 200), y: 400, width: 140, height: 100,
                shape: TableShape.Rectangle, floorAreaId: terrace.Id));
        }

        tables.Add(new DiningTable(
            branch.Id, label: "8", seats: 10,
            x: 700, y: 380, width: 240, height: 140,
            shape: TableShape.Rectangle, floorAreaId: terrace.Id));

        db.DiningTables.AddRange(tables);

        logger.LogInformation("Seeding {Count} development tables for branch {BranchId}.", tables.Count, branch.Id);
    }
}
