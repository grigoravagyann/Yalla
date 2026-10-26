using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.BranchSettings;
using Yalla.Domain.Enums;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The reservation policy: reading it, its defaults for the venue's type, and saving it.
/// </summary>
/// <remarks>
/// Saving never touches a booking already made: the ones the new rules would not have allowed are counted,
/// reported and left exactly as they were.
/// </remarks>
internal sealed class BranchPolicySettings(
    YallaDbContext db,
    IClock clock,
    IStaffBranchGuard branchGuard,
    BranchSettingsHistory history,
    ILogger logger,
    BranchSettingsLookups lookups)
{
    public async Task<ReservationPolicyView> GetReservationPolicyAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the reservation policy", cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        return ReservationPolicyView.From(branch.ReservationPolicy);
    }

    public async Task<ReservationPolicyDefaultsView> GetReservationPolicyDefaultsAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the reservation policy", cancellationToken);

        // The venue's type as it is now: the defaults follow it, as a new branch's policy would.
        var venueType = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => (VenueType?)b.Venue.Type)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        return new ReservationPolicyDefaultsView(
            ReservationPolicyView.From(ReservationPolicy.DefaultFor(venueType)),
            ReservationPolicyDefaultsView.AllBounds);
    }

    public async Task<ReservationPolicyChangeResult> UpdateReservationPolicyAsync(
        Guid branchId,
        ReservationPolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await branchGuard.RequireAtBranchAsync(branchId, "Save the reservation policy", cancellationToken);

        var branch = await lookups.LoadBranchAsync(branchId, cancellationToken);

        // Constructor checks, then the editing bounds. Refuses; never clamps.
        var policy = command.ToPolicy(branch.ReservationPolicy);
        var nowUtc = clock.UtcNow;

        // Which live bookings the new rules would not have allowed. Counted, reported, and left
        // alone: a settings change must never rewrite or cancel a reservation as a side effect.
        var windowEndsUtc = nowUtc.AddDays(policy.BookingWindowDays);

        var affected = await db.Reservations
            .AsNoTracking()
            .Where(r => r.BranchId == branch.Id
                        && r.StartUtc > nowUtc
                        && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.PendingApproval))
            .Where(r => r.StartUtc > windowEndsUtc

                        // A booking cut short by its guest's promise to free the table ends when they
                        // promised, not a turn time after it starts: it is outside the new rules only if
                        // it runs longer than the new turn time.
                        || (r.PromisedFreeByUtc == null
                            && EF.Functions.DateDiffMinute(r.StartUtc, r.EndUtc) != policy.TurnTimeMinutes)
                        || (r.PromisedFreeByUtc != null
                            && EF.Functions.DateDiffMinute(r.StartUtc, r.EndUtc) > policy.TurnTimeMinutes))
            .OrderBy(r => r.StartUtc)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        // Saving the form is what counts as having reviewed it, which is the line the onboarding
        // checklist reads. A branch ships with defaults, so "has a policy" was never the question.
        var policyBefore = ReservationPolicyView.From(branch.ReservationPolicy);

        branch.UpdateReservationPolicy(policy, nowUtc);

        await history.RecordAsync(
            branch,
            ActivityActions.SettingsPolicy,
            policyBefore,
            ReservationPolicyView.From(policy),
            new { affectedBookings = affected.Count },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        if (affected.Count > 0)
        {
            logger.LogInformation(
                "Reservation policy on branch {BranchId} changed; {Count} existing booking(s) fall outside the new rules and were left unchanged.",
                branch.Id, affected.Count);
        }

        return new ReservationPolicyChangeResult(ReservationPolicyView.From(policy), affected.Count, affected);
    }
}
