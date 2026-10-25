# Domain events - one place for each side effect

A state change in Yalla is rarely just the row. An order marked ready is also a line in the venue's History, an
event on the tab's stream that every phone at the table follows, a push and a feed entry for the guest whose food it
is, and a buzz on the waiter's phone. Until 2026-10-25 each service wrote those by hand, beside the change, and QA
found twice that a path had forgotten one (a whole-order void that left its lines on the bill; a claimed call that
still buzzed the floor). A rule every path has to remember is a rule one of them forgets.

So the aggregate says what happened - it **raises a domain event** - and **handlers**, registered once, produce the
side effects in the same save and the same transaction. The service decides whether the change may happen and makes
it; it no longer writes what the change tells the rest of the system, so it cannot forget any of it.

Small on purpose: no MediatR, no new package, no bus, no queue - an interface, two methods on `Entity`, a dispatcher
and a few lines in `SaveChanges`.

## 1. The pieces

| Piece | Where | What it does |
|---|---|---|
| `IDomainEvent` | `Domain/Common/IDomainEvent.cs` | A record naming what happened, with `OccurredAtUtc` (an `IClock` instant, passed in like every domain instant). In-process only, never serialised, so it may carry the aggregate itself. |
| `Entity.Raise(e)` / `Entity.DequeueEvents()` | `Domain/Common/Entity.cs` | Every entity can raise. Events wait on the entity (a field EF never maps; null until the first raise) and are stamped with a process-wide order, so a save handles them in the order they were raised across aggregates. |
| `IDomainEventHandler<TEvent>` | `Application/Abstractions/IDomainEventHandler.cs` | One side effect of one event type. Exact type match: dispatch does not look at base types. |
| `DomainEventDispatcher` | `Infrastructure/Persistence/DomainEventDispatcher.cs` | Takes the pending events off the tracked entities and runs their handlers, round after round until nothing is pending; more than `MaxRounds` (8) is a loop and fails the save. Handlers come from the scope it was built in. `DomainEventDispatcher.None` is for a context composed without handlers. |
| `YallaDbContext.SaveChanges[Async]` | `Infrastructure/Persistence/YallaDbContext.cs` | Dispatches **first**, then runs the change-tracker hooks (booking and pre-order signals, staff push detection), stamps creation times and saves. |
| `TabLedger.SaveAppendedAsync` | `Infrastructure/Services/TabLedger.cs` | Dispatches before it numbers the tab's stream, so the `TabEvent`s a handler appends are numbered with the rest. The save's own dispatch then finds nothing. |
| `AddDomainEventHandlers()` | `Infrastructure/DependencyInjection.cs` | Every handler, in the order an event's handlers run. The test fixture composes the same list. |

## 2. Raising

- Raise from the **domain method that makes the change**, after the change, and only when it happened: a refused or
  no-op call raises nothing (`TabOrder.MoveTo` raises after the rail allowed the move; a replayed command returns
  before reaching the aggregate, so it raises nothing either).
- One event per thing that happened, named in the past tense (`TabOrderPlaced`, `TabOrderMoved`, `TabOrderVoided`,
  `TabOrderLineVoided`), carrying what a handler cannot read back afterwards: where the order *was* (`From`), the lines
  a void took off, the reason as the caller gave it (the entity stores it trimmed).
- Raise on an entity the context **tracks** - added to its set, or loaded. Dispatch walks the tracker without
  `DetectChanges`, so a child reachable only through a navigation is not seen: raise on the aggregate root
  (`TabOrder.VoidLine`, not the line). A save that would write an entity still carrying an event its dispatch never
  saw fails, naming the entity, rather than write the change without its side effects.
