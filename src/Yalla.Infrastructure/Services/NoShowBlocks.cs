using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Application.Tables;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// The no-show block's writer (<see cref="NoShowBlockRules"/>): judges, after a no-show is saved, whether the guest's
/// no-shows now reach the threshold, and if so blocks them and cancels every booking of theirs not yet started. Shared
/// by a booking's no-show (<c>ReservationService.ReleaseAsync</c>) and a takeaway order's (<c>TakeawayService</c>), which
/// count toward the block alike (<see cref="GuestRecords.BlockingNoShowDaysAsync"/>).
/// </summary>
/// <remarks>
/// The caller holds the guest's account lock in an open transaction and commits; everything here is staged in its unit
/// of work, except <see cref="ReleaseHoldsOfAsync"/>, which runs after the commit.
/// </remarks>
internal sealed class NoShowBlocks(
    YallaDbContext db,
    ITableStateService tableState,
    ILogger<NoShowBlocks> logger)
{
    /// <summary>
    /// Blocks the guest for <see cref="NoShowBlockRules.BlockDays"/> days when the no-shows that count toward a block
    /// reach <see cref="NoShowBlockRules.Threshold"/>, and cancels every booking of theirs not yet started. Stages
    /// everything in the caller's unit of work; returns the bookings it cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called under the account's lock, inside the no-show's transaction, after the no-show is saved: every no-show
    /// is read as committed - one another venue marked a moment ago included, since it held the same lock until it
    /// committed.
    /// </para>
    /// <para>
    /// What counts is <see cref="GuestRecords.BlockingNoShowDaysAsync"/>: the account's own proved bookings, marked
    /// after the guest's grace, at most one per venue per service day, started in the last 72 hours and after the
    /// last block ended or was lifted - and the account's takeaway orders not collected, alike. So one venue can never
    /// block a guest in one evening, a waiter who is quick to mark never does, and a lifted block's no-shows never
    /// block again.
    /// </para>
    /// <para>
    /// A block already in force is left as it is: a fourth no-show in the same days neither extends it nor
    /// cancels again (there is nothing left to cancel; the block refused every booking since).
    /// </para>
    /// </remarks>
    public async Task<(IReadOnlyList<Reservation> Cancelled, DateTime? BlockedUntilUtc)> BlockIfDueAsync(
        Guid account,
        Branch triggeredAt,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var counted = await GuestRecords.BlockingNoShowDaysAsync(db, account, nowUtc, cancellationToken);

        if (counted < NoShowBlockRules.Threshold)
        {
            return ([], null);
        }

        var proved = await db.DinerUsers
            .AsNoTracking()
            .Where(d => d.Id == account && d.PhoneVerifiedAtUtc != null)
            .Select(d => d.PhoneE164)
            .FirstOrDefaultAsync(cancellationToken);
        var phone = ReservationCreation.BookerPhone(proved);

        if (await GuestRecords.BlockedUntilAsync(db, account, phone, nowUtc, cancellationToken) is not null)
        {
            return ([], null);
        }

        var block = new BookingBlock(account, phone, nowUtc);
        db.BookingBlocks.Add(block);

        // Every booking of the guest's not yet started, at every venue. Read with an update lock, so a guest
        // cancelling or moving one this moment waits for this to commit rather than failing it (review N8).
        var toCancel = await db.Reservations
            .FromSql(
                $"""
                 SELECT * FROM dbo.Reservations WITH (UPDLOCK, ROWLOCK)
                 WHERE DinerUserId = {account} AND Status IN (1, 2) AND StartUtc > {nowUtc}
                 """)
            .ToListAsync(cancellationToken);

        foreach (var booking in toCancel)
        {
            // As for any cancellation: its questions go and the guest before it is settled. Its unsent reminders and
            // feed entries, its History's line - the platform's doing, not the staff member's at the venue whose no-show
            // made it - and a row on its table's change stream naming it (signal 4, review S8) are the cancellation's
            // handlers'.
            booking.CancelForNoShowBlock(nowUtc);

            var branch = await LoadBranchAsync(booking.BranchId, cancellationToken);

            await TableShares.CloseForLaterAsync(db, booking.Id, TableShareRequestStatus.Withdrawn, nowUtc, cancellationToken);
            await TableShares.EarlierChangedAsync(db, booking, branch, nowUtc, cancellationToken);
        }

        // One push and one feed entry for the whole block - what happened, and the day they can book again, on the wall
        // clock of the venue whose no-show made it (review N3) - the block's handler's.
        var until = block.BlockedUntilUtc;
        var untilLocal = BranchZone.For(triggeredAt.TimeZoneId).LocalDateAt(until);

        block.Impose(toCancel.Count, untilLocal);

        logger.LogInformation(
            "Diner {DinerUserId} missed bookings at {Days} venue-days in {Hours} hours; blocked until {BlockedUntilUtc} and {Cancelled} bookings cancelled.",
            account, counted, NoShowBlockRules.WindowHours, until, toCancel.Count);

        return (toCancel, until);
    }

    /// <summary>
    /// Gives back, through the table state machine, the tables held for bookings a block just cancelled - so the
    /// change stream moves and every floor sees them free. Best effort: the cancellations are already committed,
    /// and a hold left behind is the floor's to release by hand, as for any cancelled booking.
    /// </summary>
    public async Task ReleaseHoldsOfAsync(IReadOnlyList<Reservation> cancelled, CancellationToken cancellationToken)
    {
        foreach (var booking in cancelled)
        {
            try
            {
                var held = await db.DiningTables
                    .AsNoTracking()
                    .AnyAsync(t => t.Id == booking.DiningTableId && t.Status == TableStatus.Held, cancellationToken);

                if (held && await HoldIsForAsync(db, booking.DiningTableId, booking.Id, cancellationToken))
                {
                    await tableState.ReleaseHoldAsync(
                        new TableStateCommand(
                            booking.BranchId, booking.DiningTableId, Guid.CreateVersion7(), NoShowBlockRules.CancellationReason),
                        cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "The hold for booking {Code}, cancelled by a no-show block, was not released.", booking.Code);
            }
        }
    }

    /// <summary>
    /// Whether the table's current hold was placed for this booking.
    /// </summary>
    /// <remarks>
    /// <c>DiningTable</c> stores that it is <c>Held</c> and not who for - the hold is a physical
    /// fact and the booking is a financial one, and coupling them on the row was the wrong trade.
    /// The audit log knows: the most recent transition into <c>Held</c> names the booking it was
    /// placed for. Releasing a different booking must not free a table being held for somebody else.
    /// </remarks>
    internal static async Task<bool> HoldIsForAsync(YallaDbContext db, Guid tableId, Guid reservationId, CancellationToken cancellationToken)
    {
        var mostRecentHold = await db.TableStateChanges
            .AsNoTracking()
            .Where(c => c.DiningTableId == tableId && c.ToStatus == TableStatus.Held)
            .OrderByDescending(c => c.Sequence)
            .Select(c => c.ReservationId)
            .FirstOrDefaultAsync(cancellationToken);

        return mostRecentHold == reservationId;
    }

    private async Task<Branch> LoadBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .Include(b => b.Venue)
            .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);

        return branch ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");
    }
}
