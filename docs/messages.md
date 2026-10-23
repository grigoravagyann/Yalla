# Messages with guests — one thread per booking or takeaway order

A venue and a guest write short text messages to each other inside **one booking** or **one takeaway order**. There is
no general chat and no thread without a subject. The venue's "Ask: are you coming?" is a message in the thread, and the
guest's answer buttons write theirs. Added 2026-10-23 (the "SHURJ Նամակներ" design).

Domain: `Domain/Messages` (`Conversation`, `ConversationMessage`, `ConversationRules`). Service:
`Infrastructure/Services/GuestMessageService.cs`, with `GuestThreads.cs` for what the booking and pre-order services
append and the summaries other reads carry. Endpoints: `Api/Endpoints/MessageEndpoints.cs`. Push:
`VenueMessageHandler` (`message.to-guest`).

---

## 1. The thread

- **`Conversation`** - `BranchId`, exactly one of `ReservationId` / `TakeawayOrderId` (a check constraint; each unique
  by a filtered index), `DinerUserId` (the subject's account; null for a web booking without one and after the account
  is deleted), `LastMessageUtc`, `GuestLastReadUtc`, `VenueLastReadUtc`, `CreatedAtUtc`.
- **Made lazily** by the first message, the venue's ask or the guest's answer - under the subject's row lock
  (`UPDLOCK, HOLDLOCK` on the booking or order, after the account's lock when the write takes that too), so two first
  messages at once make one thread. The unique index is the backstop. A subject nobody wrote about has none, and its
  reads answer `conversationId` absent and `messages: []`.
- **`ConversationMessage`** - `Author` (1 Guest, 2 Venue, 3 System), `Kind` (1 Text, 2 AskComing, 3 Answer, 4 Status),
  `Body` (Text only - a check constraint), `AnswerKind` (1 Coming, 2 Late, 3 NotComing) and `LateMinutes` on an answer
  and its status line, `HeldUntilLocal` on a late answer's status line, `StaffMemberId` and `StaffName` (a snapshot) on a
  venue message, `ClientCommandId` (unique per thread and author), `CreatedAtUtc`, and `Sequence` (database-assigned).
- **Structured, not rendered.** Only a text message has words. The ask, the answer and the status line carry their
  facts and every client writes the sentence in its reader's language.
- **Order** is `CreatedAtUtc`, then `Sequence`. One save writes an answer and its status line, and a database-assigned
  number is not promised to follow the order rows were added in - so the status line is stamped one tick after the
  answer.

### Who sees what

The guest sees the **venue's name** on the venue's side, never who wrote; staff see `staffName` on each venue message.
The guest's phone number is never a message: the staff view carries it beside the thread as `guestPhone` for the
Call button (empty for a platform admin, as on the takeaway counter). Text is plain: 1 to 500 characters
after trimming, never linkified, no photos.

## 2. When a thread is open

| Subject | Open | Closes |
|---|---|---|
| Booking | Pending, confirmed or seated, until two hours after `EndUtc` | Two hours after the end - or after it was cancelled, declined or marked a no-show (`CancelledAtUtc`), or completed (its table session closed) |
| Takeaway order | Sent, Accepted, Ready | Two hours after it was collected, declined, cancelled or marked not collected |

Writing to a closed thread is **409 `conversation-closed`** (`context.subjectKind`, `subjectId`, `closedAtUtc`). The
guest still reads a closed thread **for 30 days** after it closed; after that the diner's routes answer 404. Staff read
it any time.

## 3. Who writes

| Who | Writes? |
|---|---|
| The guest (the subject's own account) | Yes, while the venue's `guestsCanWrite` is on; off: **403 `guests-cannot-write`** - the answer buttons still work |
| Manager, general manager, owner (at the branch) | Always |
| Waiter (at the branch) | While `waitersCanWrite` is on; off: **403 `waiters-cannot-write`** |
| Kitchen | Never (the route's `WaiterOrAbove`) |
| Platform admin | Reads only: writing is 403 `forbidden`, and its "read" marks nothing |

- **Pace.** A guest writes at most **5 messages a minute and 60 in a thread**: **429 `messages-too-fast`**
  (`context.conversationId`, `perMinute`, `perConversation`, and `retryAfterUtc` with a `Retry-After` header while
  waiting helps; absent once the thread's own limit is spent). Counted under the subject's lock, so it is a real limit.
- **Idempotent on `clientCommandId`.** A retry answers the message the first try wrote, `wasReplay: true`; nobody is
  told twice. Checked under the lock, before the open, permission and pace checks, so a retry after the thread closed
  still answers what was written. The same id with other words - or from another member of staff - is a client bug,
  not a retry: **409 `client-command-id-in-use`**, rather than the first message answered and the new one dropped.
- **Read marks.** Writing marks the writer's side read. Any member of staff reading marks it for the venue.

## 4. The ask and the answers

- **"Ask: are you coming?"** (`POST /api/reservations/{id}/ask-on-my-way`, unchanged refusals) appends an `ask-coming`
  venue message with the asker's name, in the same transaction as the ask's stamp, and tells the guest (§6). It no longer
  sends the automatic question's push on top.
- **The guest's answers** - `on-my-way`, `running-late`, by the app or the manage link (`/api/public/bookings/{token}/…`)
  - append an `answer` guest message and a `status` system line, making the thread if needed: coming (`status.answer`
  `coming`), late (`answer.lateMinutes`, `status.heldUntilLocal` = the start plus the minutes on the branch's clock).
- **"Not coming"** is the booking's own cancel (app or link). It appends `answer` `not-coming` and a `status` with
  `cancelled: true` when the booking has a thread already or the answer window has opened (two hours before the start) -
  a booking cancelled next week is a cancellation, not an answer to anybody.
- **`pendingAsk`** (the guest's answer bar): there is an ask with no answer after it, and the answer is still taken - a
  confirmed booking from two hours before its start until its end.
- **The automatic question** (30 or 45 minutes before) stays a push only and writes no message.
- The staff pre-order action "not coming / no answer" (`pre-order/not-coming`) is the waiter's mark, not a guest's answer,
  and writes nothing to the thread.

## 5. The routes

### The diner (verified diner; the subject's own account; others' are 404)

| Route | operationId | Answers |
|---|---|---|
| `GET /api/diner/reservations/{id}/conversation` | `getReservationConversation` | `DinerConversationView` |
| `GET /api/diner/takeaway-orders/{id}/conversation` | `getTakeawayConversation` | `DinerConversationView` |
| `POST /api/diner/reservations/{id}/conversation/messages` | `sendReservationMessage` | `DinerMessageResult` |
| `POST /api/diner/takeaway-orders/{id}/conversation/messages` | `sendTakeawayMessage` | `DinerMessageResult` |
| `POST /api/diner/reservations/{id}/conversation/read` | `markReservationConversationRead` | 204 |
| `POST /api/diner/takeaway-orders/{id}/conversation/read` | `markTakeawayConversationRead` | 204 |
| `GET /api/diner/messages/unread` | `getUnreadMessages` | `DinerUnreadMessages` |

```
DinerConversationView { conversationId?, subject, open, closesAtUtc?, guestsCanWrite, venuePhone?, pendingAsk,
                        venueLastReadUtc?, unreadCount, messages[] }
subject      { kind: "reservation"|"takeaway", id, venueName, branchId,
               startUtc?, localDate?, localTime?, partySize?, tableLabel?,      // a booking
               takeawayCode?, readyAtLocal?, readyAtUtc? }                       // an order
message      { id, author: "guest"|"venue"|"system", kind: "text"|"ask-coming"|"answer"|"status", text?,
               answer?: { kind: "coming"|"late"|"not-coming", lateMinutes? },
               status?: { answer, heldUntilLocal?, cancelled }, createdAtUtc }
DinerMessageResult { conversationId, id, author, kind, text, createdAtUtc, wasReplay }
DinerUnreadMessages { total, items: [{ conversationId, subjectKind, subjectId, venueName, unread, lastText?, lastKind,
                                       lastAuthor, lastAtUtc, open }] }
```

Send body: `{ text, clientCommandId }`. `unreadCount` / `unread`: venue messages after `GuestLastReadUtc`. The unread list
holds the threads that are open or have something unread (and are still readable), newest first.

**Summaries.** The diner's booking list and detail (`getMyReservations`, each booking) and the Orders tab's takeaway
items (`getDinerOrders`) carry `messages: { unread, lastAuthor?, lastKind?, lastText?, lastAtUtc?, open }` - absent when
the subject has no thread and takes none, or its thread closed over 30 days ago. `lastText` is a text's first 140
characters.

### The floor tablet (waiter or above at the subject's branch)

| Route | operationId | Answers |
|---|---|---|
| `GET /api/branches/{branchId}/conversations` | `listBranchConversations` | `BranchConversations` |
| `GET /api/reservations/{id}/conversation` | `getStaffReservationConversation` | `StaffConversationView` |
| `GET /api/takeaway-orders/{id}/conversation` | `getStaffTakeawayConversation` | `StaffConversationView` |
| `POST /api/reservations/{id}/conversation/messages` | `sendStaffReservationMessage` | `StaffMessageResult` |
| `POST /api/takeaway-orders/{id}/conversation/messages` | `sendStaffTakeawayMessage` | `StaffMessageResult` |
| `POST /api/reservations/{id}/conversation/read` | `markStaffReservationConversationRead` | 204 |
| `POST /api/takeaway-orders/{id}/conversation/read` | `markStaffTakeawayConversationRead` | 204 |

```
BranchConversations { items[], unreadTotal, remindAfterMinutes, templates?, waitersCanWrite, asOfUtc }
item     { conversationId, subject: { kind, id, label, guestName, localTime, localDate?, partySize?, itemCount?, totalAmd? },
           lastMessage (the last non-system message, with answer? and staffName?), latestStatus?, unreadFromGuest,
           awaitingGuestAnswer,
           oldestUnansweredGuestMessageUtc?, open }
StaffConversationView = the diner's view + guestName, guestPhone (empty for a platform admin), canWrite,
                        guestLastReadUtc; messages carry staffName? on venue ones
StaffMessageResult { conversationId, id, author, kind, text, createdAtUtc, staffName, wasReplay }
```

- **The inbox** holds threads that are open or had a message in the last 24 hours (`GuestThreads.RelevantAt`). A thread
  with a guest message the venue has not answered since comes first, the longest waiting first; the rest newest first.
  `label` is the table for a booking and the `T-` code for an order; `localTime` the booking's start or the order's ready
  time. `awaitingGuestAnswer`: the last message is the venue's ask.
- **Unread** for the venue: guest messages (text and answers) after `VenueLastReadUtc`. **Unanswered**: a guest message
  with no venue message after it, in a thread still open - the remind rings when the oldest has waited
  `remindAfterMinutes`. A closed thread cannot be answered, so nothing in it waits or rings (`GuestThreads.OpenAt`).
- **The floor** (`getBranchFloor`) carries `messages: { unreadTotal, oldestUnansweredUtc? }` over the same threads, and
  each table `nextReservationUnreadMessages` and `sittingUnreadMessages` - folded into its one statement, so the tile,
  the Now rail and the tab's badge need no second read. The tablet polls, as for takeaway: messages write nothing to the
  change stream.

### The console (manager or above, branch-scoped)

`GET` / `PUT /api/branches/{branchId}/message-settings` (`getMessageSettings`, `putMessageSettings`):
`{ guestsCanWrite, waitersCanWrite, templates, remindAfterMinutes }`, answered with `bounds`.

| Setting | Default | Bounds |
|---|---|---|
| `guestsCanWrite` | true | |
| `waitersCanWrite` | true | |
| `templates` | null - the app's default four | at most 8, each 1 to 120 characters after trimming (stored trimmed) |
| `remindAfterMinutes` | 3 | 1 to 15 |

On the branch, not in the reservation policy - the policy is replaced whole by every save of the reservation form, and a
switch living there would be reset by an edit that never showed it (the takeaway settings' reason). `guestsCanWrite`,
`waitersCanWrite` and `remindAfterMinutes` are required; `templates` may be null or left out (the default set). One 422
`validation-failed` names every bad field: `templates`, `templates[i]`, `remindAfterMinutes`.

## 6. Telling the guest

Every **venue** message - text or ask - writes, in the same transaction and under the account's lock, the push
`message.to-guest` (due at once, keyed `conversation:{id}:message:{messageId}`) and the feed entry `venue-message`. See
[notifications.md](notifications.md#guest-messages-messageto-guest) for the payload, the `venue-ask` / `venue-message`
categories and when it is not sent. **Guest messages push nothing**: the tablet polls.

## 7. The account's deletion

The thread is the venue's record beside the booking or the order: it stays, with `DinerUserId` cut. The guest's own
typed messages are deleted with the person's other rows; their answers (on my way, late, not coming) and the venue's
messages stay. A push still queued is not sent: the handler sends only while the thread is still the account's.

## 8. What it does not do

- No thread without a subject, no group chat, no photos, no links made clickable, nothing promotional.
- No push to the venue: the tablet polls the floor and the inbox.
- No editing or deleting a message once sent.
- No staff name, and no guest number, reaches the other side.
