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
| `Seated` | always — the venue already put the party there, and the sitting holds the table until staff free it | — |
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

Every 409 above carries the same `context` — `reservationId`, `status`, `startUtc`, `endUtc`,
`earliestUtc` (`BookingTabRefusedProblem`) — and none of them touches the table.

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
nobody new. An occupied table with a tab puts the booker on it as a pending guest, and one with no tab
makes them its host. If a friend in the party scanned the table first, the booker lands pending on the
friend's tab and the friend approves them, exactly as for a second scan — and the booking itself stays
`Confirmed`, as it always has when a booked party scans rather than being seated by a waiter.

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
| **A participant always sees their own items**, whatever the flags. | `TabProjection.Project` | `myLines` and `myItemsSubtotalAmd` are present on every view — for a pending participant, for a guest with the total hidden, for the host. That is what settles "I didn't order that". |
| **Hiding the total hides the table total and other people's items only.** | `TabProjection.Project` | Menu prices stay visible through the menu endpoints, so a guest can always work out what their own order costs. |
| **The hidden aggregate is absent, not zero.** | `TabView` shape + JSON serialisation | When `tableTotalVisible` is `false` there is **no `tableTotal` and no `tableLines` member on the wire at all**. A zero would read as "nothing owed"; a `null` a client might render as free; an absent member beside an explicit `false` flag can only be read as "not shown to you". |
| **Pending sees only themself.** | `TabPermissions.MaySeeRoster` / `MaySeeTableTotal` | A pending participant may read the menu and their own (empty) state. Not the total, not anyone else's items, not the roster — the next table must not learn who is sitting here by scanning the wrong code. |
| **Pending cannot order.** | `TabPermissions.MayOrder`, used by both the projection (`canOrderNow`) and the `TabParticipantCanOrder` policy | Approved, allowed to order, and the tab still `Open`. |

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

## 5. Host and lifecycle

| Action | Endpoint | Who | Notes |
| --- | --- | --- | --- |
| Remove a participant | `POST .../participants/{id}/remove` | Host | A **status change, never a delete**. Their order lines and any payment they made are financial records and survive them leaving. The host cannot remove themself. |
| Leave the tab | `POST .../leave` | The participant themself | The same status change, so their lines and payments stay, they are on no shared line ordered afterwards, and their token stops working. **A host** hands the tab to the approved guest who has been on it longest, who gains sight of the total and the right to pay. With nobody approved to take it the host cannot leave (409) and a waiter takes the tab over or closes it. The app used to fake leaving on the phone while the server kept the guest on the bill. |
| Reassign the host | `POST .../reassign-host` | **Staff**, `WaiterOrAbove` + `BranchScoped` | The host left early or their phone died; otherwise the tab is stuck with nobody able to approve or change the split. The new host must be approved; they gain sight of the total and the right to pay. The old host stays as a guest with their flags unchanged. |
| Set the settlement mode | `POST .../settlement-mode` | Host | Chosen at open; changeable **until the first payment lands** (reserved or succeeded), then locked and `SettlementModeLockedAtUtc` stamped. After that, 409. |
| Mark closing | `POST .../closing` | **Staff** | The bill has been asked for. No new participants, no new orders, every live invitation revoked. |
| Everyone on the tab | `GET /api/tabs/{tabId}` for an approved participant; `GET .../participants` for staff | Host, approved guests, staff | "Aram, Nare, +1 guest" — so the host can remove someone and staff can see how many phones are on the table. Staff are not participants and the host's visibility flags do not apply to them. |

## 6. Authorization, by surface

| Surface | Policy | Extra check |
| --- | --- | --- |
| `/api/tabs/open`, `/api/tabs/join` | anonymous | `clientCommandId` required on open |
| `/api/tabs/open-by-booking` | `VerifiedDiner`, on a group of its own - the scan's is `AllowAnonymous` | the booking must be the caller's (else 404 `booking-not-found`, identical to an unknown code) and its table theirs now; `clientCommandId` required |
| `/api/tabs/{tabId}` and the participant actions | `TabParticipant` — the token's `tabId` claim must equal the route, and the participant must not be removed | Host-only actions check the host **inside the service**, so the rule holds for every caller and not only HTTP |
| `/api/tabs/{tabId}/reassign-host`, `/closing`, `/participants` | `WaiterOrAbove` + `BranchScoped` — the branch is resolved from the tab in the route | The service re-checks the actor is staff |

A participant token for tab A gets **403** on tab B. The comparison is between the claim and the
route value, made once in the policy handler, before any handler runs.

## Out of scope here

Menu, ordering, order lines, totals, service charge, splitting, payments, fiscal receipts,
SignalR broadcasting, the kitchen queue. The permission model is built now so that when ordering
arrives it has a rule to enforce rather than inventing one.
