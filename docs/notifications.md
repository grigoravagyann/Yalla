# Notifications — the outbox, what is sent, and what nothing sends

Yalla sends a diner six things and no more. Every one of them is written to a database table in the
same transaction as the fact that caused it, and a background loop takes them from there.

This document is the contract that table makes, because a notification system fails silently by
construction: when it stops working the symptom is nothing happening, and nothing happening is
exactly what a quiet Tuesday looks like.

---

## 1. Why an outbox and not a push at the call site

The obvious implementation sends the push where the thing happens: book the table, then call Expo.
It has two failure modes and both are bad.

**The booking commits and the push does not.** Expo is down, or the process dies between the two.
The diner has a table and no reminder, and nobody finds out until they do not turn up.

**The push goes and the booking does not.** The insert fails on the unique index a moment later,
after the notification is already on somebody's lock screen. Now a person has been told about a
booking that does not exist, and the only correction available is another notification.

So the message is a **row**, written by `IOutbox.Enqueue` into the caller's own unit of work:

```csharp
outbox.Enqueue(OutboxMessageTypes.ReservationReminder, notice, remindAt, key);
await db.SaveChangesAsync(cancellationToken);   // booking and message, one transaction
```

`IOutbox` has **no `SaveChangesAsync` of its own**, deliberately. The moment it grows one, a caller
can enqueue and save independently of the change it describes, and the guarantee is gone without
anything looking wrong. The interface enforces the pattern rather than documenting it.

---

## 2. The message types