- A change that is history being replayed, not something happening now, raises nothing - say so in the method
  (`TabOrder.ArriveFromHeldTicket`: a booking's held kitchen ticket becoming an order when the party sits; its moves
  were the pre-order's and were told then).
- Make the non-raising way to do the same thing impossible: `TabOrderLine.Void` is `internal`, so a line is voided
  through its order, which raises.

## 3. Handling

- `internal sealed class X(...) : IDomainEventHandler<TEvent>` in `Infrastructure/DomainEvents/<Area>/`, registered in
  `AddDomainEventHandlers`. One class may handle several events of one vertical (`TabOrderActivity`), one side effect
  per class.
- **Inject what a service would**: `YallaDbContext`, `IClock`, `ICurrentActor`, `IActivityLog`, `IOutbox`, `TabLedger`.
  The handler runs in the scope of the code that raised the event, so the activity log attributes the entry to the same
  person and device, and the ledger names the same actor on the stream.
- **Add or change tracked rows; never save.** `IActivityLog.RecordAsync`, `IOutbox.Enqueue`, `DinerNotices.X(db, ...)`,
  `TabLedger.Append`, `StaffPushSignals.StageOne`, `db.X.Add(...)` all enlist in the unit of work. No HTTP, no Expo, no
  Web Push: anything that leaves the process is an outbox message, sent after the commit.
- **Read from the tracker first.** The code that raised the event has nearly always loaded the tab, its table, its
  branch. `TabOrderReads` reads `Local` (or `Entry(...).IsLoaded`) and queries only when the path loaded less, so a
  handler costs no round trip the hand-written code did not.
- **A lock held to the commit** needs a transaction open when it is taken: call `db.EnsureTransactionAsync(ct)`. It
  joins the caller's transaction (a lock scope's) or begins one that the next successful save commits; a failed save
  leaves it open for the retry on the same context, and disposing the context rolls it back (a caller that ends it
  through `Database` in between leaves the save nothing to commit). Model: `OrderReadyNotice`
  and `DinerAccountLock.IsLiveAsync`.
- A handler may change an aggregate that raises another event; the next round handles it. A handler that re-raises
  what it handles is a loop: the save fails after 8 rounds.

## 4. The rules

**Same unit of work, same transaction.** Handlers run before the save, inside `SaveChanges`; their rows commit or roll
back with the change. A rollback discards them. A handler that throws fails the save and nothing is written.

**Once per event.** Dispatch dequeues: a save that fails and is retried on the same context (the ledger's renumbering,
the totals retry) keeps what the handlers added and does not run them again. A command replayed by its
`clientCommandId` never reaches the aggregate, so it raises nothing. A unit of work redone on a fresh context raises
afresh, because its entities are new.

**Ordering.** Events are handled in the order they were raised, across all entities in the save. An event's handlers
run in registration order - for tab orders: the activity entry, then the tab's stream, then the diner's push and feed
entry, then the staff push, which is the order the services wrote them in before. Handlers of one event must not
depend on each other beyond that.

**Who acted.** Handlers read `ICurrentActor` (through the activity log and the ledger) as the service did; a sweep's
handler sees the sweep's system actor. An event does not carry the actor.

**No events, no cost.** A save that raised nothing walks the tracked entries once (no `DetectChanges`), resolves no
handler, opens no transaction and sends no extra command (`DomainEventDispatchTests`).

**The stream is numbered by the ledger.** A handler that appends to a tab's stream (`TabOrderStream`) needs its save
to go through `TabLedger.SaveAppendedAsync`; a save that would write a `TabEvent` with no sequence fails instead of
writing it where no phone catching up reads it.

**A context without handlers refuses events.** `new YallaDbContext(options, clock)` - the design-time factory, a test
seeding rows - uses `DomainEventDispatcher.None`; a save on it that finds an event pending throws, naming the event,
instead of silently dropping the side effects. Production contexts always come from DI with the scope's dispatcher.

## 5. The change-tracker hooks that remain

`YallaDbContext.SaveChanges` still runs four hooks that detect events from the change tracker rather than being told:
the booking signal (signal 5), the pre-order signal (signal 6), voiding a held pre-order of an ended booking, and
`StaffPushSignals` for **approvals, guestMessages and tableCalls**. They predate this mechanism and do the
same job for their verticals - one place, every path - by inspection. They stay until their verticals move (section
7), each then becoming a handler of that vertical's event; they run after dispatch, so they also see what handlers
wrote.

