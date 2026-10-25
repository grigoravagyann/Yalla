using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yalla.Application.Abstractions;
using Yalla.Application.Activity;
using Yalla.Application.Auth;
using Yalla.Application.Messaging;
using Yalla.Application.Reservations;
using Yalla.Domain.Enums;
using Yalla.Infrastructure.Persistence;

namespace Yalla.Infrastructure.Services;

/// <summary>
/// Creating, cancelling and deciding bookings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where each command lives.</b> This is the one <see cref="IReservationService"/> the endpoints see.
/// Each use case is a class of its own over the same unit of work - <see cref="ReservationCreation"/>,
/// <see cref="ReservationChanges"/>, <see cref="ReservationAnswers"/>, <see cref="ReservationCancellation"/>,
/// <see cref="ReservationDecisions"/>, <see cref="ReservationRelease"/> and <see cref="ReservationReads"/> -
/// beside the parts they share: <see cref="ReservationLookups"/>, <see cref="ReservationSlotRules"/>,
/// <see cref="ReservationViews"/>, <see cref="ReservationReminders"/> and <see cref="ReservationNotices"/>.
/// They are composed here rather than registered one by one, so this constructor stays the service's whole
/// list of dependencies.
/// </para>
/// <para>
/// <b>Why this locks and the table state machine does not.</b> Two waiters seating a walk-in both
/// update the <i>same</i> <c>DiningTables</c> row, so its <c>RowVersion</c> catches the race with
/// no locks at all. Two diners booking the same table insert <i>different</i> <c>Reservations</c>
/// rows: nothing they touch collides, so optimistic concurrency sees no conflict and both commit.
/// The table is then double-booked and the system does not know it.
/// </para>
/// <para>
/// Touching the parent table row to force a version clash would fix that and break something
/// worse: two bookings for the same table at <i>different</i> times would also collide, so the
/// most popular tables in the room would start refusing perfectly good bookings, at the busiest
/// moment, for no reason a diner could understand.
/// </para>
/// <para>
/// So bookers are serialised per table instead. Take an update lock on the table row, re-check for
/// overlaps <b>inside</b> the lock, insert, commit. A second booker waits, re-reads, and then
/// either succeeds - different slot - or is told the table went. Both are the right answer.
/// </para>
/// <para>
/// The locked span is deliberately tiny: one indexed range read and one insert. No availability
/// snapshot, no push notification, no email happens inside it. The 409's availability payload is
/// built after the transaction has already rolled back.
/// </para>
/// <para>
/// <b>Which commands take the table's write lock.</b> A list rather than a rule, because the rule
/// has been stated twice and been wrong twice.
/// </para>
/// <list type="table">
/// <listheader><term>Command</term><description>Locks, and why</description></listheader>
/// <item>
/// <term>Create a booking</term>
/// <description><b>Yes.</b> Two bookers insert different rows and collide on nothing, so optimistic
/// concurrency sees no conflict and both commit.</description>
/// </item>
/// <item>
/// <term>Move a booking (reschedule)</term>
/// <description><b>Yes, the target table.</b> The moved booking updates its own row and the booking it
/// now overlaps is a different one, so no row version sees the clash. Leaving the old table only frees
/// it, which narrows nothing.</description>
/// </item>
/// <item>
/// <term>Seat (walk-in, QR, reservation, held party)</term>
/// <description><b>Yes.</b> Booking's re-check reads <c>TableSessions</c>; a seating that skipped the
/// lock could commit between that read and the booking's commit, and both would succeed.</description>
/// </item>
/// <item>
/// <term>Mark out of service</term>
/// <description><b>Yes.</b> A booking validated while the table was <c>Free</c> and committing after
/// this leaves a confirmed reservation on a broken table. Prompt 7 grouped this with the commands
/// below and that was wrong: it is not a narrower answer, it is a wrong one. Rare enough that the
/// throughput argument does not apply.</description>
/// </item>
/// <item>
/// <term>Free, hold, release a hold, return to service</term>
/// <description><b>No.</b> These only ever <i>narrow</i> what a booking finds - a booking that saw a
/// sitting about to close refuses a slot that would have been fine, which is a worse answer and never
/// a wrong one. They are also the floor's whole write traffic, and putting that through one queue per
/// table buys nothing.</description>
/// </item>
/// </list>
/// </remarks>
internal sealed class ReservationService : IReservationService
{
    private readonly ReservationCreation creation;
    private readonly ReservationChanges changes;
    private readonly ReservationAnswers answers;
    private readonly ReservationCancellation cancellation;
    private readonly ReservationDecisions decisions;
    private readonly ReservationReads reads;
    private readonly ReservationRelease release;

