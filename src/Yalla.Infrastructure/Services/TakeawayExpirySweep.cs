using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Messaging;
using Yalla.Domain.Takeaway;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Ends the takeaway orders nobody at the venue answered within five minutes: declined, and the guest told - "no answer
/// in time", an apology rather than the venue's no.
/// </summary>
/// <remarks>
/// <para>
/// <b>A sweep, for the approval sweep's reason</b> (<see cref="ApprovalExpirySweep"/>): the outbox drops what is an hour
/// overdue, and a guest left on "waiting for the venue" must be answered however late the server notices. It reads the
/// orders still waiting whose <c>AcceptDeadlineUtc</c> has passed, through a small filtered index.
/// </para>
/// <para>
/// <b>A branch that accepts automatically</b> never has an order waiting - except one sent before the switch was turned
/// on. That one is accepted here, ready when the guest asked for it (or now, if that has passed), rather than declined
/// for an answer the venue said it no longer gives.
/// </para>
/// <para>
/// Each order on its own, under its row version: a waiter who accepted it a moment ago wins, and the sweep writes
/// nothing for it. One that fails is logged and left for the next pass.
/// </para>
/// <para>
/// The order's move raises its event (<see cref="TakeawayDeclined"/> for nobody's answer, <see cref="TakeawayAccepted"/>
/// by nobody), whose handlers write the History's line - the system's - and tell the guest, in this save.
/// </para>
/// </remarks>
internal sealed class TakeawayExpirySweep(
    YallaDbContext db,
    IClock clock,
    ILogger<TakeawayExpirySweep> logger)
{
    internal const int BatchSize = 50;

    /// <summary>One pass. Returns how many orders it declined or accepted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = clock.UtcNow;
        var tried = new HashSet<Guid>();
        var ended = 0;

        while (true)
        {
            var exclude = tried.ToList();

            var lapsed = await db.TakeawayOrders
                .AsNoTracking()
                .Where(o => o.AcceptDeadlineUtc != null
                            && o.AcceptDeadlineUtc <= nowUtc
                            && o.Status == TakeawayStatus.Sent
                            && !exclude.Contains(o.Id))
                .OrderBy(o => o.AcceptDeadlineUtc)
                .Select(o => o.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (lapsed.Count == 0)
            {
                return ended;
            }

            foreach (var id in lapsed)
            {
                tried.Add(id);

                try
                {
                    if (await EndAsync(id, nowUtc, cancellationToken))
                    {
                        ended++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The takeaway sweep could not end order {OrderId}; the next pass tries again.", id);
                }
                finally
                {
                    db.ChangeTracker.Clear();
                }
            }
        }
    }

    private async Task<bool> EndAsync(Guid orderId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var order = await db.TakeawayOrders.Include(o => o.Lines).FirstAsync(o => o.Id == orderId, cancellationToken);

        var autoAccept = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == order.BranchId)
            .Select(b => b.TakeawayAutoAccept)
            .FirstAsync(cancellationToken);

        if (autoAccept)
        {
            if (order.Status != TakeawayStatus.Sent)
            {
                return false;
            }

            order.Accept(order.RequestedPickupUtc > nowUtc ? order.RequestedPickupUtc : nowUtc, null, nowUtc);
        }
        else if (!order.DeclineForNoAnswer(nowUtc))
        {
            return false;
        }

        // The guest's notice takes the account's lock in it; a race lost rolls it back with the rest.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody at the venue answered in the same moment: their answer stands.
            return false;
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Takeaway order {Code} at branch {BranchId}: nobody answered in {Minutes} minutes - {Outcome}.",
            order.Code,
            order.BranchId,
            TakeawayRules.AcceptWindowMinutes,
            autoAccept ? "accepted automatically" : "declined, and the guest told");

        return true;
    }
}

/// <summary>
/// Runs <see cref="TakeawayExpirySweep.RunOnceAsync"/> on the approval sweep's interval
/// (<c>ApprovalExpiry:SweepIntervalSeconds</c>, default 20): the five-minute deadline is noticed within seconds. Zero
/// switches it off, as it does the approval sweep - a test host sharing a database.
/// </summary>
internal sealed class TakeawayExpiryHostedService(
    IServiceScopeFactory scopes,
    ApprovalExpiryOptions options,
    TimeProvider time,
    ILogger<TakeawayExpiryHostedService> logger) : BackgroundService
{
    private PeriodicTimer? _timer;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.SweepIntervalSeconds > 0)
        {
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepIntervalSeconds), time);
        }

        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_timer is not { } timer)
        {
            return;
        }

        try
        {
            await SweepOnceAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            return await scope.ServiceProvider.GetRequiredService<TakeawayExpirySweep>().RunOnceAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The takeaway sweep failed. It will try again in {Seconds} seconds.", options.SweepIntervalSeconds);

            return 0;
        }
    }
}
