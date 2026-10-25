using Microsoft.EntityFrameworkCore;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The questions one booking asks the booking before it on the same table - "will you have left by
/// 19:40?" - in one place: asking, saying yes, closing, and what happens to them when either booking
/// moves or ends. See <see cref="TableShareRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// A static helper over the caller's unit of work, like <see cref="PendingDecline"/> and
/// <see cref="DinerNotices"/>: nothing here saves. Every change commits with the change to the booking
/// that caused it, and every outcome of a request changes the later booking's row as well - so the later
/// booking's row version is what settles a yes, a no, the sweep and a move racing one another.
/// </para>
/// <para>
/// <b>The earlier guest is never locked.</b> Their account is read, not locked, when they are asked: a
/// booking already holds a table's lock and its own diner's, and taking a second diner's from inside
/// would let two such bookings wait on each other. The cost is a question that can reach an account
/// deleted at that same instant, whose feed then holds one entry nobody reads.
/// </para>
/// </remarks>
internal static class TableShares
{
    /// <summary>
    /// Whether the guest of <paramref name="earlierReservationId"/> can be asked in the app: they booked
    /// from it (not the web), the account is live, and it has a phone to push to.
    /// </summary>
    public static async Task<bool> CanAskInAppAsync(
        YallaDbContext db,
        Guid earlierReservationId,
        CancellationToken cancellationToken)
    {
        var earlier = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == earlierReservationId)
            .Select(r => new { r.Channel, r.DinerUserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (earlier is not { DinerUserId: { } dinerUserId } || earlier.Channel == ReservationChannel.Web)
        {
            return false;
        }

        return await db.DinerUsers
                   .AsNoTracking()
                   .AnyAsync(u => u.Id == dinerUserId && u.IsActive && u.DeletedAtUtc == null, cancellationToken)
               && await db.DinerDevices
                   .AsNoTracking()
                   .AnyAsync(d => d.DinerUserId == dinerUserId && d.RevokedAtUtc == null, cancellationToken);
    }

    /// <summary>
    /// Asks: stages the request and puts the question (<see cref="TableShareRequest.Ask"/>) - whose handler pushes it to
    /// the earlier guest when they are asked in the app. Called inside the table's lock, beside the later booking it is
    /// for.
    /// </summary>
    public static TableShareRequest Ask(
        YallaDbContext db,
        Reservation later,
        Guid earlierReservationId,
        DateTime freeByUtc,
        DateTime expiresAtUtc,
        bool asksEarlierGuest,
        ApprovalTrigger? fallbackTrigger,
        DateTime nowUtc)
    {
        var request = new TableShareRequest(
            earlierReservationId, later.Id, later.StartUtc, freeByUtc, nowUtc, expiresAtUtc, asksEarlierGuest,
            fallbackTrigger);

        db.TableShareRequests.Add(request);
        request.Ask();

        return request;
    }

    /// <summary>
    /// The later booking's request was said yes to - by the earlier guest, or by the floor for them: the
    /// earlier booking promises to free the table by the time asked (its end cut to it, the earlier guest told by
    /// the promise's handler), and the request is accepted. The caller confirms the later booking.
    /// </summary>
    /// <returns>The request, or null when the later booking was waiting on none.</returns>
    public static async Task<TableShareRequest?> ApplyYesAsync(
        YallaDbContext db,
        Guid laterReservationId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var request = await PendingForLaterAsync(db, laterReservationId, cancellationToken);

        if (request is null)
        {
            return null;
        }

        var earlier = await db.Reservations.FirstOrDefaultAsync(r => r.Id == request.EarlierReservationId, cancellationToken);
        var later = await db.Reservations.FirstOrDefaultAsync(r => r.Id == laterReservationId, cancellationToken);

        // A booking that has gone since needs no promise: the table is free of it.
        if (earlier is null || !ReservationOverlap.Blocks(earlier.Status))
        {
            request.Accept(nowUtc);

            return request;
        }

        // Still the question that was asked: the earlier booking on the later one's table, starting
        // before the time it is asked to leave by. A move under another table's lock can land between
        // the question and the answer; then there is nothing to say yes to, and the answer is refused
        // rather than written onto a booking it was never about.
        if (later is null
            || earlier.DiningTableId != later.DiningTableId
            || earlier.StartUtc >= request.FreeByUtc
            || earlier.StartUtc >= later.StartUtc)
        {
            throw new TableRequestNotPendingException(
                earlier.Id, request.Id, TableShareRequestStatus.Withdrawn, request.ExpiresAtUtc);
        }

        request.Accept(nowUtc);
        earlier.PromiseToFreeBy(request, nowUtc);

        return request;
    }

    /// <summary>
    /// Ends the later booking's open request without a yes: declined, expired, or withdrawn because the
    /// later booking moved or ended.
    /// </summary>
    /// <returns>The request, or null when there was none open.</returns>
    public static async Task<TableShareRequest?> CloseForLaterAsync(
        YallaDbContext db,
        Guid laterReservationId,
        TableShareRequestStatus to,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var request = await PendingForLaterAsync(db, laterReservationId, cancellationToken);

        if (request is null)
        {
            return null;
        }

        switch (to)
        {
            case TableShareRequestStatus.Declined:
                request.Decline(nowUtc);
                break;

            case TableShareRequestStatus.Expired:
                request.Expire(nowUtc);
                break;

            case TableShareRequestStatus.Withdrawn:
                request.Withdraw(nowUtc);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(to), to, "Not a way to close a request without a yes.");
        }

        return request;
    }

