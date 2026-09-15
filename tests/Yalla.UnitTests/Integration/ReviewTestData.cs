using Microsoft.EntityFrameworkCore;
using Yalla.Domain.Enums;
using Yalla.Domain.Identity;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Tabs;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.UnitTests.Integration;

/// <summary>
/// Fixture data for reviews, their moderation, the app booking gate and booking notes (K8, K9).
/// </summary>
/// <remarks>
/// Written straight to the tables, because what these build - a visit, a review somebody wrote, a
/// policy somebody saved - is the precondition of a test, not its subject. The subject always goes
/// through the real endpoint.
/// </remarks>
internal static class ReviewTestData
{
    /// <summary>
    /// The next number from the +374 99 000 xxx test range. The fixture's database is created for the
    /// run and dropped after it, so unique within a run is unique.
    /// </summary>
    public static string NextPhone() => $"+37499000{Interlocked.Increment(ref _nextPhone) % 1000:000}";

    private static int _nextPhone = 300;

    /// <summary>
    /// A visit: a party seated at the branch's first free table with a tab, and each diner given an
    /// approved place on it at <paramref name="atUtc"/>.
    /// </summary>
    public static async Task<Guid> SeedTabVisitAsync(
        YallaDbContext db,
        AuthBranch branch,
        DateTime atUtc,
        params Guid[] dinerUserIds)
    {
        var tableId = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.BranchId == branch.BranchId && t.IsActive && t.Status == TableStatus.Free)
            .OrderBy(t => t.Label)
            .Select(t => t.Id)
            .FirstAsync();

        var tab = await AuthTestData.CreateOpenTabAsync(db, branch, tableId, atUtc);

        foreach (var dinerUserId in dinerUserIds)
        {
            var place = TabParticipant.Guest(
                tab.TabId, "Guest", $"device-{Guid.NewGuid():N}", atUtc, hideTotalFromGuests: false, dinerUserId);

            place.Approve(atUtc);
            db.TabParticipants.Add(place);
        }

        await db.SaveChangesAsync();

        return tab.TabId;
    }

    /// <summary>A booking of the diner's at the branch's first table, moved to <paramref name="status"/>.</summary>
    public static async Task<Guid> SeedBookingAsync(
        YallaDbContext db,
        AuthBranch branch,
        Guid dinerUserId,
        DateTime startUtc,
        ReservationStatus status)
    {
        var policy = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branch.BranchId)
            .Select(b => b.ReservationPolicy)
            .FirstAsync();

        // Asia/Yerevan has no daylight saving, so the wall clock is always four hours ahead.
        var local = startUtc.AddHours(4);

        var booking = Reservation.Create(
            branch.BranchId,
            branch.FirstTableId,
            startUtc,
            DateOnly.FromDateTime(local),
            TimeOnly.FromDateTime(local),
            partySize: 2,
            guestName: "Ani Visit",
            guestPhone: NextPhone(),
            code: Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            policy: policy,
            dinerUserId: dinerUserId);

        if (status is ReservationStatus.Seated or ReservationStatus.Completed)
        {
            booking.MarkSeated();
        }

        if (status == ReservationStatus.Completed)
        {
            booking.MarkCompleted();
        }

        db.Reservations.Add(booking);
        await db.SaveChangesAsync();

        return booking.Id;
    }

    /// <summary>A phone-verified diner account with the given display name and no sign-in.</summary>
    public static async Task<Guid> SeedDinerAsync(YallaDbContext db, string? displayName, DateTime atUtc)
    {
        var diner = new DinerUser(NextPhone(), "en", displayName);
        diner.MarkPhoneVerified(atUtc);

        db.DinerUsers.Add(diner);
        await db.SaveChangesAsync();

        return diner.Id;
    }

    /// <summary>A review, written straight to the table - no visit needed, this is its precondition.</summary>
    public static async Task<Guid> SeedReviewAsync(
        YallaDbContext db,
        Guid branchId,
        Guid dinerUserId,
        int rating,
        string? text,
        DateTime atUtc)
    {
        var review = new BranchReview(branchId, dinerUserId, rating, text, atUtc);

        db.BranchReviews.Add(review);
        await db.SaveChangesAsync();

        return review.Id;
    }

    /// <summary>
    /// Saves the branch's reservation policy as it stands - which is what marks it reviewed - and
    /// optionally switches online bookings on or off and changes whether bookings confirm themselves.
    /// </summary>
    public static async Task SavePolicyAsync(
        YallaDbContext db,
        Guid branchId,
        DateTime atUtc,
        bool autoConfirm = true,
        bool? acceptsOnlineBookings = null)
    {
        var branch = await db.Branches.FirstAsync(b => b.Id == branchId);
        var p = branch.ReservationPolicy;

        branch.UpdateReservationPolicy(
            new ReservationPolicy(
                p.TurnTimeMinutes,
                p.BufferMinutes,
                p.GraceMinutes,
                p.LateNudgeAfterMinutes,
                p.GraceExtensionMinutes,
                p.MinLeadMinutes,
                p.BookingWindowDays,
                p.CancellationDeadlineMinutes,
                autoConfirm,
                p.ServiceChargePercent,
                p.PricesIncludeVat,
                p.MaxSeatOverhang,
                p.ApprovalRequiredAbovePartySize,
                p.WalkInHoldbackMinutes,
                p.ReminderHoursBefore),
            atUtc);

        if (acceptsOnlineBookings is { } accepts)
        {
            branch.SetAcceptsWebBookings(accepts);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A booking body for <c>POST /api/reservations</c>: two days out at 18:00 at the branch, party of two.
    /// </summary>
    public static object Booking(
        YallaApiFactory factory,
        AuthBranch branch,
        Guid tableId,
        ReservationChannel channel,
        string? note = null)
    {
        var local = factory.Clock.UtcNow.AddHours(4);

        return new
        {
            branchId = branch.BranchId,
            tableId,
            date = DateOnly.FromDateTime(local).AddDays(2).ToString("yyyy-MM-dd"),
            time = "18:00",
            partySize = 2,
            guestName = "Ani Test",
            guestPhone = "+37499000999",
            clientCommandId = Guid.CreateVersion7(),
            channel,
            note,
        };
    }
}
