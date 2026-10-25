using Yalla.Application.Abstractions;
using Yalla.Domain.Enums;
using Yalla.Domain.Tabs;
using Yalla.Infrastructure.Services;

namespace Yalla.Infrastructure.DomainEvents.Tabs;

/// <summary>
/// What every phone at the table, following the tab's stream, learns about the tab itself (<c>docs/tabs.md</c>): it was
/// let in (<c>TabAdmitted</c>) or turned away (<c>TabAdmissionRejected</c>), the roster moved
/// (<c>ParticipantJoined</c>, <c>ParticipantApproved</c>, <c>ParticipantRejected</c>, <c>ParticipantRemoved</c>,
/// <c>ParticipantPermissionsChanged</c>, <c>ParticipantRenamed</c>), the host moved (<c>HostReassigned</c>), the split
/// changed (<c>SettlementModeChanged</c>), the bill was presented (<c>TabClosing</c>).
/// </summary>
/// <remarks>
/// Appended through the ledger, which raises the unit of work's events before it numbers the stream - so these take
/// their places in the order the events were raised: a booker let onto a waiting tab reads <c>TabAdmitted</c>, then
/// <c>ParticipantApproved</c>; a host leaving reads <c>HostReassigned</c>, then <c>ParticipantRemoved</c>. The ledger
/// reads who acted from the request. The roster events share one shape, so the redactor's participant rule decides
/// them all: a pending reader sees who it is about and nothing that says who they are.
/// </remarks>
internal sealed class TabLifecycleStream(TabLedger ledger) :
    IDomainEventHandler<TabAdmitted>,
    IDomainEventHandler<TabTurnedAway>,
    IDomainEventHandler<TabJoinRequested>,
    IDomainEventHandler<TabParticipantApproved>,
    IDomainEventHandler<TabParticipantRejected>,
    IDomainEventHandler<TabParticipantRemoved>,
    IDomainEventHandler<TabParticipantPermissionsChanged>,
    IDomainEventHandler<TabParticipantRenamed>,
    IDomainEventHandler<TabHostMoved>,
    IDomainEventHandler<TabSettlementModeChanged>,
    IDomainEventHandler<TabBillPresented>
{
    public Task HandleAsync(TabAdmitted domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Tab.Id, TabEventType.TabAdmitted, new
        {
            admittedBecause = (int)domainEvent.Because,
            byStaffId = domainEvent.ByStaffId,
            atUtc = domainEvent.OccurredAtUtc,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabTurnedAway domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Tab.Id, TabEventType.TabAdmissionRejected, new
        {
            byStaffId = domainEvent.ByStaffId,
            atUtc = domainEvent.OccurredAtUtc,
            tableFreed = domainEvent.TableFreed,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabJoinRequested domainEvent, CancellationToken cancellationToken) =>
        Roster(domainEvent.Tab, TabEventType.ParticipantJoined, domainEvent.Participant);

    public Task HandleAsync(TabParticipantApproved domainEvent, CancellationToken cancellationToken) =>
        Roster(domainEvent.Tab, TabEventType.ParticipantApproved, domainEvent.Participant);

    public Task HandleAsync(TabParticipantRejected domainEvent, CancellationToken cancellationToken) =>
        Roster(domainEvent.Tab, TabEventType.ParticipantRejected, domainEvent.Participant);

    public Task HandleAsync(TabParticipantRemoved domainEvent, CancellationToken cancellationToken)
    {
        if (!domainEvent.Left)
        {
            return Roster(domainEvent.Tab, TabEventType.ParticipantRemoved, domainEvent.Participant);
        }

        var leaving = domainEvent.Participant;

        // The roster event every phone at the table already handles, marked as their own doing.
        ledger.Append(domainEvent.Tab.Id, TabEventType.ParticipantRemoved, new
        {
            participantId = leaving.Id,
            displayName = leaving.DisplayName,
            status = (int)leaving.Status,
            canOrder = leaving.CanOrder,
            canSeeTableTotal = leaving.CanSeeTableTotal,
            canPay = leaving.CanPay,
            left = true,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabParticipantPermissionsChanged domainEvent, CancellationToken cancellationToken) =>
        Roster(domainEvent.Tab, TabEventType.ParticipantPermissionsChanged, domainEvent.Participant);

    public Task HandleAsync(TabParticipantRenamed domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Tab.Id, TabEventType.ParticipantRenamed, new
        {
            participantId = domainEvent.Participant.Id,
            displayName = domainEvent.Participant.DisplayName,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabHostMoved domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Tab.Id, TabEventType.HostReassigned, new
        {
            fromParticipantId = domainEvent.From?.Id,
            toParticipantId = domainEvent.To.Id,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabSettlementModeChanged domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Tab.Id, TabEventType.SettlementModeChanged, new
        {
            settlementMode = (int)domainEvent.Tab.SettlementMode,
            lockedAtUtc = domainEvent.Tab.SettlementModeLockedAtUtc,
        });

        return Task.CompletedTask;
    }

    public Task HandleAsync(TabBillPresented domainEvent, CancellationToken cancellationToken)
    {
        ledger.Append(domainEvent.Tab.Id, TabEventType.TabClosing, new { atUtc = domainEvent.OccurredAtUtc });

        return Task.CompletedTask;
    }

    /// <summary>One participant as every roster event carries them, written in the same save as the change.</summary>
    private Task Roster(Tab tab, TabEventType type, TabParticipant participant)
    {
        ledger.Append(tab.Id, type, new
        {
            participantId = participant.Id,
            displayName = participant.DisplayName,
            status = (int)participant.Status,
            canOrder = participant.CanOrder,
            canSeeTableTotal = participant.CanSeeTableTotal,
            canPay = participant.CanPay,
        });

        return Task.CompletedTask;
    }
}
