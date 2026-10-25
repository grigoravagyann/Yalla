using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;
using Yalla.Domain.Venues;
using Yalla.Infrastructure.DomainEvents.TabOrders;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.DomainEvents.Tables;

/// <summary>
/// The History's line for what happens to a table on the floor (<c>docs/activity.md</c>): <c>table.seated</c> or
/// <c>booking.seated</c>, <c>table.freed</c>, <c>table.held</c>, <c>table.hold-released</c>, <c>table.out-of-service</c>,
/// <c>table.back-in-service</c>, <c>table.transferred</c>. The one place each is written; who acted and on which device
/// is the request's, read by the activity log.
/// </summary>
/// <remarks>
/// A hold the diner extended has no line here: the booking's own <c>booking.hold-extended</c> is the History's, written
/// with the booking.
/// </remarks>
internal sealed class TableActivity(YallaDbContext db, IActivityLog activity) :
    IDomainEventHandler<TableSeated>,
    IDomainEventHandler<TableFreed>,
    IDomainEventHandler<TableHeld>,
    IDomainEventHandler<TableHoldReleased>,
    IDomainEventHandler<TableTakenOutOfService>,
    IDomainEventHandler<TableReturnedToService>,
    IDomainEventHandler<TableTransferred>
{
    /// <summary>A party sat down: <c>booking.seated</c> for a booking, <c>table.seated</c> otherwise.</summary>
    public async Task HandleAsync(TableSeated domainEvent, CancellationToken cancellationToken)
    {
        var (table, session, reservation) = (domainEvent.Table, domainEvent.Sitting, domainEvent.Booking);
        var branch = await TableReads.BranchAsync(db, table, cancellationToken);

        var source = domainEvent.How switch
        {
            TableSeating.WalkIn => "walk-in",
            TableSeating.QrScan => "scan",
            TableSeating.HeldParty when reservation is null => "held",
            _ => "booking",
        };

        await activity.RecordAsync(
            new ActivityRecord(
                reservation is null ? ActivityCategory.Table : ActivityCategory.Booking,
                reservation is null ? ActivityActions.TableSeated : ActivityActions.BookingSeated,
                reservation is null ? ActivitySubjects.TableSession : ActivitySubjects.Reservation,
                reservation?.Id ?? session.Id)
            {
                BranchId = table.BranchId,
                VenueId = branch.VenueId,
                TableSessionId = session.Id,
                TableLabel = table.Label,
                GuestName = reservation?.GuestName,
                SubjectCode = reservation?.Code,
                Summary = new
                {
                    tableId = table.Id,
                    partySize = session.PartySize,
                    source,
                    reservationId = reservation?.Id,
                },
            },
            cancellationToken);
    }

    /// <summary>The table freed: the sitting it ended (or the table, when it had none), its tab, and what was left owing.</summary>
    public async Task HandleAsync(TableFreed domainEvent, CancellationToken cancellationToken)
    {
        var table = domainEvent.Table;
        var branch = await TableReads.BranchAsync(db, table, cancellationToken);
        var sittingId = domainEvent.SittingId;

        await activity.RecordAsync(
            new ActivityRecord(
                ActivityCategory.Table,
                ActivityActions.TableFreed,
                sittingId is null ? ActivitySubjects.Table : ActivitySubjects.TableSession,
                sittingId ?? table.Id)
            {
                BranchId = table.BranchId,
                VenueId = branch.VenueId,
                TableSessionId = sittingId,
                TableLabel = table.Label,
                AmountAmd = domainEvent.OutstandingAmd,
                Reason = domainEvent.Reason,
                Summary = new
                {
                    tableId = table.Id,
                    tabId = domainEvent.TabId,
                    reservationId = domainEvent.ReservationId,
                    outstandingAmd = domainEvent.OutstandingAmd,
                },
            },
            cancellationToken);
    }

    public Task HandleAsync(TableHeld domainEvent, CancellationToken cancellationToken) =>
        TableAsync(
            domainEvent.Table, ActivityActions.TableHeld, domainEvent.Reason, new { reservationId = domainEvent.ForReservationId },
            cancellationToken);

    public Task HandleAsync(TableHoldReleased domainEvent, CancellationToken cancellationToken) =>
        TableAsync(domainEvent.Table, ActivityActions.TableHoldReleased, domainEvent.Reason, null, cancellationToken);

    public Task HandleAsync(TableTakenOutOfService domainEvent, CancellationToken cancellationToken) =>
        TableAsync(
            domainEvent.Table, ActivityActions.TableOutOfService, domainEvent.Reason,
            new { strandedBookings = domainEvent.StrandedBookings }, cancellationToken);

    public Task HandleAsync(TableReturnedToService domainEvent, CancellationToken cancellationToken) =>
        TableAsync(domainEvent.Table, ActivityActions.TableBackInService, domainEvent.Reason, null, cancellationToken);

    /// <summary>The table handed over: who had it and who has it now, on the tab it was asked through.</summary>
    public async Task HandleAsync(TableTransferred domainEvent, CancellationToken cancellationToken)
    {
        var tab = await TableReads.TabAsync(db, domainEvent.TabId, cancellationToken);
        var fromId = domainEvent.FromStaffId;
        var fromName = fromId is { } from ? await TableReads.StaffNameAsync(db, from, cancellationToken) : null;
        var toName = await TableReads.StaffNameAsync(db, domainEvent.ToStaffId, cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Table, ActivityActions.TableTransferred, ActivitySubjects.Tab, tab.Id)
            {
                BranchId = tab.BranchId,
                TableSessionId = domainEvent.Sitting.Id,
                TableLabel = await TabOrderReads.TableLabelAsync(db, tab, cancellationToken),
                Before = new { staffMemberId = fromId, name = fromName },
                After = new { staffMemberId = domainEvent.ToStaffId, name = toName },
                Summary = new
                {
                    fromStaffMemberId = fromId,
                    fromName,
                    toStaffMemberId = domainEvent.ToStaffId,
                    toName,
                },
            },
            cancellationToken);
    }

    /// <summary>Something done to a table that is not a seating: held, released, out of service, back.</summary>
    private async Task TableAsync(
        DiningTable table, string action, string? reason, object? summary, CancellationToken cancellationToken)
    {
        var branch = await TableReads.BranchAsync(db, table, cancellationToken);

        await activity.RecordAsync(
            new ActivityRecord(ActivityCategory.Table, action, ActivitySubjects.Table, table.Id)
            {
                BranchId = table.BranchId,
                VenueId = branch.VenueId,
                TableLabel = table.Label,
                Reason = reason,
                Summary = summary,
            },
            cancellationToken);
    }
}
