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
the table's current state. That is the right tool when writers collide on one row.

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

### So: serialise bookers per table

```
BEGIN TRANSACTION (READ COMMITTED)
    SET LOCK_TIMEOUT <BookingLock:LockTimeoutMilliseconds>

    SELECT Id FROM DiningTables WITH (UPDLOCK, HOLDLOCK) WHERE Id = @tableId

    -- inside the lock, never before it:
    1. re-check ClientCommandId   (a racing retry of this same booking)
    2. re-check for overlaps      (the range predicate + the pure rule)
    3. INSERT the reservation
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

### Order inside the lock

The command-id re-check runs **before** the overlap check, and the order is easy to get backwards.
Two retries of the *same* booking race: the first commits while the second waits on the lock. By the
time the second gets in, the first is visible — and an overlap check run first would call the
diner's own booking a conflict and answer 409 for a table they already have.

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
| `ReservationLockTimeoutException` | the question was never asked | **503** + `retryable: true` | yes, with the same `clientCommandId` |

Collapsing them into one code would teach clients to retry conflicts, which is how a party ends up
holding two tables.

### Idempotency

`Reservations.ClientCommandId` carries a **unique index**, exactly as `TableStateChanges` does. A
diner on a patchy mobile connection taps Book, sees nothing happen, and taps again. The index — not
a check-then-insert in the service — is what settles it, because two retries can race each other and
a check-then-insert lets both through. The loser of that race answers with the booking that won.

---

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

---

## 6. Endpoints

| Endpoint | Who |
| --- | --- |
| `GET /api/branches/{branchId}/availability` | anonymous |
| `POST /api/reservations` | verified diner; takes `clientCommandId` |
| `POST /api/reservations/{id}/cancel` | the diner who made it |
| `GET /api/reservations/mine` | the calling diner |
| `POST /api/reservations/{id}/approve` · `/reject` | manager or owner, scoped to the branch |

Cancellation is free until `CancellationDeadlineMinutes` before the start and **still allowed after
it**, with the lateness recorded on the booking as `CancelledAfterDeadline`. Refusing a late
cancellation converts it into a no-show, which costs the venue the same table plus the chance to
resell it. It is stored rather than derived because the deadline is a setting: an owner who shortens
it next month must not retroactively reclassify last month's cancellations.

---

## Out of scope, deliberately

Short "hold this table for 10 minutes" holds, the late-nudge scheduler (a genuine scheduled action
needing an outbox and a delivery record), table combining, deposits, SignalR broadcasting, ordering
and payments.
