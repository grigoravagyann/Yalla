# Tabs — opening by scan, joining, and who may see what

A tab is the running bill for one seating. This document is the decision table for what a QR scan
does, the permission rules between the people on a tab, and the argument for why none of those
people needs an account.

---

## 1. Why a tab participant has no account

Ordering is deliberately **not** gated behind a reservation. The QR code on the table opens a tab
for anyone: booked through the app, phoned ahead, or walked in off the street. In a cafe, walk-ins
are most of the traffic, and someone who wandered in for a coffee is exactly the person who wants
to tap and pay without waiting for a waiter.

If that person is asked to register, they put the phone down and wave at a waiter instead, and
the product has failed at the one moment it exists for. So:

- `POST /api/tabs/open` and `POST /api/tabs/join` are **anonymous**. The caller has no token yet.
- What comes back is a **participant token** (see `docs/auth.md`): a JWT naming a `participantId`,
  a `tabId` and a `branchId`, and nothing else. There is no user row behind it and never will be.
- The phone is recognised by a **`deviceId`** the app generates once per install. Re-scanning after
  the screen locked lands the same person on the same participant row rather than putting them on
  the bill twice. It is not a login; it is a sticker on the phone.
- A participant may put a **display name** on the tab so the host sees "Ani" rather than
  "Guest 3". That is the entire extent of their profile, and it costs nothing to skip.

A diner who *does* have an account gets the same participant token; the token additionally
records their `userId` on the participant row for later, and grants them nothing extra on the tab.
Booking needs an account because a no-show has to be counted against somebody. Ordering does not.

## 2. What a scan does — the table-state decision table

`POST /api/tabs/open` with the table's `qrToken`, the phone's `deviceId` and a caller-generated
`clientCommandId`. The answer is always **the one tab for that table**; which of four cases
applied is reported in `outcome`.

The token is matched **lower-cased, on a column that compares exactly** (`Latin1_General_100_BIN2`).
Every token is written lower-case and a scan is lower-cased before the lookup, so a code read back
in capitals still opens its table - by the server's own rule. It used to work only because the
column had no collation of its own and the database's default ignores case, which hid the diner app
upper-casing every scan.

| Table state when scanned | What happens | `outcome` | Scanner becomes |
| --- | --- | --- | --- |
| **Free** — no open `TableSession` | A `TableSession` opens (`Source = WalkIn`) through the table state machine, which writes the `TableStateChange` audit row (`Free → Occupied`, reason "opened by QR scan"). A `Tab` opens on that session. | `OpenedNewSession` (1) | **Host**, approved, may order, see the total and pay |
| **Occupied, open session, no tab** — a party a waiter sat down, or one seated from a booking, now wanting to order | A `Tab` is attached to the *existing* session. No table state changes and no audit row, because the table was already occupied. | `OpenedOnExistingSession` (2) | **Host** |
| **Occupied, open tab** | No second tab. The scanner is put on the existing tab as a **pending** participant until the host approves them, and the host is told (section 3). The same phone re-scanning is recognised and lands on its own row. | `JoinedExistingTab` (3) | **Guest**, `PendingApproval` |
| **OutOfService** | Refused with 409 and a message to ask staff for another table. | — | — |

Two things the table does not decide:

- **A `Closing` tab takes nobody new.** A scan on a table whose tab staff have marked closing is
  refused with 409. Someone who already paid their share must not find a stranger's dessert added
  after they have left.
- **An inactive table** (`IsActive = false`) does not exist for the scanner: 404.

### Concurrency: two phones scan a free table at once

Exactly one tab must exist. The service takes **no lock**. It relies on the constraints the
database already enforces, and treats losing any of them as new information about the table:

1. The **filtered unique index on open sessions per table** (`UX_TableSessions_OpenPerTable`,
   from the state-machine task) means only one scanner's seating commits. The loser's seating
   raises the state machine's conflict, is dropped, and the scanner re-reads the table — which is
   now occupied with a tab — and lands in case three as a pending guest.
2. The **unique index on tab per session** (`UX_Tabs_TableSessionId`) covers the narrower race
   where the party was already seated and two phones both try to attach the first tab. The loser
   drops its tab and joins the winner's.

Either way one phone is host and the other is pending on the same tab. There is no second
locking mechanism; adding one would make the failure mode "both mechanisms disagree".

### Idempotency: a double scan or a retry on flaky wifi

The `clientCommandId` is unique across tabs (`UX_Tabs_ClientCommandId`). A replay from the **same
device** returns the original answer with `wasReplay: true` and writes nothing. A check-then-insert
would lose the race between two simultaneous retries; the index does not. A different device
presenting someone else's command id gets 409 — the id is taken — rather than a token onto the
tab it names.

**Orders follow the same rule, per tab.** `UX_TabOrders_TabId_ClientCommandId` makes a retry that
races its original lose, and answer with the order that won, rather than send the kitchen the order
twice - the replay check alone is a read followed by an insert, and both requests passed it. A
phone's replay is matched on the tab and the participant: another participant presenting the id
gets 409 `client-command-id-in-use`. A staff replay is matched on the tab and **any** staff-placed
order on it, whichever waiter placed it: the tablet queues per device and sends under whoever is
signed in when the connection returns, so waiter A's order whose answer was lost comes back under
waiter B's PIN, and B gets A's order with `wasReplay: true` rather than a 409 that ends with the
order keyed in twice. The branch check runs before that answer, so a waiter from another branch
gets 403. A waiter presenting a phone's command id, or a phone a waiter's, still gets 409. And an
order, a void, an adjustment or a cash payment with no
`clientCommandId` is refused with 400 before it runs. The lookup used to match the id across every
tab, so a missing id - bound as `Guid.Empty` - replayed some other tab's order, totals and all.

### From a booking: `POST /api/tabs/open-by-booking`

A diner who booked through the app arrives, and the app offers "I'm at my table". The request is the
scan's body with a **`bookingCode`** in place of the `qrToken` — the six characters on their booking,
`DFJFQY` — and the answer is the scan's answer: the same `TabAccessResult`, the same four cases, the
same `outcome`, the same host and pending guests, the same replay on `clientCommandId`. The code finds
the booking, the booking names the table, and from there both routes run one loop in `TabService`
(`OpenAtTableAsync`), so the two cannot drift.

