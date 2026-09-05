# Notifications — the outbox, what is sent, and what nothing sends

Yalla sends a diner five things and no more. Every one of them is written to a database table in the
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

## 2. The five message types

| Type | Written when | Due at | Sent to |
|---|---|---|---|
| `reservation.reminder` | A booking is created | `StartUtc − ReminderHoursBefore` (default 3h) | The diner |
| `reservation.late-nudge` | A booking is created | `StartUtc + LateNudgeAfterMinutes` (default 10m) | The diner |
| `reservation.decided` | A manager approves or rejects a pending booking | Immediately | The diner |
| `tab.participant-approved` | The host lets a pending joiner onto the tab | Immediately | The joiner |
| `tab.order-ready` | An order moves to `Ready` | Immediately | Whoever ordered it |

Both reservation messages are written when the booking is made, hours before either is due. A
reminder whose moment has **already passed** is not written at all: a booking made an hour before it
starts has no three-hours-before, and queueing one in the past only feeds the staleness rule with a
log line implying something went wrong.

`tab.order-ready` is **off by default**, per branch, on `Branch.NotifyOnOrderReady`. It is noise in a
cafe where a waiter carries the plate ten feet and genuinely useful in a canteen where the diner
collects it. Defaulting it on would train a city to switch our notifications off, and the two that
matter would go with them.

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
re-reads the booking's status and stays quiet if it is no longer `Confirmed`.

---

## 3. The idempotency key, and cancelling a cause

Every message carries a key derived from what caused it:

```
reservation:{reservationId}:reminder
reservation:{reservationId}:late-nudge
reservation:{reservationId}:approved      (or :rejected)
participant:{participantId}:approved
order:{orderId}:ready
```

It is **unique across the table**, enforced by an index rather than by a check in a service — two
concurrent callers can both pass a check, and neither can pass the index.

The key's second job is cancellation. Cancelling a booking, releasing it, or marking it a no-show
calls `IOutbox.CancelAsync` with the prefix `reservation:{id}:`, which deletes everything still
unsent for that booking, in the same transaction as the cancellation itself.

> A push asking "still coming?" that arrives an hour after the diner cancelled is worse than
> silence. It is the notification they remember, and it teaches them ours are wrong.

Sent messages are left alone. They are history, and history does not get cancelled.

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

## 7. Language belongs to the person, not the venue

`DinerDevice.Locale`, chosen from the diner's most recently seen live device, normalised to `hy`,
`ru` or `en`. Never the branch's.

A Russian-speaking regular at an Armenian venue is written to in Russian, and a venue serving three
languages does not have to pick one on their diners' behalf. Anything unrecognised falls back to
Armenian.

Every word lives in `NotificationText` — eleven strings, three languages, in one file, because the
translator is a person reading the file and not a tool. They are short on purpose: a push is read on
a lock screen at a glance and the second sentence is never read at all.

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
