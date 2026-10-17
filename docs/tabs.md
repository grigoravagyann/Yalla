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
| **Occupied, open tab** | No second tab. The scanner is put on the existing tab as a **pending** participant until the host approves them. The same phone re-scanning is recognised and lands on its own row. | `JoinedExistingTab` (3) | **Guest**, `PendingApproval` |
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
- `POST /api/tabs/{tabId}/participants/{id}/approve` and `/reject` are host only.

**Approval, not a password.** The first scanner is host; everyone else lands pending until the
host taps approve. There is no code to say out loud, and the next table cannot order on your bill.

## 4. The permission model

Three flags per participant, set together by the host, with a table default for the second:

| Flag | Default for a guest | Host |
| --- | --- | --- |
| `CanOrder` | `true` | `true` |
| `CanSeeTableTotal` | `!tab.HideTotalFromGuests`, copied onto the row at join time | `true` |
| `CanPay` | **`false`** — the host is often treating | `true` |

`HideTotalFromGuests` is the *table default*, chosen when the tab opens. Changing a person's flag
afterwards is a per-row override; changing the tab default later does not re-flag people already
on the tab.

### The rules, all enforced server-side

| Rule | Where it lives | Consequence |
| --- | --- | --- |
| **`CanPay` ⇒ `CanSeeTableTotal`.** Nobody puts money toward a total they may not see. | `TabParticipant.SetPermissions`, the only writer of the three flags | Setting `canPay: true` with `canSeeTableTotal: false` is **refused with 400**, not silently corrected. A host who tapped one thing and got another has been surprised, and their next tap is made on the wrong assumption. Because the constructor calls the same method, no code path can build a participant that breaks it. |
| **A participant always sees their own items**, whatever the flags. | `TabProjection.Project`, by `TabLineSnapshot.IsOnTheBillOf` | `myLines` and `myItemsSubtotalAmd` are present on every view — for a pending participant, for a guest with the total hidden, for the host. That is what settles "I didn't order that". Own means what `/shares` charges them for: their phone's orders, a waiter's on their behalf, and the shared and table-attributed lines they were at the table for (section 7.1). |
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

A participant token for tab A gets **403** on tab B. The comparison is between the claim and the
route value, made once in the policy handler, before any handler runs.

## 7. A reader without the table total, on every read

