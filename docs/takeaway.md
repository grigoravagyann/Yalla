# Takeaway — order from the menu, pick up later

A guest orders from a place's menu in the app, says when they will come, and picks the food up at the counter. They
pay there - cash or card at the venue's own terminal; **nothing is charged in the app**. The floor tablet accepts the
order and hands it over, the kitchen cooks it from its own screen, the console sets it up. Added 2026-10-22 (the "SHURJ
Takeaway" design).

Domain: `Domain/Takeaway` (`TakeawayOrder`, `TakeawayRules`). Service: `Infrastructure/Services/TakeawayService.cs`,
the sweep `TakeawayExpirySweep.cs`, the notices `TakeawayNotices.cs`. Endpoints: `Api/Endpoints/TakeawayEndpoints.cs`.

---

## 1. The venue's settings

On the branch, not in the reservation policy - the policy is replaced whole by every save of the reservation form,
and a switch living there would be reset by an edit that never showed it (the pre-order switch's reason).

| Setting | Default | Bounds | What it does |
|---|---|---|---|
| `enabled` | off | | The place page shows "Order to take away"; orders are taken. Off: a venue that never looked at the feature receives no orders. |
| `autoAccept` | off | | On: an order is accepted the moment it is sent, ready when the guest asked. Off: staff accept on the tablet, and an order nobody accepts within **5 minutes** is declined and the guest told. |
| `prepMinutes` | 20 | 5 to 90, in fives | The earliest an "as soon as possible" order is ready. |
| `lastOrderMinutes` | 30 | 0 to 120, in fifteens | Ordering closes this long before closing time: the last pickup is closing minus this. |
| `maxInProgress` | 5 | 1 to 30 | How many orders may be **sent or accepted** at once. Full: ASAP is refused as busy, and slots that would exceed it are not offered. |

`GET`/`PUT /api/branches/{branchId}/takeaway-settings` (manager or above, branch-scoped). All five are required on a
save - a missing bool would silently switch takeaway off - and every field out of bounds or off its step is named in
one 422.

**Dishes for the hall only.** `MenuItems.AvailableForTakeaway` (default true), switched per dish with
`availableForTakeaway` on `PATCH /api/branches/{branchId}/menu/items/{itemId}`, and read on every menu item view. The
diner's takeaway menu greys such a dish out ("Only in the hall · not for takeaway"); an order naming one is refused.

## 2. When orders are taken, and the pickup times

Only **inside an opening block** - the one open now, from the opening calendar every reader shares (weekly hours with
special days laid over them; a block past midnight belongs to the day it opened). In that block:

- the **earliest pickup** is now + `prepMinutes` ("as soon as possible");
- the **last pickup** is the block's close − `lastOrderMinutes`;
- orders are taken while the earliest is not after the last;
- **slots** are the quarter hours from the earliest (rounded up) to the last, both included, as local `HH:mm`.

Closed - before opening, between blocks, or past the last order - nothing is offered, and the availability says when
it **opens** next (the first opening within a week from which an order could be taken): "Takeaway from opening,
10:00".

**The kitchen's capacity.** An order is in progress while it is Sent or Accepted. With `maxInProgress` of them, ASAP is
refused (`409 takeaway-busy`) and `busy` is true. A slot is offered while fewer than `maxInProgress` orders in progress
would be cooked in a window overlapping its own - each order is cooked in the `prepMinutes` before its own pickup (its
promised ready time once accepted, the requested time before) - so a full 19:15 also hides 19:30 with twenty minutes'
prep, and 19:45 is offered again. It is a model, not a timetable: a venue that finds it too tight raises the number.

`GET /api/diner/branches/{branchId}/takeaway` answers `{ branchId, enabled, open, prepMinutes, asapReadyAtUtc,
asapReadyAt, slots, lastPickupAt, busy, opensAt, opensAtUtc }`. The place page reads `takeaway: { enabled, prepMinutes }`
from `GET /api/public/branches/{branchId}`.

## 3. The order

`POST /api/diner/takeaway-orders` with `{ branchId, pickup: { asap: true } | { at: "HH:mm" }, note?, lines: [{ menuItemId,
quantity }], clientCommandId }`.

- **A proved phone**, as for booking: an account whose number was never proved is `403 phone-not-verified`. The venue
  may call the number on the order.
- **Idempotent on `clientCommandId`**: a retry answers the order the first try made (`wasReplay: true`); somebody else's
  id is `409 client-command-id-in-use`. Under the account's lock, then the branch row's lock - so two guests at once
  cannot both take the kitchen's last place or the same code.
- Refusals, before anything is saved: the shape (422 naming `lines`, `lines[i].quantity`, `note`, `pickup`,
  `pickup.at`; 1 to 30 lines, 1 to 20 of a dish, at most 60 dishes, a note of at most 500); `takeaway-not-accepted`;
  `takeaway-closed`; `takeaway-busy`; `takeaway-slot-unavailable` (with the slots on offer now); and
  `422 takeaway-items-unavailable` naming every dish that cannot go on - not on this branch's menu as a diner sees it
  (1), sold out (2), hall only (3).
- Names and prices are **snapshotted** on the lines; a menu change tomorrow does not change the order.
- **The code** is `T-` and three digits, random, unique per branch per service day (a unique index backs the branch
  lock). It is what the guest says at the counter; nothing is authorised by quoting it.

## 4. Statuses

```
Sent ──accept──▶ Accepted ──ready──▶ Ready ──collect──▶ Collected
 │                                     │
 ├─decline / nobody in 5 min─▶ Declined └─30 min after ready─▶ NoShow
 └─guest cancels──▶ Cancelled
```

| Move | Who | Rule |
|---|---|---|
| accept `{ readyAt }` | waiter or above at the branch | Only while Sent. `readyAt` (UTC) from now (a minute of clock skew) to 12 h on; the tablet starts from the requested time and steps by 5 min. The order goes to the kitchen; the guest is told the time. |
| decline `{ reason? }` | waiter or above | Only while Sent. The reason (≤ 300) is shown to the guest. |
| (sweep) | - | Sent, and 5 minutes past `acceptDeadlineUtc`, at a branch that does not auto-accept: declined, `declinedBecause` 2 (nobody answered), the guest told - an apology. At a branch switched to auto-accept since: accepted instead. |
| cancel | the guest | Only while Sent - after that the venue is cooking, and it is a phone call. |
| ready | **the Kitchen role**, or the floor | Only while Accepted. The guest is told "ready, come and pick it up". |
| collect `{ method }` | waiter or above | Only while Ready. `method` 1 Cash or 2 Card; the amount recorded is the total. Not a fiscal receipt: the venue's own register issues that. |
| no-show | waiter or above | Only while Ready, and from **30 minutes after ready** - the later of the promised time and the kitchen's "ready", since the guest was told when it was. Counts in the guest record and toward the no-show block (§6a). Cannot be withdrawn. |

Each move refuses a state it is not for with `409 takeaway-status-conflict` (`context.status`: where the order is now -
redraw it). The move already made is answered 200 `wasReplay: true` - two tablets, one answer; a save that loses a race
to another tablet reads what won and answers the same way. Every step is stamped, so the guest's status screen shows a
time per step without an event stream. The app shows Accepted as "Accepted · being prepared".

## 5. The floor, the kitchen and the guest

**The counter.** `GET /api/branches/{branchId}/takeaway-orders` (waiter or above at the branch): `active` (Sent, Accepted,
Ready, oldest first, each Sent one with `acceptDeadlineUtc` - the answer countdown), `finishedToday` (Collected, NoShow,
Declined placed on the branch's service day, newest first), `sentCount` and `readyCount` (the tab's badge). Each order
carries the guest's name and proved number (empty for a platform admin), the lines, the note, the requested and
promised times, `noShowFromUtc` and the payment once collected. **The tablet polls**, as the rest of the floor is polled:
takeaway writes nothing to the change stream.

**The kitchen.** `GET /api/branches/{branchId}/orders` - the kitchen queue - carries the accepted and ready takeaway
orders beside the tabs' orders and the held pre-orders: the code as `tableLabel`, no `tabId`, status InKitchen or
Ready, `estimatedReadyAtUtc` the promised time, and a `takeaway` band `{ code, readyAtUtc, readyAtLocalTime, guestName,
note }`. The kitchen's "Ready" is `POST /api/takeaway-orders/{id}/ready` (`moveOrderStatus` refuses a takeaway ticket, so
the guest is always told). A collected order leaves the screen.

**The guest.** `GET /api/diner/takeaway-orders/{id}` is the status screen: code, status and a time per step, lines and
total, the branch's name, address, number and position (call, directions), the decline's reason or cause. Poll while
active, in the foreground. **The Orders tab** (`GET /api/diner/orders`) lists takeaway orders beside dine-in ones with
`kind: takeaway` and a `takeaway` summary (code, exact status, pickup and ready times, item count); the ones still moving
come first.

**Notifications** (see [notifications.md](notifications.md)): `takeaway.accepted` (with the ready time),
`takeaway.ready`, `takeaway.declined` (with the reason, or "no answer in time"), each a push and a feed entry
(`takeaway-accepted`, `takeaway-ready`, `takeaway-declined`, `orderId` set). An auto-accepted order writes the feed entry
and no push.

## 5a. Messages with the venue

Each order has a thread with the venue, as a booking does - the guest asks ("can I come ten minutes early?"), the counter
answers, the guest is pushed (`message.to-guest`). Open from Sent until two hours after the order is collected, declined,
cancelled or not collected. The Orders tab's takeaway items carry `messages` (unread, the last message, open). See
[messages.md](messages.md).

## 6a. The guest record, the no-show block and the reports

Added 2026-10-23 (Yalla#61's follow-up).

- **Guest record.** `guestRecord.takeawayNoShows`: the account's takeaway orders marked not collected whose promised
  ready time is in the last 365 days - its own count, beside the bookings and in none of them (`isNewGuest` stays about
  bookings). Wherever the record is shown. A guest with one is not a **good record** (`GuestRecordRules.IsGood`), so a
  venue confirming good records at once looks at their booking. See [reservations.md](reservations.md#guest-record).
- **No-show block.** A takeaway no-show counts toward the block exactly as a missed booking does: one venue-day per
  venue per service day (the order's `LocalDate`; a missed booking and an uncollected order at one venue on one day are
  one), over the promised ready time - when the guest failed to come - inside the 72 hours, after the last block ended
  or was lifted. Marked under the guest's account lock and judged in the same transaction (`NoShowBlocks`, shared with
  the booking's no-show); the third blocks the guest and cancels their bookings still to come, as a booking's would.
- **A blocked guest orders nothing.** `POST /api/diner/takeaway-orders` answers **`403 booking-blocked-no-shows`**,
  `context.blockedUntilUtc` - the booking's own refusal, under the account's lock, before anything about the branch is
  asked. Account or number, as for a booking. Lifting the block (the platform's list) lifts this too.
- **Reports.** Collected orders are revenue, counted when collected, split from the hall - see
  [reports.md](reports.md#takeaway-in-the-revenue-report).

## 6. What it does not do

- **No payment in the app**, and no deposit. A no-show costs the venue the food; the venue's protection is the proved
  number, the five-minute answer, and the guest record and no-show block below.
- **No withdrawing a takeaway no-show.** A booking's no-show can be withdrawn until 05:00 after its service day; a
  takeaway one cannot - it is marked only thirty minutes after the guest was told it was ready, and the order is final.
  A mistake is put right by a platform admin lifting the block it made.
- **No delivery.** The guest comes to the counter.
- **One day.** Slots are today's (the open block's); there is no ordering for tomorrow.
- **No editing** an order once sent: cancel while Sent, and order again.
- **No change-stream signal**: the counter polls.
- The account's deletion keeps the order (the venue's record of what it cooked and was paid) with the account link cut.