It exists because the Scan screen's "type the code" field took only the table's QR token — the
32-character string printed small under the QR — and a diner who typed their booking code into it was
told it belonged to no table. Nothing led from a booking to its table's tab.

**Whose booking.** This is the one opening that is not anonymous, and only because a booking is not:
the route carries `VerifiedDiner`, on a group of its own — the scan's group is `AllowAnonymous`, which
would wave a stacked policy straight through. The booking must be the caller's own
(`Reservation.DinerUserId`). Somebody else's code, or a phone booking with no account behind it,
answers **404 `booking-not-found`, word for word** what a code nobody holds gets, with no `context`.
The code is six characters read out at the door and proves nothing (see `ReservationCode`); a distinct
"exists, not yours" would tell anybody with an account which codes are live. Ownership is checked
before the replay and before the table is looked at.

**The code as typed.** Matched in the stored form (`ReservationCode.Normalise`): spaces and every
Unicode dash removed, upper-cased. `dfj-fqy`, ` DFJ FQY ` and `DFJFQY` are one code; none of those
characters can be part of a code, so dropping them cannot turn one code into another.

**When the table is theirs** — `Reservation.RequireTableIsTheirsAt`:

| Booking | Opens | Otherwise |
| --- | --- | --- |
| `Confirmed` | from **`StartUtc − WalkInHoldbackMinutes`** until **`EndUtc`** (exclusive) | before: **409 `booking-too-early`**, with `context.earliestUtc`; from `EndUtc`: **409 `booking-ended`** |
| `Seated` | while their sitting is open — the venue already put the party there, and the sitting holds the table whatever the clock says | settled, with the sitting closed and the table not yet cleared: **409 `booking-ended`** |
| `Completed` | — | **409 `booking-ended`** |
| `PendingApproval`, `CancelledByDiner`, `CancelledByVenue`, `NoShow` | — | **409 `booking-not-active`**, with `context.status` saying which |

The start is not a number of its own. It is the branch's **walk-in holdback**
(`ReservationPolicy.WalkInHoldbackMinutes`, 30 by default): the moment the branch starts holding the
table back from walk-ins for this booking, from which seating anybody else there earns a waiter the
"reserved 20:00 for 6" warning. Both readers take that instant from
`SessionOccupancy.HoldbackBeginsAtUtc`, so the venue keeping the table for the party and the party
being allowed to take it cannot disagree. A branch with a holdback of zero keeps nothing back, and the
table is theirs from the start time.

Late is not refused. Past `GraceMinutes` a waiter *may* release the table; until one does, it is still
the party's, up to `EndUtc`.

Nor is running over. A `Seated` booking is not measured against the clock at all: `EndUtc` is the turn
time fixed when the booking was made, a party that sits longer than it is still eating, and their own
sitting is what holds the table. So a diner who lost their per-tab token — the phone died, the app was
reinstalled — gets back onto their own open tab with the booking code at any hour.

**Settled, but not cleared.** Paying the last of a bill closes the sitting and deliberately leaves the
table occupied — the party usually sits on for another twenty minutes — and only a waiter freeing the
table completes the booking. In that window the booking still reads `Seated` with nothing left to join,
and the answer is **409 `booking-ended`**. The sticker is no help either: a table that is occupied with
no open sitting refuses a seating, so anything more goes through a waiter. `TabService` decides this
rather than letting the seating throw, and that is what keeps the answer in the booking family —
`SeatBookedPartyAsync` would raise the state machine's "Only a confirmed reservation can be seated;
DFJFQY is Seated.", which reaches the app as a bare `conflicting-state` with none of the booking's
facts and the machine's own wording in the diner's face.