Hiding the total on the bill hid nothing while three other diner reads still carried it: the event
stream (every line's price, every payment with the balance left after it, the total the tab closed
on), the bill's own list of comps (a percentage off the whole tab beside what it took off *is* the
subtotal), and the answer to placing an order (the whole tab's `totals`). And the bill's idea of
"my items" disagreed with what `/shares` charged. All four now follow one decision, the projection's.

### 7.1 Whose line it is

One rule, `TabLineSnapshot.IsOnTheBillOf`, and it is the rule `TabBilling` charges by, so `myLines`,
the stream's own lines and `/shares` agree on what is somebody's:

| Line | On whose `myLines` | Because `/shares` |
| --- | --- | --- |
| Ordered from their phone | theirs | charges it to them |
| Keyed in by a waiter with `onBehalfOfParticipantId` | that person's, though `placedByParticipantId` is null | charges it to them (the owner is `PlacedBy ?? OnBehalfOf`) |
| Shared (`isShared`) | everyone on its snapshot | splits it across the snapshot |
| Table-attributed (a waiter's, for nobody in particular) | everyone on its snapshot - the people approved at the moment it was ordered, not anyone who arrived later | splits it across the snapshot, exactly like a shared bottle |
| A removed participant's | nobody's own | gives it to the host - who is always shown every line |

It used to be "placed from my phone, or shared with me", which left a guest's own spoken order and
every table-attributed line off their bill while `/shares` charged them for both.
`myItemsSubtotalAmd` is the unsplit, unvoided own lines; split ones are listed with `isShared` or
`isTableAttributed` set and apportioned by `/shares`. Every line on the wire now carries
`onBehalfOfParticipantId` and `isTableAttributed`. Pinned by
`TabProjectionTests.Own_items_are_exactly_what_the_split_charges_each_person_for`, and over HTTP by
`ownItemsAmd = myItemsSubtotalAmd - the comps on their own lines`.

### 7.2 The bill's comps and discounts

`adjustments[]` on `GET /api/tabs/{tabId}`: **every one** with `tableTotalVisible`; otherwise only
those on one of the reader's `myLines`, whole. One off the whole tab, or off somebody else's dish, is
**dropped**, not shown without its amounts - the diner app draws every adjustment it is given with
`formatDram(reductionDram)`, which asserts a whole number, so a row without one would break the bill.
The reader's part of a comp off the whole tab still reaches them: it is in their `/shares`, which the
pay sheet shows as a discount line.

### 7.3 The answer to an order

`POST /api/tabs/{tabId}/orders` (and every `OrderView`) carries `tableTotalVisible` beside `totals`.
Staff always get the totals; a diner gets them by `TabPermissions.MaySeeTableTotal` on their own row,
the rule behind `tableTotalVisible` on their tab. Without it `totals` is **absent**, never zeroed.

### 7.4 The event stream

`GET /api/tabs/{tabId}/events` is the catch-up stream every phone at the table polls. Each reader gets
it **as their own view of the tab allows**.

**Who gets what is the projection's decision, not a second one.** `TabBillingQuery.GetEventsAsync`
builds the reader's `TabView` with `ITabQuery` - the very answer `GET /api/tabs/{tabId}` gives them -
and `TabEventRedactor.For(view)` reads four things off it: `tableTotalVisible`,
`TabPermissions.MaySeeRoster(me.status)`, the line ids on `myLines` and the adjustment ids on
`adjustments`. "My line" and "my adjustment" therefore mean exactly what they mean on the bill.

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
- **The snapshot is read after the events**, so every line and adjustment an event on the page names
  is on it, and a reader's own order placed a moment ago is never mistaken for someone else's. A poll
  that finds no new events reads no snapshot at all. A page that does read it answers like the tab
  read on a branch no longer on the paid tier: feature not enabled.

#### What a reader without the table total gets, by type

"Own" means the event is about one of the reader's `myLines` or `adjustments`, or about the reader
themself. "Pending also loses" is what `OnlyThemself` takes away on top - only where it is not about
them.

| # | Type | Kept for every reader | Kept only when own | Never for a restricted reader | Pending also loses |
| --- | --- | --- | --- | --- | --- |
| 1 | `TabOpened` | nothing - never written today | | everything | |
| 2 | `ParticipantJoined` | `participantId`, `status`, `canOrder`, `canSeeTableTotal`, `canPay`, `left` - never written today | | | `displayName` |
| 3 | `ParticipantApproved` | `participantId`, `status`, `canOrder`, `canSeeTableTotal`, `canPay` | | | `displayName` |
| 4 | `ParticipantRejected` | as 3 | | | `displayName` |
| 5 | `ParticipantRemoved` | as 3, and `left` | | | `displayName` |
| 6 | `ParticipantPermissionsChanged` | as 3 | | | `displayName` |
| 7 | `ParticipantRenamed` | `participantId` | | | `displayName` |
| 8 | `HostReassigned` | `fromParticipantId`, `toParticipantId` | | | |
| 9 | `SettlementModeChanged` | `settlementMode`, `lockedAtUtc` | | | |
| 10 | `OrderPlaced` | `orderId`, `placedByParticipantId`, `placedByStaffId`, `onBehalfOfParticipantId`, `estimatedReadyAtUtc`, and `lines` (filtered) | each of their lines (section 7.1), whole (`lineId`, `name`, `unitPriceAmd`, `quantity`, `isShared`, `isTableAttributed`) | every other line, **whole** - the menu is public, so a name and a quantity are a price | |
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

Participant ids, staff ids and the event's `actorId` stay for everyone: they are opaque, the
projection shows a pending reader `hostParticipantId` already, and the clients key on them.

### 7.5 What the clients do with it

- **The stream.** The diner app folds it into "refetch, and say what changed"
  (`apps/diner/src/tab/events.ts`): it reads `lineId`, `orderId` and `serviceRequestId`, each guarded
  by `typeof`, and takes every number from the refetched tab and `/shares`. None of those are
  redacted, so a redacted payload changes nothing it shows. A waiter's order whose lines are hidden
  from the reader already falls back to the plain "added by staff" marker.
- **The bill.** The two new line members are ignored by today's mapper. Until the app reads them, a
  line keyed in on the reader's behalf is labelled "for the table" rather than "yours" (`isMine` reads
  `placedByParticipantId` only), and a table-attributed line shows no "split N ways"; the money is
  right either way. Dropped adjustments need nothing.
- **The order answer.** The app's `placeOrderResult` maps `totals` unconditionally, so it throws on a
  hidden-total guest's order, after the order has gone through. It has to read `totals` as optional
  (`TabTotals | null`) **before or with** this backend.

Pinned by `TabEventRedactorTests` (each type, each audience, failing closed), `TabProjectionTests`
(whose line, which adjustments) and `TabEventVisibilityTests` (a whole evening over HTTP, read by the
host, a hidden-total guest and a pending stranger, through the stream, the tab, the order answers and
`/shares`).

## Out of scope here

Menu, ordering, order lines, totals, service charge, splitting, payments, fiscal receipts,
SignalR broadcasting, the kitchen queue. The permission model is built now so that when ordering
arrives it has a rule to enforce rather than inventing one.
