# Activity history and staff results

Who did what at a venue: who took which order, who answered which message, who gave the discount, from which tablet
or phone. The console's **History** page, the **Staff → Results** table, a person's page, a bill's timeline (the
Check board) and a waiter's own **My results** all read what this records.

Decided with the user (2026-10-24):

- Managers, the general manager and owners see everyone - a branch manager their own branch only. A waiter sees only
  their own numbers. **There is no ranking a waiter can open.**
- The log is kept **12 months**; entries about money (payments, discounts, comps, voids, write-offs, takeaway
  collected) **3 years**. A daily purge, like the message history's.
- Sales, guests and tips are the **table's waiter's**. A transfer moves the table from that moment; what was done
  before stays with the earlier waiter.
- A call's response time is from the call to the claim ("I'm on it"); a message's reply time from the guest's text to
  the first venue message. Both are reported as **medians**.

---

## 1. The log: `ActivityEntries`

One row per thing done, written through **one service, `IActivityLog`, in the same unit of work as the change**: a
change cannot land without its entry and an entry cannot survive a change that rolled back. `IActivityLog` only adds;
the caller's own save commits it. A path that retries a lost race clears its change tracker, entry included, before it
tries again, so an entry is never written twice or for the losing attempt.

| Column | What |
| --- | --- |
| `VenueId`, `BranchId?` | Where. `BranchId` is null for something venue-wide - an owner's account. |
| `OccurredAtUtc` | When. |
| `ActorKind` | `1 Staff`, `2 Diner` (the guest), `3 System`, `4 PlatformAdmin` - pinned ints in the database; on the wire the words `staff`, `guest`, `system`, `platform`, as the feed's `actorKind` filter takes them. |
| `StaffMemberId?`, `ActorName` | Who, and their name **then** (copied from the stored row, not the token). The guest's name for a guest; nothing for the system. |
| `DeviceId?`, `DeviceLabel` | The enrolled device the request came from and its label then: a tablet's name ("Hall tablet"), a personal phone's person ("Aram's phone"), or `Console` for a console sign-in. |
| `Category` | `1 booking`, `2 table`, `3 order`, `4 payment`, `5 takeaway`, `6 message`, `7 settings`, `8 staff`, `9 device` - pinned ints, words on the wire. |
| `Action` | A stable code, `category.verb` - §3. |
| `SubjectType`, `SubjectId`, `SubjectCode?` | What it was done to - the detail link - and its short code (`T-482`, a booking's). |
| `TableSessionId?` | The sitting it belongs to. A bill's timeline is every entry of its sitting: the seat came before the tab existed. |
| `TableLabel?`, `GuestName?`, `ItemNames?` | Copied names - what the search matches. |
| `AmountAmd?`, `IsMoney` | The amount it is about; whether it is kept three years. |
| `BeforeJson?`, `AfterJson?`, `Reason?` | What changed, and why as somebody typed it. Never a PIN, a password, a link or its token. |
| `SummaryJson` | The parameters a client renders the line from, in its own language. **Never prose.** |

No foreign keys, as `PlatformAuditLogs` has none: the log is the record and outlives what it names. Indexes: one per
read - `(BranchId, OccurredAtUtc, Id)` the feed, `(StaffMemberId, OccurredAtUtc)` a person, `(SubjectType, SubjectId,
OccurredAtUtc)` a subject, `(TableSessionId, OccurredAtUtc)` a bill, `(VenueId, OccurredAtUtc, Id)` filtered to
`BranchId IS NULL` the venue-wide entries, `(IsMoney, OccurredAtUtc)` the purge. For somebody venue-wide the feed reads
the branch's entries and the branch-less ones as two ordered ranges of a page each and merges them (ids compared as SQL
Server orders them), rather than one `OR` that would sort the whole window on every page.

**Guest messages in the log.** `message.sent` carries the venue's own text (`summary.text`) - never the guest's. It
follows the threads' rules: a venue message is kept when a guest deletes their account (only the guest's texts go), and
an entry goes at a year from when it was written, which is never later than its thread's purge (a year after the
thread's last message). No entry carries a guest's phone number; a person's `staff.updated` before/after carries a
staff phone, which the Staff page already shows the same readers.

**`PlatformAuditLogs` stays** the platform tier's own record - Yalla reads it, and the password-reset rules read their
history from it. The venue actions that also write there (a person added, a sign-in issued) write an entry here too, for
the venue's History.

### Who acted, and from where

