# Reservations — the conflict rule, and why this one locks

Two bookings for the same table at overlapping times must be impossible. This document is the
argument for how that is achieved, because the reasoning is not obvious and the wrong version of it
looks identical until a Friday night.

---

## 1. A reservation is an interval

`EndUtc = StartUtc + TurnTimeMinutes`, fixed at creation from the branch's own
`ReservationPolicy`. The diner is never asked how long they intend to stay.

The branch's `BufferMinutes` is **clearing time between sittings**. It is applied when *checking*
for conflicts and is deliberately **not** baked into `EndUtc`, so:

- the stored interval stays the one the diner booked and sees on their confirmation, and
- an owner can change the turnaround tomorrow without rewriting history.

### A sitting is an interval too

A booking says when it starts and the policy says how long it lasts. A **`TableSession`** — the party
physically at the table — says when it started and *nothing at all* about when it ends, because
nobody knows. It closes when a waiter taps Free, which may be twenty minutes later or ninety.

An open sitting is therefore projected forward by the branch's **current** turn time, in
`SessionOccupancy.ProjectedInterval`:

```
sittingStart = SeatedAtUtc
sittingEnd   = SeatedAtUtc + TurnTimeMinutes   (an estimate, not a fact)
```

Two consequences follow, and both are deliberate:

- **The projection is re-derived on every read, never stored.** A manager who shortens the turn time
  at four o'clock changes what every table in the room is projected to do at five. A stored end would
  have frozen yesterday's policy onto today's sittings.
- **The estimate is stated as an estimate.** `TableCurrentlyOccupiedException` ends with *"They may
  leave sooner — the finish time is an estimate from the branch's turn time."* A refusal whose
  reasoning a diner cannot see reads as a broken system rather than a full table.

Until Prompt 7 the conflict rule could not see sittings at all: a waiter seating a walk-in at seven
did not stop a phone booking the same table for eight, and the diner arrived to find it still
occupied. Walk-ins are most of a cafe's traffic, so this was the common case, not the edge one.

## 2. The rule

Two bookings on one table conflict when:

```
newStart < existingEnd + buffer   AND   existingStart < newEnd + buffer
```

Both comparisons are **strict**, and that is the whole rule. It lives in exactly one place —
`ReservationOverlap.Conflicts` in `Yalla.Domain.Occupancy` — as a pure function with no database
anywhere near it, so it can be tested to the minute on both sides of every edge.

Only a booking in a **live** status blocks anything (`ReservationOverlap.Blocks`):

| Blocks | Does not block |
| --- | --- |
| `PendingApproval`, `Confirmed`, `Seated` | `Completed`, `CancelledByDiner`, `CancelledByVenue`, `NoShow` |

`PendingApproval` blocks on purpose. Offering the slot to somebody else while a manager decides is
how one of the two parties gets turned away at the door.

### The boundaries

Existing booking **18:00–19:30**, clearing time **15 minutes** - a branch that kept fifteen; the
shipped default is five since the booking-times change. The table is unsellable from **17:45** to
**19:45**, and sellable at exactly those two instants. (Whether it should be sold then is the table's
evening's question - [3e](#3e-a-tables-evening-when-one-booking-may-follow-another) - which this rule
sits underneath.)

```
                  17:45                18:00                 19:30                19:45
                    |                    |                     |                    |
  buffer            |<---- 15 min ------>|                     |<---- 15 min ------>|
                    |                    |                     |                    |
  existing          |                    |#####################|                    |
                    |                    |     18:00-19:30     |                    |
                    v                    v                     v                    v
  ================= | ================== | =================== | ================== | =========
                    |                                                               |
  [ ...16:15-17:45 ]|  OK      exactly at existingStart - buffer                     |
   [ ...16:16-17:46 ]  CONFLICT  one minute later                                    |
                    |                                                  |[ 19:45-21:15 ... ]  OK
                    |                                              [ 19:44-21:14 ... ]  CONFLICT
                    |
  [ 17:00 ================================================================ 21:00 ]   CONFLICT
                                        (fully contains the existing booking)
```

Five cases, each a test in `ReservationOverlapTests`:

| # | Proposed sitting | Result | Why |
| --- | --- | --- | --- |
| 1 | starts **19:45** | allowed | exactly `existingEnd + buffer`; back-to-back sittings are how a venue makes money |
| 2 | starts **19:44** | conflict | leaves 14 minutes to clear a table the branch says needs 15 |
| 3 | ends **17:45** | allowed | exactly `existingStart - buffer` |
| 4 | ends **17:46** | conflict | one minute over |
| 5 | **17:00–21:00** | conflict | wholly contains the existing booking — the case a naive "is either endpoint inside?" test misses |

Getting case 1 wrong in the safe-looking direction (using `<=`) costs the venue a whole second
cover on every table, every evening. It is not a rounding detail.

### The rule in SQL

The overlap check has to be a range predicate SQL Server can seek, not arithmetic per row. The
first comparison rearranges:

```
newStart < existingEnd + buffer     ⟺     existingEnd > newStart - buffer
```

so every conflicting row satisfies `EndUtc > (newStart - buffer)` **and**
`StartUtc < (newEnd + buffer)` — a plain range scan of
`IX_Reservations_DiningTableId_StartUtc_EndUtc`. That is `ReservationOverlap.SearchWindow`.

The predicate **narrows candidates; it does not decide.** Every row it returns is then put through
`ReservationOverlap.Conflicts` itself, so the rule proved at its boundaries by the unit tests is the
rule that decides the insert, and there is never a second copy of it written in a second language.
A unit test walks a candidate booking minute by minute across the whole boundary to prove the
window can never hide a conflict from the rule.

---

## 3. Concurrency: why this locks and table state does not

This is the part worth reading twice.

### Table state uses optimistic concurrency, correctly

Two waiters seating a walk-in both **`UPDATE` the same `DiningTables` row**. Its `RowVersion` catches
the race for free — no locks, no hints, and the loser gets `TableStateConflictException` carrying
the table's current state. That is the right tool when writers collide on one row, and it is still
how two seatings settle between themselves.

### The same tool is silently wrong here

Two diners booking the same table **`INSERT` different `Reservations` rows.** Nothing they touch
collides. No row version ever differs, no exception is raised, **both commit** — and the table is
double-booked with nothing anywhere recording that it happened. The failure is invisible: the
system is not aware it has a problem until two parties arrive.

### And the obvious fix is worse

Touching the parent `DiningTable` row on every booking to force a version clash *would* stop it.
It would also make two bookings for the same table at **different times** collide — a spurious
conflict, on the most in-demand tables in the room, at the busiest moment, for a reason no diner
could understand. A venue's whole business is booking the same table three times in an evening.

**Test 12 is the test that fails if somebody reaches for that.** Two concurrent bookings for the
same table at non-overlapping times must both succeed.

### So: serialise writers per table

```
BEGIN TRANSACTION (READ COMMITTED)
    SET LOCK_TIMEOUT <BookingLock:LockTimeoutMilliseconds>

    SELECT Id FROM DiningTables WITH (UPDLOCK, HOLDLOCK) WHERE Id = @tableId

    -- inside the lock, never before it:
    1. re-check ClientCommandId   (a racing retry of this same booking)
    2. re-check for overlaps      (the range predicate + the pure rule)
    3. re-check for an open sitting (the projected interval)
    4. INSERT the reservation
COMMIT
```

- `UPDLOCK` serialises bookers against each other without blocking readers — the availability
  screen keeps working while a booking commits.
- `HOLDLOCK` keeps the lock to the end of the transaction instead of releasing it the instant the
  `SELECT` finishes. Without it the re-check below means nothing.
- **The re-check happens inside the lock and never before it.** A check taken before the lock proves
  only that the table was free at some earlier moment, which is precisely the window the lock exists
  to close.

A second booker waits, re-reads, and then either succeeds — different slot — or is told the table
went. Both are the right answer.

### Seating takes the same lock, and step 3 is why

Step 3 is new in Prompt 7, and adding it changed who has to hold the lock. The moment booking's
re-check started **reading `TableSessions`**, a seating that wrote one *without* taking the lock could
commit in the window between that read and the booking's commit. Both then succeed — the exact
double-booking step 3 was added to prevent.

So the four seating commands — `SeatWalkIn`, `SeatQrScan`, `SeatReservation`, `SeatHeldParty` — take
the table lock, load the table **inside** it, and commit inside it. `RowVersion` still settles two
seatings against each other; the lock is what settles a seating against a booking.

Freeing, holding and taking a table out of service do **not** take it. They only ever *narrow* what a
booking would find — a booking that saw a sitting which was about to close refuses a slot that would
have been fine, a worse answer but never a wrong one — and locking every tap on the floor screen
would put the room's whole write traffic through one queue per table for no gain.

### Which commands take the lock

A list, not a rule. The rule has been stated twice and been wrong twice — first that table state
never needs the lock, then that the three "narrowing" commands never do — so what follows is the
enumeration, and anything added to the state machine belongs in it explicitly.

| Command | Locks | Why |
|---|---|---|
| Create a booking | **Yes** | Two bookers insert different rows and collide on nothing, so optimistic concurrency sees no conflict and both commit. |
| Move a booking (reschedule) | **Yes**, the **target** table | The same hazard: the moved booking updates its own row, and the booking it now overlaps is a different one, so no row version sees the clash. Leaving the old table only frees it, which narrows nothing - see [3c](#3c-changing-a-booking). |
| Seat: walk-in, QR, reservation, held party | **Yes** | Booking's re-check reads `TableSessions`. A seating that skipped the lock could commit between that read and the booking's commit, and both would succeed. |
| Mark out of service | **Yes** | A booking validated while the table was `Free`, committing after this commits, is a **confirmed reservation on a broken table**. Not a narrower answer — a wrong one. Rare enough that the throughput argument below does not apply. |
| Free | No | Only narrows what a booking finds. |
| Hold for a late party | No | Only narrows what a booking finds. |
| Release a hold | No | Only narrows what a booking finds. |
| Return to service | No | Widens, and only ever toward the table being usable. |

The three that do not lock are the floor's entire write traffic. Putting that through one queue per
table buys nothing, because the worst they can do is make a booking's re-check more conservative: a
booking that saw a sitting which was about to close refuses a slot that would have been fine. A worse
answer, never a wrong one.

**The lock alone is not enough for the out-of-service case.** Serialising the two commits still lets
a booking that waited for the lock commit onto a table that has since been marked broken, so
`ReservationWriter` re-reads the table's status **inside** the lock and refuses. The lock makes the
order deterministic; the re-check is what makes the outcome right.

### Marking a table out of service surfaces its bookings

Separate from the race, and the more useful half. A waiter can mark table 7 broken at six o'clock
with three bookings on it tonight, and until Prompt 8b nothing anywhere told anybody.

`MarkOutOfService` now returns the confirmed and pending bookings the table still has — time, party
size, guest name, code and **telephone number** — and cancels none of them. Ten minutes while a
chair is replaced and a week while a floor is relaid look identical from here, and only the person
standing in the room knows which this is.

Releasing one of them uses `CancelledByVenue`, never `NoShow`: a broken table is the venue's doing
and must not count against a diner who was never given the chance to turn up.

### The lock is a convention, not a constraint

This is the uncomfortable part, and it is the reason `ReservationWriter` exists.

PostgreSQL has `EXCLUDE USING gist (table_id WITH =, during WITH &&)`, which makes overlapping
intervals *unrepresentable*: no application code can produce one, however it is written. **SQL Server
has no interval exclusion constraint.** A filtered unique index cannot express "these two ranges must
not intersect", and a `CHECK` constraint cannot see other rows.

So **nothing in the database catches a booking that skipped the lock.** No violation, no error, no
repair job that finds it afterwards — just two confirmations and two parties at one table. The
protocol above is the entire guarantee, and a protocol anyone can bypass by typing
`db.Reservations.Add` in the next feature is not a guarantee at all.

The insert therefore lives behind exactly one method, and moving a booking - the other way a
booking comes to occupy a slot - behind its twin:

```csharp
ReservationWriter.InsertUnderTableLockAsync(...)   // the only db.Reservations.Add in the codebase
ReservationWriter.MoveUnderTableLockAsync(...)     // the only change of a booking's table or time
```

`ReservationService` validates, decides approval and builds the refusals; it cannot insert. The rule
is enforced by there being one door — weaker than a constraint, and the strongest thing this database
offers. **If a second `db.Reservations.Add` appears in a diff, that is the bug**, not whatever it was
added to do.

What the database *does* guarantee is worth naming, because those are the backstops the code leans
on: `TableSessions` carries a filtered unique index allowing at most one open session per table, and
`Reservations.ClientCommandId` is unique. Both are real constraints.

### Order inside the lock

The command-id re-check runs **before** the overlap check, and the order is easy to get backwards.
Two retries of the *same* booking race: the first commits while the second waits on the lock. By the
time the second gets in, the first is visible — and an overlap check run first would call the
diner's own booking a conflict and answer 409 for a table they already have.

Bookings are checked before sittings for a smaller reason: a clash with a booking is a firmer fact
than a clash with a projection, so when both are true the diner is told the one that will still be
true in an hour.

The full order: the table's lock, the diner's account lock, the replay, the table's own status, the
table's evening read, **the diner's own other bookings** (below) refused first, then the table's own
refusals and its sittings, then the time to leave by.

### One diner, one table at a time

A signed-in diner - every booking here is one: the app's, or the public page's after a phone proof -
cannot book, or move a booking to, a sitting that overlaps one of **their own live bookings** at the same
branch - or that comes within **30 minutes** of one at **another venue** (since 2026-10-20, §3g): `409
reservation-overlaps-own`. QA found one diner holding table 1 at 19:45 and table 2 at
19:30 the same evening - two tables kept from everybody else for one party.

```json
409 { "code": "reservation-overlaps-own",
      "context": { "reservationId": "guid", "code": "K7Q2M9", "localDate": "2026-10-22", "localStartTime": "19:30:00",
                   "tableLabel": "2", "venueName": "Café Aram", "branchName": "Main" } }
```

The context names the booking already held, so the client can say "You already have a booking at 19:30
(table 2)" - and the day, from `localDate` (the branch's calendar), when it is not the one being booked. Live is pending, confirmed or seated. Both sittings are the ones the bookings have: the new
one is the full turn from its start, or - when the table's next booking needs a time to leave by - cut to
that time (one diner can book 13:00 promising to leave by 13:55 and their own 14:00 on another table; not
sending the promise, they are asked for it, `422 reservation-free-by-required`, not refused as
overlapping); the existing one runs to its stored end, which a promise already cut. Sittings that only
touch (one ends at 19:30, the next starts at 19:30) do not overlap - no clearing time, it is the same
people. At another branch the two sittings need half an hour between them to get there - with a 90-minute
sitting, starts under two hours apart are refused (§3g). A move leaves the booking being moved out, so moving dinner half an hour is never refused
because of itself. It is judged inside the table's lock **after the account's lock and the replay**: a
retry with the same `clientCommandId` is still answered as a replay, and two bookings sent at once on two
tables queue on the account's row (and the number's lock, §3g), so the second sees the first. Bookings that already overlapped before
this rule (2026-10-18) are left as they are; a change that keeps the table, the start and the end - a
party size - is not judged (it makes no new overlap, so such a pair stays editable), and any other move
of either is.

**Nor through a change that falls through.** A booking waiting on a change does not hold the slot it
came from, so the diner can book that time again elsewhere - tomorrow's 20:00 on table 1 moved to
tonight, then table 3 tomorrow at 20:00. If the change is then declined or not answered, going back to
table 1 would give them two tables at once: the slot counts as not free (§3c), the booking ends with its
change, and the diner is told as for any slot that was gone (`originalKept` false).

### The lock is held for the shortest possible span

One indexed range read and one insert. No availability snapshot, no push notification, no email
happens inside the transaction. The 409's availability payload is built **after** the transaction
has rolled back, so a losing booker never queues everyone else behind a response body.

### Lock timeout is not a conflict

`SET LOCK_TIMEOUT` bounds the wait so one stuck transaction cannot hang every booking for that
table. The two outcomes are deliberately different answers:

| | Meaning | HTTP | Retry? |
| --- | --- | --- | --- |
| `TableAlreadyBookedException` | the answer is no, and will stay no | **409** + clashing window + fresh availability | never |
| `TableCurrentlyOccupiedException` | somebody is sitting there; the finish time is an estimate | **409** + projected free time + fresh availability | not for this slot |
| `TableStateConflictException` | another waiter changed the table first | **409** + the table's current state | never |
| `ReservationLockTimeoutException` | the question was never asked | **503** `reservation-lock-timeout` + `retryable: true` | yes, with the same `clientCommandId` |
| `TableLockTimeoutException` | the same wait, reached from the floor screen | **503** `table-lock-timeout` + `retryable: true` | yes |