    /// <summary>
    /// Withdraws the later booking's open request at once, inside the caller's transaction - for a move,
    /// which may ask a new question of the same guest in the same save, where the filtered unique index
    /// must see the old one closed first.
    /// </summary>
    public static Task<int> WithdrawForLaterNowAsync(
        YallaDbContext db,
        Guid laterReservationId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        db.TableShareRequests
            .Where(r => r.LaterReservationId == laterReservationId && r.Status == TableShareRequestStatus.Pending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, TableShareRequestStatus.Withdrawn)
                    .SetProperty(r => r.DecidedAtUtc, nowUtc),
                cancellationToken);

    /// <summary>
    /// The earlier booking moved, or ended: every booking waiting on a question to its guest is judged
    /// again by the table's evening (<see cref="TableEvening"/>), with the earlier booking where it now
    /// stands.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>
    /// <b>Still asking the same guest</b> - it is still the booking before, within three hours: the
    /// question stands.
    /// </item>
    /// <item>
    /// <b>Free now</b> - the earlier booking left, moved off the table, moved early enough, or promised a
    /// time that covers it: the request is withdrawn and the later booking decided again without it
    /// (<see cref="DecideWithoutTheQuestion"/>): confirmed when nothing else needs a human, otherwise
    /// left waiting for that reason - which is why a request stores <c>FallbackTrigger</c>.
    /// </item>
    /// <item>
    /// <b>Needing somebody else's yes</b> - another booking's sitting now runs into it: the request is
    /// withdrawn and the floor decides the later booking from the awaiting list, by the deadline it
    /// already had, never past the new guest's start.
    /// </item>
    /// <item>
    /// <b>Crowded out</b> - the earlier booking now leaves it no room. A booking only asking for a table is
    /// never forced on the booking that has it, so the earlier booking's change stands and is taken as its
    /// guest's no: the later booking is declined, <c>EarlierGuestDeclined</c>. A later booking that was
    /// itself a change of a confirmed one is declined outright here - going back to its old slot would
    /// need that table's lock inside this one - the one case this does not restore.
    /// </item>
    /// </list>
    /// A later booking already past its deadline, or already under way, is left for the sweep.
    /// </remarks>
    public static async Task EarlierChangedAsync(
        YallaDbContext db,
        Reservation earlier,
        Branch branch,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var open = await db.TableShareRequests
            .Where(r => r.EarlierReservationId == earlier.Id && r.Status == TableShareRequestStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var request in open)
        {
            var later = await db.Reservations.FirstOrDefaultAsync(r => r.Id == request.LaterReservationId, cancellationToken);

            if (later is null || later.Status != ReservationStatus.PendingApproval)
            {
                request.Withdraw(nowUtc);
                continue;
            }

            if (later.ApprovalHasLapsedAt(nowUtc) || later.StartUtc <= nowUtc)
            {
                continue;
            }

            var (verdict, _) = await TableEvening.JudgeAsync(
                db,
                later.DiningTableId,
                later.StartUtc,
                branch.ReservationPolicy,
                excludingReservationId: later.Id,
                cancellationToken,
                overlay: earlier);

            // Still the same question, to the same guest.
            if (verdict is { NeedsEarlierGuest: true, Earlier: { } stillAsked } && stillAsked.ReservationId == earlier.Id)
            {
                continue;
            }

            if (verdict.IsFree && TableEvening.Holds(verdict, later.EndUtc))
            {
                request.Withdraw(nowUtc);
                DecideWithoutTheQuestion(later, request, nowUtc);
                continue;
            }

            if (verdict.NeedsEarlierGuest)
            {
                // Somebody else's guest is the one before it now - a long sitting, rare. Nothing new is
                // pushed to anybody: the floor decides it from the awaiting list, by the deadline the
                // booking already had - a wait never gets a later one - but never past the new guest's
                // start; one from before every such wait had a clock gets the two hours a new one would.
                request.Withdraw(nowUtc);
                later.WaitInsteadFor(
                    ApprovalTrigger.EarlierGuestConsent,
                    TableSequencing.AnswerDeadlineNoLaterThan(
                        later.ApprovalExpiresAtUtc,
                        nowUtc,
                        TableShareRequest.AnswerWindowMinutes,
                        verdict.Earlier!.Value.StartUtc,
                        later.StartUtc),
                    nowUtc);

                continue;
            }

            // Crowded out: declined, and its guest told, by the decline's handlers.
            request.Decline(nowUtc);
            later.Reject(nowUtc, reason: null, DeclineReason.EarlierGuestDeclined);
        }
    }

