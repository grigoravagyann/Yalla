using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Application.Takeaway;
using Yalla.Domain;
using Yalla.Domain.Enums;
using Yalla.Domain.Menus;
using Yalla.Domain.Staff;
using Yalla.Domain.Takeaway;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Takeaway orders: the diner's availability, ordering, status and cancel; the floor's counter and its five actions;
/// the console's settings. See <c>docs/takeaway.md</c>.
/// </summary>
/// <remarks>
/// It decides whether a move may happen and makes it through the order; what the move tells the rest of the system -
/// the History's line, the guest's push and feed entry, the floor's push - is the order's events' handlers'
/// (<c>DomainEvents/TakeawayOrders</c>, <c>docs/domain-events.md</c>), written in the same save.
/// </remarks>
internal sealed class TakeawayService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    IAuthorizationQueries authorization,
    StaffBranchGuard branchGuard,
    NoShowBlocks noShowBlocks,
    IActivityLog activity,
    ILogger<TakeawayService> logger) : ITakeawayService
{
    /// <summary>A code taken by a racing order is tried again this often, under the branch's lock it should never be.</summary>
    private const int MaxCodeAttempts = 3;

    // ---------------------------------------------------------------- the diner

    public async Task<TakeawayAvailability> GetAvailabilityAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var branch = await OpenBranchAsync(branchId, cancellationToken);
        var nowUtc = clock.UtcNow;

        // Blocked by the platform reads as switched off (docs/branch-features.md): the app hides takeaway rather than
        // erroring on a screen that asked whether to show it. The owner's switch is untouched underneath.
        if (!branch.TakeawayEnabled || !branch.Allows(BranchFeature.Takeaway))
        {
            return new TakeawayAvailability(branchId, false, false, branch.TakeawayPrepMinutes, null, null, [], null, false, null, null);
        }

        var day = await DayAsync(branch, nowUtc, cancellationToken);
        var pickups = await InProgressPickupsAsync(branchId, cancellationToken);
        var busy = TakeawayRules.IsBusy(pickups.Count, branch.TakeawayMaxInProgress);

        if (!day.Window.Open)
        {
            var (opensAt, opensAtUtc) = Opening(day);

            return new TakeawayAvailability(
                branchId, true, false, branch.TakeawayPrepMinutes, null, null, [], null, busy, opensAt, opensAtUtc);
        }

        return new TakeawayAvailability(
            branchId,
            true,
            true,
            branch.TakeawayPrepMinutes,
            nowUtc.AddMinutes(branch.TakeawayPrepMinutes),
            TakeawayRules.SlotText(day.Window.AsapReadyLocal!.Value),
            [.. Offered(branch, day, pickups).Select(s => s.Text)],
            TakeawayRules.SlotText(day.Window.LastPickupLocal!.Value),
            busy,
            null,
            null);
    }

    /// <summary>
    /// Places the order. In this order, each refusal before anything is written: a replay (answered as the order stands);
    /// a proved phone (403 <c>phone-not-verified</c>); the shape (422, every field named); the branch open for business,
    /// allowed takeaway by the platform (409 <c>feature-not-enabled</c>, feature <c>Takeaway</c>) and taking it (409
    /// <c>takeaway-not-accepted</c>); open now (409 <c>takeaway-closed</c>); the pickup - ASAP
    /// while the kitchen is not full (409 <c>takeaway-busy</c>), or a slot on offer (409 <c>takeaway-slot-unavailable</c>);
    /// the dishes (422 <c>takeaway-items-unavailable</c>, every one named).
    /// </summary>
    /// <remarks>
    /// <b>Under the account's lock, then the branch's.</b> The account's first, as every guest write takes it; the
    /// branch's row lock then makes the in-progress count, the code and the insert one step, so two guests at once
    /// cannot both take the kitchen's last place or the same code. A retry racing its own first try waits on the account
    /// lock and then finds the order the first one made.
    /// </remarks>
    public async Task<TakeawayOrderView> PlaceAsync(PlaceTakeawayOrderCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dinerUserId = RequireDiner("Order to take away");

        if (await ReplayAsync(command.ClientCommandId, dinerUserId, cancellationToken) is { } early)
        {
            return early;
        }

        await DinerPhoneGate.RequireVerifiedPhoneAsync(db, dinerUserId, "Order to take away", cancellationToken);

        var lines = command.Lines ?? [];
        var violations = TakeawayRules.Check([.. lines.Select(l => l.Quantity)], command.Note).ToList();
        TimeOnly? at = null;

        if (command.Asap == command.At is not null)
        {
            violations.Add(new FieldViolation(
                TakeawayRules.Fields.Pickup,
                "Say either \"as soon as possible\" (asap: true) or a pickup time (at), not both and not neither.",
                FieldBounds.Required));
        }
        else if (command.At is { } text)
        {
            if (TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                at = parsed;
            }
            else
            {
                violations.Add(new FieldViolation(
                    TakeawayRules.Fields.PickupAt, "A pickup time is HH:mm, on the branch's clock.", FieldBounds.Required, Value: text));
            }
        }

        if (violations.Count > 0)
        {
            throw new FieldValidationException(violations);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DinerAccountLock.RequireLiveAsync(db, dinerUserId, cancellationToken);

        if (await ReplayAsync(command.ClientCommandId, dinerUserId, cancellationToken) is { } replay)
        {
            return replay;
        }

        // A guest blocked for no-shows orders nothing either: a takeaway order not collected counts toward the block as a
        // missed booking does. Under the account's lock, which a no-show takes too, so an order racing the third
        // no-show is refused rather than slipping in. Keyed on the number too, as a booking is.
        var phone = await db.DinerUsers
            .AsNoTracking()
            .Where(d => d.Id == dinerUserId)
            .Select(d => d.PhoneE164)
            .FirstOrDefaultAsync(cancellationToken);

        if (await GuestRecords.BlockedUntilAsync(
                db, dinerUserId, ReservationCreation.BookerPhone(phone), clock.UtcNow, cancellationToken) is { } blockedUntil)
        {
            throw new Domain.Occupancy.BookingBlockedForNoShowsException(
                blockedUntil, Domain.Occupancy.BookingBlockedForNoShowsException.TakeawayMessage(blockedUntil));
        }

        await db.Database.ExecuteSqlRawAsync(
            "SELECT Id FROM Branches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @branchId",
            [new SqlParameter("@branchId", command.BranchId)],
            cancellationToken);

        var branch = await OpenBranchAsync(command.BranchId, cancellationToken);

        // The platform's switch before the owner's (docs/branch-features.md). Only placing is gated: an order already
        // placed is accepted, readied, collected or cancelled as before.
        branch.RequireAllowed(BranchFeature.Takeaway);

        if (!branch.TakeawayEnabled)
        {
            throw new TakeawayNotAcceptedException(branch.Id);
        }

        var nowUtc = clock.UtcNow;
        var day = await DayAsync(branch, nowUtc, cancellationToken);

        if (!day.Window.Open)
        {
            var (opensAt, opensAtUtc) = Opening(day);

            throw new TakeawayClosedException(branch.Id, opensAtUtc, opensAt);
        }

        var pickups = await InProgressPickupsAsync(branch.Id, cancellationToken);
        DateTime pickupUtc;
        TimeOnly pickupLocal;

        if (at is null)
        {
            if (TakeawayRules.IsBusy(pickups.Count, branch.TakeawayMaxInProgress))
            {
                throw new TakeawayBusyException(branch.Id, branch.TakeawayMaxInProgress);
            }

            pickupUtc = nowUtc.AddMinutes(branch.TakeawayPrepMinutes);
            pickupLocal = Minute(TimeOnly.FromDateTime(day.Window.AsapReadyLocal!.Value));
        }
        else
        {
            var offered = Offered(branch, day, pickups);
            var slot = offered.FirstOrDefault(s => s.Text == command.At)
                       ?? throw new TakeawaySlotUnavailableException(branch.Id, command.At!, [.. offered.Select(s => s.Text)]);

            pickupUtc = slot.Utc;
            pickupLocal = TimeOnly.FromDateTime(slot.Local);
        }

        var dishes = await DishesAsync(lines.Select(l => l.MenuItemId), cancellationToken);

        // Every dish that cannot go on, each once, in the order the guest listed them - so the app can mark them all.
        var refused = lines
            .Select(l => l.MenuItemId)
            .Distinct()
            .Select(id => (MenuItemId: id, Reason: RefusalFor(dishes.GetValueOrDefault(id), branch.Id)))
            .Where(r => r.Reason is not null)
            .Select(r => (r.MenuItemId, r.Reason!.Value))
            .ToList();

        if (refused.Count > 0)
        {
            throw new TakeawayItemsUnavailableException(refused);
        }

        var guest = await db.DinerUsers
            .AsNoTracking()
            .Where(d => d.Id == dinerUserId)
            .Select(d => new { d.DisplayName, d.PhoneE164 })
            .FirstAsync(cancellationToken);

        var used = (await db.TakeawayOrders
                .Where(o => o.BranchId == branch.Id && o.LocalDate == day.ServiceDate)
                .Select(o => o.Code)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        for (var attempt = 1; ; attempt++)
        {
            var code = TakeawayRules.FreshCode(used)
                       ?? throw new TakeawayBusyException(branch.Id, branch.TakeawayMaxInProgress);

            var order = new TakeawayOrder(
                branch.Id,
                dinerUserId,
                code,
                day.ServiceDate,
                string.IsNullOrWhiteSpace(guest.DisplayName) ? DinerAccountDeletion.GuestName : guest.DisplayName,
                guest.PhoneE164!,
                at is null,
                pickupUtc,
                pickupLocal,
                command.Note,
                [.. lines.Select(l => new TakeawayLineInput(l.MenuItemId, dishes[l.MenuItemId].Name, dishes[l.MenuItemId].PriceAmd, l.Quantity))],
                command.ClientCommandId,
                nowUtc);

            // Auto-accept: ready when the guest asked for it, straight to the kitchen - accepted as part of being placed.
            // The feed records it; no push - the answer to this request already says so.
            order.Place(acceptAutomatically: branch.TakeawayAutoAccept);
            db.TakeawayOrders.Add(order);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TakeawayCodePerBranchDay)
                                               && attempt < MaxCodeAttempts)
            {
                db.ChangeTracker.Clear();
                used.Add(code);
                continue;
            }
            catch (DbUpdateException ex) when (UniqueViolation.IsOn(ex, DatabaseIndexNames.TakeawayClientCommand))
            {
                db.ChangeTracker.Clear();

                return await ReplayAsync(command.ClientCommandId, dinerUserId, cancellationToken)
                       ?? throw new ClientCommandIdAlreadyUsedException(command.ClientCommandId, ClientCommandIdTaken);
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Takeaway order {Code} placed at branch {BranchId}: {Lines} lines, {Total} AMD, pickup {Pickup}{Auto}.",
                order.Code, branch.Id, order.Lines.Count, order.TotalAmd, pickupLocal, branch.TakeawayAutoAccept ? ", auto-accepted" : string.Empty);

            return await GuestViewAsync(order.Id, wasReplay: false, cancellationToken);
        }
    }

    public async Task<TakeawayOrderView> GetMineAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner("Read a takeaway order");
        await OwnOrderAsync(orderId, dinerUserId, cancellationToken);

        return await GuestViewAsync(orderId, wasReplay: false, cancellationToken);
    }

    public async Task<TakeawayOrderView> CancelAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var dinerUserId = RequireDiner("Cancel a takeaway order");
        var order = await OwnOrderAsync(orderId, dinerUserId, cancellationToken);

        if (!order.CancelByGuest(clock.UtcNow))
        {
            return await GuestViewAsync(orderId, wasReplay: true, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The venue answered in the same moment. What won decides: a cancel is a replay; anything else is a refusal.
            var status = await StatusAfterRaceAsync(orderId, cancellationToken);

            return status == TakeawayStatus.Cancelled
                ? await GuestViewAsync(orderId, wasReplay: true, cancellationToken)
                : throw new TakeawayStatusConflictException(
                    orderId, status, "The venue answered this order a moment ago. Call the venue to change it.");
        }

        logger.LogInformation("Takeaway order {Code} cancelled by the guest.", order.Code);

        return await GuestViewAsync(orderId, wasReplay: false, cancellationToken);
    }

    // ---------------------------------------------------------------- the floor

    public async Task<BranchTakeawayOrders> ListForBranchAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await FloorStaffGuard.RequireAtBranchAsync(db, actor, authorization, branchId, "Read the takeaway counter", cancellationToken);

        var tz = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.TimeZoneId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var nowUtc = clock.UtcNow;
        var (serviceDate, _) = await OpeningCalendars.ServiceDayAsync(db, branchId, tz, nowUtc, cancellationToken);

        var active = await StaffViewsAsync(
            db.TakeawayOrders.Where(o => o.BranchId == branchId
                                         && (o.Status == TakeawayStatus.Sent
                                             || o.Status == TakeawayStatus.Accepted
                                             || o.Status == TakeawayStatus.Ready)),
            wasReplay: false,
            cancellationToken);

        var finished = await StaffViewsAsync(
            db.TakeawayOrders.Where(o => o.BranchId == branchId
                                         && o.LocalDate == serviceDate
                                         && (o.Status == TakeawayStatus.Collected
                                             || o.Status == TakeawayStatus.NoShow
                                             || o.Status == TakeawayStatus.Declined)),
            wasReplay: false,
            cancellationToken);

        return new BranchTakeawayOrders(
            branchId,
            [.. active.OrderBy(o => o.SentAtUtc)],
            [.. finished.OrderByDescending(o => o.CollectedAtUtc ?? o.NoShowAtUtc ?? o.DeclinedAtUtc ?? o.SentAtUtc)],
            active.Count(o => o.Status == TakeawayStatus.Sent),
            active.Count(o => o.Status == TakeawayStatus.Ready),
            nowUtc);
    }

    public Task<StaffTakeawayOrderView> AcceptAsync(Guid orderId, DateTime readyAtUtc, CancellationToken cancellationToken = default) =>
        StaffActAsync(
            orderId,
            "Accept a takeaway order",
            kitchenMay: false,
            TakeawayStatus.Accepted,
            (order, staffId, nowUtc) => order.Accept(AsUtc(readyAtUtc), staffId, nowUtc),
            cancellationToken);

    public Task<StaffTakeawayOrderView> DeclineAsync(Guid orderId, string? reason, CancellationToken cancellationToken = default)
    {
        if (reason is { Length: > TakeawayRules.MaxDeclineReasonLength })
        {
            throw new FieldValidationException(
            [
                new FieldViolation(
                    TakeawayRules.Fields.Reason,
                    $"The reason is at most {TakeawayRules.MaxDeclineReasonLength} characters.",
                    FieldBounds.Max,
                    Max: TakeawayRules.MaxDeclineReasonLength,
                    Value: reason.Length),
            ]);
        }

        return StaffActAsync(
            orderId,
            "Decline a takeaway order",
            kitchenMay: false,
            TakeawayStatus.Declined,
            (order, staffId, nowUtc) => order.Decline(reason, staffId, nowUtc),
            cancellationToken);
    }

    public Task<StaffTakeawayOrderView> MarkReadyAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        StaffActAsync(
            orderId,
            "Mark a takeaway order ready",
            kitchenMay: true,
            TakeawayStatus.Ready,
            (order, staffId, nowUtc) => order.MarkReady(staffId, nowUtc),
            cancellationToken);

    public Task<StaffTakeawayOrderView> CollectAsync(
        Guid orderId, TakeawayPaymentMethod method, CancellationToken cancellationToken = default) =>
        StaffActAsync(
            orderId,
            "Hand over a takeaway order",
            kitchenMay: false,
            TakeawayStatus.Collected,
            (order, staffId, nowUtc) => order.Collect(method, staffId, nowUtc),
            cancellationToken);

    /// <summary>
    /// Nobody came for it. Counts toward the guest's no-show block exactly as a missed booking does
    /// (<see cref="GuestRecords.BlockingNoShowDaysAsync"/>): recorded under the guest's account lock - the lock a
    /// booking's no-show takes - and judged in the same transaction, so a third no-show is never missed and the block
    /// never written twice. A block cancels the guest's bookings still to come, and their tables' holds are given back
    /// after the commit. Unlike a booking's, it cannot be withdrawn.
    /// </summary>
    /// <remarks>
    /// The order's own side effects are its <see cref="TakeawayMarkedNoShow"/> handlers' (the History's line). The block
    /// stays here: it is judged on the no-show as saved, and a handler runs before the save; it is the bookings' rule as
    /// much as this one's (<see cref="NoShowBlocks"/>, shared with a booking's no-show); and the holds it frees are given
    /// back after the commit, which no handler sees.
    /// </remarks>
    public async Task<StaffTakeawayOrderView> MarkNoShowAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await db.TakeawayOrders
                        .Include(o => o.Lines)
                        .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Takeaway order {orderId} was not found.");

        var staffId = await RequireStaffAtAsync(order.BranchId, "Mark a takeaway order not collected", kitchenMay: false, cancellationToken);
        var nowUtc = clock.UtcNow;

        if (!order.MarkNoShow(staffId, nowUtc))
        {
            return await StaffViewAsync(orderId, wasReplay: true, cancellationToken);
        }

        IReadOnlyList<Domain.Occupancy.Reservation> cancelledByBlock = [];

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (order.DinerUserId is { } locked)
            {
                await DinerAccountLock.IsLiveAsync(db, locked, cancellationToken);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                var status = await StatusAfterRaceAsync(orderId, cancellationToken);

                return status == TakeawayStatus.NoShow
                    ? await StaffViewAsync(orderId, wasReplay: true, cancellationToken)
                    : throw new TakeawayStatusConflictException(
                        orderId, status, "Somebody changed this order a moment ago. Read the counter again.");
            }

            // Saved first, so the count reads this one as it reads the others.
            if (order.DinerUserId is { } account)
            {
                var branch = await db.Branches
                    .Include(b => b.Venue)
                    .FirstAsync(b => b.Id == order.BranchId, cancellationToken);

                (cancelledByBlock, _) = await noShowBlocks.BlockIfDueAsync(account, branch, nowUtc, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await noShowBlocks.ReleaseHoldsOfAsync(cancelledByBlock, cancellationToken);

        logger.LogInformation(
            "Takeaway order {Code} at branch {BranchId} was not collected, marked by staff {StaffId}.",
            order.Code, order.BranchId, staffId);

        return await StaffViewAsync(orderId, wasReplay: false, cancellationToken);
    }

    /// <summary>
    /// One staff action: the caller may act at the order's branch, the order makes the move, and its event's handlers
    /// write the History's line and tell the guest in the same save. The move already made is answered as a replay - two
    /// tablets, one answer; the order moved elsewhere under the save is 409 <c>takeaway-status-conflict</c> with where it
    /// is now.
    /// </summary>
    private async Task<StaffTakeawayOrderView> StaffActAsync(
        Guid orderId,
        string operation,
        bool kitchenMay,
        TakeawayStatus target,
        Func<TakeawayOrder, Guid, DateTime, bool> act,
        CancellationToken cancellationToken)
    {
        var order = await db.TakeawayOrders
                        .Include(o => o.Lines)
                        .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Takeaway order {orderId} was not found.");

        var staffId = await RequireStaffAtAsync(order.BranchId, operation, kitchenMay, cancellationToken);
        var nowUtc = clock.UtcNow;

        if (!act(order, staffId, nowUtc))
        {
            return await StaffViewAsync(orderId, wasReplay: true, cancellationToken);
        }

        // The guest's notice takes the account's lock in the save's transaction, and joins this one.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var status = await StatusAfterRaceAsync(orderId, cancellationToken);

            return status == target
                ? await StaffViewAsync(orderId, wasReplay: true, cancellationToken)
                : throw new TakeawayStatusConflictException(
                    orderId, status, "Somebody changed this order a moment ago. Read the counter again.");
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Takeaway order {Code} at branch {BranchId} is now {Status}, by staff {StaffId}.",
            order.Code, order.BranchId, order.Status, staffId);

        return await StaffViewAsync(orderId, wasReplay: false, cancellationToken);
    }

    // ---------------------------------------------------------------- the console

    public async Task<TakeawaySettingsView> GetSettingsAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await branchGuard.RequireAtBranchAsync(branchId, "Read the takeaway settings", cancellationToken);

        return await SettingsViewAsync(branchId, cancellationToken);
    }

    public async Task<TakeawaySettingsView> SaveSettingsAsync(
        Guid branchId, TakeawaySettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await branchGuard.RequireAtBranchAsync(branchId, "Change the takeaway settings", cancellationToken);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        var before = new
        {
            enabled = branch.TakeawayEnabled,
            autoAccept = branch.TakeawayAutoAccept,
            prepMinutes = branch.TakeawayPrepMinutes,
            lastOrderMinutes = branch.TakeawayLastOrderMinutes,
            maxInProgress = branch.TakeawayMaxInProgress,
        };

        branch.SetTakeaway(
            settings.Enabled, settings.AutoAccept, settings.PrepMinutes, settings.LastOrderMinutes, settings.MaxInProgress);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Settings, ActivityActions.SettingsTakeaway, ActivitySubjects.Branch, branchId)
            {
                BranchId = branchId,
                VenueId = branch.VenueId,
                Before = before,
                After = new
                {
                    enabled = branch.TakeawayEnabled,
                    autoAccept = branch.TakeawayAutoAccept,
                    prepMinutes = branch.TakeawayPrepMinutes,
                    lastOrderMinutes = branch.TakeawayLastOrderMinutes,
                    maxInProgress = branch.TakeawayMaxInProgress,
                },
                Summary = new { settings.Enabled, settings.AutoAccept },
            },
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Branch {BranchId} takeaway {State}{Auto}: {Prep} min prep, last order {Last} min before closing, {Max} at once.",
            branchId,
            settings.Enabled ? "on" : "off",
            settings.AutoAccept ? ", accepted automatically" : string.Empty,
            settings.PrepMinutes,
            settings.LastOrderMinutes,
            settings.MaxInProgress);

        return await SettingsViewAsync(branchId, cancellationToken);
    }

    private async Task<TakeawaySettingsView> SettingsViewAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => new
            {
                b.TakeawayEnabled,
                b.TakeawayAutoAccept,
                b.TakeawayPrepMinutes,
                b.TakeawayLastOrderMinutes,
                b.TakeawayMaxInProgress,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        return new TakeawaySettingsView(
            branch.TakeawayEnabled,
            branch.TakeawayAutoAccept,
            branch.TakeawayPrepMinutes,
            branch.TakeawayLastOrderMinutes,
            branch.TakeawayMaxInProgress,
            Bounds,
            TakeawayRules.AcceptWindowMinutes,
            TakeawayRules.NoShowAfterMinutes);
    }

    internal static readonly TakeawaySettingsBounds Bounds = new(
        TakeawayRules.MinPrepMinutes,
        TakeawayRules.MaxPrepMinutes,
        TakeawayRules.PrepMinutesStep,
        TakeawayRules.MinLastOrderMinutes,
        TakeawayRules.MaxLastOrderMinutes,
        TakeawayRules.LastOrderMinutesStep,
        TakeawayRules.MinMaxInProgress,
        TakeawayRules.MaxMaxInProgress);

    // ---------------------------------------------------------------- the day, the slots, the dishes

    /// <summary>The branch as a guest may order from it: open for business, or 404 / 409 <c>branch-unavailable</c>.</summary>
    private async Task<Branch> OpenBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches
                         .AsNoTracking()
                         .Include(b => b.Venue)
                         .FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
                     ?? throw new KeyNotFoundException($"Branch {branchId} was not found.");

        VenueGate.RequireOpenForBusiness(branch);
        await DinerVisibility.RequireTakingGuestsAsync(db, branch.Id, cancellationToken);

        return branch;
    }

    private async Task<Day> DayAsync(Branch branch, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var localNow = BranchTime.ToLocal(nowUtc, branch.TimeZoneId);
        var today = DateOnly.FromDateTime(localNow);
        var calendar = await OpeningCalendars.ForBranchAsync(db, branch.Id, today.AddDays(-1), today.AddDays(8), cancellationToken);
        var window = TakeawayRules.Window(calendar, localNow, branch.TakeawayPrepMinutes, branch.TakeawayLastOrderMinutes);

        return new Day(BranchZone.For(branch.TimeZoneId), localNow, calendar.ServiceDateOf(localNow), window);
    }

    /// <summary>The next opening, local <c>HH:mm</c> and UTC, or nothing when none is known.</summary>
    private static (string? Local, DateTime? Utc) Opening(Day day) =>
        day.Window.OpensAtLocal is { } opens
        && day.Zone.TryToUtc(DateOnly.FromDateTime(opens), TimeOnly.FromDateTime(opens), out var utc)
            ? (TakeawayRules.SlotText(opens), utc)
            : (null, null);

    /// <summary>The window's slots that fit beside the orders in progress, with their instants.</summary>
    private static List<Slot> Offered(Branch branch, Day day, IReadOnlyList<DateTime> pickups)
    {
        var offered = new List<Slot>();

        foreach (var local in day.Window.SlotsLocal)
        {
            if (day.Zone.TryToUtc(DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local), out var utc)
                && TakeawayRules.SlotFits(utc, branch.TakeawayPrepMinutes, pickups, branch.TakeawayMaxInProgress))
            {
                offered.Add(new Slot(TakeawayRules.SlotText(local), local, utc));
            }
        }

        return offered;
    }

    /// <summary>The pickup instants of the branch's orders in progress - sent or accepted.</summary>
    private async Task<IReadOnlyList<DateTime>> InProgressPickupsAsync(Guid branchId, CancellationToken cancellationToken) =>
        await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.BranchId == branchId
                        && (o.Status == TakeawayStatus.Sent || o.Status == TakeawayStatus.Accepted))
            .Select(o => o.ReadyAtUtc ?? o.RequestedPickupUtc)
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<Guid, Dish>> DishesAsync(IEnumerable<Guid> menuItemIds, CancellationToken cancellationToken)
    {
        var ids = menuItemIds.Distinct().ToList();

        // The item itself, so completeness is MenuItemCompleteness's - the rule the diner's menu applies.
        var items = await db.MenuItems
            .AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Select(m => new { Item = m, m.MenuCategory.BranchId })
            .ToListAsync(cancellationToken);

        return items.ToDictionary(
            i => i.Item.Id,
            i => new Dish(
                i.BranchId,
                i.Item.Name,
                i.Item.PriceAmd,
                MenuItemCompleteness.IsComplete(i.Item),
                i.Item.IsAvailable,
                i.Item.AvailableForTakeaway));
    }

    /// <summary>Why a dish cannot be ordered to take away here, or null when it can.</summary>
    private static TakeawayItemRefusal? RefusalFor(Dish? dish, Guid branchId) =>
        dish is null || dish.BranchId != branchId || !dish.IsComplete
            ? TakeawayItemRefusal.NotOnMenu
            : !dish.IsAvailable
                ? TakeawayItemRefusal.SoldOut
                : !dish.AvailableForTakeaway
                    ? TakeawayItemRefusal.NotForTakeaway
                    : null;

    private static TimeOnly Minute(TimeOnly time) => new(time.Hour, time.Minute);

    /// <summary>An instant as the wire sent it: with an offset it was read as local and goes back; without one it is UTC.</summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // ---------------------------------------------------------------- who

    private Guid RequireDiner(string operation) =>
        actor.Type == ActorType.Diner && actor.DinerUserId is { } id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException($"{operation} requires a diner account.");

    /// <summary>
    /// The caller may act on a takeaway order at the branch: floor staff at it, or - for the kitchen's one move - the
    /// Kitchen role there. Anybody else is 403.
    /// </summary>
    private async Task<Guid> RequireStaffAtAsync(Guid branchId, string operation, bool kitchenMay, CancellationToken cancellationToken)
    {
        if (actor.Type == ActorType.Staff && actor.Role == StaffRole.Kitchen && actor.StaffMemberId is { } cookId)
        {
            if (!kitchenMay)
            {
                throw new StaffPermissionException(operation, StaffRole.Kitchen, StaffRole.Waiter);
            }

            await branchGuard.RequireAsync(cookId, branchId, operation, "takeaway order", cancellationToken);

            return cookId;
        }

        return await FloorStaffGuard.RequireAtBranchAsync(db, actor, authorization, branchId, operation, cancellationToken);
    }

    /// <summary>The caller's own order, tracked; anybody else's is not found, word for word as no order.</summary>
    private async Task<TakeawayOrder> OwnOrderAsync(Guid orderId, Guid dinerUserId, CancellationToken cancellationToken) =>
        await db.TakeawayOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.DinerUserId == dinerUserId, cancellationToken)
        ?? throw new KeyNotFoundException($"Takeaway order {orderId} was not found.");

    private const string ClientCommandIdTaken =
        "That clientCommandId already belongs to another order. Generate a fresh one per order, and reuse it only when "
        + "retrying that same order.";

    /// <summary>The order this command already made, answered as a replay; another person's id is 409.</summary>
    private async Task<TakeawayOrderView?> ReplayAsync(Guid clientCommandId, Guid dinerUserId, CancellationToken cancellationToken)
    {
        var existing = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.ClientCommandId == clientCommandId)
            .Select(o => new { o.Id, o.DinerUserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return null;
        }

        return existing.DinerUserId == dinerUserId
            ? await GuestViewAsync(existing.Id, wasReplay: true, cancellationToken)
            : throw new ClientCommandIdAlreadyUsedException(clientCommandId, ClientCommandIdTaken);
    }

    private async Task<TakeawayStatus> StatusAfterRaceAsync(Guid orderId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();

        return await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => o.Status)
            .FirstAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- views

    private async Task<TakeawayOrderView> GuestViewAsync(Guid orderId, bool wasReplay, CancellationToken cancellationToken)
    {
        var row = await db.TakeawayOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new
            {
                Order = o,
                Lines = o.Lines.OrderBy(l => l.Position).ToList(),
                Branch = db.Branches
                    .Where(b => b.Id == o.BranchId)
                    .Select(b => new
                    {
                        VenueName = b.Venue.Name,
                        b.Name,
                        b.Address,
                        b.PhoneE164,
                        b.Latitude,
                        b.Longitude,
                        b.TimeZoneId,
                    })
                    .First(),
            })
            .FirstAsync(cancellationToken);

        var o = row.Order;
        var zone = BranchZone.For(row.Branch.TimeZoneId);
        var lines = row.Lines.Select(LineView).ToList();

        return new TakeawayOrderView(
            o.Id,
            o.BranchId,
            row.Branch.VenueName,
            row.Branch.Name,
            row.Branch.Address,
            row.Branch.PhoneE164,
            row.Branch.Latitude,
            row.Branch.Longitude,
            o.Code,
            o.Status,
            o.PickupAsap,
            o.RequestedPickupUtc,
            Hhmm(o.RequestedPickupLocalTime),
            o.ReadyAtUtc,
            o.ReadyAtUtc is { } readyAt ? Hhmm(zone.LocalTimeAt(readyAt)) : null,
            o.Note,
            lines,
            lines.Sum(l => l.LineTotalAmd),
            o.PaymentMethod,
            o.PaidAmountAmd,
            o.CreatedAtUtc,
            o.AcceptedAtUtc,
            o.MarkedReadyAtUtc,
            o.CollectedAtUtc,
            o.DeclinedAtUtc,
            o.CancelledAtUtc,
            o.NoShowAtUtc,
            o.DeclinedBecause,
            o.DeclineReason,
            o.AcceptDeadlineUtc,
            o.Status == TakeawayStatus.Sent,
            wasReplay);
    }

    private async Task<StaffTakeawayOrderView> StaffViewAsync(Guid orderId, bool wasReplay, CancellationToken cancellationToken) =>
        (await StaffViewsAsync(db.TakeawayOrders.Where(o => o.Id == orderId), wasReplay, cancellationToken)).Single();

    /// <summary>The orders as the counter reads them: one statement for the orders, their lines and who accepted.</summary>
    private async Task<List<StaffTakeawayOrderView>> StaffViewsAsync(
        IQueryable<TakeawayOrder> orders, bool wasReplay, CancellationToken cancellationToken)
    {
        var rows = await orders
            .AsNoTracking()
            .Select(o => new
            {
                Order = o,
                Lines = o.Lines.OrderBy(l => l.Position).ToList(),
                AcceptedByName = db.StaffMembers.Where(s => s.Id == o.AcceptedByStaffId).Select(s => s.FullName).FirstOrDefault(),
                TimeZoneId = db.Branches.Where(b => b.Id == o.BranchId).Select(b => b.TimeZoneId).First(),
            })
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        // The guests' numbers are the venue's to call, not the platform's to read.
        var withPhones = actor.Role != StaffRole.PlatformAdmin;

        return
        [
            .. rows.Select(r =>
            {
                var o = r.Order;
                var zone = BranchZone.For(r.TimeZoneId);
                var lines = r.Lines.Select(LineView).ToList();

                return new StaffTakeawayOrderView(
                    o.Id,
                    o.Code,
                    o.Status,
                    o.GuestName,
                    withPhones ? o.GuestPhone : string.Empty,
                    o.PickupAsap,
                    o.RequestedPickupUtc,
                    Hhmm(o.RequestedPickupLocalTime),
                    o.ReadyAtUtc,
                    o.ReadyAtUtc is { } readyAt ? Hhmm(zone.LocalTimeAt(readyAt)) : null,
                    o.Note,
                    lines,
                    lines.Sum(l => l.LineTotalAmd),
                    o.CreatedAtUtc,
                    o.AcceptDeadlineUtc,
                    o.AcceptedAtUtc,
                    r.AcceptedByName,
                    o.MarkedReadyAtUtc,
                    o.NoShowFromUtc,
                    o.CollectedAtUtc,
                    o.PaymentMethod,
                    o.PaidAmountAmd,
                    o.DeclinedAtUtc,
                    o.DeclinedBecause,
                    o.DeclineReason,
                    o.NoShowAtUtc,
                    o.CancelledAtUtc,
                    wasReplay);
            }),
        ];
    }

    private static TakeawayLineView LineView(TakeawayOrderLine l) =>
        new(l.Id, l.MenuItemId, l.NameSnapshot, l.UnitPriceAmdSnapshot, l.Quantity, l.LineTotalAmd);

    private static string Hhmm(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The branch's day as takeaway reads it.</summary>
    private sealed record Day(BranchZone Zone, DateTime LocalNow, DateOnly ServiceDate, TakeawayWindow Window);

    /// <summary>A pickup slot on offer: as the wire writes it, on the wall clock, and the instant.</summary>
    private sealed record Slot(string Text, DateTime Local, DateTime Utc);

    /// <summary>A dish as takeaway judges it.</summary>
    private sealed record Dish(Guid BranchId, string Name, long PriceAmd, bool IsComplete, bool IsAvailable, bool AvailableForTakeaway);
}
