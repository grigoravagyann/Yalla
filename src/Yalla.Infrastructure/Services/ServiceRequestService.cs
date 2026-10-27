using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Ordering;
using Yalla.Application.Tabs;
using Yalla.Domain;
using Yalla.Domain.Billing;
using Yalla.Domain.Enums;
using Yalla.Domain.Staff;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Calling a waiter, and a waiter answering.
/// </summary>
/// <remarks>
/// The smallest thing in the product and one of the most useful. Catching a waiter's eye is the
/// single most common friction in a busy room, and it is worst for the diner who is shy, sitting
/// where the round does not pass, or not speaking the language.
/// <para>
/// What a call tells the rest of the system - the tab's stream, the floor's push, the History's claim - is written by
/// the handlers of its domain events (<c>DomainEvents/Calls/</c>, <c>docs/domain-events.md</c>); this service decides
/// whether the change may happen, makes it on the request, and saves through the ledger.
/// </para>
/// </remarks>
internal sealed class ServiceRequestService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
    StaffBranchGuard branchGuard,
    TabLedger ledger,
    ILogger<ServiceRequestService> logger) : IServiceRequestService
{
    /// <summary>
    /// How many requests one tab may raise inside <see cref="RateWindowMinutes"/>.
    /// </summary>
    /// <remarks>
    /// Per <b>tab</b>, not per person. The list a waiter is looking at is the table's, and one
    /// bored guest tapping "napkins" twenty times must not be able to bury another table's request
    /// - or their own tablemate's. Five in ten minutes is well above what a real table does and far
    /// below what makes the floor screen useless.
    /// </remarks>
    private const int RateLimit = 5;

    private const int RateWindowMinutes = 10;

    public async Task<ServiceRequestView> RaiseAsync(
        Guid tabId,
        Guid actingParticipantId,
        ServiceRequestPreset preset,
        string? note,
        BillScope? billScope = null,
        int? tipPercent = null,
        CancellationToken cancellationToken = default)
    {
        // The shape first, every field at once, before anything is read: a tip on a request for
        // napkins is wrong whoever sends it.
        ServiceRequest.ValidateBillFields(preset, billScope, tipPercent);

        var tab = await db.Tabs
            .Include(t => t.Participants)
            .Include(t => t.DiningTable)
            .FirstOrDefaultAsync(t => t.Id == tabId, cancellationToken)
            ?? throw new KeyNotFoundException($"Tab {tabId} was not found.");

        var participant = tab.Participants.FirstOrDefault(p => p.Id == actingParticipantId)
                          ?? throw new TabPermissionException("Calling a waiter", "somebody on this tab");

        // A pending joiner may ask for water. They are sitting at the table; the host's approval
        // gates the bill, not their existence.
        if (!TabPermissions.MayReadTab(participant.Status))
        {
            throw new TabPermissionException("Calling a waiter", "somebody still on this tab");
        }

        // A tab written off is one whose party left without paying: its calls were answered with it (WrittenOffTabCalls),
        // and a phone still holding its token within the receipt grace does not put a new one on the floor's rail. A
        // settled tab may still call: a party that has paid usually sits on.
        if (tab.Status == TabStatus.Abandoned)
        {
            throw new DomainStateException("This tab was written off: there is nobody at the table to bring anything to.");
        }

        // A tab still waiting for a waiter may call one - that is how it gets let in - but there is no bill
        // to bring for a table nobody has admitted.
        if (preset == ServiceRequestPreset.TheBill && tab.Admission != TabAdmission.Admitted)
        {
            throw new TabAwaitingAdmissionException(tab.Id);
        }

        var nowUtc = clock.UtcNow;
        var since = nowUtc.AddMinutes(-RateWindowMinutes);

        var recent = await db.ServiceRequests
            .CountAsync(r => r.TabId == tabId && r.CreatedAtUtc >= since, cancellationToken);

        if (recent >= RateLimit)
        {
            logger.LogInformation(
                "Tab {TabId} has raised {Recent} service requests in {WindowMinutes} minutes; refusing.",
                tabId, recent, RateWindowMinutes);

            throw new ServiceRequestRateLimitedException(tabId, RateLimit, RateWindowMinutes);
        }

        var bill = billScope is { } scope
            ? await QuoteAsync(tab, participant, scope, tipPercent, cancellationToken)
            : null;

        var request = new ServiceRequest(
            tab.Id, tab.BranchId, tab.DiningTableId, participant.Id, preset, nowUtc, note, bill);

        db.ServiceRequests.Add(request);

        // On the floor's list: the tab's stream hears it and the floor's phones buzz (ServiceRequestRaised).
        request.Send();

        if (bill is not null)
        {
            await SupersedeEarlierBillRequestsAsync(tab.Id, participant.Id, nowUtc, cancellationToken);
        }

        await ledger.SaveAppendedAsync(cancellationToken);

        logger.LogInformation(
            "Table {TableLabel} at branch {BranchId} asked for {Preset}.",
            tab.DiningTable.Label, tab.BranchId, preset);

        return ToView(
            request,
            tab.DiningTable.Label,
            nowUtc,
            await ledger.MaxSequenceAsync(tab.Id, cancellationToken));
    }

    public async Task<IReadOnlyList<ServiceRequestView>> GetOpenAsync(
        Guid branchId,
        CancellationToken cancellationToken = default)
    {
        RequireStaff("Read service requests");

        var nowUtc = clock.UtcNow;

        // Newest first: on a busy floor the screen is glanced at, not read, and the thing that just
        // came in is the thing nobody has walked to yet. A request for the bill the diner has since
        // replaced with another is not outstanding - the newer one is. A claimed one stays, with who has it.
        var rows = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.BranchId == branchId && r.AcknowledgedAtUtc == null && r.SupersededAtUtc == null)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => new
            {
                Request = r,
                TableLabel = r.DiningTable.Label,
                ClaimedBy = r.ClaimedByStaff != null ? r.ClaimedByStaff.FullName : null,
            })
            .ToListAsync(cancellationToken);

        // Zero rather than a per-row query. This list spans every tab in the branch, so there is no
        // single stream to be caught up with - and it is a staff screen, which follows the floor
        // rather than one tab's events.
        return [.. rows.Select(row => ToView(row.Request, row.TableLabel, nowUtc, 0L, claimedBy: row.ClaimedBy))];
    }

    public async Task<ServiceRequestView> ClaimAsync(
        Guid branchId,
        Guid serviceRequestId,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Claim a call");

        // Under the request's row lock, read inside it: the first claim stands even when two waiters tap at the same
        // instant. Without it both read "unclaimed", both write, the later one overwrites the first and the History
        // gets two claims of one call.
        await using var transaction = await LockRequestAsync(serviceRequestId, cancellationToken);

        var request = await db.ServiceRequests
            .Include(r => r.DiningTable)
            .FirstOrDefaultAsync(r => r.Id == serviceRequestId && r.BranchId == branchId, cancellationToken)
            ?? throw new KeyNotFoundException($"Call {serviceRequestId} was not found at this branch.");

        await branchGuard.RequireAsync(staffId, request.BranchId, "Claiming this call", "request", cancellationToken);

        var nowUtc = clock.UtcNow;

        // The first claim stands - two waiters tapping at once both see who has it. Nothing goes to the guest's phone:
        // a claim is the floor's, the acknowledgement is what the table hears about. The History's entry is the
        // claim's (ServiceRequestClaimed); saved through the ledger, which dispatches it.
        if (request.Claim(nowUtc, staffId))
        {
            await ledger.SaveAppendedAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await ViewAsync(request.Id, nowUtc, 0L, cancellationToken);
    }

    public async Task<ServiceRequestView> AcknowledgeAsync(
        Guid serviceRequestId,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Acknowledge a service request");

        // The same row lock as a claim: an acknowledgement that read "unclaimed" must not overwrite a claim that
        // landed in between, nor two acknowledgements both record the claim.
        await using var transaction = await LockRequestAsync(serviceRequestId, cancellationToken);

        var request = await db.ServiceRequests
            .Include(r => r.DiningTable)
            .FirstOrDefaultAsync(r => r.Id == serviceRequestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Service request {serviceRequestId} was not found.");

        await branchGuard.RequireAsync(
            staffId, request.BranchId, "Acknowledging this request", "request", cancellationToken);

        var nowUtc = clock.UtcNow;

        // Two waiters tapping the same request at once is the normal case, not a race worth
        // reporting: both saw it, which is what the table wanted. The first one stands - and when nobody
        // claimed it first, it is the claim too (docs/activity.md). The table's stream hears it
        // (ServiceRequestAcknowledged), and the claim it made is the History's (ServiceRequestClaimed).
        if (request.Acknowledge(nowUtc, staffId))
        {
            await ledger.SaveAppendedAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await ViewAsync(
            request.Id, nowUtc, await ledger.MaxSequenceAsync(request.TabId, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Opens a transaction holding the request's row with <c>UPDLOCK, HOLDLOCK</c> to its end, so a claim and an
    /// acknowledgement of the same call run one after the other and each reads what the one before wrote. Readers - the
    /// floor's open list - are not blocked.
    /// </summary>
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> LockRequestAsync(
        Guid serviceRequestId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT Id FROM ServiceRequests WITH (UPDLOCK, HOLDLOCK) WHERE Id = {serviceRequestId}",
                cancellationToken);

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>The request as it now stands, with who claimed and who acknowledged it. One statement.</summary>
    private async Task<ServiceRequestView> ViewAsync(Guid id, DateTime nowUtc, long sequence, CancellationToken cancellationToken)
    {
        var row = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new
            {
                Request = r,
                TableLabel = r.DiningTable.Label,
                ClaimedBy = r.ClaimedByStaff != null ? r.ClaimedByStaff.FullName : null,
                AcknowledgedBy = r.AcknowledgedByStaff != null ? r.AcknowledgedByStaff.FullName : null,
            })
            .FirstAsync(cancellationToken);

        return ToView(row.Request, row.TableLabel, nowUtc, sequence, row.AcknowledgedBy, row.ClaimedBy);
    }

    /// <summary>
    /// Closes the asker's own earlier requests for the bill on this tab that nobody has answered yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only when the new request says what to bring. A diner who asked for their share and then for
    /// the whole bill wants one bill brought, the latest; leaving both open sends the waiter over with
    /// a figure the table has moved on from, or twice. A plain "the bill, please" replaces nothing: it
    /// names no amount to bring instead.
    /// </para>
    /// <para>
    /// The asker's own, never a tablemate's: two people may well each want to pay their part. Saved
    /// with the new request, in its unit of work, so the list never shows both or neither.
    /// </para>
    /// </remarks>
    private async Task SupersedeEarlierBillRequestsAsync(
        Guid tabId,
        Guid participantId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var earlier = await db.ServiceRequests
            .Where(r => r.TabId == tabId
                        && r.RequestedByParticipantId == participantId
                        && r.Preset == ServiceRequestPreset.TheBill
                        && r.AcknowledgedAtUtc == null
                        && r.SupersededAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var request in earlier)
        {
            request.Supersede(nowUtc);
        }

        if (earlier.Count > 0)
        {
            logger.LogInformation(
                "Participant {ParticipantId} asked for the bill again on tab {TabId}; superseded {Count} earlier request(s).",
                participantId, tabId, earlier.Count);
        }
    }

    /// <summary>
    /// Prices a request for the bill against the tab as it stands, from its lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same <see cref="TabLedger.Compute(Domain.Tabs.Tab)"/> the shares view and the cash endpoint read, so the
    /// amount on the waiter's screen is the amount the diner's split showed and the amount the cash
    /// keypad will accept. Read-only: nothing on the tab row changes, so raising a request still
    /// cannot collide with a payment.
    /// </para>
    /// <para>
    /// <b>The whole bill needs sight of the total.</b> A guest the host has hidden the total from
    /// would otherwise read it back off their own request, which is the leak the projection exists to
    /// prevent - so they may ask for their own share, and only that. The same sight decides whether
    /// their share is capped at the tab's balance, for the same reason (<see cref="BillQuote.For"/>).
    /// </para>
    /// </remarks>
    private async Task<BillQuote> QuoteAsync(
        Tab tab,
        TabParticipant participant,
        BillScope scope,
        int? tipPercent,
        CancellationToken cancellationToken)
    {
        var canSeeTableTotal = TabPermissions.MaySeeTableTotal(participant.Status, participant.CanSeeTableTotal);

        if (scope == BillScope.WholeBill && !canSeeTableTotal)
        {
            throw new TabPermissionException(
                "Asking for the whole bill", "somebody who can see the table total");
        }

        var loaded = await ledger.LoadForWriteAsync(tab.Id, cancellationToken);

        return BillQuote.For(ledger.Compute(loaded), participant.Id, scope, tipPercent, canSeeTableTotal);
    }

    private static ServiceRequestView ToView(
        ServiceRequest request,
        string tableLabel,
        DateTime nowUtc,
        long tabEventSequence,
        string? acknowledgedBy = null,
        string? claimedBy = null) =>
        new(
            request.Id,
            request.TabId,
            tableLabel,
            request.Preset,
            request.Note,
            request.RequestedByParticipantId,
            request.CreatedAtUtc,
            (int)Math.Max(
                0d, ((request.AcknowledgedAtUtc ?? request.SupersededAtUtc ?? nowUtc) - request.CreatedAtUtc).TotalMinutes),
            request.AcknowledgedAtUtc,
            tabEventSequence,
            request.BillScope,
            request.BillAmountAmd,
            request.TipPercent,
            request.TipAmd,
            request.AcknowledgedByStaffId,
            request.AcknowledgedByStaffId is null ? null : acknowledgedBy,
            request.ClaimedAtUtc,
            request.ClaimedByStaffId,
            request.ClaimedByStaffId is null ? null : claimedBy);

    private Guid RequireStaff(string operation) =>
        actor.Type == ActorType.Staff && actor.StaffMemberId is { } id
            ? id
            : throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);

}
