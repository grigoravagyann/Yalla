# Booking by time only

Added 2026-10-29. A branch can let diners book by **time and party size alone**: no room photo, no table
pins, no table numbers. The venue chooses the table when the party arrives.

## The setting

`ReservationPolicy.BookingMode` on `GET`/`PUT /api/branches/{id}/reservation-policy`:

| Value | Name | What a diner does |
|---|---|---|
| 1 | `ChooseTable` | Picks a table on a room photo (every branch before this, and the default). |
| 2 | `TimeOnly` | Picks a time and how many, and may add a wish in `note` ("by the window", "smoking room"). |

`TimeOnly` needs `guestsAtOnce` (1 to 2000): the most booked guests the branch takes **at the same moment**.
It is counted over each booking's sitting (start plus the turn time), so two parties who never overlap do not
add up; a new booking fits when the busiest moment inside its own sitting still has room for the party
(`GuestsAtOnce` in the domain). A `PUT` that sends neither field keeps what the branch has; one that sends the
mode sends its capacity with it.

## Booking

`POST /api/reservations` and `POST /api/public/branches/{id}/reservations` take **no `tableId`** at such a branch
(naming one is `422 validation-failed` on `tableId`), and need one at a table branch (the same refusal). The
insert runs under the branch row's own lock: the account, the replay, the guest's no-show block, their own
overlapping booking, the room's guests at once and the three-active-bookings limit. Too many guests is
`409 time-full` with `context.guestsLeft`.

The booking is `bookedWithoutTable: true` with no `tableId` / `tableLabel` until it is seated. It is confirmed
or waits for approval by the same policy rules as any booking. It cannot be moved (cancel and book again) and
takes no pre-order: a pre-order's ticket waits at a table.

The floor lists the ones not yet seated at `GET /api/branches/{id}/reservations/without-table` (waiter or above), and releasing one (`/release`) works with no table to free.

`GET /api/branches/{id}/availability/slots` answers such a branch in guests: each slot's `guestsLeft`, and
`isAvailable` when the party fits (`unavailableReason` 16 `TimeFull` when not). Its public detail and page carry
`bookingMode` so a client knows which flow to show.

## Seating

`POST /api/branches/{id}/tables/{tableId}/seat-reservation` with the booking's id seats it **at the table the
waiter chose** and gives the booking that table from then on (`Reservation.AssignTable`). Opening a tab from the
booking code before then is refused: there is no table yet.

## Taking guests

The readiness checklist's table line (see `DinerVisibility`) is satisfied by `TimeOnly` with `guestsAtOnce` set,
instead of a bookable pinned table: `booksByTimeOnly` on the readiness read.
