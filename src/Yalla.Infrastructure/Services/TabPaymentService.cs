using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Ordering;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Settling a tab. Cash is the only rail here, and the atomic reserve is built for the ones that
/// come next.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here is a fiscal receipt.</b> The venue's registered cash register still issues the
/// receipt for every one of these payments. This records what the venue took, so the tab balances
/// and the drawer reconciles - it is not, and must never be described to a venue as, fiscal
/// compliance. See <c>docs/billing.md</c>.
/// </para>
/// <para>
/// <b>Payments never retry.</b> <see cref="TabLedger.SaveWithTotalsAsync"/> reloads and retries a
/// totals clash because two orders genuinely commute; a payment does not. Reserving twice against
/// the same remaining balance is precisely the failure the reserve exists to prevent, so a
/// concurrency clash here is reported to the caller and the waiter tries again with fresh numbers
/// in front of them.
/// </para>
/// </remarks>
internal sealed class TabPaymentService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    TabLedger ledger,
    ILogger<TabPaymentService> logger) : ITabPaymentService
{
    public async Task<CashPaymentView> RecordCashAsync(
        RecordCashPaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var staffId = RequireStaff("Record a cash payment");

        if (command.AmountAmd <= 0L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command), command.AmountAmd, "A payment must be for a positive amount.");
        }

        if (await FindReplayAsync(command.ClientCommandId, cancellationToken) is { } replayed)
        {
            logger.LogInformation(
                "Cash command {ClientCommandId} was already applied as payment {PaymentId}.",
                command.ClientCommandId, replayed);

            return await BuildViewAsync(replayed, wasReplay: true, false, false, cancellationToken);
        }

        var tab = await ledger.LoadForWriteAsync(command.TabId, cancellationToken);

        if (tab.Status is TabStatus.Closed or TabStatus.Abandoned)
        {
            throw new PaymentExceedsRemainingException(tab.Id, command.AmountAmd, 0L);
        }

        if (command.TabParticipantId is { } payerId && tab.Participants.All(p => p.Id != payerId))
        {
            throw new KeyNotFoundException($"Participant {payerId} is not on tab {tab.Id}.");
        }

        // The reserve, and the whole point of this method. What is owed is read from the lines
        // rather than from the cached column, so a stale cache cannot let a payment through that
        // the bill does not support.
        var before = ledger.Compute(tab);

        if (command.AmountAmd > before.RemainingAmd)
        {
            // The usual cause is two people settling at once. The waiter is standing at the table
            // holding notes, so the exception carries the number rather than only a refusal.
            throw new PaymentExceedsRemainingException(tab.Id, command.AmountAmd, before.RemainingAmd);
        }

        var nowUtc = clock.UtcNow;

        var payment = Payment.Reserve(
            tab.Id, command.AmountAmd, PaymentMethod.Cash, nowUtc, command.TabParticipantId,
            command.TipAmd, command.ClientCommandId);

        // Cash is already on the table, so the hold and the settlement happen together. The two
        // steps exist so a wallet can sit in Reserved while the provider is called.
        payment.MarkSucceeded(nowUtc);

        db.Payments.Add(payment);

        // Navigation fixup does not reach a collection that was loaded before the add, and the
        // recomputation below reads tab.Payments - so this payment has to be put where it will be
        // seen, or the tab would be saved still believing nothing had been paid.
        await db.Entry(tab).Collection(t => t.Payments).LoadAsync(cancellationToken);

        var after = ledger.Compute(tab);

        tab.ApplyComputedTotals(after.SubtotalAmd, after.ServiceChargeAmd, after.PaidAmd);

        // The split cannot be renegotiated once money has landed against it.
        tab.LockSettlementMode(nowUtc);

        var closed = false;
        var sessionClosed = false;

        if (tab.RemainingAmd == 0L)
        {
            tab.Close(nowUtc);
            closed = true;
            sessionClosed = await CloseSessionAsync(tab, nowUtc, cancellationToken);
        }

        ledger.Append(tab.Id, TabEventType.PaymentRecorded, new
        {
            paymentId = payment.Id,
            amountAmd = payment.AmountAmd,
            tipAmd = payment.TipAmd,
            method = (int)payment.Method,
            tabParticipantId = payment.TabParticipantId,
            byStaffId = staffId,
            remainingAmd = tab.RemainingAmd,
        });

        if (closed)
        {
            ledger.Append(tab.Id, TabEventType.TabClosed, new
            {
                closedAtUtc = nowUtc,
                totalAmd = tab.TotalAmd,
                tableSessionClosed = sessionClosed,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else settled against this tab between the reserve and the commit. This is
            // the case the row version exists to catch, and it is deliberately NOT retried: a retry
            // would reserve a second time against a balance that has already moved, which is the
            // exact double-charge the reserve is there to prevent. See TabLedger for why order
            // placement is the opposite case and does retry.
            db.ChangeTracker.Clear();

            var fresh = await CurrentRemainingAsync(tab.Id, cancellationToken);

            logger.LogInformation(
                "Cash payment on tab {TabId} lost the race; {RemainingAmd} AMD is now outstanding.",
                tab.Id, fresh);

            throw new PaymentExceedsRemainingException(tab.Id, command.AmountAmd, fresh);
        }
        catch (DbUpdateException ex)
            when (UniqueViolation.IsOn(ex, DatabaseIndexNames.PaymentClientCommand))
        {
            // Two copies of the same command raced. The other did the work; this one answers with
            // it rather than taking the money twice.
            db.ChangeTracker.Clear();

            var winner = await FindReplayAsync(command.ClientCommandId, cancellationToken)
                         ?? throw new InvalidOperationException(
                             $"Payment command {command.ClientCommandId} violated the idempotency index "
                             + "but no payment was found.");

            return await BuildViewAsync(winner, wasReplay: true, false, false, cancellationToken);
        }

        logger.LogInformation(
            "Cash payment {PaymentId} of {AmountAmd} AMD (tip {TipAmd}) recorded on tab {TabId} by staff "
            + "{StaffId}. {RemainingAmd} AMD remains.",
            payment.Id, payment.AmountAmd, payment.TipAmd, tab.Id, staffId, tab.RemainingAmd);

        return await BuildViewAsync(payment.Id, false, closed, sessionClosed, cancellationToken);
    }

    /// <summary>
    /// Closes the sitting the tab belonged to, if it is still open.
    /// </summary>
    /// <remarks>
    /// <b>The table is not freed.</b> That stays an explicit waiter action, because physical state
    /// and financial state are independent: a party that has paid usually sits on for another
    /// twenty minutes, and a floor plan that shows their table as free the moment the bill settles
    /// is lying to the person trying to seat the next walk-in.
    /// </remarks>
    private async Task<bool> CloseSessionAsync(Tab tab, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var session = await db.TableSessions
            .FirstOrDefaultAsync(s => s.Id == tab.TableSessionId && s.ClosedAtUtc == null, cancellationToken);

        if (session is null)
        {
            return false;
        }

        session.Close(nowUtc);

        return true;
    }

    public async Task<TabTotalsSnapshot> AbandonAsync(
        Guid tabId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireManager("Write off a tab");

        var tab = await ledger.LoadForWriteAsync(tabId, cancellationToken);
        var bill = ledger.Compute(tab);

        tab.ApplyComputedTotals(bill.SubtotalAmd, bill.ServiceChargeAmd, bill.PaidAmd);
        tab.MarkAbandoned(clock.UtcNow);

        ledger.Append(tab.Id, TabEventType.TabAbandoned, new
        {
            reason,
            writtenOffAmd = tab.RemainingAmd,
            byStaffId = staffId,
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Tab {TabId} written off by manager {StaffId} with {RemainingAmd} AMD outstanding: {Reason}",
            tab.Id, staffId, tab.RemainingAmd, reason);

        return TabLedger.ToSnapshot(ledger.Compute(tab));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The payment this command already made, if it made one.</summary>
    /// <remarks>
    /// Nullable because <c>Guid</c> is a value type: returning a bare <c>Guid</c> would make the
    /// caller's <c>is { }</c> pattern match <c>Guid.Empty</c>, and every first payment would be
    /// treated as a replay of one that does not exist.
    /// </remarks>
    private Task<Guid?> FindReplayAsync(Guid clientCommandId, CancellationToken cancellationToken) =>
        db.Payments
            .AsNoTracking()
            .Where(p => p.ClientCommandId == clientCommandId)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<CashPaymentView> BuildViewAsync(
        Guid paymentId,
        bool wasReplay,
        bool tabClosed,
        bool sessionClosed,
        CancellationToken cancellationToken)
    {
        var payment = await db.Payments
            .AsNoTracking()
            .Include(p => p.Tab)
            .FirstAsync(p => p.Id == paymentId, cancellationToken);

        var tab = payment.Tab;

        return new CashPaymentView(
            payment.Id,
            payment.TabId,
            payment.AmountAmd,
            payment.TipAmd,
            payment.Status,
            payment.TabParticipantId,
            new TabTotalsSnapshot(
                tab.SubtotalAmd, tab.ServiceChargeAmd, tab.TotalAmd, tab.PaidAmd, tab.RemainingAmd),
            tabClosed || tab.Status == TabStatus.Closed,
            sessionClosed,
            await ledger.MaxSequenceAsync(payment.TabId, cancellationToken),
            wasReplay);
    }

    /// <summary>What the tab owes right now, read fresh from the lines after a lost race.</summary>
    private async Task<long> CurrentRemainingAsync(Guid tabId, CancellationToken cancellationToken)
    {
        var tab = await ledger.LoadForWriteAsync(tabId, cancellationToken);

        return ledger.Compute(tab).RemainingAmd;
    }

    private Guid RequireStaff(string operation) =>
        actor.Type == ActorType.Staff && actor.StaffMemberId is { } id
            ? id
            : throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);

    private Guid RequireManager(string operation)
    {
        var staffId = RequireStaff(operation);

        if (actor.Role is not (StaffRole.Manager or StaffRole.Owner or StaffRole.PlatformAdmin))
        {
            throw new StaffPermissionException(operation, actor.Role, StaffRole.Manager);
        }

        return staffId;
    }
}