`kitchenReady` was the fifth staff push kind; it is now `KitchenReadyStaffPush`, a `TabOrderMoved` handler, and the
tracker hook no longer looks at orders. `takeaway` followed: `TakeawayStaffPush`, a `TakeawayPlaced` handler (section 6,
takeaway orders). A new staff push kind goes on events: `AGENTS.md`, "Add a staff push kind".

## 6. Tab orders - the migrated vertical

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `TabOrder.Place()` (from `TabOrderService.PlaceOrderAsync`; a waiter's order moves to InKitchen inside it) | `TabOrderPlaced(Order, WentStraightToKitchen)` | `TabOrderActivity`: `order.sent` (waiter) / `order.placed-by-guest` (phone), with the guest's name, items, amount and the table's server. `TabOrderStream`: `OrderPlaced`, and `OrderStatusChanged` New to InKitchen for a waiter's order. |
| `TabOrder.MoveTo(next, at, byStaffId)` (from `MoveOrderStatusAsync`) | `TabOrderMoved(Order, From, To, ByStaffId)` | `TabOrderActivity`: `order.in-kitchen` / `order.ready` / `order.served`. `TabOrderStream`: `OrderStatusChanged`. On Ready: `OrderReadyNotice` - `tab.order-ready` outbox message and `order-ready` feed entry for each guest with an account whose food is on it, under the account lock, when the branch notifies; `KitchenReadyStaffPush` - `staff.push` kind `kitchenReady`. |
| `TabOrder.Void(at, byStaffId)` | `TabOrderVoided(Order, From, LinesTakenOff, ByStaffId)` | `TabOrderActivity`: `order.voided` (money; the amount is what the void took off). `TabOrderStream`: `OrderStatusChanged` to Voided. The lines are voided by the domain method itself. |
| `TabOrder.VoidLine(line, at, reason, byStaffId)` | `TabOrderLineVoided(Order, Line, Reason, ByStaffId)` | `TabOrderActivity`: `order.line-voided` (money; the tab's total before and after - "before" is the bill with that line counted as live, `TabLedger.Compute(tab, countingAsLive)`). `TabOrderStream`: `LineVoided`. |

Handlers live in `Infrastructure/DomainEvents/TabOrders/`. What stayed in `TabOrderService`: who may do it, the
replay and the refusals (a paid tab refuses a void), building the order, the server claim, and saving through the
ledger. Discounts and comps still record their own activity there until the payments vertical moves.

**Proof it is the same.** `TabOrderSideEffectsSnapshotTests` was written and committed on the code before the move: a
diner order and its replay, a waiter's round for a guest, the rail with the guest told and the floor pushed, a line
void, a whole-order void and a void while new, through the real host - and it snapshots every activity entry (codes,
summaries, attribution, device labels), outbox row (keys, payloads), feed row, tab-stream event in sequence,
change-stream row, and the end state of the tab, its sitting, orders and lines
(`Integration/Snapshots/TabOrderSideEffects.approved.txt`). The refactor keeps it byte for byte. Regenerate only on
purpose: `YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~TabOrderSideEffectsSnapshotTests"`.

### Takeaway orders - migrated

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `TakeawayOrder.Place(acceptAutomatically)` (from `TakeawayService.PlaceAsync`; at an automatic branch it is accepted inside it, ready when the guest asked, by nobody) | `TakeawayPlaced(Order, AcceptedOnPlacing)` | `TakeawayOrderActivity`: `takeaway.placed` (the guest's; shown as sent - waiting, no ready time), and `takeaway.accepted` (the system's) when accepted on placing. `TakeawayGuestNotice`: accepted on placing, the `takeaway-accepted` feed entry and no push (the answer to the guest's tap says it). `TakeawayStaffPush`: otherwise, `staff.push` kind `takeaway`, key `staff-push:takeaway:{id}`. |
| `TakeawayOrder.Accept(readyAt, staffId, at)` (the counter; the sweep at a branch that turned automatic acceptance on, `staffId` null) | `TakeawayAccepted(Order, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.accepted` (the system's when nobody's). `TakeawayGuestNotice`: `takeaway.accepted` push and feed entry. |
| `TakeawayOrder.Decline(reason, staffId, at)` / `DeclineForNoAnswer(at)` (the sweep) | `TakeawayDeclined(Order, Cause, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.declined` with the reason, or `takeaway.auto-declined` (the system's). `TakeawayGuestNotice`: `takeaway.declined` push and feed entry (`noAnswer`, the reason). |
| `TakeawayOrder.CancelByGuest(at)` | `TakeawayCancelled(Order)` | `TakeawayOrderActivity`: `takeaway.cancelled` (the guest's). |
| `TakeawayOrder.MarkReady(staffId, at)` (the kitchen) | `TakeawayMarkedReady(Order, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.ready`. `TakeawayGuestNotice`: `takeaway.ready` push and feed entry. |
| `TakeawayOrder.Collect(method, staffId, at)` | `TakeawayCollected(Order, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.collected` (money: what was taken). |
| `TakeawayOrder.MarkNoShow(staffId, at)` | `TakeawayMarkedNoShow(Order, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.no-show`. |

Handlers live in `Infrastructure/DomainEvents/TakeawayOrders/`, registered after the tab orders' in the order the
services wrote them: the activity entry, the guest's notice, the floor's push. `TakeawayGuestNotice` takes the
account's lock (`db.EnsureTransactionAsync`, then `DinerAccountLock.IsLiveAsync`), as `TakeawayNotices` did; placing,
the counter's answers and the sweep already hold a transaction, which it joins. `TakeawayActivity` and
`TakeawayNotices` are gone; the `StaffPushSignals` hook no longer looks at takeaway orders. The kitchen's queue shows
takeaway tickets by reading the orders (`TabOrderService`), so no write feeds it.

What stayed in the services: who may act, the replays and refusals, the counter's race handling, the code and its
retry, the branch's lock - and **the no-show block**. `TakeawayService.MarkNoShowAsync` still judges it with
`NoShowBlocks` after the no-show is saved, under the guest's account lock in its own transaction, and frees the
cancelled bookings' holds after the commit: the count must read the no-show as saved (handlers run before the save),
the rule is shared with a booking's no-show (the bookings vertical's), and the release is after the commit, where no
handler runs. It moves with the bookings vertical.

Test seeders that build an order's history straight to the rows (`ReportTests`, `ActivityHistoryTests`,
`GuestRecordTakeawayTests`) call `order.DequeueEvents()` before saving on a bare context: those moves were told when
they happened. The sweep is tested as the host composes it (`TakeawayTests.SweepAsync`).

**Proof it is the same.** `TakeawaySideEffectsSnapshotTests` was committed on the code before the move: placed and
replayed, auto-accepted on placing, accepted and replayed, declined with a reason, cancelled and replayed, declined
and accepted by the sweep, ready, collected, and a third no-show that blocks the guest and cancels her booking - every
activity entry, outbox row (the guest's pushes, the staff push, the block's notice), feed row, change-stream row, and
the end state of the orders, lines, block and booking (`Integration/Snapshots/TakeawaySideEffects.approved.txt`). The
refactor keeps it byte for byte.

## 7. Testing

- The mechanism: `DomainEventDispatchTests` (handled once before the save; sync save; a failing save and a rollback
  discard the handlers' rows; a handler raising another event; the loop guard; a failing handler fails the save; raise
  order across entities; no events, no cost; a context without handlers refuses; the owned transaction).
- An aggregate's events: unit tests on the entity, `DequeueEvents()` after each call (`TabOrderTests`).
- Services built by `SqlServerFixture`: `CreateLedger` (and every service factory that uses it) composes
  `CreateUnitOfWork(db, clock, actor)` - the context, clock and caller, the outbox, the activity log, the ledger, the
  dispatcher and `AddDomainEventHandlers()` - so a service-level test runs production's handlers, as that caller. Per
  caller, because these tests often act as several people over one context and a request never does.
- A bare `fixture.CreateContext(clock)` has no handlers: seed with it, but drive a change that raises an event through a
  service the fixture composes (`StaffPushTests.An_order_up_goes_to_the_tables_own_waiter_and_not_to_every_phone`).

## 8. Moving the next vertical

The recipe, as tab orders went:

1. **Characterise first.** A snapshot test of every side-effect row a scripted lifecycle of the vertical writes,
   through the real host, committed on the old code (copy `TabOrderSideEffectsSnapshotTests`: name the ids you made,
   make instants relative, sort what has no contract order).
2. Events in `Domain/<Area>/<Aggregate>Events.cs`; raise them from the domain methods; make the non-raising path
   impossible (`internal`, or a method on the root).
3. Handlers in `Infrastructure/DomainEvents/<Area>/`, registered in `AddDomainEventHandlers` in the old write order.
4. Delete the hand-written calls from every service on the vertical's paths; fold the vertical's tracker hook into a
   handler.
5. Snapshot green, the full suite green (and with `YALLA_TEST_NOW=05:30`), OpenAPI byte-identical.

Remaining, in the order worth doing them (rough effort, one person, with the snapshot test):

| Vertical | Hand-written side effects today | Notes | Effort |
|---|---|---|---|
| **Payments** - cash, discount/comp and their reversal, write-off | `TabPaymentService`, `TabOrderService` adjustments, `TabStaffActions`: activity, tab stream | Money: the History's amounts must match the reports to the dram; `PaymentRecorded`, `AdjustmentAdded/Reversed`, `TabWrittenOff` on `Tab`/`TabAdjustment` | 1 day |
| **Calls** (service requests) | `ServiceRequestService`: activity, tab stream; `tableCalls` staff push in the tracker hook | The "claimed call still pushed" QA find lives here; `CallRaised/Claimed/Acknowledged` on `ServiceRequest` | 0.5 day |
| **Tabs** - join, admit, turn away, host moved, bill presented, transfer | `TabRoster`, `TabJoining`, `TabAdmissions(Commands)`, `TabSignals`, `TabOpener`, `TableTransferService`: tab stream, change-stream signals 1-3 and 7, activity, outbox | Many small paths and the admission app lock; `TabSignals` becomes handlers | 1.5-2 days |
| **Tables** - seat, free, hold, out of service | `TableStateService` (19 sites): change-stream rows, activity, feed | Signals carry the table's status as the save leaves it; events on `DiningTable`/`TableSession` | 1.5 days |
| **Bookings** - made, moved, cancelled, approved/declined, no-show, reminders, blocks, shares | `ReservationCreation/Changes/Cancellation/Decisions/Answers/Release/Notices/Reminders`, `NoShowBlocks`, `TableShares`, `PendingDecline`, `ApprovalExpirySweep`: activity, outbox (+ cancellation by prefix), feed; the booking signal, the held-pre-order void and the `approvals` push are tracker hooks | The largest: events on `Reservation` would replace three `SaveChanges` hooks; locking paths (`ReservationWriter`) already hold transactions | 3-4 days |
| **Pre-orders** | `PreOrderService`, `PreOrderSittings` (it appends the order's stream events on arrival itself), `PreOrderQuestions`; signal 6 hook | Arrival on the tab should raise its own event rather than append by hand | 1 day |
| **Messages** | `GuestMessageService`, `GuestThreads`; `guestMessages` push in the hook | Small | 0.5-1 day |
| **Settings, menu, staff, platform, reviews** | One activity call (or a few) per path | One path per change already: an event buys little. Move only when a second path appears | as needed |
