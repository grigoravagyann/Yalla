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

Existing booking **18:00–19:30**, clearing time **15 minutes**. The table is unsellable from
**17:45** to **19:45**, and sellable at exactly those two instants.

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

The insert therefore lives behind exactly one method:

```csharp
ReservationWriter.InsertUnderTableLockAsync(...)   // the only db.Reservations.Add in the codebase
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

The threshold rule itself is unchanged: `RequiresApproval` is `>` and the shipped threshold is three,
so it is the **fourth** no-show in the window that costs instant confirmation.

## 4. The rules, and why none of them is a number in code

Every threshold comes from the branch's `ReservationPolicy`. A lunch cafe and a dinner restaurant
answer all of these differently, and Yerevan cafes sit far longer than the textbook 90 minutes.

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

Worked: a 90-minute turn, 15 minutes of clearing time, a diner asking about **18:00**, and a **20:00**
booking already on the table.

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

The window is never *shorter* than the sitting it reserves. A table whose next booking leaves no
room for a full turn fails the overlap rule outright and is returned with
`UnavailableReason: TableAlreadyBooked`, rather than offered with a window too short to use.

Each table also carries its derived state **at the requested instant** — not at now — using the same
`TableStateProjection` the floor plan uses. A table free this afternoon but booked at 20:00 reads as
`ReservedSoon` when the question is about 20:00.

### What the diner is told before they commit

- **`requiresApproval`** on a table is true when the booking will land `PendingApproval`: the branch
  approves every booking by hand (`autoConfirm` off), or the party is over
  `approvalRequiredAbovePartySize` (`ReservationRules.AwaitsApproval`). It was the threshold alone,
  so a branch with auto-confirm off promised an instant confirmation it was never going to give.
  The third reason, the diner's own no-show record, needs the diner and is only known once they
  book - the booking says so in `awaitingApprovalBecause`.
- **`cancellationDeadlineUtc`** is the instant past which cancelling this slot is recorded as late:
  the requested start less `cancellationDeadlineMinutes`, both on the response. The same rule
  (`ReservationRules.CancellationDeadline`) decides `cancelledAfterDeadline`, so the promise and the
  record cannot disagree - the app had nothing to show and promised free cancellation until the
  start. It can already be past, since a slot inside the window has no free cancellation, and it is
  absent when no slot could be computed. Every booking read carries it too.

---

## 6. Endpoints

| Endpoint | Policy | Also enforced in the service |
| --- | --- | --- |
| `GET /api/branches/{branchId}/availability` | `AllowAnonymous` | — |
| `POST /api/reservations` | `VerifiedDiner` | — |
| `GET /api/reservations/mine` | `VerifiedDiner` | filtered to the caller's own `DinerUserId` |
| `POST /api/reservations/{id}/cancel` | `VerifiedDiner` | the booking must be **theirs**, else 403 |
| `POST /api/reservations/{id}/extend-hold` | `VerifiedDiner` | the booking must be theirs, **started**, and not yet extended - see [notifications.md](notifications.md) |
| `POST /api/reservations/{id}/approve` · `/reject` | `ManagerOrAbove` | the booking's **branch and venue** must be theirs |

Availability is the only anonymous endpoint outside the sign-in flows. Browsing needs no account:
somebody deciding whether to eat here has to see the room before they are asked who they are.

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
  branch and venue. A manager of another venue gets 403, and `ReservationEndpointTests` proves it.

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
| the table went first | 409 | `table-already-booked`, with the clashing window and a fresh floor |
| the command id belongs to another caller | 409 | `client-command-id-in-use` |
| the lock could not be had | 503 | `reservation-lock-timeout`, with `context.retryable` true |
| keep-my-table before the start, or on a booking that is not confirmed | 409 | `hold-not-active`, with `context.startUtc` |
| keep-my-table a second time | 409 | `hold-already-extended` |
| keep-my-table at a branch that offers no extensions | 409 | `extensions-not-offered` |

---

## Out of scope, deliberately

Short "hold this table for 10 minutes" holds, the late-nudge scheduler (a genuine scheduled action
needing an outbox and a delivery record), table combining, deposits, SignalR broadcasting, ordering
and payments.
