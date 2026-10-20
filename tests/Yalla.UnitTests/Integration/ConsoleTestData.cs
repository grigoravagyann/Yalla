using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Fixture data for the platform console's read models: bookings on given local dates in given
/// statuses, open and closing tabs, and a branch with its whole checklist done.
/// </summary>
/// <remarks>
/// Written straight to the tables, like <see cref="ReviewTestData"/>: what these build is the
/// precondition of a count, not its subject. Every booking carries a guest name and number that the
/// tests then look for in the response, to prove the aggregates leave them out.
/// </remarks>
internal static class ConsoleTestData
{
    /// <summary>The guest name every seeded booking carries, so a response can be searched for it.</summary>
    public const string GuestName = "Anahit Secretguest";

    /// <summary>The guest number every seeded booking carries - in no other fixture.</summary>
    public const string GuestPhone = "+37491555044";

    /// <summary>
    /// A booking on <paramref name="localDate"/> at 19:00 at the branch (Asia/Yerevan, four hours ahead
    /// of UTC all year), at table <paramref name="tableIndex"/>, moved to <paramref name="status"/>.
    /// </summary>
    public static async Task<Reservation> AddBookingAsync(
        YallaDbContext db,
        TestBranch branch,
        DateOnly localDate,
        ReservationStatus status,
        int partySize = 2,
        int tableIndex = 0,
        TimeOnly? localTime = null)
    {
        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .FirstAsync();

        var time = localTime ?? new TimeOnly(19, 0);
        var startUtc = DateTime.SpecifyKind(localDate.ToDateTime(time), DateTimeKind.Utc).AddHours(-4);

        var booking = Reservation.Create(
            branch.BranchId,
            branch.TableIds[tableIndex],
            startUtc,
            localDate,
            time,
            partySize,
            GuestName,
            GuestPhone,
            Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            policy,
            initialStatus: status == ReservationStatus.PendingApproval
                ? ReservationStatus.PendingApproval
                : ReservationStatus.Confirmed);

        switch (status)
        {
            case ReservationStatus.Seated:
                booking.MarkSeated();
                break;
            case ReservationStatus.Completed:
                booking.MarkSeated();
                booking.MarkCompleted();
                break;
            case ReservationStatus.CancelledByDiner:
                booking.CancelByDiner(startUtc.AddDays(-1), "Plans changed.", afterDeadline: false);
                break;
            case ReservationStatus.CancelledByVenue:
                booking.CancelByVenue(startUtc.AddDays(-1), "Closed for a private event.");
                break;
            case ReservationStatus.NoShow:
                booking.MarkNoShow(startUtc.AddMinutes(30));
                break;
        }

        db.Reservations.Add(booking);
        await db.SaveChangesAsync();

        return booking;
    }

    /// <summary>A party seated at table <paramref name="tableIndex"/> with a tab, left open or moved on.</summary>
    public static async Task<Tab> AddTabAsync(
        YallaDbContext db,
        TestBranch branch,
        int tableIndex,
        DateTime atUtc,
        TabStatus status = TabStatus.Open)
    {
        var table = await db.DiningTables.FirstAsync(t => t.Id == branch.TableIds[tableIndex]);
        var session = TableSession.SeatWalkIn(branch.BranchId, table.Id, 2, atUtc, branch.WaiterId);
        db.TableSessions.Add(session);
        table.Occupy(session.Id);

        var tab = new Tab(branch.BranchId, table.Id, session.Id, atUtc, 10m, TabAdmissionReason.OpenedByStaff);
        db.Tabs.Add(tab);
        session.AttachTab(tab.Id);

        if (status == TabStatus.Closing)
        {
            tab.BeginClosing();
        }
        else if (status == TabStatus.Closed)
        {
            tab.Close(atUtc.AddHours(1));
        }

        await db.SaveChangesAsync();

        return tab;
    }

    /// <summary>
    /// Every line of the readiness checklist done, and a cover photo: a finished menu, a saved
    /// reservation policy, an enrolled tablet. The builder's branch already has tables, hours and staff.
    /// </summary>
    public static async Task<TestBranch> ReadyBranchAsync(YallaDbContext db, DateTime atUtc, string? venueName = null)
    {
        var branch = await TestBranchBuilder.CreateAsync(db, subscriptionTier: SubscriptionTier.Free);

        await TestMenuBuilder.CreateAsync(db, branch.BranchId);
        await ReviewTestData.SavePolicyAsync(db, branch.BranchId, atUtc);

        db.StaffDevices.Add(new StaffDevice(branch.VenueId, branch.BranchId, "Bar tablet", $"client-{Guid.NewGuid():N}", atUtc));

        var cover = await TestMenuBuilder.AddPhotoAsync(db, branch.BranchId);
        var row = await db.Branches.Include(b => b.Venue).FirstAsync(b => b.Id == branch.BranchId);
        row.SetCoverPhoto(cover);

        if (venueName is not null)
        {
            row.Venue.Rename(venueName);
        }

        await db.SaveChangesAsync();

        return branch;
    }

    /// <summary>Renames the builder's venue, so a test can find it by name.</summary>
    public static async Task RenameVenueAsync(YallaDbContext db, Guid venueId, string name)
    {
        var venue = await db.Venues.FirstAsync(v => v.Id == venueId);
        venue.Rename(name);
        await db.SaveChangesAsync();
    }

    /// <summary>Another branch at the builder's venue, on the given tier, with no tables.</summary>
    public static async Task<Guid> AddBranchAsync(
        YallaDbContext db,
        Guid venueId,
        string name,
        SubscriptionTier tier,
        bool isActive = true)
    {
        var venue = await db.Venues.FirstAsync(v => v.Id == venueId);
        var branch = new Branch(
            venue,
            name: name,
            slug: $"extra-{Guid.NewGuid():N}"[..20],
            address: "2 Test Street, Yerevan",
            latitude: 40.19,
            longitude: 44.52,
            timeZoneId: "Asia/Yerevan",
            subscriptionTier: tier);

        branch.SetActive(isActive);
        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        return branch.Id;
    }

    /// <summary>The date at the branch (Asia/Yerevan, UTC+4) at <paramref name="utc"/>.</summary>
    public static DateOnly YerevanDate(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(4));
}
