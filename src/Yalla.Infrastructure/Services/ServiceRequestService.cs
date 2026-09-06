using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yalla.Application.Abstractions;
using Yalla.Application.Ordering;
using Yalla.Application.Tabs;
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
/// </remarks>
internal sealed class ServiceRequestService(
    YallaDbContext db,
    IClock clock,
    ICurrentActor actor,
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
        CancellationToken cancellationToken = default)
    {
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

        var request = new ServiceRequest(
            tab.Id, tab.BranchId, tab.DiningTableId, participant.Id, preset, nowUtc, note);

        db.ServiceRequests.Add(request);

        ledger.Append(tab.Id, TabEventType.ServiceRequested, new
        {
            serviceRequestId = request.Id,
            preset = (int)request.Preset,
            note = request.Note,
            requestedByParticipantId = participant.Id,
        });

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
        // came in is the thing nobody has walked to yet.
        var rows = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.BranchId == branchId && r.AcknowledgedAtUtc == null)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Select(r => new { Request = r, TableLabel = r.DiningTable.Label })
            .ToListAsync(cancellationToken);

        // Zero rather than a per-row query. This list spans every tab in the branch, so there is no
        // single stream to be caught up with - and it is a staff screen, which follows the floor
        // rather than one tab's events.
        return [.. rows.Select(row => ToView(row.Request, row.TableLabel, nowUtc, tabEventSequence: 0L))];
    }

    public async Task<ServiceRequestView> AcknowledgeAsync(
        Guid serviceRequestId,
        CancellationToken cancellationToken = default)
    {
        var staffId = RequireStaff("Acknowledge a service request");

        var request = await db.ServiceRequests
            .Include(r => r.DiningTable)
            .FirstOrDefaultAsync(r => r.Id == serviceRequestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Service request {serviceRequestId} was not found.");

        var nowUtc = clock.UtcNow;

        // Two waiters tapping the same request at once is the normal case, not a race worth
        // reporting: both saw it, which is what the table wanted. The first one stands.
        if (request.Acknowledge(nowUtc, staffId))
        {
            ledger.Append(request.TabId, TabEventType.ServiceRequestAcknowledged, new
            {
                serviceRequestId = request.Id,
                byStaffId = staffId,
            });

            await ledger.SaveAppendedAsync(cancellationToken);
        }

        return ToView(
            request,
            request.DiningTable.Label,
            nowUtc,
            await ledger.MaxSequenceAsync(request.TabId, cancellationToken));
    }

    private static ServiceRequestView ToView(
        ServiceRequest request,
        string tableLabel,
        DateTime nowUtc,
        long tabEventSequence) =>
        new(
            request.Id,
            request.TabId,
            tableLabel,
            request.Preset,
            request.Note,
            request.RequestedByParticipantId,
            request.CreatedAtUtc,
            (int)Math.Max(0d, ((request.AcknowledgedAtUtc ?? nowUtc) - request.CreatedAtUtc).TotalMinutes),
            request.AcknowledgedAtUtc,
            tabEventSequence);

    private Guid RequireStaff(string operation) =>
        actor.Type == ActorType.Staff && actor.StaffMemberId is { } id
            ? id
            : throw new StaffPermissionException(operation, actor.Role, StaffRole.Waiter);
}