    /// <summary>
    /// A booking whose question to the guest before it went away is decided again without it, from the
    /// reason it would have waited for anyway (<see cref="TableShareRequest.FallbackTrigger"/>): confirmed
    /// when there is none - and its diner told, by the approval's handlers - left as it is when that reason is
    /// short notice (it is already the floor's, with its fifteen minutes), and otherwise waiting for that reason,
    /// as any such booking waits, with no clock - the two hours were the question's, and the question is gone.
    /// </summary>
    public static void DecideWithoutTheQuestion(Reservation later, TableShareRequest request, DateTime nowUtc)
    {
        if (later.Status != ReservationStatus.PendingApproval || later.ApprovalHasLapsedAt(nowUtc) || later.StartUtc <= nowUtc)
        {
            return;
        }

        switch (request.FallbackTrigger)
        {
            case null:
                later.Confirm(nowUtc, ReservationApprover.NoLongerAsked);
                break;

            case ApprovalTrigger.ShortNotice:
                break;

            case { } other:
                later.WaitInsteadFor(other, approvalExpiresAtUtc: null, nowUtc);
                break;
        }
    }

    /// <summary>The later booking's open request, tracked, or null.</summary>
    public static Task<TableShareRequest?> PendingForLaterAsync(
        YallaDbContext db,
        Guid laterReservationId,
        CancellationToken cancellationToken) =>
        db.TableShareRequests
            .Where(r => r.LaterReservationId == laterReservationId && r.Status == TableShareRequestStatus.Pending)
            .OrderBy(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
}