Collapsing them into one code would teach clients to retry conflicts, which is how a party ends up
holding two tables. `ReservationLockTimeoutException` derives from `TableLockTimeoutException`: one
underlying wait, two sets of words, because a diner on a phone and a waiter on a tablet need
different ones.

### Idempotency

`Reservations.ClientCommandId` carries a **unique index**, exactly as `TableStateChanges` does. A
diner on a patchy mobile connection taps Book, sees nothing happen, and taps again. The index — not
a check-then-insert in the service — is what settles it, because two retries can race each other and
a check-then-insert lets both through. The loser of that race answers with the booking that won.

**The index is global; a replay is not.** A replay is *the same diner* sending *the same command*
again, so every lookup that answers with an existing booking is scoped to the calling
`DinerUserId`. Scoping only the index and not the lookup is a disclosure, not a subtlety: a second
diner reusing the id would be handed the first one's booking — door code, guest name and telephone
number — and the response would look like an ordinary successful retry.

What is left over is a genuine collision between two callers, which is a client bug rather than a
retry. It answers **409** `client-command-id-in-use`, saying only that the id is taken and nothing
about the booking holding it. `ReservationEndpointTests` asserts the 409 body contains neither the
other diner's code, name, nor phone.

---

## 3b. Letting a booking go

`POST /api/reservations/{id}/release`, waiter or above. The action that was missing: a waiter could
hold a table for a late booking and had no way to stop holding it.

| `outcome` | Booking becomes | Counts toward the no-show threshold |
|---|---|---|
| `NoShow` | `NoShow` | **Yes** |
| `CancelledByVenue` | `CancelledByVenue` | **No** |

**Two buttons on the tablet, never one.** *Release, marked no-show* and *release, they let us know*.
With a single button a busy waiter taps it for both cases — from the floor the two look the same —
and the threshold ends up punishing the diners who telephoned to say they could not come. Yerevan is
a small market and being unfair to a regular by accident is a cost the product cannot carry.

Both outcomes:

- **Free the table when it is held for this booking**, through the state machine, so the audit row is
  written and the branch change sequence moves. Which booking a hold is for lives in the audit log
  rather than on the table row — the hold is a physical fact and the booking is a financial one —
  so the most recent transition into `Held` is what names it.
- **Leave an occupied table alone.** The booking is released either way, because that is a fact about
  the booking; freeing a table somebody is sitting at would make the floor plan lie about where people
  are, which costs more than a stale hold.
- Idempotent by `clientCommandId`, and a booking already released answers with what the first attempt
  got. A tablet replaying its queue must not put two no-shows on a diner's record.

**A no-show only from the booking's start.** `NoShow` - through `release` or its other door,
`POST /api/reservations/{id}/no-show`, which the console's row menu uses too - is refused before `startUtc`
with **409 `reservation-not-started`** (`context.reservationId`, `context.startUtc`), and nothing changes:
the booking stays confirmed and its table's hold stays. Nobody can have failed to come before they were due,
and a no-show counts against the diner - while the tablet's hold attaches to the table's next booking
whatever day it is on, so without this the wrong button on a hold put a no-show on somebody due tomorrow.
**No grace on top:** from the start itself the waiter may record it. The branch's grace period times the
late nudge; whether a party past their time is late or absent is the waiter's call, as above.
`CancelledByVenue` and the table's own `release-hold` are allowed at any time - letting a booking or a hold
go early costs the diner nothing. Checked in `Reservation.MarkNoShow`, so every way of recording a no-show
answers the same; pinned by `LateBookingEndpointTests`.

The threshold rule itself is unchanged: `RequiresApproval` is `>` and the shipped threshold is three,
so it is the **fourth** no-show in the window that costs instant confirmation.

---

## 3c. Changing a booking

