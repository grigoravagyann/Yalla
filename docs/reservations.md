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

---

## 3c. Changing a booking

`POST /api/reservations/{reservationId}/reschedule`, `VerifiedDiner`, idempotent on `clientCommandId`.

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
overlap check, the sittings - with the booking being moved **left out of the overlap check**, so moving
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
walk-in may be sitting into it, the table may be out of service, the slot may be over), and committed in
the same transaction - the booking `Confirmed` again with its old start, end, table, party and
`ConfirmedAtUtc`, its reminder and late nudge rewritten for that slot, `DeclineReason` recording why the
change did not go through (the view's `declinedBecause` stays null - the booking is not declined). When
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
plus 15 minutes, or its start if that is sooner (stored on the booking; null on every other booking and
cleared the moment it stops waiting). The app and the public page count down to it. Until then a waiter
or above at the branch may approve or decline it.

**Unanswered, it is declined for them.** `ApprovalExpiryHostedService` runs `ApprovalExpirySweep` every
`ApprovalExpiry:SweepIntervalSeconds` (default 20; 0 switches it off, which no venue should) and once at
start-up. Each pass reads the pending bookings whose `approvalExpiresAtUtc` has passed - one filtered
index, `IX_Reservations_ApprovalExpiresAtUtc`, which holds only the requests open now - and declines each
on its own, under its own row version: `CancelledByVenue` with `declinedBecause` **2 `NoAnswer`**, its
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
| another day, otherwise - the earlier guest booked on the web, has no device, or the later booking needs approval anyway (auto-confirm off, a large party, a no-show history) | `EarlierGuestConsent` (5) | the floor, from the awaiting list, after calling them | **none** - it waits as any booking the floor decides; the request's `expiresAtUtc` is the later booking's start |

The later booking's `approvalExpiresAtUtc` is the request's `expiresAtUtc` when there is a clock, and null
on the floor's questions for another day. The later guest stays anonymous to the earlier one: the
request carries times, never a name.

- **Yes** (the earlier guest's answer in the app, or the floor approving a booking it decides): the later booking is
  `Confirmed`, the earlier one promises to free the table by `freeByUtc` (`PromisedFreeByUtc`, its end
  cut when sooner), the request is `Accepted`, and both diners are told - the later one a
  `reservation.decided` approval, the earlier one a `reservation.table-request-decided` push ("please
  free the table by 19:40") and a `table-request-decided` feed entry.
- **No**: the later booking is declined - `CancelledByVenue`, `declinedBecause` **3
  `EarlierGuestDeclined`** - or, for a change of a confirmed booking, goes back to the slot it had (§3c).
  The request is `Declined`; the later diner is told (`declinedBecause` "3" on the push and the feed).
  The floor declining is `StaffDeclined` (1) as ever.
- **No answer by `expiresAtUtc`** (only a question with a clock): the approval sweep (§3d) declines the
  later booking - `declinedBecause` **4 `EarlierGuestNoAnswer`** when it was waiting on the earlier guest
  in the app, **2 `NoAnswer`** when the floor did not answer tonight's - and the request is `Expired`. The
  earlier booking is untouched.
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
  waits on the same guest, the two hours do not restart. A move to a slot whose wait has no clock (the
  floor's, another day) has none.
- **The earlier guest's own change** - a move, a party size - is not made to promise a time, nor refused,
  because of a booking that is only asking for its table: those are left out of judging it, on the room
  screen leaving it out as under the lock. Afterwards each booking waiting on that guest is judged again
  with the earlier booking where it now is - and the same when the earlier booking is **cancelled,
  released or declined**, or its own declined change sends it back to the slot it had:
  - still the booking before it within three hours: the question stands;
  - free now - moved off the table, early enough, after it, or promised a time that covers it: the request
    is withdrawn and the later booking decided again without it, from `fallbackTrigger` - **confirmed**
    (its diner told) when nothing else needs a human, left as it is at short notice, otherwise waiting for
    that reason with no clock. So a later booking that went to the floor only because the guest before
    booked on the web is confirmed when that guest cancels;
  - another booking's sitting now runs into it (rare): withdrawn, and the floor decides, with no clock;
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
on the earlier guest's own answer - with `trigger` 5, no `approvalExpiresAtUtc` (no clock), the previous
booking and its guest's number to call, and **`earlierGuestFreeByUtc`**: the time to ask them for
(also set on a short-notice booking whose table is within three hours of its previous one). While a
question is open, **`previousBooking` is the booking it asks** - whatever day it is on (a 23:00 before a
00:30 is the day before's), never another booking that happens to sit between. Approving it is the
floor's yes for that guest. `previousBooking.promisedFreeByUtc` and `nextBooking.promisedFreeByUtc`
show promises already made; every `ReservationView` carries `promisedFreeByUtc`. The floor's table state
carries `nextReservationPromisedFreeByUtc` ("must free by 21:25" on the next-booking line) and
`sittingPromisedFreeByUtc` for the party seated now.

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
| `POST /api/reservations` | `VerifiedDiner` | — |
| `POST /api/reservations/{reservationId}/reschedule` | `VerifiedDiner` | the booking must be **theirs**, else 404 - see [3c](#3c-changing-a-booking) |
| `POST /api/reservations/{reservationId}/table-requests/{requestId}/answer` | `VerifiedDiner` | the booking must be **theirs** and the request put to them, else 404 - see [3e](#3e-a-tables-evening-when-one-booking-may-follow-another) |
| `GET /api/reservations/mine` | `VerifiedDiner` | filtered to the caller's own `DinerUserId` |
| `POST /api/reservations/{id}/cancel` | `VerifiedDiner` | the booking must be **theirs**, else 403 |
| `POST /api/reservations/{id}/extend-hold` | `VerifiedDiner` | the booking must be theirs, **started**, and not yet extended - see [notifications.md](notifications.md) |
| `POST /api/tabs/open-by-booking` | `VerifiedDiner` | "I'm at my table": the booking whose `code` was sent must be **theirs** - else 404 `booking-not-found`, identical to a code nobody holds - and its table theirs now: `Confirmed` from `StartUtc - walkInHoldbackMinutes` until `EndUtc`, or `Seated`. A free table is seated as the booking. See [tabs.md](tabs.md) |
| `POST /api/reservations/{id}/approve` · `/reject` | `WaiterOrAbove` | a waiter or above at the booking's **branch** - an owner or general manager at any branch of their venue; too late is `409 reservation-approval-expired` - see [3d](#3d-short-notice-tonight-is-the-floors-to-decide) |
| `GET /api/branches/{branchId}/reservations` | `ManagerOrAbove` + `BranchScoped` | a branch manager at their own branch only - the console's approval queue |
| `GET /api/branches/{branchId}/reservations/awaiting` | `WaiterOrAbove` + `BranchScoped` | a waiter or above at the branch, from the stored row - the floor's list, see [3d](#3d-short-notice-tonight-is-the-floors-to-decide) |

Availability - one slot or one day - is the only anonymous endpoint outside the sign-in flows.
Browsing needs no account: somebody deciding whether to eat here has to see the room before they are
asked who they are.

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
| staff approving or declining a booking that waits on the earlier guest's answer in the app | 409 | `reservation-awaiting-earlier-guest`, with `context.reservationId`, `requestId`, `expiresAtUtc` |
| the command id belongs to another caller, or another command | 409 | `client-command-id-in-use` |
| changing a booking that is not pending or confirmed, or has started | 409 | `reservation-not-changeable`, with `context.reservationId`, `status`, `startUtc` |
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

---

## Out of scope, deliberately

Short "hold this table for 10 minutes" holds, the late-nudge scheduler (a genuine scheduled action
needing an outbox and a delivery record), table combining, deposits, SignalR broadcasting, ordering
and payments.
