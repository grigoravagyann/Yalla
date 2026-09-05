using Yalla.Application.Tables;

namespace Yalla.Application.Abstractions;

/// <summary>
/// The table state machine: one named method per legal transition.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>SetStatus(status)</c>. Named transitions are what keep illegal states out, and
/// they are also what makes the audit log readable - each one knows its own reason, its own
/// related booking or tab, and what warning is worth raising.
/// </para>
/// <para>
/// Every method is idempotent on <c>ClientCommandId</c>: a replayed offline command returns the
/// original result with <c>WasReplay</c> set, and writes nothing.
/// </para>
/// <para>
/// Every method performs its table update and its audit row in a single <c>SaveChanges</c>, so a
/// state change cannot exist without the row that records it. A lost race raises
/// <c>TableStateConflictException</c> and is never retried automatically - a human has to see the
/// new state and decide.
/// </para>
/// </remarks>
public interface ITableStateService
{
    /// <summary>Free to Occupied, for a party with no booking.</summary>
    Task<TableStateChangeResult> SeatWalkInAsync(SeatWalkInCommand command, CancellationToken cancellationToken = default);

    /// <summary>Free to Occupied, against a booking, which also moves the booking to Seated.</summary>
    Task<TableStateChangeResult> SeatReservationAsync(SeatReservationCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Free to Occupied, for the party that scanned the table's QR code. The one transition that
    /// needs no staff member: the actor is whoever the token says, or the system when the scanner
    /// has no account yet, and the audit row says so.
    /// </summary>
    Task<TableStateChangeResult> SeatQrScanAsync(SeatQrScanCommand command, CancellationToken cancellationToken = default);

    /// <summary>Free to Held, keeping the table for a party expected imminently.</summary>
    Task<TableStateChangeResult> HoldForLatePartyAsync(TableStateCommand command, CancellationToken cancellationToken = default);

    /// <summary>Held to Free, giving up on the party the hold was for.</summary>
    Task<TableStateChangeResult> ReleaseHoldAsync(TableStateCommand command, CancellationToken cancellationToken = default);

    /// <summary>Held to Occupied, seating the party the hold was placed for.</summary>
    Task<TableStateChangeResult> SeatHeldPartyAsync(SeatHeldPartyCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Occupied to Free. Closes the occupancy, and closes the tab when nothing is owed. A
    /// positive balance does not block the table - the diners have physically left.
    /// </summary>
    Task<TableStateChangeResult> FreeTableAsync(TableStateCommand command, CancellationToken cancellationToken = default);

    /// <summary>Free or Held to OutOfService. Refuses an occupied table: free it first.</summary>
    Task<TableStateChangeResult> MarkOutOfServiceAsync(TableStateCommand command, CancellationToken cancellationToken = default);

    /// <summary>OutOfService to Free.</summary>
    Task<TableStateChangeResult> ReturnToServiceAsync(TableStateCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes off an outstanding balance on a tab whose diners have gone. <b>Manager-only</b> -
    /// this is a decision about money, unlike every transition above, which any waiter may make.
    /// </summary>
    Task<TabAbandonResult> AbandonTabAsync(AbandonTabCommand command, CancellationToken cancellationToken = default);
}