Read from the request, never passed in. `ICurrentActor.DeviceId` is the `deviceId` claim of **any staff token that
carries one** - a tablet's staff session or a personal phone's - and the log looks the device up in `StaffDevices` by
id. A tablet is labelled with its `Name`; a phone (`Kind = Phone`) with its bound person's first name, "Aram's phone",
not the name the browser gave itself when it was paired. A device it cannot find is recorded by id with no label; a
staff token with no device is the console.

A caller may say only three things about the actor: **the system did it** (`BySystem` - a deadline passed: the approval
and takeaway sweeps, a takeaway auto-accepted), **the guest did it** (`ByGuest` - a booking's manage link carries no
diner token, but it is the guest), and **this request proved who it is** (`ProvedBy` - pairing a phone checks its
person's PIN before any token names them; the entry is theirs, on the phone being paired).

## 2. What is recorded

Staff actions throughout; the system's; the guest's only where they matter to the venue.

- **Bookings** (`ReservationService`, `TableStateService`, the approval sweep, pre-order answers): the guest's booking
  made, moved and cancelled (app or link), the hold extended, the "on my way" / "running late" answers and a table
  request answered; staff approve, decline, seat, no-show, release, withdraw a no-show, mark not coming; the system's
  auto-decline when nobody answered in time. There is no staff "create a booking" path in the API, so none is logged.
- **Tables and tabs**: a party seated (walk-in, scan, held), freed, held, hold released, out of / back in service; a tab
  opened by staff, admitted, turned away; the host moved; the bill presented; **the table transferred**; **a call
  claimed**.
- **Orders**: a waiter's order (straight to the kitchen), a guest's phone order, a pre-order sent, the kitchen rail's
  moves (in kitchen, ready, served), a line voided, an order voided.
- **Money**: cash taken (with its tip), a discount or comp and its reversal, a bill written off.
- **Takeaway**: placed and cancelled by the guest; accepted, declined, ready, collected (cash or card), not collected
  by staff; accepted or declined by the system.
- **Guest messages**: a venue text, "are you coming?".
- **Settings**: a menu price, an item's availability (hall or takeaway), opening hours, special days, the reservation
  policy, takeaway, messages, pre-orders.
- **The team and devices**: a person added or changed (name, phone, role, branch, active), a PIN set, a sign-in issued,
  a PIN lockout cleared, an enrolment code made, a tablet enrolled, a tablet revoked, a phone paired, a phone unpaired
  (from the console, by its own person, by pairing the same browser again, or because its person was deactivated,
  moved to the kitchen or moved to another branch).

Every device entry is written by `StaffAuthService` (or the staff screen's save, for a person's change) in the same
unit of work as the change, beside the platform audit the phone work keeps (`staff.phone-unpaired`) - not a second
save.

## 3. Action codes

Stable: clients render and filter on them. Add codes; never reword one. `summary` keys per code; every booking entry's
summary also carries `localDate`, `localTime`, `partySize`, `status` and `tableId`, with the rest under `extra`.

| Code | Actor | Subject | Summary / notes |
| --- | --- | --- | --- |
| `booking.created` | guest | reservation | `extra.channel`, `extra.trigger` |
| `booking.moved` | guest | reservation | `extra.viaManageLink`; `before`/`after` the slot |
| `booking.cancelled` | guest | reservation | `extra.late`, `extra.viaManageLink`; `reason` |
| `booking.approved` | staff | reservation | `extra.trigger`, `extra.decisionSeconds` |
| `booking.declined` | staff | reservation | `extra.trigger`, `extra.decisionSeconds`, `extra.keptEarlierSlot`; `reason` |
| `booking.auto-declined` | system | reservation | `extra.declineReason` (2 NoAnswer, 4 EarlierGuestNoAnswer), `extra.deadlineUtc` |
| `booking.seated` | staff / guest | reservation | `tableId`, `partySize`, `source` `booking`; on the bill's timeline |
| `booking.no-show`, `booking.released` | staff | reservation | `extra.countsTowardBlock`; `reason` |
| `booking.no-show-undone` | staff | reservation | `reason` |
| `booking.hold-extended` | guest | reservation | `extra.minutes`, `extra.holdExpiresAtUtc` |
| `booking.guest-answered` | guest (staff for not-coming) | reservation | `extra.answer` `coming`, `late` (+`minutes`), `not-coming`, `table-request` (+`accepted`) |
| `table.seated` | staff / guest | table-session | `tableId`, `partySize`, `source` `walk-in`, `scan`, `held` |
| `table.freed` | staff | table-session | `tabId`, `reservationId`, `outstandingAmd` |
| `table.held`, `table.hold-released`, `table.out-of-service`, `table.back-in-service` | staff | table | `reason` |
| `table.tab-opened`, `table.tab-admitted`, `table.tab-turned-away`, `table.host-reassigned`, `table.bill-presented` | staff | tab | |
| `table.transferred` | staff | tab | `fromStaffMemberId`, `fromName`, `toStaffMemberId`, `toName`; `before`/`after` |
| `table.call-claimed` | staff | service-request | `tabId`, `preset`, `calledAtUtc`, `responseSeconds` |
| `order.sent` | staff | tab | `orderId`, `items`, `amountAmd`, `lines` `[{name, quantity}]`, `serverStaffMemberId` |
| `order.placed-by-guest` | guest | tab | as `order.sent` |
| `order.pre-order-sent` | staff | tab or reservation | `reservationId`, `items`, `amountAmd`, `seated`, `lines` |
| `order.in-kitchen`, `order.ready`, `order.served` | staff | tab | `orderId`, `from`, `to`, `items`, `lines` |
| `order.voided` (m), `order.line-voided` (m) | staff | tab | `lineId`/`orderId`, `name`, `quantity`, `totalBeforeAmd`, `totalAfterAmd`; `reason` |
| `payment.cash` (m) | staff | tab | `paymentId`, `amountAmd`, `tipAmd`, `remainingAmd`, `totalAmd`, `closed`, `serverStaffMemberId` |
| `payment.discount` (m), `payment.comp` (m) | manager | tab | `adjustmentId`, `percent` or `amountAmd`, `lineName`, `totalBeforeAmd`, `totalAfterAmd`; `reason` |
| `payment.adjustment-reversed` (m) | manager | tab | `adjustmentId`, `kind`, `totalBeforeAmd`, `totalAfterAmd` |
| `payment.unpaid` (m) | manager | tab | `writtenOffAmd`, `totalAmd`, `paidAmd`; `reason` |
| `takeaway.placed`, `takeaway.cancelled` | guest | takeaway | `code`, `items`, `totalAmd`, `status`, `pickupLocal` |
| `takeaway.accepted`, `takeaway.declined`, `takeaway.ready`, `takeaway.no-show` | staff (system when automatic) | takeaway | as above, `readyAtUtc` |
| `takeaway.auto-declined` | system | takeaway | as above |
| `takeaway.collected` (m) | staff | takeaway | `paymentMethod` (1 cash, 2 card), `paidAmd` |
| `message.sent` | staff | reservation or takeaway | `conversationId`, `messageId`, `text`, `replySeconds` (null when it answers nothing) |
| `message.asked-on-my-way` | staff | reservation | `notified`, `startUtc` |
| `settings.menu-price` | manager | menu-item | `before`/`after` `{priceAmd}` |
| `settings.menu-availability` | manager | menu-item | `before`/`after` `{isAvailable}` or `{availableForTakeaway}` |
| `settings.hours`, `settings.special-days`, `settings.policy`, `settings.takeaway`, `settings.messages`, `settings.pre-orders` | manager | branch | `before`/`after` the whole setting |
| `staff.created`, `staff.updated`, `staff.pin-set`, `staff.sign-in-issued`, `staff.pin-lockout-cleared` | manager | staff | `name`, `role`, `branchId`; `before`/`after` for an update |
| `device.enrolment-code-created` | manager | branch | `expiresAtUtc` |
| `device.enrolled` | system (nobody is signed in to a tablet being enrolled) | device | `name`, `codeCreatedByStaffMemberId` |
| `device.revoked` | manager | device (a tablet) | `name` |
| `device.phone-paired` | the phone's person, on that phone (`ProvedBy`) | device | `name` (what the browser called itself), `staffMemberId`, `staffName`, `repaired` (the same phone paired again), `codeCreatedByStaffMemberId` |
| `device.phone-unpaired` | manager, the phone's person, or whoever changed the person | device | `name`, `staffMemberId`, `staffName`, `cause`: `console`, `its-owner`, `replaced`, `deactivated`, `kitchen`, `other-branch` |

(m) = `IsMoney`, kept three years.

## 4. Retention

`ActivityRetention`, on the outbox loop's wake-up pass and then about daily (beside `ConversationRetention`): entries
older than **365 days** go, except money entries, which go after **1,095 days**. Oldest first, 1,000 a statement, so a
first sweep never locks the table while the tablets write. The History reads back as far as money is kept; an
ordinary entry older than a year is simply gone.

## 5. The table's waiter, and transfer

`TableSession.ServerStaffMemberId` (with `ServerAssignedAtUtc`) - on the sitting, not the tab: a party is seated and
served before anybody opens a bill, and the sitting has no row version for ten phones' orders to contend on.

- **Who serves.** Whoever seats the party (walk-in, booking, held). On a table a guest's scan or booking seated:
  whoever opens its tab, lets it in (admits it), or - nobody having done either - **the first staff member to order on
  it**.
- **`POST /api/tabs/{tabId}/transfer { toStaffMemberId, clientCommandId? }`** (`transferTable`), answering
  `{ tabId, serverStaffMemberId, serverName, previousServerStaffMemberId, previousServerName, transferredAtUtc,
  tableSessionId, tableLabel, wasReplay }`. The table's waiter, or a manager or above
  at the branch. The sitting's row is held (`UPDLOCK`) for the transfer and read inside the hold, so two at once go one
  after the other: the second is re-checked against the waiter the first left, and logged from them. The target: active, at the branch (or venue-wide), a waiter or above - never the kitchen (422
  `transfer-target-ineligible`). A closed bill or a party gone: 409 `tab-not-transferable`. Already theirs: done,
  `wasReplay`. Logged as `table.transferred`, signalled on the branch stream (`TableTransferred`, 7).
- **`GET /api/tabs/{tabId}/transfer-targets`** (`listTransferTargets`) is who the sheet offers, for a waiter's phone
  whose own roster lists only its person: `{ items: [{ staffMemberId, name, role, onShiftNow, openTables }] }`. The
  POST's target rule exactly - active, at the tab's branch or venue-wide, a waiter, manager, general manager or owner -
  less the table's waiter now; on shift first (a shift at this branch running now, `ShiftInstants`), then by name.
  `openTables` counts the live sittings at this branch whose waiter they are. No PIN, phone or email. Readable by
  whoever may transfer the table - its waiter, or a manager or above at the branch, stored roles; anybody else 403,
  the kitchen and another branch included, and a tab that does not exist is the same 403 (the branch scope resolves
  no branch for it). Nothing is logged: it is a read.
- **What came before stays.** Each order records the waiter it was placed under (`TabOrder.ServerStaffMemberId`), each
  payment the waiter whose table it was (`Payment.ServerStaffMemberId`, for the tip) and who took it
  (`Payment.RecordedByStaffId`, for the cash). A transfer moves none of them.
- Order lines carry their adder through their order: `TabOrder.PlacedByStaffId` (or `PlacedByParticipantId` for a
  phone), and `VoidedByStaffId` on the line. No column was added to the line.
- The floor (`getBranchFloor`) shows `serverStaffMemberId` and `serverName` per table.

## 6. Calls and their claim

**`POST /api/branches/{branchId}/calls/{callId}/claim`** (`claimServiceRequest`, no body) is "I'm on it": it records
`ServiceRequest.ClaimedByStaffId` and `ClaimedAtUtc`, the first claim wins and a repeat answers as it stands. A claim
and an acknowledgement hold the request's row (`UPDLOCK`) and read it inside the hold, so taps at the same instant still
leave one claim and one `table.call-claimed`. The guest
is not told, and the call **stays on the open list** (`getOpenServiceRequests`), showing `claimedByStaffMemberId` and
`claimedByName`, until somebody acknowledges it (`acknowledgeServiceRequest`, unchanged). Acknowledging a call nobody
claimed claims it in the same moment, so every answered call has a claim. The claim is logged as `table.call-claimed`
with `responseSeconds`, and its time from the call is the response time Staff → Results reports. The migration
backfills the claim of every call already acknowledged from its acknowledgement.

**A claimed call buzzes nobody.** The `tableCalls` staff push is decided at send time (`docs/notifications.md`), and
a call claimed by then is not pushed: the push asks for somebody to go, and somebody is going. It is sent once per call
and never repeated, so there is nothing further to silence; the call itself stays open on the list until it is
acknowledged.

## 7. Reading it

| Route | operationId | Who |
| --- | --- | --- |
| `GET /api/branches/{branchId}/activity` | `listBranchActivity` | manager+ at the branch |
| `GET /api/branches/{branchId}/activity.csv` | `exportBranchActivity` | manager+ at the branch |
| `GET /api/branches/{branchId}/activity/subjects/{subjectType}/{subjectId}` | `getActivityTimeline` | manager+ at the branch |
| `GET /api/branches/{branchId}/staff-performance` | `getStaffPerformance` | manager+ at the branch |
| `GET /api/branches/{branchId}/staff-performance.csv` | `exportStaffPerformance` | manager+ at the branch |
| `GET /api/branches/{branchId}/staff/{staffMemberId}/performance` | `getStaffMemberPerformance` | manager+ at the branch |
| `GET /api/staff/me/performance` | `getMyPerformance` | the caller, waiter or above |

"At the branch" is the stored row, checked by `StaffBranchGuard`: a branch manager their own branch, the owner and the
general manager every branch of their venue, a platform admin any. A waiter, the kitchen, or a manager of another
branch: **403**. Every query value is a string and every bad one is named in one **422 `validation-failed`**.

**The feed** is keyset-paged like the message history: newest first then by id, `nextCursor` until the last page.
`date` or `from`/`to` (default today), at most 366 days, not before 1,095 days ago nor after 366 days ahead (the same
bounds on the staff results; a day sent as `date` is refused as `date`). Filters: `staffMemberId`,
`actorKind` (`staff`, `guest`, `system`, `platform`), `category`, `q` (table exact, guest contains, a code, a dish
contains). The **CSV** is the same window, every row, at
most 20,000 (more: 422 `activity-export-too-large`), with the reports' CSV rules and a formula guard on text.

**The timeline** (`tab`, `reservation`, `takeawayOrder` - or `takeaway` - or any other subject's entries): a bill's every entry of its
sitting, oldest first, its header with `servers` (the waiters in turn, each `fromUtc` when the table became theirs: the
seat for whoever seated it; their opening, letting in or first order on a table a guest's scan or booking seated; the
hand-over for each after - and none before a hand-over from nobody), and its lines with who added and voided each. A
line's amount is the bill's: zero once voided, on its own or with its whole order, and nothing else.

## 8. Staff results

Read live off the operational tables, as the reports are - a fixed number of statements whatever the window.

- **Bills** are the revenue report's: closed in the window on the branch's own dates (`BranchTime.RangeToUtc`),
  `Status` Closed, admitted; their `TotalAmd`.
- **Sales.** Each bill's total is split over the waiters who held its lines, in proportion to the lines' worth (lines
  not voided - a voided order's lines are voided with it, `docs/billing.md` "Voiding a whole order"), **largest remainder** so the shares add up to the bill exactly. Lines placed while nobody
  served the table are the closing waiter's; with nobody then either, the sale is `unattributedSalesAmd`.
- **Tables and guests** count for each waiter with a share of the bill, and the waiter at the close. The totals row
  counts each bill once.
- **Average check** = sales ÷ tables, rounded as the report rounds its average tab.
- **Tips** - a payment's tip goes to the waiter whose table it was when paid (else the closing waiter).
- **Cash** - whoever took it: tab cash on the window's bills, and the takeaway counter's cash collected in the window.
- **Discounts, comps** - who gave them, still standing, created in the window, and what they took off by the bill's
  own arithmetic (`TabBilling.Reductions`: a line's own off that line, then the tab-wide ones off what is left, each in
  turn - never a percentage of the subtotal stored after them). The revenue report adds them up the same way. **Voids** - lines voided in the window, by who voided them;
  voiding a whole order counts each line it took off the bill.
- **Calls** - claims in the window; seconds from the call to the claim. **Messages** - venue messages in the window
  that answered a waiting guest text; seconds from the guest's first unanswered text to the reply.
- **Approvals, declines** - from the History (the booking does not record who decided it), so they reach back a year.
- **Medians** are of whole seconds: the middle value of an odd count; of an even count, the mean of the middle two,
  rounded half away from zero. A totals median is over every claim or reply, never a median of medians.

**The totals match the revenue report for the same dates**: `salesAmd` = `hallAmd`, `tables` = `tabs`,
`averageCheckAmd` = `averageTabAmd`, `cashAmd` = `cashAmd`, `takeawayAmd` = `takeawayAmd`, `adjustmentsAmd` =
`adjustmentsTotalAmd`. Takeaway is nobody's sale - it is counted in the totals and in the takeaway cash, not in a
waiter's sales.

A person's page adds a **daily series** (a week ending on `to` at least) and **recent lists** - `orders`, `messages`,
`bookings`, `cash`, `calls` - which are History entries, keyset-paged, with `seconds` where the action measures it.

## 9. Reports and the old "no leaderboard" note

`docs/reports.md` §5 explains why the reports never grew a per-waiter ranking. This is not that: it is a page a
manager opens, and a waiter's own numbers, which nobody else at their level can see. The staff report stays
aggregated.
