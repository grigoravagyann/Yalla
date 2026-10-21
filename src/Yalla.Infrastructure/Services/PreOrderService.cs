using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.Messaging;
using Yalla.Application.Public;
using Yalla.Application.Reservations;
using Yalla.Domain;
using Yalla.Domain.Audit;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Staff;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Pre-orders on a booking: the guest's edits (app and manage link), "on my way", the floor tablet's rail with its two
/// actions, and the console's settings. See <c>docs/reservations.md</c>, "Pre-orders".
/// </summary>
internal sealed class PreOrderService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IAuthorizationQueries authorization,
    IStaffBranchGuard branchGuard,
    TableLock tableLock,
    IOutbox outbox,
    TabLedger ledger,
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
    /// In this order, each refusal before anything is written: a replay (answered as the pre-order stands); the booking
    /// still to come (409 <c>reservation-not-changeable</c>); not already with the kitchen (409
    /// <c>pre-order-already-sent</c>); before the lock two hours ahead (409 <c>pre-order-locked</c>); the branch taking
    /// pre-orders (409 <c>pre-orders-not-accepted</c>); the shape (422, every field named); the dishes (422
    /// <c>pre-order-items-unavailable</c>, every dish named). The "on your way" question is written with the first
    /// save and not again; the command is recorded in the same save.
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

        if (await IsReplayAsync(clientCommandId, ReplaceCommandType, account, reservation.Id, cancellationToken))
        {
            return await AnswerAsync(reservation.Id, wasReplay: true, cancellationToken);
        }

        var nowUtc = clock.UtcNow;
        RequireBookingTakesEdits(reservation, nowUtc);

        var preOrder = await db.PreOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.ReservationId == reservation.Id, cancellationToken);

        RequireUnlocked(reservation, preOrder, nowUtc);

        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == reservation.BranchId)
            .Select(b => new { b.AcceptsPreOrders, b.Name, VenueName = b.Venue.Name })
            .FirstAsync(cancellationToken);

        if (!branch.AcceptsPreOrders)
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

        // The account's lock, held to the save: the feed entry is the person's, and a deletion that comes after waits
        // for this commit and removes it - the reminder's rule.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var key = PreOrderQuestions.KeyFor(reservation);
        var asked = await db.OutboxMessages.AnyAsync(m => m.IdempotencyKey == key, cancellationToken)
            ? new HashSet<string>(StringComparer.Ordinal) { key }
            : [];

        PreOrderQuestions.Enqueue(
            db,
            outbox,
            reservation,
            await NoticeAsync(reservation, branch.VenueName, branch.Name, account, cancellationToken),
            branch.VenueName,
            branch.Name,
            writeFeed: await DinerAccountLock.IsLiveAsync(db, account, cancellationToken),
            asked,
            nowUtc);

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
    /// Removes the pre-order and withdraws its question. Under the same window as saving - and allowed whether or not
    /// the branch still takes pre-orders, so a guest is never stuck with one. Clearing a booking that has none is
    /// answered as done.
    /// </summary>
    private async Task<PreOrderResult> ClearCoreAsync(
        Reservation reservation, Guid account, Guid clientCommandId, CancellationToken cancellationToken)
    {
        if (await IsReplayAsync(clientCommandId, ClearCommandType, account, reservation.Id, cancellationToken))
        {
            return await AnswerAsync(reservation.Id, wasReplay: true, cancellationToken);
        }

        var nowUtc = clock.UtcNow;
        RequireBookingTakesEdits(reservation, nowUtc);

        var preOrder = await db.PreOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.ReservationId == reservation.Id, cancellationToken);

        if (preOrder is not null)
        {
            RequireUnlocked(reservation, preOrder, nowUtc);
            db.PreOrders.Remove(preOrder);
        }

        await PreOrderQuestions.WithdrawAsync(db, outbox, reservation.Id, nowUtc, cancellationToken);

        Record(clientCommandId, ClearCommandType, account, reservation.Id, nowUtc);

        if (await SaveGuestChangeAsync(clientCommandId, ClearCommandType, account, reservation.Id, cancellationToken))
        {
            return await AnswerAsync(reservation.Id, wasReplay: true, cancellationToken);
        }

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

    /// <summary>"Yes, on my way": recorded once, and the waiter's card says "Guest confirmed".</summary>
    private async Task<OnMyWayResult> ConfirmOnMyWayCoreAsync(Reservation reservation, CancellationToken cancellationToken)
    {
        if (!reservation.ConfirmOnMyWay(clock.UtcNow))
        {
            return new OnMyWayResult(reservation.Id, reservation.GuestConfirmedOnWayAtUtc!.Value, WasReplay: true);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two taps at once, or the booking changed under it. Read it again: the first tap's answer, or the refusal
            // the booking now earns.
            db.ChangeTracker.Clear();
            var current = await db.Reservations.FirstAsync(r => r.Id == reservation.Id, cancellationToken);

            return current.GuestConfirmedOnWayAtUtc is { } first
                ? new OnMyWayResult(current.Id, first, WasReplay: true)
                : await ConfirmOnMyWayCoreAsync(current, cancellationToken);
        }

        logger.LogInformation("Booking {Code}: the guest is on their way.", reservation.Code);

        return new OnMyWayResult(reservation.Id, reservation.GuestConfirmedOnWayAtUtc!.Value, WasReplay: false);
    }

    // ---------------------------------------------------------------- the floor

    public async Task<DuePreOrders> ListDueAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await FloorStaffGuard.RequireAtBranchAsync(
            db, actor, authorization, branchId, "Read the pre-order rail", cancellationToken);

        var minutes = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => (int?)b.PreOrderMinutesBefore)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var nowUtc = clock.UtcNow;
        var dueBy = nowUtc.AddMinutes(minutes);

        // On the rail: a confirmed booking from its card's time until its end, or a seated one whose pre-order is not on
        // their tab yet (sent before they sat and somehow not attached, or not sent - the waiter can still send it).
        var due = db.PreOrders
            .Where(p => p.BranchId == branchId && p.Lines.Any())
            .Where(p => (p.Reservation.Status == ReservationStatus.Confirmed
                         && p.Reservation.StartUtc <= dueBy
                         && p.Reservation.EndUtc > nowUtc)
                        || (p.Reservation.Status == ReservationStatus.Seated
                            && p.TabOrderId == null
                            && db.TableSessions.Any(s => s.ReservationId == p.ReservationId && s.ClosedAtUtc == null)));

        var cards = await CardsAsync(due, minutes, nowUtc, wasReplay: false, cancellationToken);

        // When the next card appears, so the tablet reads again then rather than polling: time passing has no writer.
        var nextStart = await db.PreOrders
            .Where(p => p.BranchId == branchId
                        && p.Lines.Any()
                        && p.Reservation.Status == ReservationStatus.Confirmed
                        && p.Reservation.StartUtc > dueBy)
            .MinAsync(p => (DateTime?)p.Reservation.StartUtc, cancellationToken);

        return new DuePreOrders(cards, nextStart?.AddMinutes(-minutes));
    }

    public async Task<DuePreOrder> SendAsync(PreOrderStaffCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (booking, staffId) = await RequireFloorForBookingAsync(command.ReservationId, "Send a pre-order to the kitchen", cancellationToken);

        // Two tablets, one ticket: already sent is answered as done, whoever sent it and under whatever command id.
        if (await SentAlreadyAsync(command.ReservationId, cancellationToken))
        {
            return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);
        }

        Guid? recomputeTab = null;

        try
        {
            // The table's lock, the one every seating takes: a send that finds the party not seated and a seating that
            // finds the pre-order not sent cannot both be right, so they take turns - and whichever goes second puts the
            // pre-order on the tab.
            await using var locked = await tableLock.AcquireAsync(booking.DiningTableId, booking.TableLabel, cancellationToken);

            var reservation = await db.Reservations.FirstAsync(r => r.Id == command.ReservationId, cancellationToken);
            var preOrder = await db.PreOrders
                .Include(p => p.Lines)
                .FirstAsync(p => p.ReservationId == command.ReservationId, cancellationToken);

            if (preOrder.IsSent)
            {
                return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);
            }

            var nowUtc = clock.UtcNow;
            var session = await RequireSendableAsync(reservation, nowUtc, cancellationToken);

            preOrder.MarkSent(staffId, nowUtc);

            if (session is null)
            {
                // Not seated yet: the pre-order is the kitchen's ticket now, held until the party sits.
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (session.TabId is { } tabId)
            {
                var tab = await ledger.LoadForWriteAsync(tabId, cancellationToken);
                PreOrderSittings.AddToTab(db, ledger, preOrder, tab, nowUtc);
                await ledger.SaveAppendedAsync(cancellationToken);
                recomputeTab = tabId;
            }
            else
            {
                var serviceCharge = await db.Branches
                    .Where(b => b.Id == reservation.BranchId)
                    .Select(b => b.ReservationPolicy.ServiceChargePercent)
                    .FirstAsync(cancellationToken);

                PreOrderSittings.OpenTabWithOrder(
                    db, preOrder, session, serviceCharge, TabAdmissionReason.OpenedByStaff, staffId, nowUtc);
                await db.SaveChangesAsync(cancellationToken);
            }

            await locked.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The pre-order changed under the send - another tablet's send through another path, or a move. Read it again.
            db.ChangeTracker.Clear();
            ledger.DiscardPending();

            if (await SentAlreadyAsync(command.ReservationId, cancellationToken))
            {
                return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);
            }

            throw;
        }

        if (recomputeTab is { } recompute)
        {
            await ledger.RecomputeTotalsAsync(recompute, cancellationToken);
        }

        logger.LogInformation("Pre-order of booking {ReservationId} sent to the kitchen by staff {StaffId}.", command.ReservationId, staffId);

        return await CardAsync(command.ReservationId, wasReplay: false, cancellationToken);
    }

    public async Task<DuePreOrder> MarkNotComingAsync(PreOrderStaffCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (_, staffId) = await RequireFloorForBookingAsync(command.ReservationId, "Mark a party not coming", cancellationToken);

        var reservation = await db.Reservations.FirstAsync(r => r.Id == command.ReservationId, cancellationToken);
        var preOrder = await db.PreOrders.FirstOrDefaultAsync(p => p.ReservationId == command.ReservationId, cancellationToken)
                       ?? throw new KeyNotFoundException("This booking has no pre-order.");

        if (preOrder.IsSent)
        {
            throw new PreOrderAlreadySentException(reservation.Id, preOrder.SentAtUtc!.Value);
        }

        if (preOrder.NotComingAtUtc is not null)
        {
            return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);
        }

        var nowUtc = clock.UtcNow;

        // Only for a party expected and not here: a seated party is coming by definition.
        if (reservation.Status != ReservationStatus.Confirmed
            || nowUtc < PreOrderRules.LockedAtUtc(reservation.StartUtc)
            || nowUtc >= reservation.EndUtc)
        {
            throw NotSendable(reservation, "Only a confirmed booking within two hours of its start can be marked not coming.");
        }

        preOrder.MarkNotComing(staffId, nowUtc);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();

            return await CardAsync(command.ReservationId, wasReplay: true, cancellationToken);
        }

        return await CardAsync(command.ReservationId, wasReplay: false, cancellationToken);
    }

    // ---------------------------------------------------------------- the console

    public async Task<PreOrderSettingsView> GetSettingsAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the pre-order settings", cancellationToken);

        return await SettingsViewAsync(branchId, cancellationToken);
    }

    public async Task<PreOrderSettingsView> SaveSettingsAsync(
        Guid branchId, PreOrderSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await branchGuard.RequireAtBranchAsync(branchId, "Change the pre-order settings", cancellationToken);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var items = await db.MenuItems
            .Where(m => m.MenuCategory.BranchId == branchId)
            .ToListAsync(cancellationToken);

        var excluded = (settings.ExcludedMenuItemIds ?? []).ToHashSet();
        var violations = new List<FieldViolation>();

        if (settings.MinutesBefore is < PreOrderRules.MinMinutesBefore or > PreOrderRules.MaxMinutesBefore)
        {
            violations.Add(new FieldViolation(
                PreOrderRules.Fields.MinutesBefore,
                $"The card appears {PreOrderRules.MinMinutesBefore} to {PreOrderRules.MaxMinutesBefore} minutes before a "
                + $"booking; {settings.MinutesBefore} was given.",
                settings.MinutesBefore < PreOrderRules.MinMinutesBefore ? FieldBounds.Min : FieldBounds.Max,
                Min: PreOrderRules.MinMinutesBefore,
                Max: PreOrderRules.MaxMinutesBefore,
                Value: settings.MinutesBefore));
        }

        if (excluded.Except(items.Select(i => i.Id)).Any())
        {
            violations.Add(new FieldViolation(
                PreOrderRules.Fields.ExcludedMenuItemIds,
                "Some of these dishes are not on this branch's menu. Reload the menu and choose again.",
                FieldBounds.Conflict));
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        branch.SetPreOrders(settings.AcceptsPreOrders, settings.MinutesBefore);

        // The whole list each time: every dish not named may be pre-ordered.
        foreach (var item in items)
        {
            item.SetAvailableForPreOrder(!excluded.Contains(item.Id));
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Branch {BranchId} pre-orders {State}, {Minutes} minutes before, {Excluded} dishes excluded.",
            branchId, settings.AcceptsPreOrders ? "on" : "off", settings.MinutesBefore, excluded.Count);

        return await SettingsViewAsync(branchId, cancellationToken);
    }

    private async Task<PreOrderSettingsView> SettingsViewAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new { b.AcceptsPreOrders, b.PreOrderMinutesBefore })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var excluded = await db.MenuItems
            .AsNoTracking()
            .Where(m => m.MenuCategory.BranchId == branchId && !m.AvailableForPreOrder)
            .OrderBy(m => m.MenuCategory.DisplayOrder)
            .ThenBy(m => m.DisplayOrder)
            .ThenBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        return new PreOrderSettingsView(
            branch.AcceptsPreOrders,
            branch.PreOrderMinutesBefore,
            excluded,
            new PreOrderSettingsBounds(PreOrderRules.MinMinutesBefore, PreOrderRules.MaxMinutesBefore),
            PreOrderRules.EditableUntilHoursBefore,
            PreOrderRules.OnYourWayMinutesBefore);
    }

    // ---------------------------------------------------------------- rules

    /// <summary>A booking still to come, pending or confirmed: what a guest may still change.</summary>
    private static void RequireBookingTakesEdits(Reservation reservation, DateTime nowUtc)
    {
        if (!PreOrderRules.BookingTakesEdits(reservation.Status) || reservation.StartUtc <= nowUtc)
        {
            throw new ReservationNotChangeableException(reservation.Id, reservation.Code, reservation.Status, reservation.StartUtc);
        }
    }

    /// <summary>Not with the kitchen, and more than two hours before the start.</summary>
    private static void RequireUnlocked(Reservation reservation, PreOrder? preOrder, DateTime nowUtc)
    {
        if (preOrder is { IsSent: true })
        {
            throw new PreOrderAlreadySentException(reservation.Id, preOrder.SentAtUtc!.Value);
        }

        var lockedAt = PreOrderRules.LockedAtUtc(reservation.StartUtc);

        if (nowUtc >= lockedAt)
        {
            throw new PreOrderLockedException(reservation.Id, lockedAt);
        }
    }

    /// <summary>
    /// A confirmed booking from two hours before its start until its end, or a seated one with its sitting open - and
    /// that sitting, when seated.
    /// </summary>
    private async Task<Domain.Occupancy.TableSession?> RequireSendableAsync(
        Reservation reservation, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (reservation.Status == ReservationStatus.Seated)
        {
            return await db.TableSessions.FirstOrDefaultAsync(
                       s => s.ReservationId == reservation.Id && s.ClosedAtUtc == null, cancellationToken)
                   ?? throw NotSendable(reservation, "This party's sitting is over; the pre-order can no longer be sent.");
        }

        if (reservation.Status != ReservationStatus.Confirmed
            || nowUtc < PreOrderRules.LockedAtUtc(reservation.StartUtc)
            || nowUtc >= reservation.EndUtc)
        {
            throw NotSendable(
                reservation,
                "A pre-order goes to the kitchen for a confirmed booking from two hours before it until it ends.");
        }

        return null;
    }

    private static PreOrderNotSendableException NotSendable(Reservation reservation, string message) =>
        new(reservation.Id, reservation.Status, PreOrderRules.LockedAtUtc(reservation.StartUtc), reservation.EndUtc, message);

    // ---------------------------------------------------------------- the floor's reads

    /// <summary>The booking's facts the floor's actions need, and the caller's staff id when they may act on it.</summary>
    private async Task<(BookingFacts Booking, Guid StaffId)> RequireFloorForBookingAsync(
        Guid reservationId, string operation, CancellationToken cancellationToken)
    {
        if (actor.Type != ActorType.Staff)
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
        }

        var booking = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Id == reservationId)
            .Select(r => new BookingFacts(r.BranchId, r.DiningTableId, r.DiningTable.Label))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Booking {reservationId} was not found.");

        var staffId = await FloorStaffGuard.RequireAtBranchAsync(db, actor, authorization, booking.BranchId, operation, cancellationToken);

        return (booking, staffId);
    }

    private Task<bool> SentAlreadyAsync(Guid reservationId, CancellationToken cancellationToken) =>
        db.PreOrders.AsNoTracking().AnyAsync(p => p.ReservationId == reservationId && p.SentAtUtc != null, cancellationToken);

    /// <summary>One booking's card, for an action's answer, wherever the rail would place it.</summary>
    private async Task<DuePreOrder> CardAsync(Guid reservationId, bool wasReplay, CancellationToken cancellationToken)
    {
        var minutes = await db.PreOrders
            .AsNoTracking()
            .Where(p => p.ReservationId == reservationId)
            .Select(p => (int?)p.Reservation.Branch.PreOrderMinutesBefore)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("This booking has no pre-order.");

        var cards = await CardsAsync(
            db.PreOrders.Where(p => p.ReservationId == reservationId), minutes, clock.UtcNow, wasReplay, cancellationToken);

        return cards.Single();
    }

    /// <summary>
    /// The cards for these pre-orders: one statement for the bookings, one (two at most) for the pre-orders, one for the
    /// guests' records - whatever the number of cards.
    /// </summary>
    private async Task<IReadOnlyList<DuePreOrder>> CardsAsync(
        IQueryable<PreOrder> preOrders, int minutesBefore, DateTime nowUtc, bool wasReplay, CancellationToken cancellationToken)
    {
        var rows = await preOrders
            .AsNoTracking()
            .OrderBy(p => p.Reservation.StartUtc)
            .ThenBy(p => p.Reservation.DiningTable.Label)
            .Select(p => new CardRow
            {
                ReservationId = p.ReservationId,
                Code = p.Reservation.Code,
                TableId = p.Reservation.DiningTableId,
                TableLabel = p.Reservation.DiningTable.Label,
                RoomName = p.Reservation.DiningTable.FloorArea == null ? null : p.Reservation.DiningTable.FloorArea.Name,
                StartUtc = p.Reservation.StartUtc,
                LocalDate = p.Reservation.LocalDate,
                LocalStartTime = p.Reservation.LocalStartTime,
                TimeZoneId = p.Reservation.Branch.TimeZoneId,
                PartySize = p.Reservation.PartySize,
                Status = p.Reservation.Status,
                GuestName = p.Reservation.GuestName,
                GuestPhone = p.Reservation.GuestPhone,
                GuestConfirmedOnWayAtUtc = p.Reservation.GuestConfirmedOnWayAtUtc,
                DinerUserId = p.Reservation.DinerUserId,
                PhoneUnverified = p.Reservation.PhoneUnverified,

                // The names alone, joined here - never a lookup per card.
                SentByName = db.StaffMembers.Where(s => s.Id == p.SentByStaffId).Select(s => s.FullName).FirstOrDefault(),
                NotComingByName = db.StaffMembers.Where(s => s.Id == p.NotComingByStaffId).Select(s => s.FullName).FirstOrDefault(),
                TabId = p.TabOrderId == null
                    ? null
                    : db.TabOrders.Where(o => o.Id == p.Id).Select(o => (Guid?)o.TabId).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var views = await PreOrderReads.ForReservationsAsync(db, rows.Select(r => r.ReservationId), nowUtc, cancellationToken);

        // The guest's record, under the console list's rule: a proved account's booking, confirmed, today or later.
        var records = await GuestRecords.ForAccountsAsync(
            db,
            rows.Where(ShowsRecord).Select(r => r.DinerUserId!.Value),
            nowUtc,
            cancellationToken);

        // The guests' numbers are the venue's to call, not the platform's to read.
        var withPhones = actor.Role != StaffRole.PlatformAdmin;

        return
        [
            .. rows.Select(r => new DuePreOrder(
                r.ReservationId,
                r.Code,
                r.TableId,
                r.TableLabel,
                r.RoomName,
                r.StartUtc,
                r.LocalStartTime,
                r.PartySize,
                r.Status,
                r.GuestName,
                withPhones ? r.GuestPhone : string.Empty,
                r.GuestConfirmedOnWayAtUtc,
                ShowsRecord(r) && records.TryGetValue(r.DinerUserId!.Value, out var record) ? record : null,
                PreOrderRules.DueAtUtc(r.StartUtc, minutesBefore),
                views[r.ReservationId],
                r.SentByName,
                r.NotComingByName,
                r.TabId,
                wasReplay)),
        ];

        bool ShowsRecord(CardRow r) =>
            r.DinerUserId is not null
            && !r.PhoneUnverified
            && r.Status == ReservationStatus.Confirmed
            && r.LocalDate >= BranchZone.For(r.TimeZoneId).LocalDateAt(nowUtc);
    }

    // ---------------------------------------------------------------- the guest's reads and records

    private async Task<PreOrderResult> AnswerAsync(Guid reservationId, bool wasReplay, CancellationToken cancellationToken) =>
        new(reservationId, await PreOrderReads.ForReservationAsync(db, reservationId, clock.UtcNow, cancellationToken), wasReplay);

    /// <summary>
    /// Whether this command was already applied - or throws when the id belongs to another command, another booking or
    /// another person: the reschedule's rule, so a reused id never answers with somebody else's pre-order.
    /// </summary>
    private async Task<bool> IsReplayAsync(
        Guid clientCommandId, string commandType, Guid account, Guid reservationId, CancellationToken cancellationToken)
    {
        var processed = await db.ProcessedCommands
            .AsNoTracking()
            .Where(c => c.ClientCommandId == clientCommandId)
            .Select(c => new { c.CommandType, c.ActorId, c.ResponseJson })
            .FirstOrDefaultAsync(cancellationToken);

        if (processed is null)
        {
            return false;
        }

        if (processed.CommandType != commandType
            || processed.ActorId != account
            || JsonSerializer.Deserialize<PreOrderRecord>(processed.ResponseJson, RecordJson) is not { } record
            || record.ReservationId != reservationId)
        {
            throw new ClientCommandIdAlreadyUsedException(
                clientCommandId,
                "That clientCommandId already belongs to another command. Generate a fresh one per change, and reuse it "
                + "only when retrying that same change.");
        }

        return true;
    }

    private void Record(Guid clientCommandId, string commandType, Guid account, Guid reservationId, DateTime nowUtc) =>
        db.ProcessedCommands.Add(new ProcessedCommand(
            clientCommandId,
            commandType,
            ActorType.Diner,
            account,
            JsonSerializer.Serialize(new PreOrderRecord(reservationId), RecordJson),
            200,
            nowUtc));

    /// <summary>
    /// Saves a guest's change. True when the same command committed first in a race with its own retry - answer it as
    /// a replay; a first save racing another first save of the same booking is a concurrent update.
    /// </summary>
    private async Task<bool> SaveGuestChangeAsync(
        Guid clientCommandId, string commandType, Guid account, Guid reservationId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return false;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.ProcessedCommandId))
        {
            db.ChangeTracker.Clear();

            return await IsReplayAsync(clientCommandId, commandType, account, reservationId, cancellationToken)
                ? true
                : throw new ClientCommandIdAlreadyUsedException(clientCommandId);
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.PreOrderPerReservation))
        {
            db.ChangeTracker.Clear();

            throw new DbUpdateConcurrencyException(
                "Another change to this pre-order was saved at the same moment. Read it again and retry.", ex);
        }
    }

    /// <summary>The question's payload: the reminder's notice, so its handler reads the same shape.</summary>
    private async Task<object> NoticeAsync(
        Reservation reservation, string venueName, string branchName, Guid account, CancellationToken cancellationToken)
    {
        var tableLabel = await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == reservation.DiningTableId)
            .Select(t => t.Label)
            .FirstAsync(cancellationToken);

        return new
        {
            reservationId = reservation.Id,
            dinerUserId = account,
            venueName,
            branchName,
            tableLabel,
            localStartTime = reservation.LocalStartTime,
            reservationCode = reservation.Code,
        };
    }

    private async Task<Reservation> OwnBookingAsync(Guid reservationId, Guid dinerUserId, CancellationToken cancellationToken) =>

        // Somebody else's booking is not found, word for word as an id that is no booking: the reschedule's rule - a
        // stranger guessing ids learns nothing.
        await db.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId && r.DinerUserId == dinerUserId, cancellationToken)
        ?? throw new KeyNotFoundException($"Booking {reservationId} was not found.");

    /// <summary>The booking behind a manage link, or the one refusal every bad link shares.</summary>
    private async Task<Reservation> ByManageTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw ManageBookingFailure.Raise();
        }

        var hash = Secrets.Hash(token);
        var reservation = await db.Reservations.FirstOrDefaultAsync(r => r.ManageTokenHash == hash, cancellationToken);

        return reservation is not null && reservation.ManageTokenIsLiveAt(clock.UtcNow)
            ? reservation
            : throw ManageBookingFailure.Raise();
    }

    /// <summary>The account a link's change is recorded against - the guest's, as if from the app.</summary>
    private static Guid AccountBehind(Reservation reservation) =>
        reservation.DinerUserId ?? throw new ManageLinkAccountUnavailableException();

    private Guid RequireDiner(string operation) =>
        actor.Type == ActorType.Diner && actor.DinerUserId is { } id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException($"{operation} requires a verified diner account.");

    private sealed record BookingFacts(Guid BranchId, Guid DiningTableId, string TableLabel);

    /// <summary>What a guest's command recorded: which booking - never the pre-order itself.</summary>
    private sealed record PreOrderRecord(Guid ReservationId);

    private sealed class CardRow
    {
        public Guid ReservationId { get; init; }

        public string Code { get; init; } = string.Empty;

        public Guid TableId { get; init; }

        public string TableLabel { get; init; } = string.Empty;

        public string? RoomName { get; init; }

        public DateTime StartUtc { get; init; }

        public DateOnly LocalDate { get; init; }

        public TimeOnly LocalStartTime { get; init; }

        public string TimeZoneId { get; init; } = string.Empty;

        public int PartySize { get; init; }

        public ReservationStatus Status { get; init; }

        public string GuestName { get; init; } = string.Empty;

        public string GuestPhone { get; init; } = string.Empty;

        public DateTime? GuestConfirmedOnWayAtUtc { get; init; }

        public Guid? DinerUserId { get; init; }

        public bool PhoneUnverified { get; init; }

        public string? SentByName { get; init; }

        public string? NotComingByName { get; init; }

        public Guid? TabId { get; init; }
    }
}
