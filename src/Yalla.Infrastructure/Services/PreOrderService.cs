using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Pre-orders on a booking: the guest's edits (app and manage link), "on my way", the floor tablet's rail with its two
/// actions, and the console's settings. See <c>docs/reservations.md</c>, "Pre-orders".
/// </summary>
/// <remarks>
/// What a change tells the rest of the system - the History's line, the "are you on your way?" question moved or
/// withdrawn, the tab's stream when a pre-order lands on a tab, signal 6 - hangs on the pre-order's and the booking's
/// domain events (<c>docs/domain-events.md</c>, pre-orders), handled in the save. Each save here dispatches first with
/// the scope's dispatcher, as the ledger does, so a test composing this service over a bare context runs production's
/// handlers. The settings' own History line stays here: one path, the settings'.
/// </remarks>
internal sealed partial class PreOrderService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IAuthorizationQueries authorization,
    IStaffBranchGuard branchGuard,
    TableLock tableLock,
    TabLedger ledger,
    IActivityLog activity,
    DomainEventDispatcher domainEvents,
    ILogger<PreOrderService> logger) : IPreOrderService
{
    /// <summary>What a guest's save is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string ReplaceCommandType = "pre-order.replace";

    /// <summary>What a guest's clearing is recorded as in <c>ProcessedCommands</c>.</summary>
    internal const string ClearCommandType = "pre-order.clear";

    private static readonly JsonSerializerOptions RecordJson = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- the guest

    public async Task<PreOrderResult> ReplaceAsync(ReplacePreOrderCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Pre-order");
        var reservation = await OwnBookingAsync(command.ReservationId, dinerUserId, cancellationToken);

        return await ReplaceCoreAsync(
            reservation, dinerUserId, command.Lines, command.Note, command.ClientCommandId, cancellationToken);
    }

    public async Task<PreOrderResult> ClearAsync(
        Guid reservationId, Guid clientCommandId, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner("Clear a pre-order");
        var reservation = await OwnBookingAsync(reservationId, dinerUserId, cancellationToken);

        return await ClearCoreAsync(reservation, dinerUserId, clientCommandId, cancellationToken);
    }

    public async Task<PreOrderResult> ReplaceByManageTokenAsync(
        string token, ReplacePreOrderByManageTokenCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reservation = await ByManageTokenAsync(token, cancellationToken);

        return await ReplaceCoreAsync(
            reservation, AccountBehind(reservation), command.Lines, command.Note, command.ClientCommandId, cancellationToken);
    }

    public async Task<PreOrderResult> ClearByManageTokenAsync(
        string token, Guid clientCommandId, CancellationToken cancellationToken = default)
    {
        var reservation = await ByManageTokenAsync(token, cancellationToken);

        return await ClearCoreAsync(reservation, AccountBehind(reservation), clientCommandId, cancellationToken);
    }

    /// <summary>
    /// Saves the whole pre-order the guest sent - the app's and the link's one path, as moving a booking is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The account's lock first, then every read.</b> A double tap, or a retry racing its own first try, is two
    /// saves of the same booking at once. With the lock taken before the replay check, the pre-order and the question
    /// are read, the second save waits for the first to commit and then reads what it wrote: the same command id is a
    /// clean replay, a new one edits the pre-order the first created and does not ask the question twice. A unique key
    /// that still fires - the command's, the pre-order's, the question's - is answered as a replay or a 409
    /// <c>concurrent-update</c>, never a 500. The lock is also the feed entry's: a deletion that comes after waits for
    /// this commit and removes it - the reminder's rule.
    /// </para>
    /// <para>
    /// In this order, each refusal before anything is written: a replay (answered as the pre-order stands); the booking
    /// still to come (409 <c>reservation-not-changeable</c>); not already with the kitchen (409
    /// <c>pre-order-already-sent</c>); before the lock two hours ahead (409 <c>pre-order-locked</c>); the branch taking
    /// pre-orders - allowed by the platform (409 <c>feature-not-enabled</c>, feature <c>PreOrders</c>) and switched on by the
    /// venue (409 <c>pre-orders-not-accepted</c>); the shape (422, every field named); the dishes (422
    /// <c>pre-order-items-unavailable</c>, every dish named). The "on your way" question is written with the first
    /// save and not again; the command is recorded in the same save.
    /// </para>
    /// </remarks>
    private async Task<PreOrderResult> ReplaceCoreAsync(
        Reservation reservation,
        Guid account,
        IReadOnlyList<PreOrderLineCommand> lines,
        string? note,
        Guid clientCommandId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // The account's lock first, held to the commit; whether it is live is read again by the question's handler.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.IsLiveAsync(db, account, cancellationToken);

        if (await IsReplayAsync(clientCommandId, ReplaceCommandType, account, reservation.Id, cancellationToken))
        {
            return await AnswerAsync(reservation.Id, wasReplay: true, cancellationToken);
        }

        // As it stands now that this save has its turn: read before the lock, it may have moved or ended since.
        await db.Entry(reservation).ReloadAsync(cancellationToken);

        var nowUtc = clock.UtcNow;
        RequireBookingTakesEdits(reservation, nowUtc);

        var preOrder = await db.PreOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.ReservationId == reservation.Id, cancellationToken);

        RequireUnlocked(reservation, preOrder, nowUtc);

        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == reservation.BranchId)
            .Select(b => new { b.AcceptsPreOrders, b.AllowedFeatureBits })
            .FirstAsync(cancellationToken);

        // The platform's switch before the owner's (docs/branch-features.md): a pre-order the kitchen already has
        // never comes here, and clearing one stays open, so only a new save or an edit is refused.
        BranchFeatures.Require(branch.AllowedFeatureBits, BranchFeature.PreOrders, reservation.BranchId);

        if (!branch.AcceptsPreOrders)
        {
            throw new PreOrdersNotAcceptedException(reservation.BranchId);
        }

        // A pre-order's ticket is held for a table and goes onto that table's tab when the party sits down, and
        // a booking made by time only has no table until then: it takes no pre-order (2026-10-29).
        if (reservation.BookedWithoutTable)
        {
            throw new PreOrdersNotAcceptedException(reservation.BranchId);
        }

        var violations = PreOrderRules.Check([.. lines.Select(l => (l.Quantity, l.Note))], note);

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        var dishes = await PreOrderReads.DishesAsync(db, lines.Select(l => l.MenuItemId), cancellationToken);

        // Every dish that cannot go on, each once, in the order the guest listed them - so the app can mark them all.
        var refused = lines
            .Select(l => l.MenuItemId)
            .Distinct()
            .Select(id => (MenuItemId: id, Reason: PreOrderReads.RefusalFor(dishes.GetValueOrDefault(id), reservation.BranchId)))
            .Where(r => r.Reason is not null)
            .Select(r => (r.MenuItemId, r.Reason!.Value))
            .ToList();

        if (refused.Count > 0)
        {
            throw new PreOrderItemsUnavailableException(refused);
        }

        if (preOrder is null)
        {
            preOrder = new PreOrder(reservation.Id, reservation.BranchId, nowUtc);
            db.PreOrders.Add(preOrder);
        }

        preOrder.Replace(
            [.. lines.Select(l =>
            {
                var dish = dishes[l.MenuItemId];

                return new PreOrderLineInput(l.MenuItemId, dish.Name, dish.PriceAmd, l.Quantity, l.Note);
            })],
            note,
            nowUtc);

        // The "on your way" question moves to 45 minutes with the first save (PreOrderSaved).
        Record(clientCommandId, ReplaceCommandType, account, reservation.Id, nowUtc);

        if (await SaveGuestChangeAsync(clientCommandId, ReplaceCommandType, account, reservation.Id, cancellationToken))
        {
            return await AnswerAsync(reservation.Id, wasReplay: true, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Pre-order for booking {Code} saved: {Lines} lines, {Total} AMD.", reservation.Code, preOrder.Lines.Count, preOrder.TotalAmd);

        return await AnswerAsync(reservation.Id, wasReplay: false, cancellationToken);
    }

    /// <summary>
    /// Removes the pre-order and moves its question back to the 30 minutes every booking is asked at. Under the same window as saving - and allowed whether or not
    /// the branch still takes pre-orders, so a guest is never stuck with one. Clearing a booking that has none is
    /// answered as done. Under the account's lock, taken before anything is read, as saving is.
    /// </summary>
    private async Task<PreOrderResult> ClearCoreAsync(
        Reservation reservation, Guid account, Guid clientCommandId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.IsLiveAsync(db, account, cancellationToken);

        if (await IsReplayAsync(clientCommandId, ClearCommandType, account, reservation.Id, cancellationToken))
        {
            return await AnswerAsync(reservation.Id, wasReplay: true, cancellationToken);
        }

        await db.Entry(reservation).ReloadAsync(cancellationToken);

        var nowUtc = clock.UtcNow;
        RequireBookingTakesEdits(reservation, nowUtc);

        var preOrder = await db.PreOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.ReservationId == reservation.Id, cancellationToken);

        if (preOrder is not null)
        {
            // Without a pre-order the booking is still asked, at 30 minutes like every other (PreOrderCleared). A booking
            // that had none is asked as it was: its question is not written again - not to a guest who has answered it.
            RequireUnlocked(reservation, preOrder, nowUtc);
            preOrder.Clear(nowUtc);
            db.PreOrders.Remove(preOrder);
        }

        Record(clientCommandId, ClearCommandType, account, reservation.Id, nowUtc);

        if (await SaveGuestChangeAsync(clientCommandId, ClearCommandType, account, reservation.Id, cancellationToken))
        {
            return await AnswerAsync(reservation.Id, wasReplay: true, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Pre-order for booking {Code} cleared.", reservation.Code);

        return new PreOrderResult(reservation.Id, null, WasReplay: false);
    }

    public async Task<OnMyWayResult> ConfirmOnMyWayAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner("Say you are on your way");
        var reservation = await OwnBookingAsync(reservationId, dinerUserId, cancellationToken);

        return await ConfirmOnMyWayCoreAsync(reservation, cancellationToken);
    }

    public async Task<OnMyWayResult> ConfirmOnMyWayByManageTokenAsync(string token, CancellationToken cancellationToken = default) =>
        await ConfirmOnMyWayCoreAsync(await ByManageTokenAsync(token, cancellationToken), cancellationToken);

    /// <summary>
    /// "Yes, on my way": recorded once, and the waiter's card says "Guest confirmed". The question is answered, so its
    /// push and feed entry that have not gone out yet are withdrawn, and the History says so - both on the booking's
    /// <c>GuestOnTheirWay</c>. The answer and the system line after it go into the booking's thread (made if there is
    /// none) in the same transaction, under the booking's lock.
    /// </summary>
    private async Task<OnMyWayResult> ConfirmOnMyWayCoreAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;

        if (!reservation.ConfirmOnMyWay(nowUtc))
        {
            return new OnMyWayResult(reservation.Id, reservation.GuestConfirmedOnWayAtUtc!.Value, WasReplay: true);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await GuestThreads.AnswerAsync(db, reservation, Domain.Messages.GuestAnswer.Coming, null, nowUtc, cancellationToken);

        try
        {
            await domainEvents.DispatchAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two taps at once, or the booking changed under it. Read it again: the first tap's answer, or the refusal
            // the booking now earns.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var current = await db.Reservations.FirstAsync(r => r.Id == reservation.Id, cancellationToken);

            return current.GuestConfirmedOnWayAtUtc is { } first
                ? new OnMyWayResult(current.Id, first, WasReplay: true)
                : await ConfirmOnMyWayCoreAsync(current, cancellationToken);
        }

        logger.LogInformation("Booking {Code}: the guest is on their way.", reservation.Code);

        return new OnMyWayResult(reservation.Id, reservation.GuestConfirmedOnWayAtUtc!.Value, WasReplay: false);
    }

    public async Task<RunningLateResult> ReportRunningLateAsync(
        Guid reservationId, int minutes, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner("Say you are running late");
        var reservation = await OwnBookingAsync(reservationId, dinerUserId, cancellationToken);

        return await ReportRunningLateCoreAsync(reservation, minutes, cancellationToken);
    }

    public async Task<RunningLateResult> ReportRunningLateByManageTokenAsync(
        string token, int minutes, CancellationToken cancellationToken = default) =>
        await ReportRunningLateCoreAsync(await ByManageTokenAsync(token, cancellationToken), minutes, cancellationToken);

    /// <summary>
    /// "I'm running late": recorded, overwriting an earlier answer, and - an answer to the question - the question's push
    /// and feed entry not yet out are withdrawn, as "on my way" withdraws them (<c>GuestRunningLate</c>). The answer and
    /// the system line ("held until 19:45") go into the booking's thread, as "on my way"'s do.
    /// </summary>
    private async Task<RunningLateResult> ReportRunningLateCoreAsync(
        Reservation reservation, int minutes, CancellationToken cancellationToken, bool retried = false)
    {
        var nowUtc = clock.UtcNow;
        reservation.ReportRunningLate(minutes, nowUtc);

        // The answer and "held until 19:45" into the booking's thread, in the same transaction.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await GuestThreads.AnswerAsync(db, reservation, Domain.Messages.GuestAnswer.Late, minutes, nowUtc, cancellationToken);

        try
        {
            await domainEvents.DispatchAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException) when (!retried)
        {
            // The booking changed under the answer - another tap, a waiter. Read it again and answer once more: the latest
            // answer is the one that stands, or the refusal the booking now earns.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var current = await db.Reservations.FirstAsync(r => r.Id == reservation.Id, cancellationToken);

            return await ReportRunningLateCoreAsync(current, minutes, cancellationToken, retried: true);
        }

        logger.LogInformation("Booking {Code}: the guest is running {Minutes} minutes late.", reservation.Code, minutes);

        return new RunningLateResult(
            reservation.Id, minutes, reservation.GuestRunningLateAtUtc!.Value, reservation.StartUtc.AddMinutes(minutes));
    }
}