**Whose sitting.** A booking opens only **its own** sitting (`TableSession.ReservationId` = the
booking). If the table still has any other sitting open — a walk-in nobody cleared, a party a waiter
put there, a scan that sat the free table as a walk-in before the booker arrived — the answer is
**409 `booking-table-occupied`** ("Table 1 still has another party seated. Ask a member of staff to
free it."), with `context.tableLabel` set, and nobody is added to that tab. Before this rule the booker landed as a *pending guest* on
a stranger's tab and was told to wait for a host they had never met. A scan of the same table is
unchanged: it still joins whatever tab is there.

Every 409 above carries the same `context` — `reservationId`, `status`, `startUtc`, `endUtc`,
`earliestUtc`, plus `tableLabel` (null except on `booking-table-occupied`) (`BookingTabRefusedProblem`) — and none of them touches the table.

**Seated as the booking.** On a free or held table the party is seated **against the booking**, by
`ITableStateService.SeatBookedPartyAsync`: the session is `Source = Reservation` with the booking's id,
the booking moves to `Seated`, and the audit row reads "seated by the diner from booking DFJFQY" with
the diner as actor. Seating them as a walk-in, as a scan does, would leave the booking `Confirmed`
while its party ate — flagged late, nudged, and one tap from a no-show. It takes the table lock and
races like every other seating: a waiter seating the same booking at the same instant wins or loses on
the same indexes, and the loser re-reads and lands on the sitting that won.

**Everything else is the scan's**, because it is the scan's code. A table out of service is 409 with the
scan's sentence — and so is one taken off the floor plan with the booking still on it, which a scan
could not even find, because this diner holds a booking for it and needs a waiter. A closing tab takes
nobody new. On the booking's own sitting with no tab yet, the booker becomes its host. On the booking's
own sitting with a tab already there — a waiter seated the booking and a friend in the party scanned
first — the booker joins **approved**, with no host tap, because it is their booking; the friend stays
host, and the approval is written to the ledger as `ParticipantApproved` so the other phones' rosters
move. A booker already on that tab — matched by device or by their diner account, so a second phone or
a reinstalled app counts — gets that same participant back, approved if it was still pending.

### From staff: `POST /api/branches/{branchId}/tables/{tableId}/open-tab`

For the walk-in with no phone, or who would rather not use one. The waiter seats the party, taps
"Open tab", and keys the order in with `staff-orders`; cash closes it as usual. Staff-only
(`WaiterOrAbove`, `BranchScoped` on the route's branch), online-only, and convergent like a scan: a
table that already has a tab — a guest scanned first, or this is the second tap — answers with that
tab, and a retry with the same `clientCommandId` answers with the same one.

It needs a sitting. A free or held table is `409 table-not-seated`: seating is the walk-in button's
job, and that is where the party size is asked. One exception: a table still **Occupied** with no
sitting - the party paid in full, which closes the sitting but not the table, and is still there
wanting more - gets a new sitting for the same party (its size carried over) and the new tab on that.
The paid sitting stays closed, and the table's physical status does not change.

The tab opens with **nobody on it and no host** — staff are not participants. So the join rule has one
more case: **the first phone onto a tab with no host becomes its host**, approved on the spot, exactly
as if it had scanned first; anyone after that joins pending as usual. Without this rule a scan would
land pending on a tab nobody could ever approve. Two phones racing for the role meet on the tab's row
version, and the loser comes back round the open loop as an ordinary guest.

### A pre-order sent before the party sat

A booking's pre-order a waiter sent to the kitchen before anybody arrived is a **held ticket** - on the kitchen
screen, on no tab, owing nothing. When the party is seated **on the booking** (a waiter seats the booking or the
held party, or the booker opens it with the booking code), the seating opens the sitting's tab as "Open tab" does -
nobody on it, admitted, the first phone its host - and the pre-order becomes its first order, **with the same id**,
the locked prices and the kitchen's progress. So the booker's open-by-booking lands on a tab that already exists
and becomes its host. A held ticket the kitchen voided before they sat is never put on the tab (nothing is owed for
it), and one whose booking is cancelled, declined or expired before they sit is voided with it. See
[reservations.md](reservations.md#3h-pre-orders).

## 2b. Admission: a scan with no proved number waits for a waiter

Decided 2026-10-20. A QR code can be photographed and scanned from home; an anonymous phantom tab would
flood the rail and order on a table nobody sits at. The waiter physically sees who is seated, so the gate is
**at the tab, not per order** - no per-order approval beyond the existing send-to-kitchen.

**A branch setting** (after review, 2026-10-20): `ReservationPolicy.UnverifiedScansNeedAdmission` - "unverified
scans wait for a waiter", read and written with the rest of the policy (`unverifiedScansNeedAdmission`; a body
that leaves it out keeps the current value). **On by default for every branch**, existing ones included (the
migration's column default). Off, a scan with no proved number is admitted on the spot as `NotRequired` (5) and
everything is as before admission existed. The setting is read when a tab opens; switching it later changes no
open tab.

| Tab opened by | `Tabs.Admission` | `AdmittedBecause` |
| --- | --- | --- |
| A scan with no diner token, or a token whose account number is not proved, setting on | **1 AwaitingAdmission** | - |
| The same, setting off | 2 Admitted | 5 NotRequired |
| A scan by an account with a proved number (`PhoneVerifiedAtUtc`, read from the row) | 2 Admitted | 2 VerifiedHost |
| `open-by-booking` (the gate proved the number) | 2 Admitted | 4 Booker |
| Staff `open-tab` | 2 Admitted | 3 OpenedByStaff (by that waiter) |
| Tabs from before admission | 2 Admitted | - |

Later admission happens **only** by a waiter's Admit (`POST /api/tabs/{tabId}/admit`, reason 1) or the
**booker joining their own booking's sitting** (reason 4). **A waiter's own order never admits a tab** (after
review, S4): staff may always order, on any tab, waiting or not - the tablet's offline queue replays orders, so
refusing them would turn a queued round into a conflict - but a phantom that scanned a photographed code first
would host the party's tab, and a waiter keying the party's round must not hand it the bill and the joiners. The
tab goes on waiting until Admit.

**While waiting** the phones read the tab and the menu, the host approves joiners, invitations work, and a
waiter can be called (service requests - the per-tab limit bounds a phantom). Phone orders are **409
`tab-awaiting-admission`** (`context.tabId`), from the ordering service rather than the policy: the
participant's flags are fine, the table is not let in yet, and "a waiter will be with you" is an answer, not
a 403. A request for `TheBill` is the same 409. `canOrderNow` is false for everybody
(`TabPermissions.TakesOrders`). After admission nothing changes from section 4: approved guests with
`canOrder` order, a joiner waits for the host, a phone's order waits at New for a waiter.

**Reject** (`POST /api/tabs/{tabId}/reject`): the tab closes (`Status` Closed, `Admission` Rejected), its
live invitation is revoked and its open calls acknowledged by the rejecting waiter. When **the scan seated the table** - the sitting
is a walk-in with no waiter and no booking behind it - the table is freed by the state machine's own free
transition, in the same transaction (audit row Occupied→Free, the reason given, the waiter as actor,
`signal` 3); **a table the scan took from `Held` goes back to Held** (after review, S6: the hold is exactly the
window a phantom could use to take a late booked party's table) - a second audit row Free→Held in the same
transaction. Otherwise the party is really there: the sitting stays and lets go of the tab (`TableSession.TabId`
cleared; `UX_Tabs_TableSessionId` is filtered `[Admission] <> 3`, so the sitting's one tab slot is free), and
the party's own scan opens a new tab. A waiting tab **a waiter has keyed orders on** cannot be turned away
(`409 conflicting-state`): somebody is there; move the host or remove the phone, then admit. Admitting a
rejected tab, or rejecting an admitted one, is **409 `tab-admission-decided`**.

**After a reject, a pause** (after review, S7). The phone that **opened** the turned-away tab (its host's device
id) opens or joins no tab at the branch for **30 minutes** (`ScanCooldown.Device`), and the table opens no tab
by a scan with no proved number for **2 minutes** (`ScanCooldown.Table`) - joining a live tab there is not
affected. Both answer the scan **409 `scan-turned-away`** with `context.retryAfterUtc`. Only where the scan
would wait for a waiter at all: a proved scanner, and a branch with the setting off, are never paused. A real
party that had joined the phantom's tab is not paused with it. The device id is the phone's own choice, so that
half is friction; the table's pause is what stops a script re-opening a phantom the moment it is closed. During
it a waiter can open the tab for a party really there (staff `open-tab`). Both read the branch's turned-away
tabs through the filtered index `IX_Tabs_BranchId_Rejected` (`BranchId, AdmissionDecidedAtUtc`,
`[Admission] = 3`).

**Who**: `WaiterOrAbove` + `BranchScoped` on the tab's branch, and the stored staff row's branch
(`StaffBranchGuard`). Who and when are on the tab (`AdmissionDecidedByStaffId`, `AdmissionDecidedAtUtc`).
**Idempotent**: naturally (admit an admitted tab, reject a rejected one: 200 as it is, no event) and on
`clientCommandId` through `ProcessedCommands` (`tab.admit`, `tab.reject`): the same id on another tab or
command is `409 client-command-id-in-use`. **One decision at a time**: Admit and Reject on one card - or one
tap twice - queue on a session-owned application lock per tab (`tab-admission:{tabId}`), so the second reads
what the first decided: the other way is `409 tab-admission-decided`, the same way answers 200 as it is. A
lost row-version race that still slips through answers the same. **Online only**, like opening a tab and
closing it.

**Live**: tab events 21 `TabAdmitted` `{ admittedBecause, byStaffId, atUtc }` and 22 `TabAdmissionRejected`
`{ byStaffId, atUtc, tableFreed }` - `byStaffId` only to readers with the table total (the host, and a guest
allowed it); a stranger or a pending joiner gets the rest (7.4). Branch change stream: a `signal` row
(`TableStateChanges.Signal`, with `tabId`) - 1 when an awaiting tab opens (same commit as the tab), 2 on
admission, 3 on rejection. The floor carries `openTabAwaitingAdmission`; the rail reads
`GET /api/branches/{branchId}/tabs/awaiting-admission` (oldest first, `sittingSeatedByScan` says whether a
reject frees the table). The revenue report and the overview count **only admitted tabs**: a rejected one, and a
phantom the waiter cleared by freeing the table (closed, never admitted, nothing on it), was never a tab the
venue served. Pinned by `TabAdmissionTests`.

**Rollout.** A diner app build that does not know `tab-awaiting-admission` shows an unknown 409, and a tablet
build without the rail has no Admit button. The setting defaults on, so the tablet and diner builds that handle
admission go out before (or with) the backend; a venue on an old tablet can switch it off meanwhile.

## 3. Inviting others

The host invites; nobody types a code.

- `POST /api/tabs/{tabId}/join-tokens` (host only) creates a `TabJoinToken` and returns the token
  and a **share URL** carrying it. The **same token** backs the QR the host shows the table and the
  link they paste into WhatsApp or Telegram for the friend who is fifteen minutes late. Two ways to
  hand over one thing. The share URL is a **path on the diner link domain** -
  `https://yalla.am/join/{token}` by default (`Tabs:JoinUrlTemplate`) - which the app's
  `/join/[token]` route and its Android intent filter match. It was a query string on the web
  console's local address, which the app's link parser read as the word "join".
- Tokens live **30 minutes**. The host refreshes by asking for another, which also **revokes any
  earlier one still live** — so a refresh kills a screenshot doing the rounds, and a screenshot from
  last Tuesday gets 401 whatever it once pointed at.
- `POST /api/tabs/join` with the token puts the device on the tab as `PendingApproval` and issues
  it a participant token scoped to this tab.
- `POST /api/tabs/{tabId}/participants/{id}/approve` and `/reject` are host only. Asking twice is
  answered as the first time: approving somebody already approved, or rejecting somebody already
  turned away, is 200 with them as they are and writes no event and no push. The banner and the
  People screen offer the same tap, and a retry on flaky wifi sends it twice; a second approval used
  to collide with the first one's push on its idempotency key and come back as a 500. Two approvals
  at the same instant write one, and both answer with it.

**Approval, not a password.** The first scanner is host; everyone else lands pending until the
host taps approve. There is no code to say out loud, and the next table cannot order on your bill.

### Telling the host somebody is waiting

The joiner sits on "waiting for the host", and the host has no reason to open the People screen. So
a join that lands **pending** - a scan of a table whose tab is open, or `POST /api/tabs/join` with
the host's invitation - tells them twice, in the same `SaveChanges` as the participant row
(`TabService.AnnouncePendingJoin`):

- **On the stream.** One `ParticipantJoined` (2), in the shape of every other roster event, so the
  redactor's participant rule already decides it (section 7.4):

  ```json
  { "participantId": "…", "displayName": "Anna", "status": 1,
    "canOrder": true, "canSeeTableTotal": true, "canPay": false }
  ```

  `status` is always 1 (`PendingApproval`) and the flags are the tab's guest defaults.
  `displayName` is what the joiner typed, or the roster's placeholder `Guest {n}` when they typed
  nothing. Every phone at the table polls the stream, so the host's bill and roster move within one
  poll. A pending reader sees only their own join with its name; anybody else's, without it.
- **On the host's lock screen**, when the host scanned with a diner account
  (`TabParticipant.UserId`): an outbox message `tab.join-requested` to that account, keyed
  `participant:{participantId}:join-requested` - see [notifications.md](notifications.md#2-the-six-message-types).
  Most hosts scanned without one, and then the stream is all there is.

**Written once per join.** The same phone scanning again is recognised and lands on its own row, so
it writes nothing. Somebody the host turned away who scans again is a new participant row and a new
join: `ParticipantJoined` is written, so the host sees them on the bill and can still let them on,
but they are **not pushed again** when the same phone, or the same diner account on another phone,
already has a turned-away row on this tab (`TabParticipant.WasTurnedAway`: removed without ever being
approved). Somebody who was let on and later left is not turned away, and asking back in is pushed.

**Checked again at send time.** The push goes out only while the joiner is still pending, the tab is
still `Open` - closing leaves a pending joiner pending, and nobody new is let on a tab being settled -
and the account it was written for is still the tab's host, which staff can change. Otherwise it is
marked done and not sent; the new host sees the joiner on the bill.

**Only a join that waits for somebody.** Neither of these is a join request, and neither writes
`ParticipantJoined` or pushes:

- The **first phone onto a tab staff opened** hosts it (section 2). There is nobody on the tab to tell
  and nothing to wait for - the same reason a scan that opens a tab writes no `TabOpened`.
- The **booker on their own booking's sitting** is approved on the spot. `ParticipantApproved` already
  moves every roster; a `ParticipantJoined` beside it for the same instant would only make every phone
  refetch twice, and asking the host to let in somebody already let in is noise.

The **host's own account on a second phone** lands pending like anybody else - it is a second device,
and the roster says so - but is not pushed to: the host does not need telling that they want to join
their own tab. No diner-feed entry is written: the request is answered in seconds, from the tab, and
a feed row saying "Anna wanted to join" an hour later would be history nobody acts on.

Pinned by `JoinRequestTests`; what each reader gets of the event over HTTP, by
`TabEventVisibilityTests`.

## 4. The permission model

Three flags per participant, set together by the host, with a table default for the second:

| Flag | Default for a guest | Host |
| --- | --- | --- |
| `CanOrder` | `true` | `true` |
| `CanSeeTableTotal` | `!tab.HideTotalFromGuests`, copied onto the row at join time | `true`, always - see below |
| `CanPay` | **`false`** — the host is often treating | `true` |

`HideTotalFromGuests` is the *table default*, chosen when the tab opens. Changing a person's flag
afterwards is a per-row override; changing the tab default later does not re-flag people already
on the tab.

### The rules, all enforced server-side

| Rule | Where it lives | Consequence |
| --- | --- | --- |
| **`CanPay` ⇒ `CanSeeTableTotal`.** Nobody puts money toward a total they may not see. | `TabParticipant.SetPermissions`, the only writer of the three flags | Setting `canPay: true` with `canSeeTableTotal: false` is **refused with 400**, not silently corrected. A host who tapped one thing and got another has been surprised, and their next tap is made on the wrong assumption. Because the constructor calls the same method, no code path can build a participant that breaks it. |
| **A participant always sees their own items**, whatever the flags. | `TabProjection.Project`, by `TabProjection.IsOwnLine` | `myLines` and `myItemsSubtotalAmd` are present on every view — for a pending participant, for a guest with the total hidden, for the host. That is what settles "I didn't order that". Own means their phone's orders, a waiter's on their behalf, and the shared lines they were at the table for - and the table-attributed lines they were there for only with the total, because those can be the whole bill (section 7.1). |
| **The host always sees the total.** | `TabParticipant.SetPermissions` | Setting `canSeeTableTotal: false` on the host's own row is **refused with 422 `validation-failed`, field `canSeeTableTotal`**. The host approves joiners, chooses the split and usually pays; hidden from their own bottom line, nobody could ever turn it back on, because only the host sets flags. A host who hands the tab over is a guest like any other. |
| **Hiding the total hides the table total and other people's items only.** | `TabProjection.Project` | Menu prices stay visible through the menu endpoints, so a guest can always work out what their own order costs. The comps follow the items: only those on their own lines (section 7.2). |
| **The hidden aggregate is absent, not zero.** | `TabView` shape + JSON serialisation | When `tableTotalVisible` is `false` there is **no `tableTotal`, no `tableLines` and no `tipsAmd` member on the wire at all**. A zero would read as "nothing owed"; a `null` a client might render as free; an absent member beside an explicit `false` flag can only be read as "not shown to you". |
| **Pending sees only themself.** | `TabPermissions.MaySeeRoster` / `MaySeeTableTotal` | A pending participant may read the menu and their own (empty) state. Not the total, not anyone else's items, not the roster — the next table must not learn who is sitting here by scanning the wrong code. |
| **Pending cannot order.** | `TabPermissions.MayOrder`, used by both the projection (`canOrderNow`) and the `TabParticipantCanOrder` policy | Approved, allowed to order, and the tab still `Open`. |
| **The event stream keeps the same secrets.** | `TabEventRedactor`, driven by the reader's own `TabView` | A reader without the total gets every event of `GET .../events` with the table's money and other people's items taken out of the payloads; a pending reader loses other people's names and notes too. Section 7.4. |
| **So does the answer to an order.** | `TabOrderService`, by `TabPermissions.MaySeeTableTotal` | `tableTotalVisible: false` and no `totals` member for a diner without the total (section 7.3). |

### One projection, not scattered checks

Everything a client can see about a tab comes out of **one pure function**,
`TabProjection.Project(TabSnapshot, viewerParticipantId)`. The query loads the whole tab,
unfiltered; the projection applies the viewer's status and flags. There is deliberately no
`if (canSeeTotal)` in any endpoint. A check scattered across handlers is a check one handler
forgets, and with money the failure is a leak rather than a crash. The rules above are each a unit
test against that function.

The rules that decide what a participant may *do* (`TabPermissions`) are the same functions the
projection uses to tell the client what they may do, so the app never draws a button that returns
403.

The view also names where the tab is - `venueName` and `branchName` beside `tableLabel` and
`timeZoneId` - so a phone that has only scanned a code can head the bill without a second read.

`tipsAmd` - the tips on the tab's succeeded payments - travels with the table total and outside it:
present exactly when `tableTotal` is, never added into `paidAmd` or `remainingAmd`. Staff always get
it on `GET /api/tabs/{tabId}/participants`. See `docs/billing.md` section 6, which also covers a
request for the bill that names a scope and a tip.

### Faces on the roster

`participants[]` (`TabParticipantSummary`) and `me` / the staff list (`TabParticipantView`) carry
`photo` (`PhotoView`: `photoId`, `thumbnailUrl`, `cardUrl`, `fullUrl`, `width`, `height`; the URLs
relative to the API, served anonymously from `/api/photos/{photoId}/{variant}`) when the participant
scanned on a phone signed in to a diner account that has a profile photo. Absent for everybody else -
most guests scan without an account - and for an account that has since been deleted, even before its
link on the participant is cleared. Draw initials then.

The roster rules do not change: a pending joiner sees one row, their own, so one face; a removed
participant is off the roster and their face with them. `TabQuery` reads the picture in the same
statement as the participants (a correlated lookup on `DinerUsers` by `TabParticipant.UserId`), so a
table of six costs the same five statements as a table of one. The participant commands - approve,
reject, remove, leave, permissions, rename - answer with the same `photo`, so a client patching its
roster from the response keeps the face.

## 5. Host and lifecycle

| Action | Endpoint | Who | Notes |
| --- | --- | --- | --- |
| Remove a participant | `POST .../participants/{id}/remove` | Host | A **status change, never a delete**. Their order lines and any payment they made are financial records and survive them leaving. The host cannot remove themself. |
| Leave the tab | `POST .../leave` | The participant themself | The same status change, so their lines and payments stay, they are on no shared line ordered afterwards, and their token stops working. **A host** hands the tab to the approved guest who has been on it longest, who gains sight of the total and the right to pay. With nobody approved to take it the host cannot leave (409) and a waiter takes the tab over or closes it. The app used to fake leaving on the phone while the server kept the guest on the bill. |
| Reassign the host | `POST .../reassign-host` | **Staff**, `WaiterOrAbove` + `BranchScoped` | The host left early or their phone died; otherwise the tab is stuck with nobody able to approve or change the split. The new host must be approved; they gain sight of the total and the right to pay. The old host stays as a guest with their flags unchanged. |
| Set the settlement mode | `POST .../settlement-mode` | Host | Chosen at open; changeable **until the first payment lands** (reserved or succeeded), then locked and `SettlementModeLockedAtUtc` stamped. After that, 409. |
| Mark closing | `POST .../closing` | **Staff** | The bill has been asked for. No new participants, no new orders, every live invitation revoked. |
| Settle the last of the bill | `POST .../payments/cash` | **Staff** | Closes the tab and its sitting (`tableSessionClosed: true`) and **leaves the table Occupied**: the party usually sits on, and only a waiter freeing the table completes it (`TabPaymentService.CloseSessionAsync`). Not a bug when the floor still shows the table taken ten minutes after a settlement - QA 2026-10-16, D-M4. |
| Everyone on the tab | `GET /api/tabs/{tabId}` for an approved participant; `GET .../participants` for staff | Host, approved guests, staff | "Aram, Nare, +1 guest" — so the host can remove someone and staff can see how many phones are on the table. Staff are not participants and the host's visibility flags do not apply to them. |

### Ordering after closing time

**The tab decides, not the clock.** An order on an `Open` tab is accepted whatever the branch's
opening hours say right now: a tab exists because somebody scanned the code at the table, so the
party is physically there, and "last orders" is the floor's call - *Mark closing* refuses every
further order with 409 `tab-not-accepting-orders`. A clock gate would refuse a real table over hours
an owner mistyped (the public page's "Closed today" at 01:21 on a branch open until 01:00 was one),
with the kitchen still working. Pinned by
`OrderingTests.A_seated_party_keeps_ordering_after_closing_time_because_the_tab_decides_not_the_clock`
(QA 2026-10-16, parity P3). Opening hours gate what they are for: `isOpenNow` on the public page and
booking slots.

## 6. Authorization, by surface

| Surface | Policy | Extra check |
| --- | --- | --- |
| `/api/tabs/open`, `/api/tabs/join` | anonymous | `clientCommandId` required on open |
| `/api/tabs/open-by-booking` | `VerifiedDiner`, on a group of its own - the scan's is `AllowAnonymous` | the booking must be the caller's (else 404 `booking-not-found`, identical to an unknown code) and its table theirs now; `clientCommandId` required |
| `/api/tabs/{tabId}` and the participant actions | `TabParticipant` — the token's `tabId` claim must equal the route, and the participant must not be removed | Host-only actions check the host **inside the service**, so the rule holds for every caller and not only HTTP |
| `/api/tabs/{tabId}/reassign-host`, `/closing`, `/participants` | `WaiterOrAbove` + `BranchScoped` — the branch is resolved from the tab in the route | The service re-checks the actor is staff |
| `/api/tabs/{tabId}/admit`, `/reject`, `/api/branches/{branchId}/tabs/awaiting-admission` | `WaiterOrAbove` + `BranchScoped` | and the stored staff row's branch (section 2b) |

A participant token for tab A gets **403** on tab B. The comparison is between the claim and the
route value, made once in the policy handler, before any handler runs.

## 7. A reader without the table total, on every read

Hiding the total on the bill hid nothing while three other diner reads still carried it: the event
stream (every line's price, every payment with the balance left after it, the total the tab closed
on), the bill's own list of comps (a percentage off the whole tab beside what it took off *is* the
subtotal), and the answer to placing an order (the whole tab's `totals`). And the bill's idea of
"my items" left out lines `/shares` charged. All four now follow one decision, the projection's - and
where the bill and the arithmetic still differ, for a waiter's lines for the whole table, they differ
on purpose (7.1).

### 7.1 Whose line it is

Two questions, and they are deliberately not the same one.

- **Charged for** - `TabLineSnapshot.IsChargedTo`, the rule `TabBilling` and `/shares` use. A split
  line (shared or table-attributed) is charged to everyone on its snapshot - the participants approved
  at the moment it was ordered, not anyone who arrived later; any other line to its owner, who ordered
  it or had a waiter key it in for them (`PlacedBy ?? OnBehalfOf`, where a line's own
  `OnBehalfOfParticipantId` wins over its order's - `TabOrder.OwnerOf`).
- **On my lines** - `TabProjection.IsOwnLine`, what the bill itemises as yours, price and all. The
  same, with **one exception**: a table-attributed line only for a reader allowed the table total.

| Line | On `myLines` of | `/shares` charges |
| --- | --- | --- |
| Ordered from their phone | the person who ordered it | them |
| Keyed in by a waiter with `onBehalfOfParticipantId`, or with that line's own `items[i].participantId` | that person, though `placedByParticipantId` is null | them (it used to be nobody's on the bill) |
| Shared (`isShared`) - a bottle split with the people at the table | everyone on its snapshot, as it always was | everyone on its snapshot |
| Table-attributed - a waiter's line for nobody in particular | everyone on its snapshot **who may see the table total** | everyone on its snapshot, total or not |
| A removed participant's | nobody's own | the host - who is always shown every line |

**Why the exception.** At a table where the waiter keys everything in unattributed, the
table-attributed lines are the whole bill. Itemising the ones a guest was at the table for would hand
a guest the host hid the total from every line on the tab, price and all - the total, added up. So a
reader without the total gets none of them on `myLines`, nor on the stream's `orderPlaced.lines`, nor
any comp on them (7.2). They are still charged their part, and still see it **as money**: it is in
`sharedItemsAmd` on their `/shares`, which the pay sheet shows as "shared items". A reader with the
total sees those lines on `myLines` (the ones they were there for, flagged `isTableAttributed`) and in
`tableLines`.

`myItemsSubtotalAmd` is the unsplit, unvoided own lines; split ones on `myLines` carry `isShared` or
`isTableAttributed` and are apportioned by `/shares`. Every line on the wire carries
`onBehalfOfParticipantId` and `isTableAttributed`. Pinned by
`TabProjectionTests.Own_items_are_what_the_split_charges_each_person_for` (with the total and without),
and over HTTP by `ownItemsAmd = myItemsSubtotalAmd - the comps on their own lines` and
`sharedItemsAmd = half the bottle + half the table's khachapuri after its comp`.

**A waiter's order goes straight to the kitchen (2026-10-20).** Decided by the user: an order keyed
in by staff is placed and moved to `InKitchen` in the same commit, with `OrderPlaced` followed by
`OrderStatusChanged` on the stream. The waiter who took it at the table is already the check a
phone's order waits at `New` for; a second "send" tap on the tablet could only be forgotten. A
phone's order still starts at `New` and a waiter sends it on. Pinned by
`KitchenQueueEndpointTests.A_waiters_order_goes_straight_to_the_kitchen_and_a_phones_order_waits_for_a_waiter`.

**Who, per line (2026-10-19).** The floor tablet assigns each line of a spoken round to the table
(shared) or a named guest and sends the round once. `OrderItemRequest.participantId` is that line's
guest, stored on `TabOrderLines.OnBehalfOfParticipantId`, and it overrides the order's
`onBehalfOfParticipantId` for that line alone - so one send is one order and one kitchen ticket. The
order-level field is a **default for the lines that name nobody**, not a claim on the order.

- A line's owner is `TabOrder.OwnerOf`: its own guest, else the order's, and **nobody on a shared or
  table-attributed line** whatever the order says. Every read reports exactly that as the line's
  `onBehalfOfParticipantId` - the tab, the order answer, the kitchen queue, the stream - absent on a
  split line.
- Both fields are checked by one rule: an approved participant on this tab (404 `not-found`; 409
  `conflicting-state` for a guest not let on yet, or one taken off). Those refusals carry
  `context.field` (`onBehalfOfParticipantId` or `items[i].participantId`) and `context.participantId`
  (plus `participantStatus` on the 409), from `NamedGuestNotOnTabException` and
  `NamedGuestNotOrderableException`, and nothing else with the same status and code does: a dish on no menu, another branch's dish and
  an unknown tab have no `participantId`, and a closed tab is `tab-not-accepting-orders` with
  `context.status`. A phone naming anybody is 403,
  before anything else about the body. A waiter's `isShared` line that names a `participantId` is
  400 `invalid-request` with `context.field` `items[i].participantId` - checked after the replay
  lookup, so a retry gets its original order whatever its body says now.
- "Order ready" goes to the diner who ordered from their phone and to each owner of a line still on
  the bill. An order-level guest with no line of theirs left (all put on others, or voided) is not
  told. Keys: `order:{id}:ready` for the order's own owner, `order:{id}:ready:{participantId}` for
  everybody else.
- The diner's Orders tab lists the order for each guest who owns an unsplit line of it (see
  `diner-browse.md`).

Pinned by `OrderingTests` ("who, per line") and `LineAttributionEndpointTests`.

**The Orders tab agrees.** `GET /api/diner/orders` (`DinerOrderQuery`) lists an order the diner
placed, one keyed in on their behalf, and a waiter's order for the whole table only when they may see
the table total **and** were on the snapshot of one of its lines - the tab read's condition for
itemising it. One difference is deliberate: a waiter's whole-table order may also carry a shared line,
which the tab shows a guest without the total like any shared item; the Orders tab lists orders, not
lines, and that order's table-attributed lines are what such a guest may not see itemised, so the
order stays off their list and the bottle stays on their bill.

### 7.2 The bill's comps and discounts

`adjustments[]` on `GET /api/tabs/{tabId}`: **every one** with `tableTotalVisible`; otherwise only
those on one of the reader's `myLines`, whole. One off the whole tab, off somebody else's dish, or off
a table-attributed line (not itemised for them, 7.1) is **dropped**, not shown without its amounts -
the diner app draws every adjustment it is given with `formatDram(reductionDram)`, which asserts a
whole number, so a row without one would break the bill. The reader's part of a comp off the whole tab
still reaches them: it is in their `/shares`, which the pay sheet shows as a discount line.

### 7.3 The answer to an order

`POST /api/tabs/{tabId}/orders` (and every `OrderView`) carries `tableTotalVisible` beside `totals`.
Staff always get the totals; a diner gets them by `TabPermissions.MaySeeTableTotal` on their own row,
the rule behind `tableTotalVisible` on their tab. Without it `totals` is **absent**, never zeroed.

### 7.4 The event stream

`GET /api/tabs/{tabId}/events` is the catch-up stream every phone at the table polls. Each reader gets
it **as their own view of the tab allows**.

**Who gets what is the projection's decision, not a second one.** `TabBillingQuery.GetEventsAsync`
first reads the reader's own row, and `TabEventRedactor.AudienceFor(status, canSeeTableTotal)` asks
the two functions the tab read asks - `TabPermissions.MaySeeTableTotal` and `MaySeeRoster`. A reader
who gets `Everything` (the host, at nearly every table) gets the page as stored, and the tab is never
read. For a restricted reader with something on the page, it builds their `TabView` with `ITabQuery` -
the very answer `GET /api/tabs/{tabId}` gives them - and `TabEventRedactor.For(view)` takes the line
ids on `myLines` and the adjustment ids on `adjustments` from it. "My line" and "my adjustment"
therefore mean exactly what they mean on the bill. `The_audience_off_the_row_is_the_audience_off_the_view`
pins that the two reads agree for every status and flag.

| Reader | Audience | Gets |
| --- | --- | --- |
| Approved, `tableTotalVisible` (the host, a guest allowed the total) | `Everything` | every payload exactly as written |
| Approved, total hidden | `WithoutTableMoney` | everything but the table's money and other people's items - the table below |
| Pending approval (whatever its flags say) | `OnlyThemself` | the same, and no name or note but their own: the roster is not theirs yet |

**How it is applied.**

- **At read time, on this endpoint only.** The stored `TabEvents` rows stay complete. There is no
  staff event endpoint; `DinerOrderQuery` reads `OrderStatusChanged` rows directly for the Orders
  tab's timeline, carries no money from them, and is untouched.
- **Every event stays.** Type, sequence, actor and time are never changed and no event is dropped:
  the client keeps the highest sequence it has and treats a hole as missed events, so a dropped event
  would make every phone refetch on every poll.
- **A whitelist, per type.** A restricted reader gets only the members the rule for that type names.
  A member a writer adds later, or a type this build does not know, is left out for them until
  somebody decides here; `Every_event_type_has_a_rule` and the HTTP test's "every member the host
  reads is classified" check make that decision unavoidable.
- **Absent, not null.** A redacted member is not on the wire, for the projection's reason (section 4):
  absent beside a hidden total reads as "not shown to you", where a null or a zero could be drawn as
  free.
- **What a poll costs.** One statement for the reader's row and the branch's tier, the page, and the
  stream's high-water mark. Only a restricted reader with something on the page pays for the tab
  read on top (`TabEventReadPathTests` counts them), and that snapshot is read **after** the events,
  so every line and adjustment an event names is on it and a reader's own order placed a moment ago
  is never mistaken for someone else's.
- **The tier is checked first.** A branch below Paid answers every page the way the tab read does -
  409 `feature-not-enabled` - whether or not there is anything on it. An empty poll used to pass
  where a full one was refused.

#### What a reader without the table total gets, by type

"Own" means the event is about one of the reader's `myLines` or `adjustments`, or about the reader
themself. "Pending also loses" is what `OnlyThemself` takes away on top - only where it is not about
them.

| # | Type | Kept for every reader | Kept only when own | Never for a restricted reader | Pending also loses |
| --- | --- | --- | --- | --- | --- |
| 1 | `TabOpened` | nothing - never written today | | everything | |
| 2 | `ParticipantJoined` | `participantId`, `status`, `canOrder`, `canSeeTableTotal`, `canPay` - written for a join that lands pending (section 3) | | | `displayName` |
| 3 | `ParticipantApproved` | `participantId`, `status`, `canOrder`, `canSeeTableTotal`, `canPay` | | | `displayName` |
| 4 | `ParticipantRejected` | as 3 | | | `displayName` |
| 5 | `ParticipantRemoved` | as 3, and `left` | | | `displayName` |
| 6 | `ParticipantPermissionsChanged` | as 3 | | | `displayName` |
| 7 | `ParticipantRenamed` | `participantId` | | | `displayName` |
| 8 | `HostReassigned` | `fromParticipantId`, `toParticipantId` | | | |
| 9 | `SettlementModeChanged` | `settlementMode`, `lockedAtUtc` | | | |
| 10 | `OrderPlaced` | `orderId`, `placedByParticipantId`, `placedByStaffId`, `onBehalfOfParticipantId`, `estimatedReadyAtUtc`, and `lines` (filtered) | each line on their `myLines` (7.1), whole (`lineId`, `name`, `unitPriceAmd`, `quantity`, `isShared`, `isTableAttributed`, `onBehalfOfParticipantId` - the line's guest, else the order's) | every other line, **whole** - the menu is public, so a name and a quantity are a price - and so every table-attributed line | |
| 11 | `OrderStatusChanged` | `orderId`, `fromStatus`, `toStatus`, `byStaffId` | | | |
| 12 | `LineVoided` | `lineId`, `orderId`, `voidedByStaffId` | `name`, `reason` | | |
| 13 | `AdjustmentAdded` | `adjustmentId`, `tabOrderLineId` | `kind`, `percent`, `amountAmd`, `reason` (an adjustment on their own line) | all four, off the whole tab or another person's line | |
| 14 | `AdjustmentVoided` | `adjustmentId` | `reason` | | |
| 15 | `PaymentRecorded` | `paymentId`, `method`, `tabParticipantId`, `byStaffId` | `amountAmd`, `tipAmd` (their own payment) | `remainingAmd`; another's or the table's `amountAmd`, `tipAmd` | |
| 16 | `ServiceRequested` | `serviceRequestId`, `preset`, `requestedByParticipantId`, `billScope`, `tipPercent` (no amounts since #36) | | | `note` |
| 17 | `ServiceRequestAcknowledged` | `serviceRequestId`, `byStaffId` | | | |
| 18 | `TabClosing` | `atUtc` | | | |
| 19 | `TabClosed` | `closedAtUtc`, `tableSessionClosed` | | `totalAmd` | |
| 20 | `TabAbandoned` | `reason`, `byStaffId` | | `writtenOffAmd` | |
| 21 | `TabAdmitted` | `admittedBecause`, `atUtc` | | `byStaffId` | |
| 22 | `TabAdmissionRejected` | `atUtc`, `tableFreed` | | `byStaffId` | |

Participant ids, staff ids and the event's `actorId` stay for everyone: they are opaque, the
projection shows a pending reader `hostParticipantId` already, and the clients key on them. The one
exception is which waiter let a table in or turned it away (21, 22): that is staff business, not a
stranger's or a pending joiner's (after review, 2026-10-20).

### 7.5 What the clients do with it

Checked read-only against the frontend's `fix/tab-events-visibility` branch, which maps
`onBehalfOfParticipantId` and `isTableAttributed` and treats a table-attributed line as shared.

- **The stream.** The diner app folds it into "refetch, and say what changed"
  (`apps/diner/src/tab/events.ts`): it reads `lineId`, `orderId` and `serviceRequestId`, each guarded
  by `typeof`, and takes every number from the refetched tab and `/shares`. None of those are
  redacted, so a redacted payload changes nothing it shows. A waiter's order whose lines are hidden
  from the reader already falls back to the plain "added by staff" marker.
- **The bill.** A line keyed in on the reader's behalf is "yours"; a table-attributed line, where it
  is itemised at all, is drawn as split. For a reader without the total those lines are simply not
  there, which the app needs nothing to handle; their money is in `/shares`. Dropped adjustments need
  nothing.
- **The order answer.** `placeOrderResult` reads `totals` as optional (`TabTotals | null`), so a
  hidden-total guest's order answer maps.
- **The host's own flags.** The people screen offers flags for everybody but the caller, so the
  host-sees-the-total refusal (section 4) is not reachable from the app; it holds for any client.

Pinned by `TabEventRedactorTests` (each type, each audience, failing closed), `TabProjectionTests`
(whose line, which adjustments), `TabEventReadPathTests` (what a poll reads, and the tier) and
`TabEventVisibilityTests` (a whole evening over HTTP, read by the host, a hidden-total guest and a
pending stranger, through the stream, the tab, the order answers and `/shares`).

## Out of scope here

Menu, ordering, order lines, totals, service charge, splitting, payments, fiscal receipts,
SignalR broadcasting, the kitchen queue. The permission model is built now so that when ordering
arrives it has a rule to enforce rather than inventing one.
