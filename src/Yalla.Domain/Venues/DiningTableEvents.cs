using Yalla.Domain.Common;
using Yalla.Domain.Enums;
using Yalla.Domain.Occupancy;

namespace Yalla.Domain.Venues;

// What happens to a table on the floor, as its handlers hear it (docs/domain-events.md): seated, freed, held, the hold
// given up or kept for longer, out of service and back. Each carries the table itself - a handler reads its label, its
// branch and the status the change left it in - and what the change stream's row and the History's line need that the
// table cannot say afterwards: the status it came from, the reason as the caller gave it (null when none: the row then
// says what happened in its own words) and the client's command id, which the row is the idempotency ledger for.
// Who did it is the request's, read by the handlers; an event does not carry the actor.

/// <summary>How a party came to sit at a table: what the change stream's row and the History's line say about it.</summary>
public enum TableSeating
{
    /// <summary>A waiter sat a party with no booking.</summary>
    WalkIn = 1,

    /// <summary>A phone scanned the table's QR code and the scan seated it, nobody behind it yet.</summary>
    QrScan = 2,

    /// <summary>A waiter seated a booked party.</summary>
    Booking = 3,

    /// <summary>The booked party seated themselves from the diner's own phone (opening the tab from the booking).</summary>
    BookingByDiner = 4,

    /// <summary>A waiter seated the party a hold was placed for - a late booking, or somebody who phoned.</summary>
    HeldParty = 5,
}

/// <summary>A party sat down: the table is Occupied by a new sitting.</summary>
/// <param name="Table">The table, Occupied.</param>
/// <param name="Sitting">The sitting this seating opened.</param>
/// <param name="From">Free or Held.</param>
/// <param name="How">Who seated whom.</param>
/// <param name="Booking">The booking honoured, Seated now; null for a walk-in, a scan, or a held party nobody booked.</param>
/// <param name="Reason">The reason as given, or null.</param>
/// <param name="ClientCommandId">The command's id.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableSeated(
    DiningTable Table,
    TableSession Sitting,
    TableStatus From,
    TableSeating How,
    Reservation? Booking,
    string? Reason,
    Guid ClientCommandId,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The party left and the table is Free. Its sitting is closed, its booking completed, and its tab closed when nothing
/// was owed - or left open with what is, the diners having gone.
/// </summary>
/// <remarks>
/// A zero-balance tab closes with the table and raises nothing of its own: this event names it (<see cref="TabId"/>,
/// <see cref="TabClosed"/>), and freeing the table has never told the tab's stream anything (a bill settled in full is
/// <c>TabSettled</c>, on the payment that settled it).
/// </remarks>
/// <param name="Table">The table, Free.</param>
/// <param name="From">Occupied.</param>
/// <param name="SittingId">The sitting it ended: the open one, or the one a settled bill had already closed; null when the table had none.</param>
/// <param name="ReservationId">The sitting's booking, if it had one.</param>
/// <param name="TabId">The sitting's open tab, if it had one.</param>
/// <param name="TabClosed">The tab closed with the table: nothing was owed on it.</param>
/// <param name="OutstandingAmd">What is still owed on the tab, left open; null when nothing is.</param>
/// <param name="Reason">The reason as given, or null.</param>
/// <param name="ClientCommandId">The command's id.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableFreed(
    DiningTable Table,
    TableStatus From,
    Guid? SittingId,
    Guid? ReservationId,
    Guid? TabId,
    bool TabClosed,
    long? OutstandingAmd,
    string? Reason,
    Guid ClientCommandId,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The table is Held for a party expected any minute.</summary>
/// <param name="Table">The table, Held.</param>
/// <param name="From">Free.</param>
/// <param name="ForReservationId">The table's next booking when it was held - who the hold is presumably for; null when none.</param>
/// <param name="Reason">The reason as given, or null.</param>
/// <param name="ClientCommandId">The command's id.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableHeld(
    DiningTable Table,
    TableStatus From,
    Guid? ForReservationId,
    string? Reason,
    Guid ClientCommandId,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>A hold was given up: the table is Free again.</summary>
/// <param name="Table">The table, Free.</param>
/// <param name="From">Held.</param>
/// <param name="Reason">The reason as given, or null.</param>
/// <param name="ClientCommandId">The command's id.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableHoldReleased(
    DiningTable Table, TableStatus From, string? Reason, Guid ClientCommandId, DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The diner whose booking the table is held for kept it for longer. Nothing about the table changed - something
/// happened at it, and every tablet following the branch's change stream should hear it.
/// </summary>
/// <param name="Table">The table, as it was.</param>
/// <param name="ReservationId">The booking whose hold was extended.</param>
/// <param name="HoldExpiresAtUtc">When the hold now ends.</param>
/// <param name="Reason">The reason as given, or null.</param>
/// <param name="ClientCommandId">The command's id.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableHoldExtended(
    DiningTable Table,
    Guid ReservationId,
    DateTime HoldExpiresAtUtc,
    string? Reason,
    Guid ClientCommandId,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The table was taken out of service - a broken chair, a spill. Bookings still to come on it were not touched.</summary>
/// <param name="Table">The table, OutOfService.</param>
/// <param name="From">Free or Held.</param>
/// <param name="StrandedBookings">How many bookings it still has to come, for a waiter to telephone.</param>
/// <param name="Reason">The reason as given, or null.</param>
/// <param name="ClientCommandId">The command's id.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableTakenOutOfService(
    DiningTable Table,
    TableStatus From,
    int StrandedBookings,
    string? Reason,
    Guid ClientCommandId,
    DateTime OccurredAtUtc) : IDomainEvent;

/// <summary>The table is back in service, Free.</summary>
/// <param name="Table">The table, Free.</param>
/// <param name="From">OutOfService.</param>
/// <param name="Reason">The reason as given, or null.</param>
/// <param name="ClientCommandId">The command's id.</param>
/// <param name="OccurredAtUtc">When.</param>
public sealed record TableReturnedToService(
    DiningTable Table, TableStatus From, string? Reason, Guid ClientCommandId, DateTime OccurredAtUtc) : IDomainEvent;