    public ReservationService(
        YallaDbContext db,
        IClock clock,
        ICurrentActor actor,
        IAvailabilityQuery availabilityQuery,
        IAuthorizationQueries authorization,
        IStaffBranchGuard branchGuard,
        NoShowPolicy noShowPolicy,
        ReservationWriter reservations,
        ITableStateService tableState,
        NoShowBlocks noShowBlocks,
        IOutbox outbox,
        IOptions<PublicWebOptions> publicWeb,
        IActivityLog activity,
        ILogger<ReservationService> logger)
    {
        var lookups = new ReservationLookups(db, clock, actor, authorization);
        var views = new ReservationViews(db, clock, actor, lookups);
        var slots = new ReservationSlotRules(db, availabilityQuery, noShowPolicy, logger);
        var reminders = new ReservationReminders(db, clock, outbox, publicWeb);

        creation = new ReservationCreation(db, clock, reservations, logger, lookups, slots, views, reminders);
        changes = new ReservationChanges(db, clock, reservations, outbox, activity, logger, lookups, slots, views);
        answers = new ReservationAnswers(db, clock, reservations, logger, lookups, views);
        cancellation = new ReservationCancellation(db, clock, reservations, outbox, activity, logger, lookups, views);
        decisions = new ReservationDecisions(db, clock, actor, reservations, logger, lookups, views);
        reads = new ReservationReads(db, clock, actor, branchGuard, lookups);
        release = new ReservationRelease(
            db, clock, actor, reservations, tableState, noShowBlocks, outbox, activity, logger, lookups, views);
    }

    // ---------------------------------------------------------------- booking

    public Task<ReservationView> CreateAsync(
        CreateReservationCommand command,
        CancellationToken cancellationToken = default) =>
        creation.CreateAsync(command, cancellationToken);

    // ---------------------------------------------------------------- the diner moves their booking

    public Task<ReservationView> RescheduleAsync(
        RescheduleReservationCommand command,
        CancellationToken cancellationToken = default) =>
        changes.RescheduleAsync(command, cancellationToken);

    public Task<ReservationView> RescheduleByManageTokenAsync(
        string manageToken,
        RescheduleByManageTokenCommand command,
        CancellationToken cancellationToken = default) =>
        changes.RescheduleByManageTokenAsync(manageToken, command, cancellationToken);

    // ---------------------------------------------------------------- the earlier guest answers

    public Task<ReservationView> AnswerTableRequestAsync(
        AnswerTableRequestCommand command,
        CancellationToken cancellationToken = default) =>
        answers.AnswerTableRequestAsync(command, cancellationToken);

    // ---------------------------------------------------------------- cancelling

    public Task<ReservationView> CancelAsync(
        CancelReservationCommand command,
        CancellationToken cancellationToken = default) =>
        cancellation.CancelAsync(command, cancellationToken);

    public Task<ReservationView> CancelByManageTokenAsync(
        string manageToken,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        cancellation.CancelByManageTokenAsync(manageToken, reason, cancellationToken);

    // ---------------------------------------------------------------- the floor decides

    public Task<ReservationView> ApproveAsync(
        DecideReservationCommand command,
        CancellationToken cancellationToken = default) =>
        decisions.ApproveAsync(command, cancellationToken);

    public Task<ReservationView> RejectAsync(
        DecideReservationCommand command,
        CancellationToken cancellationToken = default) =>
        decisions.RejectAsync(command, cancellationToken);

    // ---------------------------------------------------------------- reads

    public Task<MyReservations> GetMineAsync(CancellationToken cancellationToken = default) =>
        reads.GetMineAsync(cancellationToken);

    public Task<IReadOnlyList<ReservationView>> ListForBranchAsync(
        Guid branchId,
        ReservationStatus status,
        CancellationToken cancellationToken = default) =>
        reads.ListForBranchAsync(branchId, status, cancellationToken);

    public Task<IReadOnlyList<AwaitingApproval>> ListAwaitingApprovalAsync(
        Guid branchId,
        CancellationToken cancellationToken = default) =>
        reads.ListAwaitingApprovalAsync(branchId, cancellationToken);

    // ---------------------------------------------------------------- a late booking

    public Task<ExtendHoldResult> ExtendHoldAsync(
        ExtendHoldCommand command,
        CancellationToken cancellationToken = default) =>
        release.ExtendHoldAsync(command, cancellationToken);

    public Task<ReservationReleaseResult> ReleaseAsync(
        ReleaseReservationCommand command,
        CancellationToken cancellationToken = default) =>
        release.ReleaseAsync(command, cancellationToken);

    public Task<ReservationView> UndoNoShowAsync(
        UndoNoShowCommand command,
        CancellationToken cancellationToken = default) =>
        release.UndoNoShowAsync(command, cancellationToken);
}