| Type | Written when | Due at | Sent to |
|---|---|---|---|
| `reservation.reminder` | A booking is created | `StartUtc − ReminderHoursBefore` (default 3h) | The diner |
| `reservation.late-nudge` | A booking is created | `StartUtc + LateNudgeAfterMinutes` (default 10m) | The diner |
| `reservation.on-your-way` | A booking is made (again when it moves; saving or clearing a pre-order moves it) | `StartUtc − 30m`, or `− 45m` while a pre-order waits | The diner - a confirmed booking whose pre-order, if any, is still waiting; withdrawn once the guest answers, the waiter sends it or marks them not coming; see [reservations.md](reservations.md#3h-pre-orders) |
| `reservation.decided` | Staff (a waiter or above) approve or decline a pending booking, the guest before it on the table says yes or no, or the approval sweep declines one nobody answered | Immediately | The diner |
| `reservation.changed-by-link` | Somebody holding a booking's manage link changes it (not a retry, and never the diner's own change in the app) | Immediately | The booking's account |
| `reservation.cancelled-by-link` | Somebody holding a booking's manage link cancels it (not an already-cancelled one, never the app's cancel) | Immediately | The booking's account |
| `diner.bookings-blocked` | A no-show is marked that makes three in three days for the guest (see [reservations.md](reservations.md#no-show-block)) | Immediately | The blocked account |
| `reservation.no-show` | A venue marks one of the guest's bookings a no-show (every time) | Immediately | The booking's account |
| `diner.bookings-unblocked` | The guest's no-show block is lifted early - a no-show withdrawn, or a platform admin | Immediately | The blocked account |
| `reservation.table-request` | A booking asks the booking before it on the same table to free the table in time, and that guest is asked in the app | Immediately | The earlier guest |
| `reservation.table-request-decided` | The earlier guest's yes - theirs, or the floor's for them - takes effect | Immediately | The earlier guest |
| `tab.participant-approved` | The host lets a pending joiner onto the tab | Immediately | The joiner |
| `tab.join-requested` | Somebody joins a tab and lands pending | Immediately | The host |
| `tab.order-ready` | An order moves to `Ready` | Immediately | Whoever ordered it |
| `takeaway.accepted` | Staff accept a takeaway order (or the sweep accepts one for a branch now on auto-accept) | Immediately | The order's guest - "ready at 19:10, pay at pickup" |
| `takeaway.ready` | A takeaway order is marked ready (kitchen or floor) | Immediately | The order's guest - "come and pick it up" |
| `takeaway.declined` | Staff decline a takeaway order, or the sweep declines one nobody answered within five minutes | Immediately | The order's guest - the reason, or the apology |
| `message.to-guest` | Somebody at the venue writes in a booking's or a takeaway order's thread, or asks "are you coming?" (`ask-on-my-way`) | Immediately | The thread's guest - the venue's name over the text, or the question; see [messages.md](messages.md) |
| `staff.push` | A booking waits for approval, a guest writes, a table calls, a takeaway order arrives, an order is up | Immediately | The branch's staff devices that want it - see [6a](#6a-staff-push---web-push-to-the-staff-app-2026-10-24) |

Both reservation messages are written when the booking is made, hours before either is due. A
reminder whose moment has **already passed** is not written at all: a booking made an hour before it
starts has no three-hours-before, and queueing one in the past only feeds the staleness rule with a
log line implying something went wrong. Nor is one that would fall while a short-notice booking is
still inside its fifteen-minute answer window - "your table is at 20:00" about a table nobody has
promised is the wrong thing to say, and the approval push says the right thing minutes later. When a
reminder fires, the handler sends it for a confirmed booking and for a pending one with no deadline (a
large party next week the venue has not got to), never for one still waiting on a short-notice answer.

### `reservation.decided`, and the apology

One message for every way a waiting booking is decided. Its data carries `kind: "reservation-decided"`,
`reservationId`, `approved`, **`expired`**, **`wasChange`** and **`originalKept`** (all strings,
`"true"`/`"false"`):

| `approved` | `expired` | `wasChange` | `originalKept` | What happened | Words (`NotificationText`) |
|---|---|---|---|---|---|
| `true` | `false` | `false` | `false` | Staff said yes | `ReservationApproved` - "Booking confirmed - {venue} is expecting you at 20:00." |
| `false` | `false` | `false` | `false` | Staff said no | `ReservationRejected` - "{venue} could not take this booking. Try another time." |
| `false` | `true` | `false` | `false` | Nobody at the venue answered in time (`declinedBecause` `"2"`): a short-notice booking within 15 minutes, or one the floor was to decide after calling the guest before it within two hours | `ReservationExpired` - "Sorry, {venue} could not confirm in time. Try another time or place." |
| `false` | either | `true` | `true` | A change of a confirmed booking was declined (or not answered); the booking is back at the slot it had | `ReservationChangeDeclined` - "Change not confirmed - {venue} could not take the change. Your booking at 19:00 stands." (the expired form apologises) |
| `false` | either | `true` | `false` | The same, but that slot had been taken or was over, so the booking ended | `ReservationChangeDeclinedOriginalGone` - "...and your earlier time has been taken since. Try another time or place." |
| `false` | `false` | `false` | `false` | `declinedBecause` `"3"`: the guest with the table before said they would not have left in time | `ReservationEarlierGuestDeclined` - "That table is not free then - {venue}: the guests before you will not have left by 19:45. Try another time or table." |
| `false` | `true` | `false` | `false` | `declinedBecause` `"4"`: that guest did not answer within the two hours | `ReservationEarlierGuestNoAnswer` - "Booking not confirmed in time - {venue}: we could not confirm the table for 19:45 in time. Try another time or table." |

The data also carries **`declinedBecause`**, the `DeclineReason` number as a string - `"1"` staff said
no, `"2"` nobody at the venue answered, `"3"` the guest before said no, `"4"` they did not answer - and an
empty string for an approval or a decline written before the key existed.

A declined change leaves a confirmed booking standing, so it can be declined again later; a second
`rejected` or `expired` key for the same booking carries its moment after it, like a second approval.

The expired one is an apology, not the venue's "no": the venue did not turn the diner down, it did not
look in time. It is written by `ApprovalExpirySweep` - a loop of its own (`ApprovalExpiryHostedService`,
every `ApprovalExpiry:SweepIntervalSeconds`, default 20), not a scheduled outbox message, because the
staleness rule below would dead-letter a deadline the machine slept through and leave the diner waiting
for good. See [reservations.md](reservations.md#3d-short-notice-tonight-is-the-floors-to-decide). A
message written before `expired`, `wasChange` or `originalKept` existed reads as `false`. **No apology is
written for a request the sweep only reaches after the booking would have started** - it is declined
quietly; an evening that has gone is not one to send the diner to.

`tab.join-requested` goes to the host's account, which a host has only when their phone was
carrying a diner token when they scanned - most hosts have none, and then the tab's own stream
(`ParticipantJoined`, [tabs.md](tabs.md#telling-the-host-somebody-is-waiting)) is all there is. It is
not written for a joiner who is let on without waiting - the first phone onto a tab staff opened, or
the booker on their own booking's sitting - nor for the host's own account joining from a second
phone, nor again for somebody the host already turned away on that tab who scans again. Its body
names the joiner by the name they typed, or "a guest" in the host's language when they typed none,
never the roster's English `Guest 3`. A tap carries `kind` = `tabJoinRequested`, `tabId` and
`participantId`, and opens the tab; the other two tab messages carry `kind` and `tabId`.

`tab.order-ready` is **off by default**, per branch, on `Branch.NotifyOnOrderReady`. It is noise in a
cafe where a waiter carries the plate ten feet and genuinely useful in a canteen where the diner
collects it. Defaulting it on would train a city to switch our notifications off, and the two that
matter would go with them.

### `reservation.table-request`: "will you have left by 19:40?"

To the guest with a table first, when a later booking asks for it - see
[reservations.md](reservations.md#3e-a-tables-evening-when-one-booking-may-follow-another). Only when
they booked from the app (not the web), the account is live and it has a live push device
(`LiveDevices`), and nothing else about the later booking needs a human; otherwise the floor asks them
by telephone from the awaiting list and nothing is pushed. Title and body
(`NotificationText.TableRequested`): "Someone would like your table at 19:45" / "{venue}: will you have
left by 19:40?". The later guest stays anonymous.

`categoryId` **`table-request`** - the app registers Yes and No actions on it, which call
`POST /api/reservations/{reservationId}/table-requests/{requestId}/answer`. Data: `kind` =
`table-request`, `reservationId` (the earlier guest's own booking), `requestId`, `requestedTime` and
`freeByTime` (`HH:mm`, branch wall clock), `freeByUtc` (ISO 8601). Keyed
`reservation:{earlierId}:table-request:{requestId}`, so cancelling the earlier booking cancels an unsent
one; the handler also sends it only while the request is still open - answered on the card, withdrawn,
or past its deadline, it is skipped.

`reservation.table-request-decided` follows a yes: "Thank you - {venue}: please free the table by 19:40 -
the next guests are coming after you." (`TableRequestAccepted`). Data: `kind` =
`table-request-decided`, `reservationId`, `requestId`, `accepted` `"true"`, `freeByTime`, `freeByUtc`. No
category. Sent while the booking still holds its table. A no has no push to the earlier guest - they gave
it - and neither does the clock running out: their booking is unchanged.

### Booking paused for no-shows: `diner.bookings-blocked`

Sent once when a guest is blocked for three no-shows in three days: "Booking paused for a week" - how many
upcoming bookings were cancelled and the day they can book again (Armenian, Russian or English, by the account's
device; the day is the triggering venue's calendar date). Data `kind` `bookings-blocked-no-shows` and `blockedUntilUtc`. Keyed `booking-block:{blockId}:notice`,
written in the same transaction as the block, with one feed entry `bookings-blocked-no-shows` (params
`blockedUntilUtc`, `blockedUntilLocalDate`, `cancelledCount`). The cancelled bookings' own unsent reminders are dropped; no per-booking push
is sent for them.

### Marked as a no-show: `reservation.no-show`; booking open again: `diner.bookings-unblocked`

`reservation.no-show` goes every time a venue marks one of the guest's bookings a no-show - "Marked as a no-show at
{venue}, {date} at {time}. If this is wrong, contact the venue." - keyed `reservation:{id}:no-show:{instant}`, with
the feed entry `booking-no-show` (the booking's params). `diner.bookings-unblocked` ("You can book again") goes when
a block is lifted early, keyed `booking-block:{blockId}:lifted`, with the feed entry `bookings-unblocked`
(`liftedBecause` 1 platform admin, 2 a no-show withdrawn). Neither restores a cancelled booking.

### Done from the manage link: `reservation.changed-by-link`, `reservation.cancelled-by-link`

The manage link is a bearer capability: it goes round group chats and into the calendar file, so
whoever changes or cancels a booking from it may not be the diner - and a booking moved without their
knowing becomes a no-show on their account. So the booking's account is told, in the same save as the
change: a push ("Your booking was changed" - where it stands now, waiting for the venue when it is, and
that it was done with the booking link; "Your booking was cancelled") in Armenian, Russian or English by
the account's device, data `kind` `reservation-changed-by-link` / `reservation-cancelled-by-link` and
`reservationId`, and a feed entry beside it. The app's own change and cancel are the account holder's
and write neither.

Keyed under the booking - `reservation:{id}:changed-by-link:{instant}`, `reservation:{id}:cancelled-by-link`
- so the next change or cancel, which cancels the booking's unsent messages, replaces one not yet sent:
the diner hears where the booking stands, once. A retry of the same change writes nothing. A booking
whose account was deleted has nobody to tell.

### The feed beside the pushes

A push is gone once it is read or swiped away, so the diner app also lists what it was told
(K12, [diner-browse.md](diner-browse.md#k12-notifications-feed---implemented-b6)). Where
`reservation.reminder`, `reservation.decided`, `reservation.changed-by-link`,
`reservation.cancelled-by-link` and `tab.order-ready` are enqueued, a `DinerNotifications`
row is added to the same unit of work - whether or not the diner has a device, because the feed is what
a diner without push reads. It carries a `kind` and string `params`, never rendered text: the app writes
the words in the diner's language. A reminder's row carries its due time and does not show before then,
and cancelling a booking deletes it with the unsent message. A `booking-declined` entry carries
`expired` in its `params` - `"true"` when nobody answered in time, `"false"` when staff said no; one
written before the key existed has none and was a staff decline. It also carries `wasChange` and
`originalKept` as the push does, and a declined change adds `requestedDate` and `requestedTime` - the
slot it asked for; `date` and `time` are the booking as it now stands, which for a kept original is
that slot. A decline because of the guest before carries `declinedBecause` `"3"` or `"4"`. The earlier
guest's feed gets **`table-request`** when they are asked in the app (`requestId`, `requestedTime`,
`freeByTime` beside the booking's own `date` and `time`) and **`table-request-decided`** when their
answer takes effect (`accepted` `"true"`/`"false"`, the same times). **`booking-on-your-way`** sits beside
`reservation.on-your-way` and, like a reminder, appears only when it is due; saving or clearing a pre-order moves
it, and cancelling the booking, the guest answering "on my way", or the waiter sending the pre-order or marking them not coming deletes
it while unshown. It shows only while its booking is confirmed (or went on from there) - a booking still waiting for
the venue's yes does not see it until approved. Two entries have no push beside them - a
booking the venue lets go, and a review moderation takes down - and are written with that change. The
late nudge, the participant approval and the join request write none. Rows older than 90 days are
deleted by this loop's host, `OutboxHostedService`, about hourly.

### Takeaway: `takeaway.accepted`, `takeaway.ready`, `takeaway.declined`

To a takeaway order's guest ([takeaway.md](takeaway.md)), each written with the step that causes it, under the account's
lock, with a feed entry beside it: **`takeaway-accepted`** and **`takeaway-ready`** (params `venueName`, `branchName`,
`code`, `readyTime` `HH:mm` and `readyAtUtc`) and **`takeaway-declined`** (`code`, `noAnswer` `"true"`/`"false"`, `reason`
when staff gave one). Each row carries the order in `orderId` - where a tap lands. Push data: `kind` =
`takeaway-accepted` / `takeaway-ready` / `takeaway-declined`, `orderId`, `code`. The handler sends only while the order
is still where the message says (an "accepted" push for an order collected since says nothing true). An order a
branch **accepts automatically** writes its feed entry and **no push**: the answer to the guest's own tap says it.
Words in Armenian, Russian or English by the account's device (`NotificationText.Takeaway*`); a decline for nobody
answering is an apology, not the venue's "no".

### Guest messages: `message.to-guest`

One per **venue** message in a booking's or a takeaway order's thread ([messages.md](messages.md)) - a typed one, or the
venue's "Ask: are you coming?" - written in the same transaction as the message, under the account's lock, keyed
`conversation:{conversationId}:message:{messageId}`. Guest messages push nothing: the tablet polls.

- **Title** the venue's name; **body** the text (at most 140 characters, an ellipsis for the cut) or, for the ask, the
  question with the booking's time in the guest's language ("Գալի՞ս եք 19:30-ին։", "Вы придёте к 19:30?", "Are you
  coming at 19:30?"). Never the staff member's name.
- **Data** `kind` `venue-message`, `conversationId`, `reservationId` or `takeawayOrderId`, `ask` `"true"`/`"false"`.
- **`categoryId`** by the kind and the branch's `guestsCanWrite` at send time (`VenueMessageCategories`), so no push
  offers a Reply the venue would refuse: guests may write - `venue-ask` for the ask ("I'm coming", "Late 15 min" and a
  text reply), `venue-message` for a text (a text reply); `guestsCanWrite` off - `venue-ask-buttons` (the two answers,
  no reply) and `venue-message-readonly` (no actions). The app registers all four. `NotificationMessage.CategoryId`,
  already sent to Expo as `categoryId`.
- **Feed** entry `venue-message` beside it: params `venueName`, `branchName`, `conversationId`, `subjectKind`
  (`reservation`/`takeaway`), `ask`, `text` (140 at most; absent for the ask), and the booking's `date`/`time` or the
  order's `code`; `reservationId` or `orderId` set.
- **Sent while still true**: the thread still belongs to the account it was written for (an account deletion cuts it),
  and an ask only while the booking is confirmed and the guest has not answered.

### The venue's "Ask: are you coming?"

Since 2026-10-23 a message in the booking's thread, pushed as `message.to-guest` with `categoryId` `venue-ask` (or
`venue-ask-buttons` while guests cannot type) and a `venue-message` feed entry - above. (Before, it was the automatic question's `reservation.on-your-way` with
`staffAsked: true` and a `booking-on-your-way` entry with `askedByVenue: "true"`; the handler still sends such a message
already queued.) The automatic question is not asked on top of one the venue already asked. See
[reservations.md](reservations.md#3h-pre-orders).

### These are not tab events

`OutboxMessages` is its own table with its own string type names. **None of them is a
`TabEventType`**, and the dispatcher appends nothing to any tab's event stream — see
`TabEventContractTests`. A notification is something that leaves the building; a tab event is a fact
about the bill. Putting delivery on the stream would hand every client an event type it has to know
to ignore, and would move a tab's sequence for something the tab did not do.

### The payload is self-contained

The message carries the venue name, the branch name, the table label and the time **as they were
when it was written** — not ids to be resolved at send time. Hours pass in between and the world
moves: the venue gets renamed, the table gets relabelled. What the message should say is what was
true when the booking was made.

The one thing read at send time is the **state that decides whether to send at all**: the handler
re-reads the booking's status and stays quiet if it is no longer `Confirmed`. The join request does
the same, in one read: it is sent only while the joiner is still pending, the tab is still `Open` and
its host is still the account the message was written for. A host who answered from the People
screen first, a tab staff marked closing, or a host role staff moved is not asked about anybody, and
the message is marked done rather than retried.

---

## 3. The idempotency key, and cancelling a cause

Every message carries a key derived from what caused it:

```
reservation:{reservationId}:reminder
reservation:{reservationId}:late-nudge
reservation:{reservationId}:reminder:{startUtc:yyyyMMddHHmm}      (a moved booking's)
reservation:{reservationId}:late-nudge:{startUtc:yyyyMMddHHmm}    (a moved booking's)
reservation:{reservationId}:on-your-way:{startUtc:yyyyMMddHHmm}   (asked 45 minutes ahead, while a pre-order waits)
reservation:{reservationId}:on-your-way:{startUtc:yyyyMMddHHmm}:30 (asked 30 minutes ahead, every other booking)
reservation:{reservationId}:approved      (or :rejected)
reservation:{reservationId}:approved:{yyyyMMddHHmmssfff}          (a second approval)
reservation:{reservationId}:expired       (declined by the sweep: nobody answered in time)
reservation:{reservationId}:rejected:{yyyyMMddHHmmssfff}          (a second decline of a kept booking; :expired:... likewise)
participant:{participantId}:approved
participant:{participantId}:join-requested
order:{orderId}:ready
reservation:{reservationId}:on-your-way:asked:{yyyyMMddHHmmss}    (the venue's "Ask: are you coming?")
takeaway:{orderId}:accepted   (or :ready, :declined)
staff-push:{approval|message|call|takeaway|ready}:{id}[:{fresh id}]   (staff push, section 6a)
```

It is **unique across the table**, enforced by an index rather than by a check in a service — two
concurrent callers can both pass a check, and neither can pass the index.

The key's second job is cancellation. Cancelling a booking, releasing it, or marking it a no-show
calls `IOutbox.CancelAsync` with the prefix `reservation:{id}:`, which deletes everything still
unsent for that booking, in the same transaction as the cancellation itself.

> A push asking "still coming?" that arrives an hour after the diner cancelled is worse than
> silence. It is the notification they remember, and it teaches them ours are wrong.

Sent messages are left alone. They are history, and history does not get cancelled.

**A moved booking** (`POST /api/reservations/{id}/reschedule`) cancels its unsent messages the same
way and writes a reminder and a late nudge for the new start, in the same transaction as the move.
Their keys carry the start they are for, because a sent message keeps its row and its key: moving
tomorrow's booking to next week must not collide with the reminder that went out an hour ago. A move
can also send a booking back for approval, so a booking can be approved twice; the second approval's
key carries when it was made. See [reservations.md](reservations.md#3c-changing-a-booking).

---

## 4. The dispatcher

`OutboxDispatcher.RunOnceAsync` is one pass. `OutboxHostedService` is a loop around it and nothing
else — which is what makes the dispatcher a method a test can call rather than a thing a test has to
wait for.

**Order within a pass:**

1. **Discard the stale.** Anything overdue by more than `StaleAfterMinutes` (default 60) is
   dead-lettered with a reason and a log line, never sent.
2. **Claim one message**, in its own transaction:
   ```sql
   SELECT TOP (1) * FROM [OutboxMessages] WITH (UPDLOCK, READPAST)
   WHERE [SentAtUtc] IS NULL AND [DeadLetteredAtUtc] IS NULL
     AND [ScheduledForUtc] <= @now
     AND ([LockedUntilUtc] IS NULL OR [LockedUntilUtc] <= @now)
   ORDER BY [ScheduledForUtc]
   ```
   `UPDLOCK` holds the row for the claim. `READPAST` is what makes a second dispatcher **skip** it
   rather than block on it, so two of them divide a batch instead of taking turns. The lease is
   committed before anything is sent, so a process that dies mid-send leaves a row that unlocks
   itself when the lease expires.
3. **Hand it to its handler.** Returning means done; throwing means retry later.
4. Repeat up to `BatchSize`.

**Failure** records the error and reschedules with exponential backoff — `BaseBackoffSeconds`
doubling to `MaxBackoffSeconds`. After `MaxAttempts` the message is **dead-lettered**: it stops being
retried, for ever, and now needs a person.

A message whose type no handler claims is dead-lettered immediately rather than retried, with an
error naming the missing handler. It will never succeed, and retrying it for a day only buries the
failures that might.

### The laptop-was-asleep rule

This is the one that is easy to leave out and expensive to leave out.

Yalla runs on a machine that sleeps. Due messages pile up while it is shut, and on wake every one of
them is due at once — including a reminder for a dinner that already happened, and a "still coming?"
for a party that came, ate and left. `StaleAfterMinutes` is the line: anything older is discarded
with the reason recorded.

The hosted service runs a pass **before** its first tick precisely so this triage is the first thing
that happens on wake, not something that happens ten seconds after the backlog has already fired.

### Configuration

| Setting | Default | What it is |
|---|---|---|
| `Outbox:PollSeconds` | 10 | How often the loop looks |
| `Outbox:BatchSize` | 20 | Messages per pass |
| `Outbox:LeaseSeconds` | 60 | How long a claim is held |
| `Outbox:MaxAttempts` | 5 | Attempts before dead-lettering |
| `Outbox:BaseBackoffSeconds` | 30 | First retry delay, doubling |
| `Outbox:MaxBackoffSeconds` | 3600 | Backoff ceiling |
| `Outbox:StaleAfterMinutes` | 60 | How overdue is too overdue |
| `Outbox:Enabled` | true | Off means messages are written and nothing sends them |

---

## 5. When it breaks, and how you find out

`GET /api/platform/outbox` — platform admin only. It exists because the alternative symptom is
silence, and silence is indistinguishable from a quiet evening.

```json
{
  "deadLettered": 1,
  "letter": {
    "id": "01a073eb-dcd4-7d03-aa38-1af77cae9ea9",
    "type": "reservation.reminder",
    "idempotencyKey": "reservation:01a073eb-dbcb-7117-94e4-f834d9aab6ef:reminder",
    "scheduledForUtc": "2026-09-13T14:01:00Z",
    "attemptCount": 3,
    "deadLetteredAtUtc": "2026-09-13T15:00:00Z",
    "lastError": "Response status code does not indicate success: 503 (Service Unavailable)."
  }
}
```

Read it in this order:

- **`sentLastDay` of zero on a live venue** means the channel is broken, whatever the other numbers
  say. Nothing else on the page is as informative.
- **`deadLettered`** is the number that needs a person. Those will never be retried.
  **`deadLetteredLastDay`** is how many of them were given up on in the last 24 hours - what the
  console's Home alert counts; Home's `GET /api/platform/overview` carries the same numbers.
- **`idempotencyKey`** names the cause, so a dead reminder traces straight back to its booking.
- **`lastError`** is why.

There is no retry button, deliberately. A dead letter is a decision to stop, and the right response
to a batch of them is to fix the cause and decide case by case whether the moment they were for has
passed. Most of the time it has.

---

## 6. Channels

`INotificationChannel` — the same shape as Prompt 3's `IVerificationCodeSender`, selected by
configuration.

**`Notifications:Channel = "Log"`** (the default) resolves the diner's real devices and logs exactly
what each would have received, in the language it would have received it. It is not a stub that
throws its argument away: the whole scheduler, locale selection included, runs end to end on a
machine with no phone attached. It **reports delivery**, so messages through it are marked sent and
not retried — a development channel that failed would fill the dead-letter list with noise and hide
the real failures.

**`Notifications:Channel = "Expo"`** posts to Expo's push service, which is reachable from localhost
with no deployment. A phone running the diner app through Expo Go receives these from a laptop.

### Receipts

A transport failure — anything but a 2xx — throws, and the outbox backs off and retries. That is a
provider having a bad minute.

A per-ticket `DeviceNotRegistered` is different: the app was uninstalled, and no number of retries
will ever succeed. The token is **revoked** on the spot. A queue that retries dead tokens for ever is
the default behaviour of every naive push implementation, and it is why push queues fill with
garbage that hides the real failures.

Re-registering a revoked token brings it back, because that is what a reinstall looks like.

---

## 6a. Staff push - Web Push to the staff app (2026-10-24)

Everything above is a diner's. The staff app - `/staff`, on a personal phone ([auth.md](auth.md#personal-phones-2026-10-24))
or a floor tablet - is pushed too, by **Web Push** rather than Expo: it is a PWA in a browser, and the browser's push
service is the only thing that wakes a phone in a pocket.

### The five kinds

One outbox type, **`staff.push`**, written by the save that causes it - `StaffPushSignals`, beside the change-stream
signals in `YallaDbContext.SaveChanges`, for their reason: each of these happens on several paths, and a rule every
path had to remember is a rule one of them forgets. The payload is only `{ kind, subjectId, occurredAtUtc }`; who is
told, in which words and whether it is still true are decided at send time, seconds later.

| Switch (`kind`) | Written when | Key | Still true at send time when | Deep link (`url`) | Collapse (`tag`) |
|---|---|---|---|---|---|
| `approvals` | A booking is added as `PendingApproval`, moved into it, moved to another time while waiting, or waits for another reason (the guest before it stepped aside: the floor decides now) | `staff-push:approval:{reservationId}:{fresh id}` | The floor's awaiting list would show it: waiting, not started, inside its answer window, not waiting on the earlier guest's own answer in the app | `/staff/approve/{reservationId}` | `approval:{reservationId}` |
| `guestMessages` | A guest's text, or answer to "are you coming?" (coming / late / not coming), lands in a booking's or a takeaway order's thread | `staff-push:message:{messageId}` | Nobody at the venue has opened the thread since | `/staff/messages/{conversationId}` | `message:{conversationId}` |
| `tableCalls` | A service request is raised - a waiter, water, napkins, the bill: what the floor shows as calling | `staff-push:call:{serviceRequestId}` | Nobody said "I'm on it" - neither claimed nor acknowledged - and no newer request replaced it. A claimed call stays open on the floor's list until acknowledged, but is not pushed | `/staff/now?call={serviceRequestId}` | `call:{tableId}` |
| `takeaway` | A takeaway order is placed and waits to be accepted (one a branch accepts automatically needs nobody) | `staff-push:takeaway:{orderId}` | Still `Sent` | `/staff/takeaway/{orderId}` | `takeaway:{orderId}` |
| `kitchenReady` | A tab order moves to `Ready` | `staff-push:ready:{orderId}:{fresh id}` | Still `Ready` - not served, not voided | `/staff/floor?table={tableId}` | `ready:{orderId}` |

There is no "on the way to pick up" for takeaway: the guest has no such answer today, so nothing writes it.

### Who is told

The live devices of the event's branch with a push subscription whose switches include the kind:

- **A tablet** is the branch's - pushed whenever it wants the kind. It has no person and no shift.
- **A phone** only while its person still opens it there - active, of the venue, at the branch or venue-wide, not
  kitchen - and, when its **"only during my shift"** is on, only while they are on a `StaffShift` at the branch now
  (decided on instants, as the PIN roster decides it). The switch defaults on for a waiter and off for a manager,
  general manager or owner.
- **An order up** goes to the table's own waiter - who seated the party, else who placed the order - on their
  phones, and to the branch's tablets that want it. With no such person, every phone **on shift** that wants it.

### What is sent

The service worker receives exactly this JSON (RFC 8291 `aes128gcm`, one record, at most 3993 bytes of plaintext so
the body stays within the 4096 a push service must accept; VAPID per RFC 8292, a JWT for the endpoint's origin good
for twelve hours):

```json
{ "title": "Table 2 · asking for the bill", "body": "The guests are ready to pay",
  "url": "/staff/now?call=0193…", "tag": "call:0193…", "kind": "tableCalls" }
```

The words (`StaffPushText`) are in the device's language - `StaffDevice.Locale`, which the app sets with its
notification switches when the person picks a language - else Armenian. Never a guest's phone number, never the
colleague who caused it; a guest's message is quoted to 140 characters. Headers: `TTL` (`WebPush:TtlSeconds`, 900),
`Urgency: high`, and `Topic` - the tag hashed - so a newer push about the same thing replaces one the phone has not
fetched.

### Delivery

- **All of a message's devices at once**, not one after another, so a few unreachable phones at the client's
  ten-second timeout cannot outlast the outbox lease and let a second dispatcher send the message again. A device
  whose push fails before it is sent (a key that will not encrypt) counts as failed; it never aborts the others.
- **2xx**: delivered. **404 or 410**: the browser unsubscribed or the app was removed - the subscription row is
  **deleted** on the spot, and the device subscribes again if it wants pushes.
- **Anything else** - 429, 5xx, a refused VAPID token, no answer - is logged (the push service's host and the status,
  never the endpoint's path, the keys or the words). The message is **retried only when nothing at all was delivered**,
  so a retry never buzzes a phone twice; otherwise the outbox's backoff and dead-lettering apply as to every message.
- **Older than `WebPush:TtlSeconds`** when its turn comes (a sleeping laptop), it is dropped: a call from twenty minutes
  ago is not news.

### Configuration

| Setting | Default | What it is |
|---|---|---|
| `WebPush:Subject` | - | The VAPID contact, `mailto:` or `https:`. Required with keys |
| `WebPush:PublicKey` | - | The VAPID public key, base64url (65 bytes uncompressed P-256) |
| `WebPush:PrivateKey` | - | **A secret**: user secrets locally, `WebPush__PrivateKey` elsewhere, never committed |
| `WebPush:TtlSeconds` | 900 | How long a push is worth delivering |
| `WebPush:AllowedEndpointHosts` | Google's, Mozilla's, Apple's, Microsoft's push services | Host suffixes a subscription's endpoint may name - the server POSTs there, so it is not any URL a client sends. The endpoint must also be `https` on the default port with no user info, and the push client follows no redirects |

The three VAPID values come together or not at all, and the keys must be a matching pair: the host refuses to start
otherwise. **None of them** and pushes are written to the log (kind, tag and device - not the words) instead of sent,
like the `Log` channel; `GET /api/staff/push-config` then says `enabled: false`. **In Development** with none, a key
pair is generated at startup for that run; the staff app compares the public key it is given with the one its
subscription was made with and subscribes again when they differ. Generate a deployment's pair once
(`npx web-push generate-vapid-keys`) and keep it - a new pair makes every phone subscribe again.

### The device's side

| Route | What it does |
|---|---|
| `GET /api/staff/push-config` | `{ enabled, publicKey }` - device token or PIN session |
| `PUT /api/staff/device/push-subscription` | `{ endpoint, keys: { p256dh, auth } }` - one per device; one browser's subscription belongs to one device |
| `DELETE /api/staff/device/push-subscription` | Forget it |
| `GET /api/staff/device/notification-preferences` | `{ approvals, guestMessages, tableCalls, takeaway, kitchenReady, onlyDuringShift, locale, subscribed, isDefault }` |
| `PUT /api/staff/device/notification-preferences` | The six switches, and optionally `locale` |

All but the first need the person's PIN session on the device and act on that device only.

---

## 7. Language belongs to the person, not the venue

`DinerDevice.Locale`, chosen from the diner's most recently seen live device, normalised to `hy`,
`ru` or `en`. Never the branch's.

A Russian-speaking regular at an Armenian venue is written to in Russian, and a venue serving three
languages does not have to pick one on their diners' behalf. Anything unrecognised falls back to
Armenian.

Every word lives in `NotificationText` — nine messages of a title and a body each, three languages,
in one file, because the translator is a person reading the file and not a tool. They are short on
purpose: a push is read on a lock screen at a glance and the second sentence is never read at all.

The **reminder is the point of the whole feature**, and its body says what it is for:

> Cannot make it? Cancel here — it takes a second and frees the table for someone else.

The cancel action travels in the payload, so it works from the notification without opening the app
and hunting for a screen. **Cancelling has to be easier than not showing up.** If it is not, this
whole module is decoration.

---

## 8. Nothing here releases a table

This is the most important sentence in the document.

The scheduler sends a diner a "still coming?" at `StartUtc + LateNudgeAfterMinutes`. It does not,
then or ever, free the table. There is no timer that expires a hold, no job that sweeps late
bookings, no automatic no-show.

**A waiter releases a table, and only a waiter.** `POST /api/reservations/{id}/no-show` and
`POST /api/reservations/{id}/release` are things a person taps.

The approval sweep is not an exception. It declines a short-notice *request* nobody answered within
fifteen minutes - a booking that was never promised, so there is no hold to let go and nothing on the
floor changes. A booking anybody said yes to is never touched by it.

The reason is that the system cannot see the dining room. The party may be standing at the door. They
may have been seated by a colleague who has not tapped anything yet. They may have telephoned. A
process that frees a table on a timer will, on some evening, give away a table with four people
walking towards it — and the venue will not trust the floor plan again, which costs more than every
notification in this document is worth.

What the diner *can* do is push the hold out by `GraceExtensionMinutes`, **once**, by tapping the
nudge:

- `POST /api/reservations/{id}/extend-hold`, the reservation's own diner, idempotent by
  `clientCommandId`.
- Once, enforced by `GraceExtensionsUsed`. "Just five more minutes" granted repeatedly is how a table
  stays held all evening for somebody who is not coming, and the venue loses the cover without ever
  making a decision.
- **Only once the booking has started.** Nothing is held before then, so there is nothing to keep.
  The app offered the button on every confirmed booking, and a tap days early spent the one
  extension and pinged the floor about a table nobody was holding. Refused with `hold-not-active`,
  and nothing is spent.
- Each refusal has its own code - `hold-not-active`, `hold-already-extended`,
  `extensions-not-offered` - so the app never tells a diner at a branch with no extensions that
  they have already let the venue know.
- It flows through the branch change sequence as a `Held → Held` transition: nothing about the table
  changed, something happened at it, and every tablet in the room finds out.

The extension moves a deadline. It does not decide anything. The deciding stays with the person who
can see the door.

---

## 9. Time

There is exactly **one** clock in the process. `SystemClock` reads `TimeProvider`, `IClock` sits in
front of it, and ninety-six call sites take it by injection. Nothing anywhere calls `DateTime.UtcNow`.

The dispatcher's `PeriodicTimer` is built from the **same** `TimeProvider`. That matters more than it
looks: two independent clocks is how a scheduler test passes while the thing it schedules never
fires — the test advances one, the timer reads the other, and nothing ticks. Advancing the fake moves
both, so a three-hour reminder is tested in milliseconds and no test in the suite sleeps.

---

## 10. What changes when this leaves the laptop

Written down now, while the reasons are still fresh.

**Photos.** `IPhotoStorage` is the seam. `LocalDiskPhotoStorage` writes
`{branchId}/{contentHash}/{variant}.webp` under a configured root; an S3 or R2 implementation writes
the same keys to a bucket and the rest of the system does not change. The paths are already
storage-agnostic — forward slashes, no drive letters, content-addressed — so the migration is a
copy of the tree and a configuration change. `GET /api/photos/{id}/{variant}` streams through the
API today and would become a redirect to a CDN URL; that is the one endpoint that changes shape.

**Photo orientation.** Decoding drops EXIF, so the Orientation tag a phone writes ("these landscape
pixels are a portrait, turn them") is applied first: every variant is encoded from the upright
bitmap, and the stored width and height are the upright ones. That matters most for room photos,
whose table pins are fractions of the stored image. Photos stored before this change are **not**
migrated - an upload of a tagged phone photo from then is on disk sideways, with width and height
swapped. Re-uploading the original fixes it (the new bytes hash differently, so it is a new photo,
and any pins on a room photo need placing again).

**More than one process.** The lease already handles it. `UPDLOCK, READPAST` means two dispatchers
divide a batch rather than duplicating it, and `LeaseSeconds` bounds how long a dead process strands
a message. Nothing needs to become a singleton, and no distributed lock is required.

**The machine stops sleeping.** `StaleAfterMinutes` can go up, or its default reconsidered. On a
server that is always awake, an hour-overdue reminder usually means something was genuinely wrong
rather than that a lid was closed.

**Push at volume.** Expo's send endpoint takes batches, and this sends one message per device list.
That is right for one venue and wrong for a hundred; the batching belongs in the channel, and the
dispatcher does not change when it arrives.

**Secrets.** `Notifications:ExpoAccessToken` follows `Jwt:SigningKey` — user secrets locally,
environment variables in a deployment, never `appsettings.json`.

---

## Related

- `docs/reservations.md` — holds, grace, and the lock protocol behind them
- `docs/billing.md` — the arithmetic, and `docs/billing-vectors.json` beside it
- `docs/auth.md` — who a diner is, and why a tab participant is not one