`POST /api/reservations/{reservationId}/reschedule`, `VerifiedDiner`, idempotent on `clientCommandId`.
The same move from a booking's manage link, for a web visitor with no app, is
`POST /api/public/bookings/{token}/reschedule` - see
[public-surface.md](public-surface.md#changing-the-booking): one code path
(`ReservationChanges.MoveAsync`), two ways of proving you may use it.

```json
{ "date": "2026-09-13", "time": "20:00", "partySize": 3, "diningTableId": "guid", "clientCommandId": "guid" }
```

The whole new slot, as a new booking states it. `diningTableId` is **required**, exactly as `tableId`
is on a new booking: the current table or another at the same branch, picked off the room photos. The
answer is `200` with the `ReservationView`.

**The same booking, not a cancellation and a new one.** The `id`, the `code` quoted at the door and the
manage link (`ManageTokenHash`) all stay, and so do the note, the guest's name and number, the channel
and the `clientCommandId` that created it. The interval is recomputed from the branch's turn time and
any hold is cleared. `Reservation.Reschedule` owns the transition.

**Only a booking still to come moves**: `PendingApproval` or `Confirmed`, and not yet started. From
the start the booking is the venue's to run - the table is held, the late nudge fires, a waiter may
release it. Anything else is **409 `reservation-not-changeable`**, with `context.reservationId`,
`status` and `startUtc`, checked **before** the new slot is looked at - a cancelled booking is not told
"that table is taken". Somebody else's booking is **404**, word for word what an id that is no booking
at all gets: unlike cancelling, which says "not yours", a change would go on to judge a slot, and a
stranger guessing ids should not learn that one is a booking.

**Judged exactly as a new booking for that slot**, in the same order and with the same `reservation-*`
422s: the phone proved, the venue open for business, the branch still taking bookings from the channel
the booking came from (`web-bookings-not-accepted`, `bookings-not-accepted`), lead time and booking
window, opening hours, the table pinned on a photo of its room, capacity and seat overhang.

**Under the target table's lock, minus itself.** `ReservationWriter.MoveUnderTableLockAsync` follows the
insert's protocol step for step - the account's lock, the replay re-check, the table's status, the
diner's own other bookings (`409 reservation-overlaps-own`), the overlap check, the sittings - with the
booking being moved **left out of both overlap checks**, so moving
dinner half an hour on the same table is never refused because of itself. A clash answers like
creation's: `409 table-already-booked` or `table-currently-occupied`, with a fresh floor that leaves the
diner's own booking out too. Only the target table is locked: leaving the old one only frees it, which
makes another booker's answer at worst a moment too cautious, never wrong.

**`RowVersion` guards a racing cancel.** The booking is read before the lock. A cancel, an approval or
a seating that commits in between moves its row version; the move's `UPDATE` then matches no row and
the answer is **409 `concurrent-update`** - a move never resurrects a cancelled booking at a new time.
`ReservationRescheduleTests` lands a real cancel at the moment the lock is taken and proves it.

**A confirmed booking that keeps its table and its start and does not grow stays `Confirmed`** - today
included, and whatever the branch's settings - without being decided again: the same table at the same
time for as many people or fewer is nothing the venue has not already said yes to. Everything else is
decided again.

**The status is decided again**, by creation's rules with the new party size (`DecideStatusAsync`): a
move into the short-notice window - see [3d](#3d-short-notice-tonight-is-the-floors-to-decide) - goes to
`PendingApproval` with a fifteen-minute window, auto-confirm off or a party over the threshold goes to
`PendingApproval`, and so does a diner over the no-show threshold; otherwise `Confirmed`. A booking
**already waiting** on a short-notice answer keeps the deadline it has through a move (never past the new
start) - moving the request around does not restart the floor's fifteen minutes - and once that deadline
has passed it cannot be moved at all: **409 `reservation-approval-expired`**, the sweep's to decline. A
move of a pending short-notice booking to another day before then confirms it and clears its window, so
the sweep never finds it. **One exception, for a booking that is `Confirmed`**: when
the only reason the re-decision would ask for approval is the party-size threshold and the new party is
**not larger** than the booking's current one, it stays `Confirmed` and keeps its `ConfirmedAtUtc`. The
venue said yes to that many people; moving them to another table or time does not undo it. A party
that grows past its current size is asked about again, and the other two reasons are asked as for any
booking - a branch with auto-confirm off sees every move, and a diner whose no-shows cost them instant
confirmation loses it on a move too. Going back for approval clears `ConfirmedAtUtc`; a party that
shrinks under the threshold is confirmed without anybody. A booking can therefore be approved twice.
The second approval's push is its own message - `reservation:{id}:approved:{yyyyMMddHHmmssfff}` once
the first key is taken - rather than a collision with the first; a second decline or expiry is named
the same way.

**A declined change does not cost the booking.** When a `Confirmed` booking is moved into
`PendingApproval` - for any reason, short notice most often - the slot it had is kept on the row
(`PreviousDiningTableId`, `PreviousStartUtc`, `PreviousEndUtc`, `PreviousLocalDate`,
`PreviousLocalStartTime`, `PreviousPartySize`, `PreviousConfirmedAtUtc`), and kept through further moves
while it waits: it is the last slot anybody said yes to. Approving the change drops it. **Declining the
change - by staff, or by the sweep for no answer - sends the booking back to that slot** when it is still
free: judged under that table's lock exactly as a move is (another diner may have booked it since, a
walk-in may be sitting into it, the table may be out of service, the slot may be over, or the diner may
have booked another table at the branch for that time meanwhile), and committed in
the same transaction - the booking `Confirmed` again with its old start, end, table, party and
`ConfirmedAtUtc`, its reminder and late nudge rewritten for that slot, `DeclineReason` recording why the
change did not go through (the view's `declinedBecause` stays null - the booking is not declined). That
reason lasts only while the booking does: a new change clears it, and so does **every cancellation** -
the venue releasing the booking, the no-show block, the guest cancelling - so a booking let go after its
change was declined is `CancelledByVenue` with no `DeclineReason`, a cancellation, not a decline
(`declinedBecause` absent, `cancelled` in the message history). Only a decline itself (`Reject`,
`DeclineForNoAnswer`) leaves a cancelled booking with a reason. Rows cancelled before this fix were
cleared by the `ClearStaleChangeDeclineReasons` migration wherever the row shows the cancel was not a
decline (the no-show block's; one that had been approved, `ConfirmedAtUtc` set, which a waiting booking
never has; a no-answer or guest-before reason with a cancellation text, which those declines never write;
the release's default text); a staff-declined change of a booking confirmed on the spot and then released
with a typed reason cannot be told from a staff decline and was left as it was. When
the slot has gone, the booking is declined as any other. Either way the diner is told which: the
`reservation.decided` push carries `wasChange` and `originalKept`, and so does the `booking-declined`
feed entry, with the slot the change asked for in `requestedDate`/`requestedTime`.

**Reminders are replaced, not edited.** Both the reminder and the late nudge name the old time, so the
unsent ones are cancelled exactly as a cancellation cancels them (`IOutbox.CancelAsync` by the booking's
prefix, `DinerNotices.CancelUnshownAsync` for the feed), and new ones are written for the new start in
the same transaction. Their keys carry the start they are for -
`reservation:{id}:reminder:{startUtc:yyyyMMddHHmm}`, `…:late-nudge:…` - because a message already
**sent** keeps its row and its key; one whose key was sent already is not written again. A web
booking's new reminder carries the manage link its old one did: the token's plaintext exists only in
the create response and the messages written then.

**Idempotency** is the `ProcessedCommands` pattern: the command is recorded (`reservation.reschedule`,
the diner as actor) in the same save as the move, and checked before the lock and again inside it. A
replay answers `200` with the booking **as it stands now** and `wasReplay: true`, moving nothing. The
record holds which booking and where it went, never the view - the view carries the guest's name and
number. The same id on another command - another diner's, another booking's, a table command - is
`409 client-command-id-in-use`, revealing nothing about it. **So is the same id on a different change
of the same booking**: a replay must name the table, the start (its local date and time, in the
branch's zone) and the party size the record holds. Answering `wasReplay` with the booking still at
eight to a command that asked for nine would tell the phone a move happened that did not; the 409 is
the client's cue to mint a fresh id for the new change.

---

## 3d. Short notice: tonight is the floor's to decide

An app booking for tonight is a promise about a table somebody may have just sat down at, and nothing the
server knows says whether they will be gone in time. So a short-notice booking is never confirmed
blindly: it waits for staff on the floor, who can see the table.

**The rule (`ShortNoticeRule`, fixed in code - not a branch setting).** A booking is at short notice when
its local date is **the branch's today** - in the branch's zone, so at 00:30 in Yerevan "today" has
already turned over - **or** it starts **less than six hours** from now (at 22:00, tomorrow's 03:59 is;
04:00 is not). Every short-notice booking a diner makes lands `PendingApproval` with
`awaitingApprovalBecause` **4 `ShortNotice`**. It applies to every booking made through
`POST /api/reservations` - the app's and the public page's - whatever `channel` the client reports:
the channel is self-reported, and a rule a client could switch off by claiming `Staff` would be no
rule. Nothing a member of staff enters goes through it. Every other booking follows the rules it always
did, on every day: auto-confirm off, a party over `approvalRequiredAbovePartySize`, a no-show history.

**The reason is stored**, on `Reservations.ApprovalTrigger`, when the booking goes to approval, and every
read says that one - a booking made at short notice at two in the afternoon is still the short-notice
booking at nine, and a policy edited since cannot rename the reason the diner was shown. When several
apply, the first of **ShortNotice, BranchApprovesEveryBooking, LargeParty, NoShowHistory** is stored:
short notice leads because it is the one with a deadline. A pending row from before the column has no
stored reason and gets the old answer, worked out from the branch's policy.

**Fifteen minutes to answer.** A short-notice booking carries `approvalExpiresAtUtc` = the request time
plus 15 minutes, or its start if that is sooner (stored on the booking and cleared the moment it stops
waiting; a booking waiting on the guest before it has two hours instead - §3e - and every other booking
none). The app and the public page count down to it. Until then a waiter
or above at the branch may approve or decline it.

**Unanswered, it is declined for them.** `ApprovalExpiryHostedService` runs `ApprovalExpirySweep` every
`ApprovalExpiry:SweepIntervalSeconds` (default 20; 0 switches it off, which no venue should) and once at
start-up. Each pass reads the pending bookings whose `approvalExpiresAtUtc` has passed - one filtered
index, `IX_Reservations_ApprovalExpiresAtUtc`, which holds only the requests open now - and declines each
on its own, under its own row version: `CancelledByVenue` with `declinedBecause` **2 `NoAnswer`** (or 4
`EarlierGuestNoAnswer` for a guest before asked in the app, §3e), its
reminder and late nudge cancelled, and the diner told in the same save - a `reservation.decided` push
with `expired: true` (an apology: *the venue could not confirm in time - try another time or place*) and
a `booking-declined` feed entry with `expired: "true"`. A waiter's approval that lands first moves the
row version, so the sweep's save matches nothing and writes nothing; a second process sweeping the same
booking loses the same way. A booking is declined, and its diner told, exactly once. A change of a
confirmed booking nobody answered goes back to the slot it had, as above.

**One bad row does not stall the sweep.** Anything a booking throws - a lock that could not be had, a row
the domain refuses - is logged at error level with its code and the pass goes on to the rest; that
booking is not tried again in the same pass, and the next pass tries it afresh. **A request the sweep
only reaches after the booking would have started** (the machine slept through the deadline) is still
declined, but without the apology push or feed entry - there is no evening left to send the diner to.

It is a sweep and not a scheduled outbox message on purpose: the outbox dead-letters anything more than
an hour overdue, and a machine that slept through the deadline would leave the diner on a "waiting"
screen for good. It releases no table - the booking was never promised, so there is no hold to let go -
which is why it is not the timer [notifications.md](notifications.md#8-nothing-here-releases-a-table)
says must never free one.

**Who decides: a waiter or above at the booking's branch.** `POST /api/reservations/{id}/approve` and
`/reject` take `WaiterOrAbove`, and the service confines them to the branch from the stored staff row: a
waiter and a branch manager at their own branch, an owner or general manager at any branch of their
venue, a platform admin anywhere. A cook, a diner and another branch's staff are 403. The **stored**
role is checked as well as the token's, so a waiter moved to the kitchen is refused at once rather than
when their token expires - the same check guards the floor's list and releasing a late booking. The
bodies are unchanged; `reject` keeps its optional `reason`, recorded as `cancellationReason`, and the
booking answers `declinedBecause` **1 `StaffDeclined`** - or, for a change of a confirmed booking whose
old slot is still free, the booking back at that slot (see [3c](#3c-changing-a-booking)). A platform
admin deciding a booking gets it back with `guestPhone` empty, as on the floor's list.

**Too late is its own answer.** Approving or declining on or after `approvalExpiresAtUtc`, or after the
sweep declined it, is **409 `reservation-approval-expired`** (`context`: `reservationId`, `status`,
`startUtc`, `approvalExpiresAtUtc`). Past the deadline the decision is the sweep's, so a late "no" is
refused as well as a late "yes": the diner is told nobody looked in time, not that the venue said no a
second after the countdown. When the sweep commits between a waiter's read and their save, the waiter
gets the same 409 rather than `concurrent-update`. Approving any pending booking - with a window or
without - once it has **started** is the same 409; declining one is still allowed.

**Reminders.** A short-notice booking whose three-hours-before reminder would fall inside its answer
window is not given one at all - the approval push ("they expect you at 20:00") says the same thing, and
truthfully. One due later is written as usual; the reminder handler skips a booking still waiting on a
short-notice answer when it fires (only a sweep running behind could leave one), while a pending booking
with no deadline - a large party next week - is reminded as before.

**The floor's list.** `GET /api/branches/{branchId}/reservations/awaiting` (`WaiterOrAbove` +
`BranchScoped`, then the same stored-row check as deciding) is what the staff tablet polls. It lists the
branch's `PendingApproval` bookings that can still be answered - not past their window, not started -
soonest deadline first, then those without one by start, at most 200, in **one query**. Each item:

```json
{
  "reservation": { "id": "guid", "code": "K7Q2LM", "startUtc": "…", "partySize": 2, "tableId": "guid",
                   "tableLabel": "4", "roomId": "guid", "roomName": "Hall", "guestName": "Gor",
                   "guestPhone": "+37499333444", "status": 1, "awaitingApprovalBecause": 4,
                   "approvalExpiresAtUtc": "…", "...": "the rest of ReservationView" },
  "approvalExpiresAtUtc": "2026-09-10T08:15:00Z",
  "trigger": 4,
  "previousBooking": { "reservationId": "guid", "startUtc": "…", "localStartTime": "17:00:00",
                       "partySize": 4, "guestName": "Anna", "guestPhone": "+37499111222", "status": 2 },
  "nextBooking": { "reservationId": "guid", "startUtc": "…", "localStartTime": "21:30:00", "partySize": 2 },
  "currentSitting": { "seatedAtUtc": "…", "partySize": 3 }
}
```

`previousBooking` is the latest booking before it on the same table that local day that was not
cancelled - still to come, at the table, gone home, a no-show or itself pending - with its guest's
number, so the floor can call and ask how long they will be. `nextBooking` is the first booking after it
that day that holds the table. `currentSitting` is the open sitting on the table now; its `partySize` is
absent when a diner's scan opened the table, which only guesses. Each is absent when there is none, like
every null in this API. Staff of the branch see the guests' numbers; a platform admin reading the list
gets `guestPhone` empty and `previousBooking.guestPhone` absent.

**What the diner is told before booking.** Availability's `requiresApproval` is asked **per slot**
(`ReservationRules.AwaitsApproval` takes the slot): every time today and the first six hours tomorrow
say true; the same times next week say false. See [5](#what-the-diner-is-told-before-they-commit).

## 3g. Booking needs a proved number; the per-number rules; the guest record

Decided by the user on 2026-10-20, revising Yalla#54 the same day. Yalla#54 let a booker who never proved their
number book (the public page's `POST /api/public/branches/{branchId}/bookings`, and an unproved app account),
with one active booking per number. Both are withdrawn:

**No booking without a proved number - anywhere.** The public page's door is gone (the route answers 404); the
page books only after the visitor proves the number (Telegram) and sends `POST /api/reservations` with the diner
token. An app account whose number is not proved is refused **`403 phone-not-verified`**, as before #54. The two
branch settings #54 added for such bookings (`unverifiedBookingsNeedApproval`, `unverifiedMaxPartySize`) are
dropped (migration `GuestRecord`), with the public page's `policy.unverifiedMaxPartySize`,
`unverifiedBookingsNeedApproval` and `acceptsUnverifiedBookings`, the branch queue cap, the per-browser limit,
the per-network budget, `422 branch-requires-verified-phone`, `422 reservation-unverified-party-too-large`
(`ReservationRejectionReason` 14 and 15 retired), `409 device-has-active-booking`,
`409 branch-unverified-queue-full`, `Reservations.BookerDeviceHash`, and `madeWithYourNumber` with the
number owner's cancel. A console that still sends the two settings on `PUT .../reservation-policy` is not
refused - unknown members are ignored. **Kept:** QR-scan admission by a waiter (`unverifiedScansNeedAdmission`,
[tabs.md](tabs.md) 2b), unchanged.

**The bookings made without proof while #54 was live are left as they are.** They are real bookings a venue
may have confirmed and a guest may be counting on; a migration cannot tell anybody it cancelled them. They keep
`PhoneUnverified` (`phoneUnverified` on reads, while their account has not proved its number since), a pending
one keeps `ApprovalTrigger` 6 `UnverifiedPhone` and the deadline it was given, and `ApprovalExpirySweep`
declines it unanswered like any other. Trigger 6 is never given again. Such a booking holds no proved number
against anybody but its own account and never counts in a guest record. Their manage links still read and
cancel; changing one with no account behind it is `403 phone-not-verified`, as for any link without a live
proved account.

### The per-number rules

`Reservations.BookerPhoneE164` is the number a booking holds: the booking account's proved `PhoneE164`, in one
canonical form (`PhoneNumber.Canonical`, libphonenumber - a trunk 0 after `+374` is the same number). *Not*
`GuestPhone`: a diner may book for a friend. A booking holds the number when it is proved (`PhoneUnverified`
false) or the caller's own account's. Staff bookings (no path exists) would write null and are exempt. Deleting
an account clears the number from its bookings.

**No two overlapping bookings, at any venue** - `409 reservation-overlaps-own` (§2's rule, widened). The diner's
live bookings (pending, confirmed, seated): their account's, and any proved booking holding their number.
- **Same branch:** unchanged - the sittings as they are; back to back is fine.
- **Another branch:** the two sittings need **30 minutes between them** (`BookingLimits.OtherVenueGapMinutes`):
  refused when `other.Start < new.End + 30 min` and `other.End > new.Start - 30 min`. At the shipped 90-minute
  restaurant turn that is "starts less than two hours apart" - the "about ±2 h" the user agreed; a longer sitting
  blocks what it really overlaps.
- The context names the booking: `reservationId, code, localDate, localStartTime, tableLabel` and now
  **`venueName`, `branchName`**. The diner's bookings are their account's: a proved number belongs to one live
  account and never moves to another (`DinerUser`), so the account is the number.
- On create and on every move (unless the move keeps table, start and end); and when a waiting change goes back
  to its confirmed slot.

**At most three active bookings per proved number** (`BookingLimits.MaxActiveBookingsPerNumber` = 3, a product
constant). Active = pending or confirmed and not over (`EndUtc` after now), at any venue. The fourth is
**`409 phone-has-active-booking`**:

```json
409 { "code": "phone-has-active-booking",
      "context": { "maxActiveBookings": 3, "reservationId": "guid", "code": "K7Q2LM", "startUtc": "…",
                   "localDate": "2026-10-22", "localStartTime": "19:30:00", "venueName": "Café Aram",
                   "branchName": "Main" } }
```

naming the caller's **own earliest** active booking (absent members when none is theirs). Moves are not judged
(they add no booking); cancelling one frees a place at once.

**Order and locks.** Inside the table's lock: the account's row lock (`DinerAccountLock`), the replay, the
no-show block, the table's status, the evening's verdict, the overlap, the table's own refusal, the three-booking
limit. A unique index cannot hold either rule ("not over yet" is about the clock, "overlapping" about two
intervals); the **account's lock** serialises two bookings for one number - on two tables, at two venues - and the
second reads the first's committed row
(`BookingPerNumberTests.Two_bookings_for_the_last_place_at_the_same_moment_write_exactly_one`). 0091c49 also took an
application lock on the number; it was redundant - two accounts cannot hold one proved number, so a race between
them cannot be set up - and was removed (review N6). A wait past the table lock's bound is
`503 reservation-lock-timeout`, retryable.

### Guest record

What a venue sees beside a booking - `guestRecord` - and the guest sees of themselves:

```json
"guestRecord": { "booked": 9, "came": 6, "noShows": 1, "lateCancellations": 0, "unmarked": 2,
                 "isNewGuest": false, "takeawayNoShows": 0, "blockedUntilUtc": "…" }
```

Over the guest's **account**'s bookings made with a proved number whose **start is in the last 365 days and has
passed** (`GuestRecordRules.WindowDays`), at **every venue**:

| Field | Counts |
| --- | --- |
| `came` | seated or completed **and vouched for by the venue** (review S1): a waiter seated the party (`TableSessions.SeatedByStaffId`), or the sitting's tab had an order sent to the kitchen (InKitchen, Ready, Served) or a successful payment. Opening a tab from the booking code alone - which anybody can do from the sofa - never counts |
| `noShows` | `NoShow` (a withdrawn one is `Confirmed` again and is not) |
| `lateCancellations` | cancelled by the guest after the deadline **when the booking was confirmed and had been made before that deadline** (review S2) - `LateCancellationCounts`, decided and stored when it is cancelled. A same-evening booking (inside its deadline from the start), one still waiting for the venue, or one confirmed inside its deadline never counts. Older rows were backfilled from `CancelledAfterDeadline`, `ConfirmedAtUtc` and the creation time |
| `unmarked` | the bookings whose time came and went with nobody on the floor settling them (review S5): confirmed and past their **end** with nobody seating or marking them, or seated/completed with nothing vouching for it (above). Neither a visit nor a no-show - most often a venue that does not seat guests in Yalla |
| `booked` | exactly `came + noShows + lateCancellations + unmarked` - the bookings whose time came |
| `isNewGuest` | `booked == 0`: a neutral "new guest", never a warning |
| `takeawayNoShows` | the account's **takeaway orders marked not collected** whose promised ready time is in the window (2026-10-23) - a count of its own, in none of the above and not in `isNewGuest`; each counts toward the no-show block below. See [takeaway.md](takeaway.md#6a-the-guest-record-the-no-show-block-and-the-reports) |
| `blockedUntilUtc` | while a no-show block is in force (below); absent otherwise |

**Never counted:** a venue's cancellation or decline (`CancelledByVenue`, `NoAnswer` included - the venue's
doing), a cancellation in good time, a booking still waiting for the venue, future bookings. **Never from
anything a stranger could create:** only the account's own bookings count - not a booking whose typed
`guestPhone` is the guest's number, not a booking made without proof with it - so nobody can spoil another
person's record. No venue names, no dates, nothing per booking.

**Where.** Staff reads only, on `ReservationView.guestRecord`, and **only beside a booking that is pending or
confirmed and today or still to come** (branch calendar) with a proved account behind it (review S3): the console
list (`GET /api/branches/{id}/reservations`), the floor's awaiting cards (`GET .../reservations/awaiting` →
`reservation.guestRecord`), the answer to approve, the console overview (`GET /api/branches/{id}/overview` →
`todayBookings[].guestRecord`), and the floor (`GET /api/branches/{id}/tables/floor` →
`tables[].nextReservationGuestRecord`, and `sittingGuestRecord` for the party seated there on a proved account's
booking; one extra statement for the whole floor, only when a table has such a guest). Never beside a past, finished or cancelled booking - the reject, no-show and
release answers included - so a booking a venue once served is no window on the guest's evenings since. Never on a
diner or public read of a booking. The guest's own: `GET /api/reservations/mine` → top-level `guestRecord`.

**Cost.** One statement for every account on a list - per account, its counts and its block, each a correlated
seek on `IX_Reservations_DinerUserId_StartUtc` / the blocks' index - never a query per booking (`GuestRecords`; pinned by
`GuestRecordTests.The_console_list_reads_every_guests_record_without_a_query_per_booking`). A maintained aggregate
was rejected: every status change and two sweeps would have to keep it, and the window slides daily.

**Privacy.** The record is shared with **every venue the guest books at**: counts across all venues, never where
or when. That is a disclosure the guest must be told about before booking - the terms, and a line near the Book
button in the app and on the public page ("Venues you book with see how many of your bookings in the last 12
months you kept, missed or cancelled late - never where"). The guest sees exactly what venues see, on `/mine`.

- **Deleting the account resets the record** - the price of erasure, accepted (review S6): the bookings it leaves
  to the venues lose the link to the person, so a new account on the same number starts as a "new guest" (neutral;
  auto-confirm needs three visits). **A block in force survives it**, through the number on the block row, until
  it ends; an ended block's number is cleared by the daily sweep (review S7).
- **The record follows the account that holds the number** (review N10): a number given up and taken by someone
  else who proves it takes over that account, its record and any block with it - as for every account feature.
  Say so in the terms.

### Auto-confirm guests with a good record

`ReservationPolicy.AutoConfirmGuestsWithGoodRecord` (`autoConfirmGuestsWithGoodRecord` on `GET/PUT
.../reservation-policy`, optional on PUT - omitted keeps the stored value; default **off**; in `/defaults`).
A **good record** (`GuestRecordRules.IsGood`) is **came ≥ 3, no no-shows, no late cancellations** in the window - and
no takeaway order left uncollected (2026-10-23).

It matters only when the branch's `autoConfirm` is **off** ("we approve every booking"): then a good-record
guest skips **exactly one** reason to wait - `ApprovalTrigger.BranchApprovesEveryBooking` (1), the trust
question the venue was asking. **Never skipped:** `ShortNotice` (4 - somebody may be sitting at the table; a
floor question), `EarlierGuestConsent` (5 - another guest's answer), `LargeParty` (2 - tables may need moving),
`NoShowHistory` (3). Decided in `DecideStatusAsync` in the usual order - short notice, then approve-every
(skipped for a good record), large party, no-show history - for a new booking and a move alike.

### No-show block

Three no-shows in three days block a guest from booking for a week (the user's rule, 2026-10-20;
`NoShowBlockRules`: `Threshold` 3, `WindowHours` 72, `BlockDays` 7, `UndoUntilLocalHourNextDay` 5). Hardened after
review (B1) so that one venue, or one careless waiter, cannot block anybody.

- **What a no-show is, when it is marked.** `POST /api/reservations/{id}/no-show` (or `/release` outcome 1;
  `ReservationService.ReleaseAsync` is the only writer of `NoShow`) records **who marked it**
  (`NoShowMarkedByStaffId`) and **tells the guest** - push `reservation.no-show`, feed entry `booking-no-show`
  ("Marked as a no-show at {venue}… if this is wrong, contact the venue"), so a mistake is put right before it
  counts. It **counts toward a block** (`NoShowCountsTowardBlock`) only when marked **after the guest's grace**: the
  start plus the branch's `GraceMinutes`, or the extended hold when later. Marked sooner, it is recorded, counts in
  the record, and never toward a block.
- **What counts toward a block** (`GuestRecords.BlockingNoShowDaysAsync`): the account's own bookings made with a
  proved number, marked no-show after the grace and not withdrawn, **at most one per venue per service day**
  (the booking's `LocalDate`), whose **start** is within the 72 hours before now - **and after the account's last
  block ended or was lifted** (review S4: a lifted block's no-shows never block again). The window is over start
  times: the start is when the guest failed to come, the marking time is the venue's business (a batch marked at
  closing blocks nobody; marking late does not let the third slip). Three such venue-days → blocked. One venue can
  therefore never block a guest in one evening, however many of their bookings it holds.
- **Takeaway orders not collected count alike** (2026-10-23): the account's takeaway orders marked no-show, whose
  **promised ready time** is in the same window, on the order's service day (`LocalDate`) - in the same venue-day pairs,
  so a missed booking and an uncollected order at one venue on one day are one. `POST /api/takeaway-orders/{id}/no-show`
  records it under the guest's account lock and judges the block in the same transaction (`NoShowBlocks`, the writer the
  booking's no-show uses), with the same effect. It cannot be withdrawn (a takeaway no-show is final; see
  [takeaway.md](takeaway.md)), and a blocked guest's takeaway order is refused with the same `403`.
- **Concurrency.** The no-show is recorded under the booking's **table lock** and then the guest's **account
  lock** - the order every booking takes them in - with the lock timeout bounding both (review N9). Two no-shows
  marked at once at two venues are judged one after the other, the second sees the first committed: the third is
  never missed and the block never written twice
  (`GuestRecordTests.Two_no_shows_marked_at_the_same_moment_write_exactly_one_block`). A block already in force is
  left alone. A replayed release (already NoShow) returns before any of it.
- **Effect, in the same transaction.** A `BookingBlocks` row (`DinerUserId`, the account's proved `PhoneE164`,
  `BlockedAtUtc`, `BlockedUntilUtc` = now + 7 days, `Reason` 1). Every booking of the guest's **not yet started**
  (pending or confirmed, start after now), at every venue, read with an update lock (so a guest cancelling one that
  moment waits rather than failing the no-show, review N8), is cancelled: status `CancelledByVenue` with
  **`CancelledBecause` 1 `NoShowBlock`** (`cancelledBecause` on reads) and the reason "Cancelled automatically: the
  guest missed three bookings in three days.". As for any cancellation, its open table questions are withdrawn and
  its unsent reminders and feed entries dropped. **Each venue is told** (review S8): a row on its branch change
  stream - the booking's table, same status, `reservationId`, **`signal` 4 `BookingCancelledByBlock`** - and, after
  the commit, a table held for the booking is released through the state machine. Reports count these as
  `systemCancelled`, not as the venue's cancellations; the overview counts them in `cancelledBySystem`. The guest
  gets **one** push (`diner.bookings-blocked`) and **one** feed entry (`bookings-blocked-no-shows`, params
  `blockedUntilUtc`, `blockedUntilLocalDate` - the end on the triggering venue's calendar, review N3 -
  `cancelledCount`).
- **Refusal.** While a block is in force for the account **or its number**, `POST /api/reservations` and every
  move (the app's reschedule, the manage link's) answer **`403 booking-blocked-no-shows`**,
  `context.blockedUntilUtc` - asked early (after the replay check, so a retry of a booking made just before the
  block answers as the booking, review N7) and again under the account's lock in the writer, so a booking racing
  the third no-show is refused or cancelled with the rest, never left standing. Keyed on the number too, so
  deleting the account and proving the number on a new one does not escape it.
- **Told to the floor at once:** the answer to the no-show that blocked the guest carries `guestBlockedUntilUtc`
  (again on its replay). Approving or rejecting a booking the block cancelled is
  `409 reservation-cancelled-by-block`; the manage link shows `cancelledBecause` 1.
- **Visible** as `guestRecord.blockedUntilUtc` on every read that carries the record - also when the block holds
  through the number only (a re-registered account, review N1).
- **Withdrawing a no-show** (review B1c). `POST /api/reservations/{id}/undo-no-show` `{ clientCommandId, reason? }`,
  waiter or above at the branch - the tablet and the console - **until 05:00 the morning after the booking's
  service day**, branch time - a start before 05:00 belongs to the evening before, so a 01:00 booking's window closes
  at 05:00 that same morning (`NoShowBlockRules.ServiceDateOf`, the frontend's rule too) (`409 no-show-undo-window-closed`, `context.undoableUntilUtc`, after). The booking is
  `Confirmed` again; who, when and why are recorded (`NoShowUndoneByStaffId`, `NoShowUndoneAtUtc`,
  `NoShowUndoReason`). Under the same two locks, the guest's block in force is judged again as of when it began; if
  fewer than three venue-days are left, it is **lifted** (`LiftedBecause` 2) - nothing it cancelled comes back -
  and the guest is told (push `diner.bookings-unblocked`, feed `bookings-unblocked`). Already withdrawn: answered
  as it stands. Not a no-show: `409 conflicting-state`.
- **Lifting by a platform admin** (review B1f). `GET /api/platform/booking-blocks` lists the blocks in force;
  `POST /api/platform/booking-blocks/{blockId}/lift` `{ reason }` (required, 3-500) lifts one (`LiftedBecause` 1,
  `LiftedByStaffId`, `LiftReason`), **audited** as `booking-block.lift`, the guest told, under the guest's account
  lock. Nothing it cancelled comes back; already lifted answers as it stands; already ended is `409`.
- **Retention.** The block row is the record of why bookings were cancelled and is kept. The number on it outlives
  an account deletion only while the block is in force; the daily feed sweep then clears it
  (`DinerNotificationRetention.ForgetEndedBlockNumbersAsync`, review S7), as deletion does at once for blocks
  already over.

### Booking events on the change stream

Every booking event writes one row on the branch change stream (`GET /api/branches/{id}/tables/changes`) for each
table it touches, in the same transaction (GR-FE-REVIEW B1): a booking made, or one whose status, table, time or
party changed - approved, declined (by staff, the guest before it, or the sweep), moved (its old table and its new
one), cancelled (app, manage link, venue), marked a no-show, a no-show withdrawn. The row is a same-status row on the
table, `actorType` 3 (system), naming the booking (`reservationId`), with **`signal` 5 `BookingChanged`**. It is
written by `BookingChangeStream`, a handler of the booking's domain events (`docs/domain-events.md`, bookings), so no
path can forget it; a booking a block cancelled has its `signal` 4 row instead. A tablet that follows the stream refreshes the table's booking line
(re-reads the floor) on signal 4 or 5 - it does not fold the row's status, which is only the status the save saw.

**Sequences.** `TableStateChanges.Sequence` is one identity across every branch, with holes (rolled-back inserts, an
idempotency key refused, a server restart). A client must not expect `lastSequence + 1`: within a page every row has a
greater sequence than the one before, every row of the branch after `afterSequence` is returned (up to `limit`, with
`hasMore`), and a gap between two numbers is never missing data.

**The floor and its sequence agree.** `GET .../tables/floor` reads the tables and `maxSequence` in one statement, and
the database runs with `READ_COMMITTED_SNAPSHOT` - EF turns it on when it creates the database, which is how the
deploy's migration bundle creates it - so that statement is one consistent snapshot: a change committed during the
read is in both or in neither. Pinned by
`GuestRecordTests.The_database_reads_committed_snapshots_so_the_floor_and_its_sequence_agree`. (A database created
by hand must have it switched on: `ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON`.)

## 3e. A table's evening: when one booking may follow another

The overlap rule (§2) says when two sittings physically clash. It does not say whether a table whose
previous party sat down twenty minutes ago should be sold again, or what to do with a Yerevan cafe
table whose guests booked ninety minutes but may well stay three hours. These are the product's
answers - **fixed rules in code (`TableSequencing`), not branch settings**, applied by one pure
function that the booking writer runs under the table's lock and every availability read runs over
the same rows: the single slot, the day slots, a change of booking, and the floor's list.

For a start `S` on a table, `P` is the latest live booking (`PendingApproval`, `Confirmed`, `Seated`)
starting at or before `S`, and `N` the first starting after it. `buffer` is the branch's clearing
time - **5 minutes** by default.

| # | When | The table at `S` | Reason / field |
|---|---|---|---|
| 1 | `S < P.start + 90 min` - unless `P` already promised to be gone by `S - buffer` | **refused** - whoever has it is barely seated | `TableJustBooked` (11) |
| 2 | `S > N.start - 45 min` | **refused** - too little time to sit down | `NextBookingTooSoon` (12) |
| 3 | `S + turn + buffer > N.start` | **must free by** `N.start - buffer`: the sitting ends then, and the diner accepts it before booking | `mustFreeByUtc`, `acceptsFreeByUtc` |
| 4 | `S < P.start + 180 min`, or `P`'s sitting plus clearing still runs into `S` - unless `P` already promised to be gone by `S - buffer` | **requestable**: `P`'s guest is asked to have left by `S - buffer` | `isRequestable`, `ApprovalTrigger.EarlierGuestConsent` (5) |
| 4a | as 4, while another booking is already asking `P`'s guest | **refused** - one question to one guest at a time | `EarlierGuestBeingAsked` (13) |
| 5 | otherwise | **free** - after the overlap rule (§2) over every other booking, and the open sitting's projection over a walk-in at the table | |

Worked, restaurant (90-minute turn), 5 minutes clearing, `P` at 18:00:

```
18:00         19:30                          21:00
  |-- refused --|------ asked for --------------|------ free ------
  P            P+90                            P+180
                 19:30 asks P to leave by 19:25; 20:59 by 20:54; 21:00 asks nobody
```

and `N` at 21:30: 20:46 is refused, 20:45 must free the table by 21:25, 19:56 must free it by 21:25,
19:55 sits a full 90 minutes and is clear exactly at 21:25. `TableSequencingTests` pins every edge to
the minute (89/90, 44/45, 179/180, `N - 5`, the clearing time).

**The clearing time is the branch's.** The time to leave by is `N.start - buffer` - five minutes before
the next party at the shipped default, fifteen at a branch that kept fifteen - so a promise kept leaves
exactly the clearing time the branch asked for, and the overlap rule underneath agrees with it.

**A promise is stored, and cuts the booking.** A booking made with a time to leave by
(`acceptsFreeByUtc`), and an earlier booking whose guest said yes to a later one, carry
`PromisedFreeByUtc`; when it is sooner than the booking's end, `EndUtc` is cut to it, so every rule
that reads the interval reads the promise. A promise is never a lengthening: a booking ending 19:30
whose guest promised to be gone by 19:40 keeps its 19:30 end, and the 19:40 beside it. A guest who
already promised to be gone in time neither blocks the next ninety minutes (rule 1) nor is asked again
(rule 4): a 21:30 booked first, then a 20:45 that promised 21:25, leaves the 21:30 its own slot - judged
again for a change of party size it is free, on the room screen leaving it out and under the lock.

**A confirmed booking keeping its table and start is never judged again by these rules** - a smaller
party, a party-size change that does not grow: nothing the venue said yes to got harder to keep, and
legacy data from before them must not cost anybody their table. Only a party sitting into it now still
refuses it, as it always has. A change that grows the party or moves it is judged as any booking.

**A party seated late is judged as sitting until its projected end.** A booking seated against its
reservation is judged as that booking - not as a walk-in in the way - running until whichever is later
of its booked end and the seat time plus the turn time, unless its guest promised a time. The writer
reads that booking whatever its booked times (a 13:00 seated at 17:00 is three hours past its end), as
the availability read does, so the two cannot disagree about it.

**One read for every writer** (`TableEvening`): an insert and a move under the table's lock, going back
to a declined change's slot (§3c - free to go back to only when nothing refuses it, nobody has to be
asked and no booking since needs it gone before it ends; the overlap rule alone is no longer enough),
and judging a waiting booking again when the guest before it changes.

**Walk-in holdback (30 minutes) is unchanged** and does not contradict these rules: it is a warning to a
waiter seating somebody near a booking, and the moment the booked party may claim the table with its
code. With five minutes of clearing that moment usually falls while the party before is still there;
"I'm at my table" then answers `booking-table-occupied`, as it always has for an occupied table.

### Must free by: the diner accepts the time before booking

Availability says it per table (`mustFreeByUtc`, `mustFreeByLocal`), and per slot when no table on offer
gives a full sitting. `POST /api/reservations` and `/reschedule` take `acceptsFreeByUtc`; the writer
judges the table again under its lock - the next booking can arrive or go in between - and when a time
to leave by is required, the request must carry exactly that instant (to the minute):

```json
422 {
  "code": "reservation-free-by-required",
  "context": { "field": "acceptsFreeByUtc", "requiredFreeByUtc": "2026-10-22T17:25:00Z", "requiredFreeByLocal": "21:25",
               "nextBookingStartUtc": "2026-10-22T17:30:00Z", "tableId": "guid", "tableLabel": "4" },
  "errors": { "acceptsFreeByUtc": ["..."] }
}
```

Missing and mismatched answer the same. Sent for a table with no such limit it is ignored. A booking that
already promised exactly the required time for the same start (a change of party size) need not send it
again. The booking answers with `promisedFreeByUtc` and the cut `endUtc`.

### Requestable: the guest before is asked

A booking for a requestable table lands `PendingApproval`, and a **`TableShareRequest`** is opened beside
it, in the same transaction under the table's lock: `{ earlierReservationId, laterReservationId,
requestedStartUtc, freeByUtc (= S - buffer), status, asksEarlierGuest, fallbackTrigger, createdAtUtc,
expiresAtUtc, decidedAtUtc }`, status `Pending` (1), `Accepted` (2), `Declined` (3), `Expired` (4),
`Withdrawn` (5). `fallbackTrigger` is why the later booking would still wait without the question - the
reason the floor decides it anyway (auto-confirm off, a large party, a no-show history, short notice) -
or null when it would be confirmed at once.

| The later booking is | It waits as | Who answers | Window |
|---|---|---|---|
| short notice (today, or under six hours) | `ShortNotice` (4), as §3d | the floor, from the awaiting list - the card names the time to ask for (`earlierGuestFreeByUtc`) | 15 minutes |
| another day, the earlier guest booked from the app, has a live push device, and nothing else about the later booking needs a human | `EarlierGuestConsent` (5) | **the earlier guest**, from a push and a card on their booking - **only them**: the floor's approve and reject are `409 reservation-awaiting-earlier-guest` | 2 hours, never past the earlier booking's start |
| another day, otherwise - the earlier guest booked on the web, has no device, or the later booking needs approval anyway (auto-confirm off, a large party, a no-show history) | `EarlierGuestConsent` (5) | the floor, from the awaiting list, after calling them | **the same 2 hours**, never past the earlier booking's start |

Every question has a clock: the later booking's `approvalExpiresAtUtc` is the request's `expiresAtUtc`,
`min(created + 2 h, the earlier booking's start, the later booking's start)` another day and the
short-notice fifteen minutes tonight. Until 2026-10-18 the floor's question for another day had **no**
clock - the later guest could wait for days on a venue that never made the call - and its request ran to
the later booking's start; requests from before then keep that, and a booking from then moved or judged
again gets the two hours (never a later deadline than it had). Both kinds of another-day wait carry the
same trigger and a deadline, so **`awaitsEarlierGuestAnswer`** on every `ReservationView` says which it
is: `true` while the booking is the later booking of a pending request asked **in the app** - only that
guest answers, the floor's approve and reject are `409 reservation-awaiting-earlier-guest`, show the
countdown and not the buttons - and `false` otherwise (the floor's questions included). It is read beside
each row in the same statement on the list endpoints (`/api/reservations/mine`, the branch list), never
one query per booking. The later guest stays anonymous to the earlier one: the request carries times,
never a name.

- **Yes** (the earlier guest's answer in the app, or the floor approving a booking it decides): the later booking is
  `Confirmed`, the earlier one promises to free the table by `freeByUtc` (`PromisedFreeByUtc`, its end
  cut when sooner), the request is `Accepted`, and both diners are told - the later one a
  `reservation.decided` approval, the earlier one a `reservation.table-request-decided` push ("please
  free the table by 19:40") and a `table-request-decided` feed entry.
- **No**: the later booking is declined - `CancelledByVenue`, `declinedBecause` **3
  `EarlierGuestDeclined`** - or, for a change of a confirmed booking, goes back to the slot it had (§3c).
  The request is `Declined`; the later diner is told (`declinedBecause` "3" on the push and the feed).
  The floor declining is `StaffDeclined` (1) as ever.
- **No answer by `expiresAtUtc`**: the approval sweep (§3d) declines the later booking -
  `declinedBecause` **4 `EarlierGuestNoAnswer`** when it was waiting on the earlier guest in the app, **2
  `NoAnswer`** when it was the floor's to answer, tonight's or another day's: the venue did not answer,
  with the same apology push as any short-notice request the venue let run out - not the earlier guest's
  silence, since they were never asked in the app. The request is `Expired`. The earlier booking is
  untouched.
- A **yes about a booking that has moved** since it was asked - to another table, or past the time it is
  asked to leave by, under another table's lock - is refused, `409 table-request-not-pending`, rather
  than written onto a booking it was never about.

**One open question per earlier booking.** While a request to a guest is pending, no other booking can
ask them: the table is refused at those times (`EarlierGuestBeingAsked`) by the writer and by
availability alike. Every booker of the table takes its lock first, so the writer's own check settles a
race between two later guests - the second sees the first's request and is refused; a filtered unique
index (`UX_TableShareRequests_EarlierReservationId_Pending`) holds the rule for any writer that skipped
it, and one per later booking (`UX_TableShareRequests_LaterReservationId_Pending`). Once the first is
answered - either way - the table can be asked for again.

**What a move or a cancellation does.**

- The later booking is **moved**: the new slot is judged afresh - confirmed, cut short, or asking. Still
  asking **the same guest the same way**, it keeps **the same request** - retimed to the new start, no
  second push to the earlier guest, no new card. Otherwise its request is withdrawn inside the move's lock
  and a new one asked. **Cancelled or released**: withdrawn.
- **A waiting booking that moves never gets a later deadline than it had.** Moved from the two hours into
  tonight it gets tonight's fifteen minutes, not what was left of the two hours; moved around while it
  waits on the same guest - in the app or the floor's - the two hours do not restart, and a move from
  one guest to another keeps the deadline it had. A move to a slot whose wait has no clock (a large party
  next week, a branch that approves every booking, with nobody before it to ask) has none.
- **The earlier guest's own change** - a move, a party size - is not made to promise a time, nor refused,
  because of a booking that is only asking for its table: those are left out of judging it, on the room
  screen leaving it out as under the lock. Afterwards each booking waiting on that guest is judged again
  with the earlier booking where it now is - and the same when the earlier booking is **cancelled,
  released or declined**, or its own declined change sends it back to the slot it had:
  - still the booking before it within three hours: the question stands;
  - free now - moved off the table, early enough, after it, or promised a time that covers it: the request
    is withdrawn and the later booking decided again without it, from `fallbackTrigger` - **confirmed**
    (its diner told) when nothing else needs a human, left as it is at short notice, otherwise waiting for
    that reason with no clock - the two hours were the question's. So a later booking that went to the
    floor only because the guest before booked on the web is confirmed when that guest cancels;
  - another booking's sitting now runs into it (rare): withdrawn, and the floor decides, by the deadline
    the booking already had, never past the start of the guest it now waits on (two hours from now, never
    past either start, for one from before every such wait had a clock);
  - crowded out - the earlier booking now leaves it no room: the change stands and is that guest's no -
    the later booking is declined, `EarlierGuestDeclined`. A later booking that was itself a change of a
    confirmed one is declined outright here (going back to its old slot would need that table's lock
    inside this one) - the one case that does not restore.
- An open request on an account deleted meanwhile is left to run out (in the app) or to the floor.

### The earlier guest answers

`POST /api/reservations/{reservationId}/table-requests/{requestId}/answer` - `VerifiedDiner`, the
diner's **own** booking (somebody else's, or a request the floor is deciding, is 404), the
`clientCommandId` filter as every diner write.

```json
{ "accept": true, "clientCommandId": "guid" }
```

Answers `200` with the earlier guest's `ReservationView`. Idempotent: a retry with the same id is the
booking as it stands (`wasReplay`), and the same answer twice under different ids - one after the other,
or two quick taps racing - is answered with the booking as the first left it; the other answer once
decided, a request past its deadline, or one withdrawn is **409
`table-request-not-pending`** (`context`: `reservationId`, `requestId`, `status`, `expiresAtUtc`). The
same id on another command, or the other answer, is `409 client-command-id-in-use`. The later booking's
row version settles the answer racing the sweep, the floor, or the other diner's move.

The question is on the earlier guest's booking while it is open - `GET /api/reservations/mine` and every
view of their own booking:

```json
"incomingTableRequest": { "requestId": "guid", "requestedStartUtc": "...", "freeByUtc": "...", "expiresAtUtc": "...",
                          "requestedLocalTime": "19:45:00", "freeByLocalTime": "19:40:00" }
```

### What staff see

The awaiting list (§3d) lists `EarlierGuestConsent` bookings the floor decides - not the ones waiting
on the earlier guest's own answer - with `trigger` 5, the two-hour `approvalExpiresAtUtc` to count down to, the previous
booking and its guest's number to call, and **`earlierGuestFreeByUtc`**: the time to ask them for
(also set on a short-notice booking whose table is within three hours of its previous one). While a
question is open, **`previousBooking` is the booking it asks** - whatever day it is on (a 23:00 before a
00:30 is the day before's), never another booking that happens to sit between. Approving it is the
floor's yes for that guest. `previousBooking.promisedFreeByUtc` and `nextBooking.promisedFreeByUtc`
show promises already made; every `ReservationView` carries `promisedFreeByUtc`. The floor's table state
carries `nextReservationPromisedFreeByUtc` ("must free by 21:25" on the next-booking line) and
`sittingPromisedFreeByUtc` for the party seated now.

## 3f. The console's Overview: the service day, and a menu on every booking

`GET /api/branches/{branchId}/overview` is the venue console's first screen (`ManagerOrAbove` +
`BranchScoped`, then the stored-row branch guard): an owner or general manager at any branch of their
venue, a branch manager at their own branch only, a platform admin anywhere (an unknown branch is 404 for
them). A waiter's or cook's tablet session is refused - the floor has its own reads. No parameters.

**Today is the branch's service day** (`serviceDate`), not the calendar's (`localDate`). It is the calendar
day at the branch, except while last night's block is still open past midnight: at 01:00 on Sunday a bar
open 20:00-02:00 is still in Saturday's service, and the Overview at 01:00 says what it said at 22:00 - the
same bookings, the same chart, the takings carried on. **A booking belongs to the service day it starts
in**: 00:30 on Sunday, inside Saturday's block, is Saturday's; 00:30 on Saturday was Friday night's. The
bookings are read for the service date and the day after and sorted by the weekly hours. A block closing at
00:00 runs past nothing: it reads "until 00:00", and at 00:30 the next day is today.

| Part | What it says |
|---|---|
| `graceMinutes` | the policy's grace: how long past its start a late party's table is held |
| `hours` | `isOpen`; while open `closesAt` and `closesNextDay` (after midnight tonight - false for a 00:00 close and for last night's block, which closes today); while shut the next `opensAt` today. The weekly hours read as `BranchOpenNow` reads them for the diner's listing |
| `bookings` | the service day's bookings by status: `total` (pending, confirmed, seated, completed, no-shows), `late` (confirmed, start passed), `cancelled` (declined requests included), `people`, and `pendingChanges` |
| `inRoom` | party sizes at the open sittings, occupied tables of the active ones, out of service |
| `openTabs` | tabs open or settling, and the sum of their server-computed `RemainingAmd` |
| `revenue` | closed tabs' totals from `sinceUtc` - **the service date's first opening**, or its local midnight when it has no hours - until now, against the same weekday a week before from the same opening **for as long on the clock**: 18:42 against last Saturday's 18:42, 01:00 on Sunday against the Sunday before's 01:00. A tab counts at the moment it closed, so a bar's tab settled at 00:45 is in Saturday's service here, where the revenue report (calendar days) files it on Sunday |
| `load` | `capacity` (seats at the active tables **in service**) and booked people per hour - confirmed, seated and completed in `people`, unanswered requests and the slots changes ask for in `pendingPeople` - counted in every hour a sitting touches, as the occupancy report counts. Hours are **numbered from the service date's midnight**: 0-23, then 24 for 00:00-01:00 of the next date, 25 for 01:00 (label `hour % 24`); they run from the first opening (or booking) to the last open (or booked) hour |
| `todayBookings` | the service day's bookings by start, **without the cancelled and declined**, at most 200, `todayBookingsTruncated` when there were more (the counts and the chart read them all). Each carries `localDate` (the next date for a sitting after midnight), `tableId`, `tableStatus`, `channel`, `changeRequested`, and for a `Confirmed` booking only `holdExpiresAtUtc` and the late nudge |

**A change that is waiting counts where the booking is still promised.** A confirmed 19:00 moved to 21:00 at
short notice is `PendingApproval` with its confirmed slot kept (§3c); until the floor answers, the diner
still has the 19:00. So it is counted as confirmed (and in `pendingChanges`), charted at 19:00, and listed
at 19:00 at its old table with `changeRequested` true - its `status` stays 1, because the floor's actions on
it wait for the answer, which is in the approvals list. The 21:00 it asks for is in `pendingPeople`. A
change that was declined leaves the booking `Confirmed` at its old slot with `DeclineReason` set; that is a
live booking and lists like any other - which is why the list no longer carries a decline reason at all.

**The late nudge is the one the outbox holds** (`reservation.late-nudge`, keyed by the booking):
`lateNudgeAtUtc` is when it went (`lateNudgeSent` true) or when it is due. A booking moved since carries a
nudge per start, and the latest stands. Absent when none is stored - a booking with no app account. There
is no manual nudge.

At most seven statements, however busy the day - the guard's two, the branch with its room and tabs, the
hours, the bookings, the takings, and the nudges when a confirmed guest has the app; `BranchOverviewTests`
counts them. No guest phone number anywhere in it.

**The row menu uses the floor's own routes**, and the console's sign-in may already call them, because
`WaiterOrAbove` admits an admin-panel identity of the three venue ranks as well as a PIN session, and the
branch is checked from the stored row: *seat* is `POST /api/branches/{branchId}/tables/{tableId}/seat-reservation`,
*no-show* `POST /api/reservations/{id}/no-show`, *cancel* `release` with `outcome` 2 (the venue's doing,
not counted against the diner, who is told in their feed), *finish and free the table* the table's `free`
(the booking completes), and the approvals `approve` and `reject`. There is **no manual nudge**: the late
nudge goes out on its own at `lateNudgeAtUtc`. `ConsoleBookingActionsTests` sends each action as every
kind of caller: the branch's waiter, its manager, the general manager and the owner act; a waiter or
manager of the sibling branch, a cook, a diner and another venue's staff are refused and the booking does
not move.

**Finishing names the booking.** A plain `free` closes whatever sitting is open at the table, which is right
for the tablet - the waiter is looking at the table - and wrong from a console row that may be stale: the
waiter may already have freed the booking's table and seated a walk-in, and "finish" would end the walk-in's
sitting, again on every retry. So the console sends `reservationId`, and the table is freed only while the
sitting open at it is that booking's and the booking is `Seated`; otherwise `409 table-sitting-changed`
(`context`: `tableId`, `tableLabel`, `reservationId`, `tableStatus`, and `currentSessionId` when somebody
else is sitting there) and nothing changes. Without `reservationId` the route behaves as it always has.
Pinned by `ConsoleBookingActionsTests.Finishing_a_booking_whose_table_has_moved_on_leaves_the_new_sitting_alone`.

**`release` on a booking already let go answers its first outcome.** A booking already `NoShow` or
`CancelledByVenue` is not released again: the answer is `200` with `wasReplay` true, `outcome` from the stored
status (1 for `NoShow`, 2 for `CancelledByVenue` - whichever outcome the repeat asked for), `reservation.status`
as stored, `tableFreed` false and `countsTowardNoShowThreshold` true only for a no-show. A tablet replaying its
queue cannot put a second no-show on a diner's record, and the console can map the repeat to the row as it is.

## 3h. Pre-orders

Decided by the user on 2026-10-21. A guest with a booking may choose dishes ahead - a **pre-order** - so the
kitchen has them ready when the party arrives. The design and its alternatives are argued here; the wire is in
the OpenAPI document (`replacePreOrder`, `listDuePreOrders`, `sendPreOrder`, ...).

### What is stored, and what is read

One `PreOrder` per booking (`PreOrders`, unique on `ReservationId`), with its `PreOrderLines` - the dish, its
**name and price as ordered**, a quantity and a note for the kitchen - and a booking-level note. Beside those it
stores only **what people did**: a waiter sent it (`SentAtUtc`, `SentByStaffId`), a waiter marked the party not
coming (`NotComingAtUtc`), it became an order on the party's tab (`TabOrderId`).

Its **state is read, never stored** (`PreOrderRules.StateOf`), from those, the booking's status and the clock:

| `state` | When |
|---|---|
| 1 Editable | more than two hours before the start, booking pending or confirmed |
| 2 Locked | two hours before the start or later (or the party is seated), not sent |
| 3 Sent | a waiter sent it - **whatever the booking does next** |
| 4 NotComing | a waiter marked the party "not coming / no answer"; still sendable |
| 5 Cancelled | the booking was cancelled, declined, expired or block-cancelled, or ended (a no-show, finished) without a send |

Why derived: a booking ends in five places (the guest's and the link's cancel, release and no-show, the no-show
block, `PendingDecline` for a decline or an expiry, a crowded-out request in `TableShares`) and comes back in a
sixth (undoing a no-show). A stored "cancelled" would have to be remembered by all six; read off the booking it
cannot drift, and **undoing a no-show brings an unsent pre-order back** with the booking.

### The guest's edits - app and manage link, one path

`PUT /api/reservations/{id}/pre-order` and `PUT /api/public/bookings/{token}/pre-order` save the **whole** list;
`.../pre-order/clear` removes it. The app's is the guest's own booking (another's is 404, as for an id that is no
booking - the reschedule's rule); the link's is the booking behind the token. Checked in this order, before
anything is written:

1. **Replay** - `clientCommandId` recorded in `ProcessedCommands` (`pre-order.replace` / `pre-order.clear`, the
   booking's account as actor, `{ reservationId }`) in the same save. A retry answers the pre-order as it stands
   (`wasReplay`); the id on another command, booking or account is `409 client-command-id-in-use`.
2. **The booking still to come**, pending or confirmed - else `409 reservation-not-changeable`.
3. **Not with the kitchen** - else `409 pre-order-already-sent`.
4. **More than two hours before the start** (`PreOrderRules.EditableUntilHoursBefore`) - else
   `409 pre-order-locked` with `lockedAtUtc`. A constant, not a setting: the kitchen's lead time is what the
   product promises every venue. A booking made less than two hours ahead cannot carry one.
5. **The branch takes pre-orders** (`Branches.AcceptsPreOrders`) - else `409 pre-orders-not-accepted`. Saving
   only: a guest can always clear theirs, and a branch that switches off keeps honouring the ones it has.
6. **The shape**: 1 to 30 lines, 1 to 20 of a dish, at most 60 dishes in all, a line note at most 200
   characters, the note at most 500 - `422 validation-failed`, every field named (`lines[2].quantity`).
7. **The dishes**: on this branch's menu as a diner sees it (complete), not sold out, not excluded from
   pre-orders - else `422 pre-order-items-unavailable`, every dish named with its reason (1 not on the menu,
   2 sold out, 3 excluded), nothing saved.

**Prices are locked.** A dish keeps the price it had when it **first** entered the pre-order (matched by menu
item); changing its quantity or note keeps it, a dish new to the pre-order is priced now. The tab order it
becomes carries those prices.

**A dish that sells out afterwards stays** - it was promised - and reads `availableNow: false` on every read, so
the waiter can tell the guest; a save that keeps it is refused, so the guest drops or swaps it rather than the
server silently removing food somebody chose.

**Exclusions are a flag on the dish** (`MenuItems.AvailableForPreOrder`, default true), edited as a list from
the console (`PUT /api/branches/{id}/pre-order-settings`, `excludedMenuItemIds`, the whole list each time). A flag
follows the dish - a deleted or renamed dish needs no list clean-up - and the diner menu says per dish whether it
can be pre-ordered in the same read.

A link holder's edit is recorded against the booking's account, as the app's is; the account is not told (unlike a
move or a cancel from the link), because nothing about the booking itself changed.

**Two saves at once are serialised by the account's lock** (`DinerAccountLock`), taken - inside the save's
transaction - **before** the replay check and every read. The second of a double tap waits for the first to commit,
then reads what it wrote: the same `clientCommandId` is a clean replay, a new one edits the pre-order the first
created and does not write the question twice. Clearing takes the same lock. A unique key that still fires (the
command's, `UX_PreOrders_ReservationId`, the question's outbox key) is answered as the replay or a `409
concurrent-update` - never a 500. `PreOrderTests.Concurrent_saves_and_clears_of_one_booking_never_answer_500`.

**A dish a pre-order names is never hard-deleted.** Deleting it marks it unavailable instead, as for a dish on an
order; deleting its category is refused the same way. The pre-order's lines keep the name and price they were ordered
at either way (`PreOrderLines.MenuItemId` is a restricting key: they are the record of what was promised).

### A move carries it

A rescheduled booking is the same row, so its pre-order goes with it and every rule is judged against the **new**
start. Moved to another day it is editable again; moved into the last two hours it is **kept, locked** - neither
dropped (the guest ordered in time) nor refused (the move would trap them); the waiter still decides. A waiter's
"not coming" was about the old time and is cleared, and so is the guest's **"on my way"** - an answer about the old
start (`Reservation.Reschedule`, and the return to the confirmed slot when a waiting change is declined, both forget
it whenever the start changes). **A booking whose pre-order was sent cannot move**:
`409 pre-order-already-sent` - food cooked for seven cannot be moved to tomorrow; cancelling stays possible.
The check is inside the move's lock, and the move saves the pre-order's row, so its row version settles a move
against a send racing it under the old table's lock.

### "Are you on your way?"

**Every booking is asked** (2026-10-21, the owner's decision: a guest who says yes is not called by the waiter):
**30 minutes before the start** (`PreOrderRules.OnYourWayMinutesBeforeWithoutPreOrder`), or **45** while a pre-order
waits for the kitchen (`PreOrderRules.OnYourWayMinutesBefore`) - the kitchen needs the answer earlier than the floor
does. The push's words say which: the pre-order's mentions the kitchen, the plain one says nobody will need to call.
It goes to a booking with an account; a web booking's manage link asks from the same moment (`onYourWayAsked`). The
waiter's floor shows the answer beside the table's next booking (`nextReservationGuestConfirmedOnWayAtUtc`).

A push `reservation.on-your-way` (data `kind: reservation-on-your-way`, `actions: on-my-way,cancel`) and a feed
entry `booking-on-your-way` appearing when it is due, written **when the booking is made** - an outbox message keyed
`reservation:{id}:on-your-way:{startUtc:yyyyMMddHHmm}:30` (without a pre-order) or
`reservation:{id}:on-your-way:{startUtc:yyyyMMddHHmm}` (with one), the reminder's pattern, so it is written once
however often the guest saves, a move asks again at the new time (`ReplaceRemindersAsync`), and a cancel withdraws
it with the booking's other messages. Saving a pre-order moves it to 45 minutes and clearing it moves it back to 30
(`PreOrderQuestions.AskAgainAsync`, from the `OnYourWayQuestion` handler - never once the question for that start
went out; clearing a booking that never had a pre-order leaves its question as it is). It is withdrawn by every way
the question becomes moot before it is asked: **the guest answering**, **the waiter sending** the pre-order, or **marking the party not
coming** - each withdraws the unsent push and the feed entry that has not appeared (`PreOrderQuestions.WithdrawAsync`,
the reminder's pattern). It is **a confirmed booking's question only**: the handler asks a confirmed booking whose
pre-order, if it has one, was not sent or marked not coming, and whose guest has not answered; and the feed shows
`booking-on-your-way` only while its booking is confirmed (or went on from there: seated, completed, a no-show) - a
booking still waiting for the venue's yes does not see it until it is approved, and one declined, expired or
cancelled never does. A booking made or moved inside the moment it would be asked asks nothing: the guest who just
booked it has said as much.

The answers: **"Yes, on my way"** - `POST /api/reservations/{id}/on-my-way` or
`POST /api/public/bookings/{token}/on-my-way` - records `Reservations.GuestConfirmedOnWayAtUtc` once (a second tap
answers the first, `wasReplay`), **from two hours before the start** (`PreOrderRules.LockedAtUtc`, the pre-order's
lock) **until the booking ends**: an answer given days ahead says nothing about tonight, and the waiter would send food
on it. Earlier, later, or a booking not confirmed or seated is `409 on-my-way-not-open` with `context.opensAtUtc`.
A move to another start forgets the answer (above). The waiter's card then says "Guest confirmed". **"Can't come"** is the booking's ordinary
cancel, with its ordinary rules: one cancel path, one late-cancel rule. The manage link's read says when to ask
(`onYourWayAsked`).

### "I'm running late" and the venue's "Ask: are you coming?"

Added 2026-10-22 (the "I'm coming" artboard). Both live beside "on my way" and are forgotten with it by a move to
another start (`Reservation.ForgetOnMyWayUnlessStartingAt`).

**Running late (the guest).** `POST /api/reservations/{id}/running-late` or `POST /api/public/bookings/{token}/running-late`
with `{ "minutes": 10 | 15 | 30 }` (anything else: 422 `validation-failed` naming `minutes`). Under the window "on my
way" is taken in - from two hours before the start until the booking ends - for a **confirmed** booking: a seated
party is not late (`409 on-my-way-not-open` otherwise). It records `GuestRunningLateMinutes` and
`GuestRunningLateAtUtc`; **a later answer overwrites an earlier one**: late 10 then late 30 is 30; "on my way" after
"late" is on time after all and clears the late answer (and stamps "on my way" again); "late" after "on my way" is
recorded beside it. On-my-way and running-late are both **"the guest answered"**: either withdraws the unsent question
and its unshown feed entry, the handler does not ask a guest who answered, and the venue's ask is refused. The answer
returns `expectedArrivalUtc` (start + minutes). It does **not** hold the table any longer - the grace and the late
release are the policy's, as before. **"Not coming"** is the booking's ordinary cancel; the app asks for confirmation
first.

**Ask now (the venue).** `POST /api/reservations/{id}/ask-on-my-way` (waiter or above at the booking's branch, no body)
writes the question into the booking's **thread with the guest** - an `ask-coming` message with the asker's name, the
thread made if there is none, in the same transaction - and tells the guest **now**: push `message.to-guest` (data
`kind` `venue-message`, `ask` `"true"`, `categoryId` `venue-ask`) and feed entry `venue-message` (see
[messages.md](messages.md)); and records `StaffAskedOnMyWayAtUtc`. (Until 2026-10-23 it sent the automatic question's
push with `staffAsked: true` and a `booking-on-your-way` entry with `askedByVenue`; the handler still sends such a
message already queued.) Allowed for a **confirmed** booking, **from two hours before its start until its start**, while
the guest **has not answered**, and **at most once every 15 minutes** (`PreOrderRules.StaffAskIntervalMinutes`). Each
refusal is its own 409: `ask-on-my-way-too-early` (`context.opensAtUtc`), `ask-on-my-way-not-open` (not confirmed, or
started: `status`, `startUtc`), `guest-already-answered` (`answeredAtUtc`, and `runningLateMinutes` when that was the
answer), `ask-on-my-way-asked-recently` (`askedAtUtc`, `nextAllowedAtUtc`). The answer says `notified: false` when no
live account stands behind the booking (a web booking without one, a deleted account) - call instead. The venue's
ask goes whatever the floor decided about a pre-order (somebody tapped it a moment ago); **the automatic question is
then not asked on top** - the handler skips it once `StaffAskedOnMyWayAtUtc` is set. The ask's push is not sent to a
booking that is no longer confirmed or whose guest has answered by the time it goes.

**The answers in the thread.** "On my way", "running late" and "not coming" - by the app, the manage link or the
notification - each append an `answer` message from the guest and a `status` line after it ("held until 18:15" for late,
"cancelled" for not coming) to the booking's thread, made if there is none. "Not coming" is the booking's cancel, so it
writes into the thread only when the booking already has one or the answer window has opened (two hours before the
start); a booking cancelled next week is a cancellation, not an answer. The automatic question itself (30 or 45 minutes
before) stays a push and writes nothing.

**Exposed.** Every booking read (`ReservationView` - the diner's `/mine` and every staff list) carries
`guestConfirmedOnWayAtUtc`, `guestRunningLateMinutes`, `guestRunningLateAtUtc`, `staffAskedOnMyWayAtUtc` and
`onMyWayOpensAtUtc` (start - 2 h: when the app shows "I'm coming"). The manage link's read carries the same, and its
`onYourWayAsked` is also true once the venue asked. The pre-order rail's cards (`pre-orders/due`) carry
`guestRunningLateMinutes`, `guestRunningLateAtUtc` and `staffAskedOnMyWayAtUtc` beside `guestConfirmedOnWayAtUtc`; the
floor's tables carry `nextReservationGuestRunningLateMinutes` and `nextReservationStaffAskedOnMyWayAtUtc` beside
`nextReservationGuestConfirmedOnWayAtUtc`. Each change rides the change stream as signals 5 and 6, as "on my way" does.

### The waiter's card

`GET /api/branches/{id}/pre-orders/due` (waiter or above at the branch, from the stored staff row - the floor's
guard, `FloorStaffGuard`) lists this branch's bookings with a pre-order that are **confirmed**, from
`start - PreOrderMinutesBefore` (a branch setting, default 30, 5 to 120 - never earlier than the edit lock, so a
card can never be sent while the guest can still change it) until the booking ends, or **seated** while the
pre-order is not on their tab. Each card: table, room, time, party, guest name, `guestPhone` to call (empty for a
platform admin - the awaiting list's rule), `guestConfirmedOnWayAtUtc`, the guest record (the console list's rule),
the pre-order, who sent it or marked the party not coming, and the tab once it is on one. One statement for the
cards, two for the pre-orders, one for the records, whatever their number.

**Becoming due is not an event.** Nothing happens at "thirty minutes before" that anything writes, so the list
answers `nextDueAtUtc` - when the next card appears - and the tablet reads again then. Every pre-order change rides
the change stream as **signal 6 `PreOrderChanged`** (`PreOrderRailSignal`, a handler of every pre-order event,
written by the save beside signal 5 - `docs/domain-events.md`): saved, edited, cleared, sent, marked not coming, put on the tab, its held ticket moved by the kitchen, a move
clearing "not coming", the guest's "on my way" (or a move forgetting it), a held ticket voided because its booking
ended - and **a save of the branch's pre-order settings**, which changes cards without touching a pre-order row: one
row for every booking whose card is or could be on a rail now (a pre-order on a confirmed booking starting within
120 minutes and not over, or a seated one not yet on its tab) and one for the next booking after those, so every
tablet re-reads the rail and its `nextDueAtUtc` (`YallaDbContext.SignalPreOrderRail`). A dish selling out still
writes none: its `availableNow` reaches the card at the next re-read. Its own number because signal 5 means "re-read
the floor" and this means "re-read the rail". **Its row carries the table's status as the save leaves it** (the
tracked table's, when the same save changed it): a tablet that does not know signal 6 folds the row's status into
the table, so the seating that attaches a held ticket writes Occupied, never the Free it read before.

### Send to kitchen, before and after the party sits

`POST /api/reservations/{id}/pre-order/send` (waiter or above at the booking's branch): from two hours before the
start until the booking ends (confirmed), or while the party's sitting is open (seated) - else
`409 pre-order-not-sendable`. Already sent answers `wasReplay` whoever sent it: two tablets, one ticket.

**Before the party sits, the pre-order is the kitchen ticket.** A `Tab` hangs off a `TableSession`, and a session
is a party at the table: opening one when the waiter taps "send" twenty minutes ahead would mark the table occupied
and the booking seated before anybody arrived. Making a `TabOrder` that belongs to no tab would touch every bill,
split and payment query in the product. So a sent pre-order is a **held ticket**: `KitchenStatus` moves along the
rail by the `TabOrder` rail's rules (a waiter's send lands at `InKitchen`, as a waiter's order does; the Kitchen
role's one move is `InKitchen -> Ready`), it appears on `GET /api/branches/{id}/orders` with its table and a
`preOrder` marker (`reservationId`, `startUtc`, `localStartTime`, `partySize`, `guestName`) and **no `tabId`**, and
nothing is owed - a party that never comes is never billed.

**When the party is seated on the booking** - a waiter seats the booking or the held party, or the guest opens it
with the booking code - the seating, inside its own table lock and save (`PreOrderSittings`), opens the sitting's
tab as a waiter's "Open tab" makes one (nobody on it, admitted - by staff, or as the booker's; the first phone
becomes host) and puts the pre-order on it as an order **with the same id** - which the kitchen screen already
knows - placed by the waiter who sent it when they sent it, its lines the table's at the locked prices, with
whatever the kitchen did with it since (`Ready` stays `Ready`). Sent **after** they sit, it goes straight onto their
tab, through the ledger like any order (a closing tab is `409 tab-not-accepting-orders`).

**Send takes the table's lock**, the one every seating takes. A send that finds the party not seated and a seating
that finds the pre-order not sent cannot both be right, so they take turns, and whichever goes second puts the
pre-order on the tab. `PreOrderTests.A_send_racing_the_seating_still_ends_on_the_tab` races them. The table is
**the booking's as read under the lock**: a booking moved to another table between the send's first read and its lock
is sent again under the new table's lock, the one its seating takes
(`PreOrderTests.A_send_follows_a_booking_moved_to_another_table_under_that_tables_lock`). A phone's scan or a
waiter's "Open tab" gives a sitting its tab without that lock; a send that loses that race to the one-tab-per-sitting
key puts the pre-order on the tab that won. A booking with no pre-order is 404. A seating that races the kitchen
moving the held ticket it attaches is `409 concurrent-update` (nothing saved; seat them again), not the
`table-state-conflict` a moved table earns - the table did not change.

**Not coming / no answer** - `POST /api/reservations/{id}/pre-order/not-coming` - records who and when, sends
nothing, keeps the card (state 4); **send stays possible** until the booking ends. One that loses to a send from
another tablet is `409 pre-order-already-sent`, never a `wasReplay` for something it did not do. A party that was sent
a pre-order and never comes is marked a no-show as today - nothing extra; the held ticket stays on the kitchen screen
until the kitchen voids or serves it. (Seated as a walk-in instead of on the booking, a held ticket is not attached:
seat the booking, or void it.)

**A booking that ends before its party sits takes its held ticket with it.** Cancelled by the guest (app or link) or
the venue (a release as "they let us know"), declined, expired, or cancelled by a no-show block: a held ticket still
on the rail (`InKitchen` or `Ready`, on no tab) is **voided** in the same save (`YallaDbContext`'s hook beside the
signals, so none of the six places a booking ends has to remember it) - it leaves the kitchen screen as any voided
ticket does, and signal 6 tells the rail. A no-show is not such an end. **A held ticket voided before the party sits
is never put on their tab** - no order, nothing owed - and a seated party's leaves the rail.

### Venue settings

`GET/PUT /api/branches/{id}/pre-order-settings` (manager or above at the branch): `acceptsPreOrders` (**off by
default**: a venue that has not looked at the feature must not receive tickets for dishes it never agreed to prepare
ahead), `minutesBefore` (5 to 120, default 30), `excludedMenuItemIds`. **All three are required** on a save: one
left out is `422 validation-failed` naming it, never a silent "off" or every dish back on. A save signals the rail
(signal 6, above). The switch and the minutes live on the
**branch, not in `ReservationPolicy`**: the policy is replaced whole by every save of the reservation form, and a
switch living there would be reset by an edit that never showed it.

### Reads

`preOrder` (and `preOrderEditableUntilUtc`, present where the branch takes pre-orders and the booking may still
have one; and `guestConfirmedOnWayAtUtc`) on every `ReservationView` - `/mine`, the console list, the awaiting cards,
every booking answer - attached for a whole page in at most three statements (`PreOrderReads`); on the manage link,
which deliberately carries **no `branchId`** (it publishes no id beyond `reservationId`/`tableId`: the menu is read
through the branch page its `venueSlug`/`branchSlug` name); `availableForPreOrder` on menu items; `policy.acceptsPreOrders` on the public
branch page. Reports do not count pre-orders yet.

## 3i. Staff-taken bookings: the floor books for a guest by hand (2026-10-31)

The owner's words (Armenian): a member of staff must be able to add a booking by hand in the program; it is linked to
them, and they must follow whether that guest comes or not. "Ձեռքով ամրագրում".

**Who.** Any floor session - a tablet PIN session or a paired phone: waiter, manager, general manager, owner
(`WaiterOrAbove` + `BranchScoped` on the branch routes, `FloorStaffGuard` from the stored row; a platform admin reads the
lists with the guests' numbers blank). **What.** A guest name, the guest's phone (required - it is what the taker calls),
the party, the local date and time, a table or none, an optional note. The booking is `Channel` `Staff`, `DinerUserId`
null, `BookerPhoneE164` null, `PhoneUnverified` false, and **`TakenByStaffId`** the acting staff member - an opaque staff
id like `NoShowMarkedByStaffId`, indexed with the start (`IX_Reservations_TakenByStaffId_StartUtc`, filtered).

**Status.** `Confirmed` at once. No approval of any kind - not at short notice, not for a large party, not at a branch
that approves every booking - and no question to the guest before it on the table: a table the diner's read could only
ask for (3e) the floor books outright, and when a full sitting runs into the table's next booking the booking takes that
time to leave by (`promisedFreeByUtc`) without asking. The person on the floor has judged the room.

**Rules skipped**, because they are about the booker and there is none: the diner account and its lock, the no-show
block, "one table at a time" (`reservation-overlaps-own`), the three-active-bookings limit, both channel gates (the
platform's `AppBookings`/`WebBookings` switches and the venue's online-bookings switch: [branch-features.md](branch-features.md)),
the diner-visibility gate, the lead time ("in twenty minutes" is a booking), and the table's bookable and pinned flags
(staff book any in-service table). **Rules kept**, because they are about the slot: the venue open for business
(`branch-unavailable`), the booking window, the opening hours, a start already past (`reservation-lead-time-too-short`
with `minLeadMinutes` 0 - the client's one code covers both), the table's seats and overhang, the table out of service,
the table's evening (`table-already-booked` / `table-currently-occupied`, with the fresh floor), and at a branch that
books by time only (`bookingMode` 2, [time-only-booking.md](time-only-booking.md)) the room's guests at once
(`time-full`) with `tableId` left out - the waiter picks the table at seating with `seat-reservation`, as for a diner's.

A sibling of the diner's creation, not a flag on it: `StaffBookingService` beside `ReservationCreation`, and
`ReservationWriter.InsertByStaffUnderTableLockAsync` / `InsertByStaffWithoutTableAsync` beside the diner's inserts, under
the same locks (3). **Replay** on `clientCommandId`, keyed on the floor at the branch (`Channel` `Staff`, the branch): a
second POST answers 200 with the booking it made; the same id on a diner's booking or at another branch is
`client-command-id-in-use`. **No diner reminders** (`ReservationNotice.DinerUserId` would have nobody; a null diner would
dead-letter) and **no manage link** (nobody to give it to).

**The taker follows up.**

- Thirty minutes before the start (`BookingToCallRules.MinutesBefore`) the taker is pushed to **call the guest**: the
  `bookingToCall` staff push, staged when the booking is made for `StartUtc − 30 min` through the outbox
  (`BookingToCallStaffPush`, a `ReservationMade` handler; [notifications.md](notifications.md#6a-staff-push---web-push-to-the-staff-app-2026-10-24)).
  A booking made inside its last half hour gets none - the card is already on the floor. At send time the push is dropped
  when the booking is no longer confirmed, the guest has said they are coming, or its grace has run out.
- The floor's read **`GET /api/branches/{branchId}/reservations/to-call`**: the branch's confirmed `Staff`-channel bookings
  from `StartUtc − 30 min` until `StartUtc + GraceMinutes` (the extended hold when later), `guestConfirmedOnWayAtUtc`
  null, not released - the caller's own first (`isMine`), then by start. Each row: `reservationId`, `code`, `guestName`,
  `guestPhone`, `partySize`, `startUtc`, `localDate`, `localStartTime`, `tableId`/`tableLabel` (null by time only until
  seated), `note`, `takenByStaffId`, `takenByName`, `isMine`.
- **`POST /api/reservations/{id}/guest-coming`** (`{ clientCommandId }`, floor staff at the booking's branch): the guest
  told them they are coming. `Reservation.ConfirmOnMyWay(now, recordedByStaffId)` - the same answer as the guest's own
  (3h), from two hours before the start, `409 on-my-way-not-open` outside it - storing **`OnWayRecordedByStaffId`** and
  raising `GuestOnTheirWay` with the recorder, so the History's line is the staff member's (`booking.guest-coming`, not
  the guest's `booking.guest-answered`) and the automatic "are you on your way?" question, where there was one, is
  withdrawn. The floor then shows the usual "on their way" state. Recorded in `ProcessedCommands`
  (`reservation.guest-coming`): a retry answers the booking as it stands, and so does a second answer. Any booking at the
  branch, not only a staff-taken one; a move to another start forgets the recorder with the answer.
- **Not coming** is the existing `release` with `outcome` 2 - cancelled by the venue, told by the guest, never a no-show.
  **Came** is the existing `seat-reservation`. **Nobody came** is the existing `no-show` - which never counts toward a
  block: no account, no proved number (3g).

**Reads that show the taker.** `ReservationView` carries `channel`, `takenByStaffId` and `takenByName` (the taker's name
now, read from the staff row) on every booking answer and list; `OverviewBooking` carries `takenByName` beside its
`channel`. The floor's list of the unseated and the next-booking line say "Ընդունեց՝ {name}"; the console's channel
cell says "Զանգ · {name}".

**The sheet's day: `GET /api/branches/{branchId}/reservations/slots?date=YYYY-MM-DD&party=N`** (`StaffDaySlots`). The
public day read (5) by staff rules, over every in-service table: for each quarter hour the **tables** that take the party -
`tableId`, `label`, `seats`, `roomId`/`roomName`, `earlierGuestMayStillBeThere` (the diner's read would ask that guest;
the floor judges it), `mustFreeByUtc`/`mustFreeByLocal` - or, by time only, `guestsLeft`. A time already past is struck
`LeadTimeTooShort`; beyond the window `OutsideBookingWindow`; the top-level `unavailableReason` refuses the whole day as
`getBranchDaySlots` does. The same per-table judgement as the booking (`AvailabilityQuery.GetStaffDaySlotsAsync`,
`ReservationRules.CheckStaffTiming`, `CheckStaffTable`), so a table listed is one the booking accepts.

**Results.** `StaffPerformanceRow` and `StaffPerformanceTotals` carry `bookingsTaken` (the taker's bookings whose start
falls in the window, whatever became of them), `bookingsCame` (seated or completed) and `bookingsNoShow` (a no-show that
stands; one withdrawn is confirmed again and counts as taken, not missed) - on `/api/staff/me/results`, a person's page
and Staff → Results; the recent `bookings` list carries the taker's `booking.created` lines
([activity.md](activity.md#8-staff-results)).

**History.** `booking.created` for a staff booking is the staff member's line on their device (`byGuest` false,
`extra.takenByStaffId`), "ամրագրում ընդունեց՝ {guest}, {party} հոգի, {date} {time}"; `booking.guest-coming` ("նշեց՝
հյուրը գալիս է") for the staff-recorded answer. Proved by `StaffBookingTests`, `StaffBookingDomainTests` and
`StaffPushTests`.

## 4. The rules, and why none of them is a number in code

Every threshold comes from the branch's `ReservationPolicy`. A lunch cafe and a dinner restaurant
answer all of these differently, and Yerevan cafes sit far longer than the textbook 90 minutes. A new
branch books from **10 minutes** ahead with **5 minutes** of clearing time (`DefaultFor`; 30 and 15
before the booking-times change, whose migration moved every branch still on those old defaults, column
by column, and left every value an owner had chosen; its Down is best-effort - nothing records which rows
it moved, so it moves every 10 and 5 back - and maps the new enum numbers onto old ones: `ApprovalTrigger`
5 to 4 where the booking has a deadline and 1 where it has none, `DeclineReason` 3 to 1 and 4 to 2). The two exceptions - how bookings follow one
another on a table, and short notice - are the product's promises, fixed in code: see
[3e](#3e-a-tables-evening-when-one-booking-may-follow-another) and [3d](#3d-short-notice-tonight-is-the-floors-to-decide).

| Rule | Refused when | Error |
| --- | --- | --- |
| Minimum lead time | `StartUtc < now + MinLeadMinutes` | `reservation-lead-time-too-short` |
| Booking window | date is past `today + BookingWindowDays` | `reservation-outside-booking-window` |
| Opening hours | the **whole** interval is not inside one block | `reservation-outside-opening-hours` |
| Table capacity | `PartySize > Seats` | `reservation-party-exceeds-capacity` |
| Seat overhang | `Seats - PartySize > MaxSeatOverhang` (when set) | `reservation-seat-overhang-exceeded` |
| Bookability | `IsBookable` is false | `reservation-table-not-bookable` |
| Bookability | table is `OutOfService` | `reservation-table-out-of-service` |
| Local time | the clocks skip that wall-clock time | `reservation-local-time-does-not-exist` |

Each is a **distinct named exception** with its own code and its own numbers in `details`. There is
deliberately no generic "invalid booking": a diner told "that table seats four" picks another table,
a diner told "we are closed then" picks another time, and a diner told "invalid booking" leaves.

Party size above `ApprovalRequiredAbovePartySize` is **not** in this table. Twelve people is the
most valuable booking of the night and the one most likely to need tables moved, so it becomes
`PendingApproval` rather than a rejection.

### Opening hours and `ClosesNextDay`

The whole interval must fit inside a **single** block. A branch open twice in a day (lunch, then
dinner) does not make 14:30–16:00 legal — straddling the afternoon closure is not "open for both
halves".

The day an interval belongs to is not always the day it starts on. A branch open 10:00–01:00 has a
Friday block running into Saturday morning, so a **22:30 Friday sitting ending 00:30 is inside
Friday's hours**. The rule therefore examines the local day *and the one before it*.

**A block opening and closing at the same minute is refused**, not read as a 24-hour day:
`PUT .../opening-hours` answers 422 `validation-failed` naming `[n].closesAt` (bound `conflict`) and
writes nothing. `closesNextDay` is derived, there is no 24-hour flag, and a branch that never shuts
says 00:00-23:59. Pinned through the pipeline by
`VenueAdminEndpointTests.Opening_hours_that_open_and_close_at_the_same_minute_are_refused_naming_the_row`
(QA 2026-10-16, parity P1 - the console's mock saved 09:00-09:00 as "next day").

### Special opening days

A holiday, a closure or different hours on one date (`GET`/`PUT /api/branches/{id}/opening-hours/exceptions`)
**replaces that date's weekly blocks**; no blocks is shut all day. A date's blocks are the ones that *open* on
it, exactly as in the week: 31 December 18:00-03:00 runs into 1 January even when 1 January is itself shut, and
a Friday's weekly 10:00-01:00 still runs into a Saturday that is a special day.

Every reader asks one type, `OpeningCalendar` (the week with the special days laid over it), loaded through
`OpeningCalendars` for the dates the question needs - and a date outside the window it was loaded for throws
rather than quietly falling back to the weekday. The readers: single-slot availability and the day's slots
(same statement as before, the special days of the two dates beside the weekly rows), booking and changing a
booking (`reservation-outside-opening-hours`), the console's Overview (service day, "open until", the load
chart's hours, where the takings start, and `hours.specialDay`), `BranchOpenNow` behind every `isOpenNow`, and
the public page and listing detail (`specialDays`, from the service date - yesterday while last night's special
block is still running, so 01:00 on New Year's Day still names the Eve - through 60 days after today). Readiness
is not one: `no-hours`
is about the week being set, and a special day is not a setup step. The floor reads no hours.

The list from today at the branch on is replaced whole, under the branch row lock, against the revision the
editor loaded: `GET` returns `{ version, days }` - from the service date, so while last night's special block
still runs its day is listed first, read-only, as on the public page - `PUT` takes `{ expectedVersion, days }`, and a list saved since
is refused with 409 `concurrent-update` and `context.currentVersion` (`Branches.SpecialDaysVersion`), so the
second of two editors cannot undo the first one's holiday. Days before today are kept, because last night's
special block may still be running and the Overview still reads it; an entry dated before today is **ignored**,
so a console left open past midnight still saves. Refusals are 422 with fields under `days` - `days[n].date`
(`required`, `max` past 366 days, `conflict` twice), `days[n].note` (`max` 60), `days[n].blocks` (`required`,
`max` 4), `days[n].blocks[m].opensAt` (`conflict`, overlap), `days[n].blocks[m].closesAt` (`conflict`, no length),
`days[100]` (`max` 100 days) - and `expectedVersion` (`required`). A lock wait that runs out, on the save or on
the delete before it, is 409 `concurrent-update`, never a 500.

**A save never touches a booking.** `affectedReservationIds` are the live future ones this save *newly* put
outside the hours: starting on a date whose special day was added, removed or given other blocks (a new note is
no change), or just after midnight following one, that fitted the calendar before the save and do not now. So
adding 6 January reports 6 January's guests, not again the 1 January ones an earlier save already reported.
Pinned by `SpecialOpeningDayTests` and `OpeningCalendarTests`.

**There is no slot list.** The time a diner picks is the client's control; every availability read
answers the one time it was asked about, and outside the hours it says `unavailableReason`
`OutsideOpeningHours` on the branch and on every table, and a booking for that time is
`reservation-outside-opening-hours`. A picker offering 03:00 is a client that has not read `hours`.
Pinned on the public route, with web bookings on, by
`PublicBookingTests.Outside_opening_hours_the_public_page_offers_no_table_and_a_web_booking_is_refused`
(QA 2026-10-16, parity P5; the switch-off half is `A_web_booking_is_refused_while_the_branch_has_not_switched_them_on`).

### Time zones

The diner picks a local date and time; the branch has an IANA `TimeZoneId`. `BranchZone` is the only
place the two are converted, and it handles both awkward cases rather than letting them escape:

- **Invalid** (the hour a spring-forward skips) — refused as `LocalTimeDoesNotExistException`, a
  named 422 the app can explain, not an `ArgumentException` surfacing as a 500.
- **Ambiguous** (the hour an autumn fall-back repeats) — resolved to the **earlier** of the two
  instants. .NET's default is the later one, which would seat one party an hour after another was
  promised the same table.

Armenia does not currently observe daylight saving. Nothing here leans on that: the zone is a
per-branch setting and the next customer may be somewhere that does.

### No-shows

`NoShowPolicy` — one class, one settings block, one `Enabled` flag. Above `Threshold` no-shows in
the last `WindowDays` (default 3 in 90), a diner loses **instant confirmation** and their bookings
land as `PendingApproval` for a human to look at. That is the entire penalty.

Nobody is ever banned. The market is too small for a wrongly-banned regular, and the count cannot
tell a serial no-show from somebody whose phone died three times. The window is rolling rather than
lifetime, because a diner who missed three bookings two years ago has served their sentence.

This is a recommendation, not a settled decision — hence one named class that is trivial to change
or switch off, rather than logic threaded through the booking service.

---

## 5. Availability

`GET /api/branches/{branchId}/availability?date=&time=&partySize=` — anonymous, because browsing
needs no account.

**One round trip.** The branch, its policy, its opening hours, every active table and the bookings
around the slot arrive in a single statement. The naive shape — load the tables, then ask "anything
booked on this one?" per table — is an N+1 on the screen a diner stares at while deciding whether to
eat here. A test counts the statements actually sent, because an N+1 returns byte-identical results.

**The bookings join is a `LEFT JOIN`, and that is load-bearing.** An inner join would compile, pass
a casual eye, and silently drop every table with nothing booked — which is exactly the set of tables
a diner most wants to see. A test asserts a table with no bookings still appears.

**The rules run in memory, on the same pure functions the booking service uses.** Writing the
overlap check a second time in SQL would let the screen and the endpoint disagree about the same
table, which reads to a diner as the product being broken. So the query fetches the small set of
bookings around the slot and `ReservationOverlap` judges them — the identical code path the creation
service takes inside its lock. This is the same argument the floor read model already makes for
deriving table state in memory rather than in SQL.

The bookings are fetched over a generous ±2 day UTC guard window rather than the exact slot, because
the exact slot is not known until the branch's time zone has been read — and reading it first would
cost the second round trip this shape exists to avoid. Any zone is within 14 hours of UTC, so two
days either side covers every offset and every turn time while still being a handful of rows per
table. Opening hours are narrowed to the weekdays the rule can actually consult, because two sibling
collections in one projection cross-multiply and every extra block is another copy of every table
row on the wire.

### The availability window

```
AvailableFromUtc  = the requested start
AvailableUntilUtc = nextBookingStart - buffer        (null = no limit at all)
```

Worked: a 90-minute turn, 15 minutes of clearing time (a branch that kept fifteen), a diner asking
about **18:00**, and a **20:00** booking already on the table.

```
   18:00                      19:30            19:45         20:00
     |                          |                |             |
     |<---- the sitting ------->|                |             |
     |                          |<-- buffer ---->|             |
     |<------ yours until 19:45 --------------- >|             |
                                                 |             |
                                            next booking starts 20:00
```

The diner is shown **18:00–19:45** before they confirm. If that is too short for what they had in
mind, they can see which tables have **no** limit and pick one of those instead. This is how the
product avoids asking people how long they intend to stay: self-reported departure times are
unreliable in a way a stated window is not — a party that said ninety minutes still leaves when they
leave, whereas a party told the table is theirs until 19:45 has been told something true.

A table whose next booking leaves no room for a full turn is offered with the time to leave by
instead - `mustFreeByUtc`, which is exactly `availableUntilUtc` - as long as it starts at least
forty-five minutes before that booking; closer than that it is refused, `NextBookingTooSoon`. See
[3e](#3e-a-tables-evening-when-one-booking-may-follow-another).

Each table also carries its derived state **at the requested instant** — not at now — using the same
`TableStateProjection` the floor plan uses. A table free this afternoon but booked at 20:00 reads as
`ReservedSoon` when the question is about 20:00.

### What the diner is told before they commit

- **`requiresApproval`** on a table, and on every day slot, is true when the booking will land
  `PendingApproval`: the slot is at short notice - today at the branch, or under six hours away, see
  [3d](#3d-short-notice-tonight-is-the-floors-to-decide) - the branch approves every booking by hand
  (`autoConfirm` off), or the party is over `approvalRequiredAbovePartySize`
  (`ReservationRules.AwaitsApproval`, asked per slot). It was the threshold alone,
  so a branch with auto-confirm off promised an instant confirmation it was never going to give.
  The third reason, the diner's own no-show record, needs the diner and is only known once they
  book - the booking says so in `awaitingApprovalBecause`.
- **`cancellationDeadlineUtc`** is the instant past which cancelling this slot is recorded as late:
  the requested start less `cancellationDeadlineMinutes`, both on the response. The same rule
  (`ReservationRules.CancellationDeadline`) decides `cancelledAfterDeadline`, so the promise and the
  record cannot disagree - the app had nothing to show and promised free cancellation until the
  start. It can already be past, since a slot inside the window has no free cancellation, and it is
  absent when no slot could be computed. Every booking read carries it too.

### Day slots: one day, every quarter of an hour

`GET /api/branches/{branchId}/availability/slots?date=&partySize=[&excludeReservationId=]`, anonymous,
on the same rate limit as the single slot; the public twin is
`GET /api/public/branches/{branchId}/availability/slots`, behind the public 404 rule.

```json
{
  "branchId": "guid", "date": "2026-09-13", "partySize": 2, "stepMinutes": 15,
  "unavailableReason": 3,              // the whole day refused; absent when not
  "slots": [
    { "time": "18:00", "startUtc": "2026-09-13T14:00:00Z", "isAvailable": false, "isRequestable": false,
      "requiresApproval": false, "freeTableCount": 0, "requestableTableCount": 0, "unavailableReason": 11 },
    { "time": "19:30", "startUtc": "…", "isAvailable": true, "isRequestable": true, "requiresApproval": true,
      "freeTableCount": 0, "requestableTableCount": 1 },
    { "time": "20:45", "startUtc": "…", "isAvailable": true, "isRequestable": false, "requiresApproval": false,
      "freeTableCount": 1, "requestableTableCount": 0, "mustFreeByUtc": "…", "mustFreeByLocal": "21:25" }
  ]
}
```

The time picker's read. Every quarter of an hour from 00:00 (00, 15, 30, 45 - it was every half hour
until the booking-times change) whose **whole sitting** fits one opening block is listed; a time the
branch is shut for is left out, never struck. A time too soon or too far out is
listed struck with `LeadTimeTooShort` or `OutsideBookingWindow`. Otherwise `isAvailable` is true when
at least one bookable table pinned on its room's photo seats the party and the table's evening
([3e](#3e-a-tables-evening-when-one-booking-may-follow-another)) allows it: `freeTableCount` counts the
tables free outright, `requestableTableCount` those that can be asked for, `isRequestable` says the
latter are all there is (and `requiresApproval` is then true), and `mustFreeByUtc`/`mustFreeByLocal` give
the latest time to leave by when no table on offer gives a full sitting. A struck time
names the commonest of `TableAlreadyBooked`, `TableCurrentlyOccupied`, `TableJustBooked`,
`NextBookingTooSoon` and `EarlierGuestBeingAsked` when tables that fit the party are taken -
even in a room of two-seaters that do not - and otherwise the commonest table reason, e.g.
`PartyExceedsCapacity`. The top-level `unavailableReason` refuses the whole day: `OutsideOpeningHours`
with no slots when the branch is shut all day, or the timing reason every listed time shares.

**It is the single-slot read, asked ninety-six times.** The day needs the same rows as one slot on
it - the bookings around it, the open sittings, its opening blocks and the evening before's - so the
day is `AvailabilityQuery.BuildQuery` too, one statement anchored on noon so the guard window covers
every offset and the longest sitting, and every time goes through
`Answer`, the one per-table judgement the single-slot read makes (the table's own rules, then
`TableSequencing.Judge`). The two cannot disagree about a
table at a time, and `AvailabilityQueryTests` asks both about every listed time, today and a later
day, with bookings, a sitting, a table too big, one that takes no bookings and one out of service.

### Leaving the diner's own booking out

Both reads take `excludeReservationId`. A diner changing a booking sees the table they already hold
as free, and every other booking still counts - the same exclusion the move's clash check makes, so
what the change screen offers is what the move accepts. An id that is not a booking at the branch
changes nothing. The parameter is as anonymous as the read: booking ids are unguessable version-7
GUIDs, and the answer says no more about the booking left out than that its table is free.

---

## 6. Endpoints

| Endpoint | Policy | Also enforced in the service |
| --- | --- | --- |
| `GET /api/branches/{branchId}/availability` | `AllowAnonymous` | — |
| `GET /api/branches/{branchId}/availability/slots` | `AllowAnonymous` | — |
| `POST /api/reservations` | `VerifiedDiner` | a proved number only (§3g); no overlap anywhere, at most three active per number, refused while blocked for no-shows |
| `POST /api/reservations/{reservationId}/reschedule` | `VerifiedDiner` | the booking must be **theirs**, else 404 - see [3c](#3c-changing-a-booking) |
| `POST /api/public/bookings/{token}/reschedule` | `AllowAnonymous` | the manage link names the booking; its account's phone must be proved - see [public-surface.md](public-surface.md#changing-the-booking) |
| `POST /api/reservations/{reservationId}/table-requests/{requestId}/answer` | `VerifiedDiner` | the booking must be **theirs** and the request put to them, else 404 - see [3e](#3e-a-tables-evening-when-one-booking-may-follow-another) |
| `GET /api/reservations/mine` | `VerifiedDiner` | filtered to the caller's own `DinerUserId` |
| `POST /api/reservations/{id}/cancel` | `VerifiedDiner` | the booking must be **theirs**, else 403 |
| `POST /api/reservations/{id}/extend-hold` | `VerifiedDiner` | the booking must be theirs, **started**, and not yet extended - see [notifications.md](notifications.md) |
| `POST /api/tabs/open-by-booking` | `VerifiedDiner` | "I'm at my table": the booking whose `code` was sent must be **theirs** - else 404 `booking-not-found`, identical to a code nobody holds - and its table theirs now: `Confirmed` from `StartUtc - walkInHoldbackMinutes` until `EndUtc`, or `Seated`. A free table is seated as the booking. See [tabs.md](tabs.md) |
| `POST /api/reservations/{id}/approve` · `/reject` | `WaiterOrAbove` | a waiter or above at the booking's **branch** - an owner or general manager at any branch of their venue; too late is `409 reservation-approval-expired` - see [3d](#3d-short-notice-tonight-is-the-floors-to-decide) |
| `POST /api/reservations/{id}/no-show` · `/release` | `WaiterOrAbove` | a waiter or above at the booking's **branch** - an owner or general manager at any branch of their venue, with the console's sign-in too - see [3b](#3b-letting-a-booking-go) and [3f](#3f-the-consoles-overview-the-service-day-and-a-menu-on-every-booking) |
| `GET /api/branches/{branchId}/reservations` | `ManagerOrAbove` + `BranchScoped` | a branch manager at their own branch only - the console's approval queue |
| `GET /api/branches/{branchId}/overview` | `ManagerOrAbove` + `BranchScoped` | a branch manager at their own branch only, from the stored row - the console's Overview, see [3f](#3f-the-consoles-overview-the-service-day-and-a-menu-on-every-booking) |
| `GET /api/staff/today?branchId=` | `ManagerOrAbove` | the manager's Today on the staff phone - the Overview's counts plus takeaway takings, guests, what waits and the shift; a branch manager their own branch, an owner or general manager any branch of their venue from a phone or the console, any PIN session on a shared tablet that tablet's branch only - see [auth.md](auth.md#personal-phones-2026-10-24) |
| `GET /api/branches/{branchId}/reservations/awaiting` | `WaiterOrAbove` + `BranchScoped` | a waiter or above at the branch, from the stored row - the floor's list, see [3d](#3d-short-notice-tonight-is-the-floors-to-decide) |
| `PUT /api/reservations/{id}/pre-order` · `POST .../pre-order/clear` · `POST .../on-my-way` | `VerifiedDiner` | the booking must be **theirs**, else 404 - see [3h](#3h-pre-orders) |
| `PUT /api/public/bookings/{token}/pre-order` · `POST .../pre-order/clear` · `POST .../on-my-way` | `AllowAnonymous` | the manage link names the booking - see [3h](#3h-pre-orders) |
| `GET /api/branches/{branchId}/pre-orders/due` | `WaiterOrAbove` + `BranchScoped` | a waiter or above at the branch, from the stored row - the pre-order rail, see [3h](#3h-pre-orders) |
| `POST /api/reservations/{id}/pre-order/send` · `/not-coming` | `WaiterOrAbove` | a waiter or above at the booking's **branch**, from the stored row - see [3h](#3h-pre-orders) |
| `GET/PUT /api/branches/{branchId}/pre-order-settings` | `ManagerOrAbove` + `BranchScoped` | a branch manager at their own branch only |
| `POST /api/reservations/{id}/running-late` · `POST /api/public/bookings/{token}/running-late` | `VerifiedDiner` · `AllowAnonymous` | the guest's own booking, or the manage link - see [3h](#3h-pre-orders) |
| `POST /api/reservations/{id}/ask-on-my-way` | `WaiterOrAbove` | a waiter or above at the booking's **branch**, from the stored row - see [3h](#3h-pre-orders) |
| `POST /api/branches/{branchId}/reservations/staff` | `WaiterOrAbove` + `BranchScoped` | a waiter or above at the branch, from the stored row - the floor books for a guest by hand, `Confirmed` at once, linked to the taker; the slot's rules only - see [3i](#3i-staff-taken-bookings-the-floor-books-for-a-guest-by-hand-2026-10-31) |
| `GET /api/branches/{branchId}/reservations/slots?date&party` | `WaiterOrAbove` + `BranchScoped` | a waiter or above at the branch - the staff sheet's day, every in-service table, no lead time - see [3i](#3i-staff-taken-bookings-the-floor-books-for-a-guest-by-hand-2026-10-31) |
| `GET /api/branches/{branchId}/reservations/to-call` | `WaiterOrAbove` + `BranchScoped` | a waiter or above at the branch - the staff-taken bookings whose guest should be called now, the caller's own first; a platform admin reads them with the numbers blank - see [3i](#3i-staff-taken-bookings-the-floor-books-for-a-guest-by-hand-2026-10-31) |
| `POST /api/reservations/{id}/guest-coming` | `WaiterOrAbove` | a waiter or above at the booking's **branch**, from the stored row - the guest told them they are coming; from two hours before the start - see [3i](#3i-staff-taken-bookings-the-floor-books-for-a-guest-by-hand-2026-10-31) |

Availability - one slot or one day - is the only anonymous endpoint here outside the sign-in flows
and the manage link, whose token is its credential. Browsing needs no account: somebody deciding
whether to eat here has to see the room before they are asked who they are.

**Every booking read names its room.** `ReservationView` carries `roomId` and `roomName` - the
`FloorArea` the table is in - on the create and change responses, `GET /api/reservations/mine` and the
staff branch list; the manage link carries `roomName`. Read live from the table, like `tableLabel`, so
a table a manager moves to another room takes its bookings with it; absent for a table in no room.

`VerifiedDiner` is deliberately narrower than "a diner". A tab participant — somebody who scanned a
QR code at a table — is also `ActorType.Diner` in the audit sense, but has no `DinerUser` row by
design, so there is nothing to list under "my bookings" and nothing to count a no-show against. The
policy reads the explicit `ytyp` principal-type claim rather than inferring it.

**Two boundaries the policies cannot draw**, and why they live in the service instead:

- **Ownership.** "This booking is yours" is a fact about a row, not about a token. No claim can
  express it, so `CancelAsync` compares `Reservation.DinerUserId` to the caller and answers 403.
- **Branch scope on approve and reject.** These routes are addressed by *reservation* id, so there
  is no `branchId` route value for `BranchScoped` to compare a claim against — and that policy
  fails closed when it cannot find one, which is correct and is the reason not to apply it here.
  The service resolves the booking's branch and checks it against the acting staff member's own
  branch and venue, from the stored staff row. A waiter or manager of another venue - or of another
  branch of the same one - gets 403, and `ReservationEndpointTests`, `ShortNoticeApprovalTests` and
  `ShortNoticeApprovalEndpointTests` prove it.

Cancellation is free until `CancellationDeadlineMinutes` before the start and **still allowed after
it**, with the lateness recorded on the booking as `CancelledAfterDeadline`. Refusing a late
cancellation converts it into a no-show, which costs the venue the same table plus the chance to
resell it. It is stored rather than derived because the deadline is a setting: an owner who shortens
it next month must not retroactively reclassify last month's cancellations. Every booking read and
every availability answer states the deadline as an instant, `cancellationDeadlineUtc`, from that
same rule.

### On the wire

Enums travel as **integers**, matching the database and the OpenAPI document's `x-enum-varnames` —
see [openapi.md](openapi.md). Failures are RFC 7807 problem documents with the machine-readable
facts under `context`:

| Failure | Status | `code` |
| --- | --- | --- |
| a branch rule refused it | 422 | one per rule, e.g. `reservation-party-exceeds-capacity` |
| the table went first, or the table's evening refuses the time | 409 | `table-already-booked`, with the clashing window and a fresh floor; `context.reason` 8 `TableAlreadyBooked`, 11 `TableJustBooked`, 12 `NextBookingTooSoon` or 13 `EarlierGuestBeingAsked` |
| the table's next booking cuts the sitting short and `acceptsFreeByUtc` is missing or not that instant | 422 | `reservation-free-by-required`, with `context.field` `acceptsFreeByUtc`, `requiredFreeByUtc`, `requiredFreeByLocal`, `nextBookingStartUtc` |
| answering a request for one's table that no longer waits, or a yes about a booking that moved since it was asked | 409 | `table-request-not-pending`, with `context.reservationId`, `requestId`, `status`, `expiresAtUtc` |
| staff approving or declining a booking that waits on the earlier guest's answer in the app (`awaitsEarlierGuestAnswer` true) | 409 | `reservation-awaiting-earlier-guest`, with `context.reservationId`, `requestId`, `expiresAtUtc` |
| a signed-in diner booking, or moving a booking to, a sitting that overlaps another of their own live bookings (pending, confirmed, seated) at the same branch | 409 | `reservation-overlaps-own`, with `context.reservationId`, `code`, `localDate` (`"2026-10-22"`), `localStartTime` (`"19:30:00"`), `tableLabel` - the booking already held |
| the command id belongs to another caller, or another command | 409 | `client-command-id-in-use` |
| changing a booking that is not pending or confirmed, or has started | 409 | `reservation-not-changeable`, with `context.reservationId`, `status`, `startUtc` |
| a no-show (`no-show`, or `release` with `outcome` 1) before the booking's start | 409 | `reservation-not-started`, with `context.reservationId`, `startUtc` |
| changing a booking that was cancelled, approved or seated meanwhile | 409 | `concurrent-update` |
| approving or declining past the short-notice window, after the sweep declined it, or approving once it has started | 409 | `reservation-approval-expired`, with `context.reservationId`, `status`, `startUtc`, `approvalExpiresAtUtc` |
| the lock could not be had | 503 | `reservation-lock-timeout`, with `context.retryable` true |
| keep-my-table before the start, or on a booking that is not confirmed | 409 | `hold-not-active`, with `context.startUtc` |
| keep-my-table a second time | 409 | `hold-already-extended` |
| keep-my-table at a branch that offers no extensions | 409 | `extensions-not-offered` |
| "I'm at my table" with a code none of the caller's bookings carries - somebody else's included | 404 | `booking-not-found`, no `context`, the same sentence either way |
| "I'm at my table" before the branch's walk-in holdback | 409 | `booking-too-early`, with `context.earliestUtc` |
| "I'm at my table" from the booking's end, or once its sitting has finished | 409 | `booking-ended`, with `context.endUtc` |
| "I'm at my table" on a booking pending approval, cancelled or a no-show | 409 | `booking-not-active`, with `context.status` |
| "I'm at my table" while another party's sitting is still open on the booked table | 409 | `booking-table-occupied`, with the booking's `context` |
| a pre-order change two hours before the start or later | 409 | `pre-order-locked`, with `context.reservationId`, `lockedAtUtc` |
| a pre-order change, a "not coming" or a move once the pre-order was sent | 409 | `pre-order-already-sent`, with `context.reservationId`, `sentAtUtc` |
| saving a pre-order at a branch that does not take them | 409 | `pre-orders-not-accepted`, with `context.branchId` |
| dishes a pre-order cannot take | 422 | `pre-order-items-unavailable`, with `context.items[]` `{ menuItemId, reason }` |
| sending a pre-order, or marking "not coming", outside its window or state | 409 | `pre-order-not-sendable`, with `context.reservationId`, `status`, `sendableFromUtc`, `endUtc` |
| "on my way" for a booking not confirmed or seated, more than two hours before its start, or over | 409 | `on-my-way-not-open`, with `context.reservationId`, `status`, `opensAtUtc` (start - 2 h) |
| "running late" for a booking not confirmed, more than two hours before its start, or over | 409 | `on-my-way-not-open`, as above |
| "Ask: are you coming?" more than two hours before the start | 409 | `ask-on-my-way-too-early`, with `context.reservationId`, `opensAtUtc` |
| "Ask: are you coming?" for a booking not confirmed, or started | 409 | `ask-on-my-way-not-open`, with `context.reservationId`, `status`, `startUtc` |
| "Ask: are you coming?" once the guest answered | 409 | `guest-already-answered`, with `context.reservationId`, `answeredAtUtc`, `runningLateMinutes` (when late) |
| "Ask: are you coming?" within 15 minutes of the last | 409 | `ask-on-my-way-asked-recently`, with `context.reservationId`, `askedAtUtc`, `nextAllowedAtUtc` |
| a staff booking at a start already past (the floor has no lead time) | 422 | `reservation-lead-time-too-short`, with `context.minLeadMinutes` 0 |
| a staff booking by time only for more than fit | 409 | `time-full`, with `context.guestsLeft` |
| a staff booking's `clientCommandId` held by a diner's booking or another branch's | 409 | `client-command-id-in-use` |
| "coming" recorded by staff outside the window (not confirmed or seated, more than two hours before the start, or over) | 409 | `on-my-way-not-open`, as the guest's own |

---

## Out of scope, deliberately

Short "hold this table for 10 minutes" holds, the late-nudge scheduler (a genuine scheduled action
needing an outbox and a delivery record), table combining, deposits, SignalR broadcasting, ordering
and payments.

## In the History (2026-10-24)

Every booking event the venue cares about is an entry in its History: made, moved and cancelled by the guest (app or
link), approved and declined by staff (with the seconds it waited), declined by the system when nobody answered, seated,
no-show, released, a no-show withdrawn, the hold extended, the guest's on-my-way answers - and, since 2026-10-31, a
booking taken by hand as the taker's line, and "coming" they recorded for the guest (`booking.guest-coming`). See
[activity.md](activity.md).
