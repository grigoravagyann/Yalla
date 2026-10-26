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
| `YallaDbContext.SaveChanges[Async]` | `Infrastructure/Persistence/YallaDbContext.cs` | Dispatches **first**, then writes the pre-order rail's signal 6 rows the handlers asked for, stamps creation times and saves. No change-tracker hook detects anything any more (section 5). |
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

None, since the bookings moved (2026-10-25). `YallaDbContext.SaveChanges` used to detect events from the change tracker
rather than be told - the booking signal (signal 5), voiding a held pre-order of an ended booking, and `StaffPushSignals`
for **approvals** were the last three - and each is now a handler of the bookings' events (section 6, bookings):
`BookingChangeStream`, `EndedBookingPreOrder` and `ApprovalStaffPush`. `StaffPushSignals.Stage` is gone; what is left of
the class is `StageOne`, `Again` and `Read`. What the save still does after dispatch is write the signal 6 rows the
pre-order events asked for (`SignalPreOrderChangesAsync`, a writer only, which detects nothing): see section 6,
bookings, for why it stays there.

`kitchenReady` was the fifth staff push kind; it is now `KitchenReadyStaffPush`, a `TabOrderMoved` handler, and the
tracker hook no longer looks at orders. `tableCalls` is now `TableCallStaffPush`, a `ServiceRequestRaised` handler,
and `takeaway` is `TakeawayStaffPush`, a `TakeawayPlaced` handler (section 6, takeaway orders); the hook no longer looks
at service requests or takeaway orders. `guestMessages` is `GuestMessageStaffPush`, a `GuestMessageWritten` and
`GuestAnswered` handler (section 6, guest messages); the hook no longer looks at messages. A new staff push kind goes on
events: `AGENTS.md`, "Add a staff push kind".

The pre-order signal (signal 6) was the fourth; it is now told by `PreOrderRailSignal`, a handler of every pre-order
event (section 6, pre-orders), and only written by the save after signal 5's rows.

## 6. Tab orders - the migrated vertical

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `TabOrder.Place()` (from `TabOrderService.PlaceOrderAsync`; a waiter's order moves to InKitchen inside it) | `TabOrderPlaced(Order, WentStraightToKitchen)` | `TabOrderActivity`: `order.sent` (waiter) / `order.placed-by-guest` (phone), with the guest's name, items, amount and the table's server. `TabOrderStream`: `OrderPlaced`, and `OrderStatusChanged` New to InKitchen for a waiter's order. |
| `TabOrder.MoveTo(next, at, byStaffId)` (from `MoveOrderStatusAsync`) | `TabOrderMoved(Order, From, To, ByStaffId)` | `TabOrderActivity`: `order.in-kitchen` / `order.ready` / `order.served`. `TabOrderStream`: `OrderStatusChanged`. On Ready: `OrderReadyNotice` - `tab.order-ready` outbox message and `order-ready` feed entry for each guest with an account whose food is on it, under the account lock, when the branch notifies; `KitchenReadyStaffPush` - `staff.push` kind `kitchenReady`. |
| `TabOrder.Void(at, byStaffId)` | `TabOrderVoided(Order, From, LinesTakenOff, ByStaffId)` | `TabOrderActivity`: `order.voided` (money; the amount is what the void took off). `TabOrderStream`: `OrderStatusChanged` to Voided. The lines are voided by the domain method itself. |
| `TabOrder.VoidLine(line, at, reason, byStaffId)` | `TabOrderLineVoided(Order, Line, Reason, ByStaffId)` | `TabOrderActivity`: `order.line-voided` (money; the tab's total before and after - "before" is the bill with that line counted as live, `TabLedger.Compute(tab, countingAsLive)`). `TabOrderStream`: `LineVoided`. |

Handlers live in `Infrastructure/DomainEvents/TabOrders/`. What stayed in `TabOrderService`: who may do it, the
replay and the refusals (a paid tab refuses a void), building the order, the server claim, and saving through the
ledger. Discounts and comps raise their own events, on `TabAdjustment` (Payments, below).

**Proof it is the same.** `TabOrderSideEffectsSnapshotTests` was written and committed on the code before the move: a
diner order and its replay, a waiter's round for a guest, the rail with the guest told and the floor pushed, a line
void, a whole-order void and a void while new, through the real host - and it snapshots every activity entry (codes,
summaries, attribution, device labels), outbox row (keys, payloads), feed row, tab-stream event in sequence,
change-stream row, and the end state of the tab, its sitting, orders and lines
(`Integration/Snapshots/TabOrderSideEffects.approved.txt`). The refactor keeps it byte for byte. Regenerate only on
purpose: `YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~TabOrderSideEffectsSnapshotTests"`.

### Calls (service requests) - migrated

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `ServiceRequest.Send()` (from `ServiceRequestService.RaiseAsync`, after the constructor) | `ServiceRequestRaised(Request)` | `CallStream`: `ServiceRequested` (the scope and tip percentage, never the priced amounts). `TableCallStaffPush`: `staff.push` kind `tableCalls`, key `staff-push:call:{id}`. |
| `ServiceRequest.Claim(at, staffId)` (from `ClaimAsync`), and `Acknowledge` of a call nobody had claimed, just before its `ServiceRequestAcknowledged` | `ServiceRequestClaimed(Request, ByStaffId)` | `CallClaimActivity`: `table.call-claimed` with `responseSeconds` from the call. |
| `ServiceRequest.Acknowledge(at, staffId)` (from `AcknowledgeAsync`) | `ServiceRequestAcknowledged(Request, ByStaffId)` | `CallStream`: `ServiceRequestAcknowledged`. |

Handlers live in `Infrastructure/DomainEvents/Calls/`. What stayed in `ServiceRequestService`: who may call or answer,
the rate limit, pricing a request for the bill, the row lock on a claim or an answer, and saving through the ledger (a
claim too, which used to save on the context). Raising nothing, on purpose: a constructed call that is not sent (a test
seeding history); `Supersede`, which writes nothing today - the newer request's `ServiceRequested` is what the table
and the floor hear; and `AnswerForTurnedAwayTab`, the turn-away answering a phantom tab's open calls, which the tab's
own `TabAdmissionRejected` and `table.tab-turned-away` tell. Second claims and answers are no-ops and raise nothing.

**Proof it is the same.** `CallSideEffectsSnapshotTests` (`Integration/Snapshots/CallSideEffects.approved.txt`),
committed on the code before the move: calls raised, claimed twice, answered after a claim and without one, answered
twice; requests for the bill with a scope and a tip replaced by the same guest but not a tablemate, a plain one that
replaces nothing, a replaced one claimed and answered (no-ops); the rate limit; and a scan turned away with a claimed and
an unclaimed call. Activity, staff push outbox rows, both tabs' streams, change-stream rows and the end state of calls,
tabs and sittings are unchanged byte for byte. Regenerate only on purpose:
`YALLA_UPDATE_SNAPSHOTS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~CallSideEffectsSnapshotTests"`.

### Payments - migrated 2026-10-25

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `Tab.TakePayment(payment, at, sitting)` (from `TabPaymentService.RecordCashAsync`, after the payment is added and the totals applied with it) - the split locks | `TabPaymentRecorded(Tab, Payment, SettledTheBill)` | `PaymentActivity`: `payment.cash` (money: amount, tip, what remains, the total, whether it closed the tab, the table's server - the tip is theirs). `PaymentStream`: `PaymentRecorded`. |
| the same call, when nothing is owed after it: the tab closes, and its sitting when still open - not the table | `TabSettled(Tab, SittingClosed)` | `PaymentStream`: `TabClosed` (`tableSessionClosed`), after the `PaymentRecorded`. |
| `TabAdjustment.ByPercent` / `ByAmount` (from `TabOrderService.AddAdjustmentAsync`) | `TabAdjustmentAdded(Adjustment)` | `PaymentActivity`: `payment.discount` / `payment.comp` (money: its own reduction in the bill's order, `TabLedger.ReductionOf` - the reports' rule; the line's name, the total before and after - "before" is the bill with it not standing, `TabLedger.ComputeTogglingAdjustment`). `PaymentStream`: `AdjustmentAdded`. |
| `TabAdjustment.Void(at, byStaffId)` (from `VoidAdjustmentAsync`) | `TabAdjustmentReversed(Adjustment)` | `PaymentActivity`: `payment.adjustment-reversed` (money: the total before and after, `ComputeTogglingAdjustment` again). `PaymentStream`: `AdjustmentVoided`. |
| `Tab.WriteOff(at, byStaffId, reason)` (from `TabPaymentService.AbandonAsync`, and `TableStateService.AbandonTabAsync`, which now saves through the ledger) - replaces `MarkAbandoned` | `TabWrittenOff(Tab, WrittenOffAmd, Reason, ByStaffId)` | `PaymentActivity`: `payment.unpaid` (money: what was still owed, the total, what had been paid). `PaymentStream`: `TabAbandoned`. |

Handlers live in `Infrastructure/DomainEvents/Payments/`. Payments write no outbox message, feed entry, staff push or
change-stream row - there is no receipt or "settled" notice to the diner - so the vertical has no tracker hook to fold
in. What stayed in the services: who may do it, the branch guard, the replay by `clientCommandId` and the refusals (more
than is owed, a closed tab, a second reversal), the reserve and its no-retry race, and saving through the ledger.
`Tab.Close` still exists without an event for the table freeing a zero-balance tab (`TableStateService.FreeTableAsync`;
the Tables vertical's `TableFreed` names it), and `Payment.Reserve` raises nothing, so tests can seed a paid tab. `TabStaffActions` (the bill
presented, the host moved), listed with payments before, writes no money: it stays with the Tabs vertical.

**Proof it is the same.** `PaymentSideEffectsSnapshotTests`, committed on the code before the move: a percent discount,
a comp on a line, the discount reversed and a second reversal refused, a flat discount, cash with a tip and its replay,
an over-payment refused, the rest settling the bill, and a second table part paid and written off - snapshotting the
activity entries, outbox and feed rows, both tabs' streams in sequence, the change stream, the end state of the tabs
(totals, paid, remaining), sittings, orders, lines, payments and adjustments, and the day's revenue report, Staff →
Results and the waiter's cash list (`Integration/Snapshots/PaymentSideEffects.approved.txt`). The refactor keeps it
byte for byte. The one deliberate change, in its own commit before the move: `TableStateService.AbandonTabAsync` (no
route of its own) now tells the tab's stream `TabAbandoned` as the payment route does.

**A discount's or comp's History amount is the reports' (2026-10-26).** It used to be what the subtotal dropped by
with the bill as it stood when it was given, while the revenue report and Staff → Results count each adjustment's own
reduction in the bill's order (`AdjustmentReductions`). They differed when another adjustment stood: in the snapshot, a
comp of a 3,200 dish under a standing 10 % discount read 2,880 in the History (the discount then takes 320 less) and
3,200 in the reports. Decided: the History uses the reports' rule - `TabLedger.ReductionOf`, the same
`TabBilling.Reductions` over the bill in the tracker, in the same order - so the line and the reports name the same
amount; the total before and after still describe the bill. The snapshot's comp line moved from 2,880 to 3,200 and
nothing else (`ActivityHistoryTests.A_comp_under_a_standing_discount_reads_in_the_History_what_the_reports_count`).
Two things stay as they are, on purpose:

- **Entries written before the change keep their amount.** The History is a record of what was written, and the old
  amount cannot be recomputed safely after the fact: it needs the bill as it stood at that instant, and lines voided and
  adjustments reversed since are not in a query of today's rows. Only a line's discount or comp given under a standing
  tab-wide discount differs, by what that discount stopped taking (any other new adjustment comes off after every
  standing one, so it took exactly what the subtotal dropped by); no migration.
- **The History says what an adjustment took when it was given; the reports read the bill as it stands.** A later
  change to the bill moves an earlier adjustment's reduction in the reports and not its line: a comp given after a
  tab-wide discount shrinks what the discount takes (the comp comes off its line first), and a reversed adjustment
  changes what the ones after it take. The History is append-only; the reversal has its own line.

### Takeaway orders - migrated

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `TakeawayOrder.Place(acceptAutomatically)` (from `TakeawayService.PlaceAsync`; at an automatic branch it is accepted inside it, ready when the guest asked, by nobody) | `TakeawayPlaced(Order, AcceptedOnPlacing)` | `TakeawayOrderActivity`: `takeaway.placed` (the guest's; shown as sent - waiting, no ready time), and `takeaway.accepted` (the system's) when accepted on placing. `TakeawayChangeStream`: signal 8 (one row, accepted on placing or not). `TakeawayGuestNotice`: accepted on placing, the `takeaway-accepted` feed entry and no push (the answer to the guest's tap says it). `TakeawayStaffPush`: otherwise, `staff.push` kind `takeaway`, key `staff-push:takeaway:{id}`. |
| `TakeawayOrder.Accept(readyAt, staffId, at)` (the counter; the sweep at a branch that turned automatic acceptance on, `staffId` null) | `TakeawayAccepted(Order, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.accepted` (the system's when nobody's). `TakeawayChangeStream`: signal 8. `TakeawayGuestNotice`: `takeaway.accepted` push and feed entry. |
| `TakeawayOrder.Decline(reason, staffId, at)` / `DeclineForNoAnswer(at)` (the sweep) | `TakeawayDeclined(Order, Cause, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.declined` with the reason, or `takeaway.auto-declined` (the system's). `TakeawayChangeStream`: signal 8. `TakeawayGuestNotice`: `takeaway.declined` push and feed entry (`noAnswer`, the reason). |
| `TakeawayOrder.CancelByGuest(at)` | `TakeawayCancelled(Order)` | `TakeawayOrderActivity`: `takeaway.cancelled` (the guest's). `TakeawayChangeStream`: signal 8. |
| `TakeawayOrder.MarkReady(staffId, at)` (the kitchen) | `TakeawayMarkedReady(Order, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.ready`. `TakeawayChangeStream`: signal 8. `TakeawayGuestNotice`: `takeaway.ready` push and feed entry. |
| `TakeawayOrder.Collect(method, staffId, at)` | `TakeawayCollected(Order, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.collected` (money: what was taken). `TakeawayChangeStream`: signal 8. |
| `TakeawayOrder.MarkNoShow(staffId, at)` | `TakeawayMarkedNoShow(Order, ByStaffId)` | `TakeawayOrderActivity`: `takeaway.no-show`. `TakeawayChangeStream`: signal 8. |

Handlers live in `Infrastructure/DomainEvents/TakeawayOrders/`, registered after the tab orders' in the order the
services wrote them: the activity entry, the change stream, the guest's notice, the floor's push.

**Signal 8 (2026-10-26).** `TakeawayChangeStream` writes one `TakeawayChanged` row on the branch's change stream for
every takeaway event, in the same save, so the floor re-reads its counter when an order moves instead of polling it every
ten seconds. The row is about the counter, not a table (`TableStateChange.ForTakeaway`: no `DiningTableId`, Free to
Free, the system's, no order named); `TableStateChanges.DiningTableId` became nullable for it. The snapshot below gained
exactly those rows (17 in its script), on purpose. `TakeawayGuestNotice` takes the
account's lock (`db.EnsureTransactionAsync`, then `DinerAccountLock.IsLiveAsync`), as `TakeawayNotices` did; placing,
the counter's answers and the sweep already hold a transaction, which it joins. `TakeawayActivity` and
`TakeawayNotices` are gone; the `StaffPushSignals` hook no longer looks at takeaway orders. The kitchen's queue shows
takeaway tickets by reading the orders (`TabOrderService`), so no write feeds it.

What stayed in the services: who may act, the replays and refusals, the counter's race handling, the code and its
retry, the branch's lock - and **the no-show block**. `TakeawayService.MarkNoShowAsync` still judges it with
`NoShowBlocks` after the no-show is saved, under the guest's account lock in its own transaction, and frees the
cancelled bookings' holds after the commit: the count must read the no-show as saved (handlers run before the save),
the rule is shared with a booking's no-show (the bookings vertical's), and the release is after the commit, where no
handler runs. It stayed when the bookings moved; what the block writes is now the bookings' events' (section 6, bookings).

Test seeders that build an order's history straight to the rows (`ReportTests`, `ActivityHistoryTests`,
`GuestRecordTakeawayTests`) call `order.DequeueEvents()` before saving on a bare context: those moves were told when
they happened. The sweep is tested as the host composes it (`TakeawayTests.SweepAsync`).

**Proof it is the same.** `TakeawaySideEffectsSnapshotTests` was committed on the code before the move: placed and
replayed, auto-accepted on placing, accepted and replayed, declined with a reason, cancelled and replayed, declined
and accepted by the sweep, ready, collected, and a third no-show that blocks the guest and cancels her booking - every
activity entry, outbox row (the guest's pushes, the staff push, the block's notice), feed row, change-stream row, and
the end state of the orders, lines, block and booking (`Integration/Snapshots/TakeawaySideEffects.approved.txt`). The
refactor keeps it byte for byte.

### Tabs (the lifecycle) - migrated

Everything that happens to a tab apart from its orders, money and calls. The events are on `Tab`, the aggregate root,
in `Domain/Tabs/TabLifecycleEvents.cs`; a participant's change is raised by the tab it is on (`tab.ApproveParticipant`,
not `participant.Approve`).

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `Tab.OpenWithHost(host)` (from `TabOpener`: a scan, or a booker opening on a free table) | `TabOpenedByScan(Tab, Host)` | `TabFloorSignal`: when the tab waits for a waiter, signal 1 (`TabAwaitingAdmission`, "a scan is waiting to be let in"). |
| `Tab.OpenByStaff(...)` (from `TabStaffActions.OpenForStaffAsync`) | `TabOpenedByStaff(Tab, ByStaffId)` | `TabLifecycleActivity`: `table.tab-opened` (the table, the party size). |
| `Tab.Admit(at, because, byStaffId)` (a waiter's tap, `TabAdmissionCommands`; the booker arriving, `TabJoining`) | `TabAdmitted(Tab, Because, ByStaffId)` | `TabLifecycleActivity`: `table.tab-admitted`, a waiter's only. `TabLifecycleStream`: `TabAdmitted`. `TabFloorSignal`: signal 2 (`TabAdmitted`). |
| `Tab.RejectAdmission(at, byStaffId, reason, tableFreed)` (`TabAdmissionCommands.RejectAdmissionAsync`) | `TabTurnedAway(Tab, Reason, TableFreed, ByStaffId)` | `TabLifecycleActivity`: `table.tab-turned-away` (the reason, `tableFreed`). `TabLifecycleStream`: `TabAdmissionRejected`. `TabFloorSignal`: signal 3, only when the party stays - a freed table's own transition row carries it. |
| `Tab.AskToJoin(joiner, givenName, at)` (`TabJoining`: a join that lands pending) | `TabJoinRequested(Tab, Participant, GivenName)` | `TabLifecycleStream`: `ParticipantJoined`. `JoinRequestNotice`: `tab.join-requested` to the host's account, not to the host's own account and not again for somebody the host turned away. |
| `Tab.ApproveParticipant(p, at, asBooker)` (the host's tap, `TabRoster`; the booker on their own booking's tab, `TabJoining`) | `TabParticipantApproved(Tab, Participant, AsBooker)` | `TabLifecycleStream`: `ParticipantApproved`. `ParticipantApprovedNotice`: `tab.participant-approved` to the guest's account - not for a booker. |
| `Tab.RejectParticipant` / `RemoveParticipant` / `SetParticipantPermissions` / `RenameParticipant` (`TabRoster`) | `TabParticipantRejected`, `TabParticipantRemoved(Left: false)`, `TabParticipantPermissionsChanged`, `TabParticipantRenamed` | `TabLifecycleStream`: `ParticipantRejected`, `ParticipantRemoved`, `ParticipantPermissionsChanged`, `ParticipantRenamed`. |
| `Tab.MoveHostTo(newHost, at, hostLeft)` (a waiter's move, `TabStaffActions`; inside `Tab.Leave` for a host handing on) | `TabHostMoved(Tab, From, To, HostLeft)` | `TabLifecycleActivity`: `table.host-reassigned` (the names), a waiter's move only. `TabLifecycleStream`: `HostReassigned`. |
| `Tab.Leave(leaving, successor, at)` (`TabRoster.LeaveAsync`) | `TabHostMoved` first when the host leaves, then `TabParticipantRemoved(Left: true)` | `TabLifecycleStream`: `ParticipantRemoved` with `left: true`. |
| `Tab.SetSettlementMode(mode, at)` (`TabRoster`) | `TabSettlementModeChanged(Tab)` | `TabLifecycleStream`: `SettlementModeChanged`. |
| `Tab.BeginClosing(at)` (`TabStaffActions.BeginClosingAsync`) | `TabBillPresented(Tab)` | `TabLifecycleActivity`: `table.bill-presented` (total, remaining). `TabLifecycleStream`: `TabClosing`. |

Handlers live in `Infrastructure/DomainEvents/Tabs/`, registered after the takeaway orders': the History's line, the
tab's stream, the change stream, then the push. `TabSignals` is gone; `TabAdmissions` keeps only `Signal`, the one
writer of a same-status tab row, shared with `TableChangeStream` (signal 7, the Tables vertical's hand-over). `TabService` and
its command classes lose their outbox and activity dependencies; the scan and the staff open now save through
`TabLedger.SaveAppendedAsync`, so a test's bare context dispatches with the caller's handlers as every other tab save does.

What stayed in the services: who may act, the replays and refusals, the convergent opening and its races, the admission
app lock, the server claim, the join tokens revoked, the cached standing dropped (`ITokenAuthorityCheck`), and the
turn-away's table: freeing it (and holding it again for a late party) is `TableStateService`'s transition, and the
service still tags that transition's row with signal 3. Raising nothing, on purpose: building a tab or a participant
(seeders); `SetHostParticipant`, the first phone onto a tab a waiter opened, which today tells nobody; a rescan that
renames somebody already on the tab (`TabParticipant.SetDisplayName`), which today writes no `ParticipantRenamed`; the
participant's own `Approve`/`Reject`/`Remove`, kept public for test seeders as `Payment.Reserve` is. Seeders that put a
tab straight into Closing (`ConsoleTestData`, `BranchOverviewTests`) or turned away
(`UnverifiedGuestsMigrationRollbackTests`) dequeue its events before saving on a bare context.

**Proof it is the same.** `TabLifecycleSideEffectsSnapshotTests`, committed on the code before the move: an unverified
scan that seats the table and waits, and its replay; guests joining by scan and by invitation; the host approving
(twice), rejecting and removing; a rejected guest scanning again (on the stream, not pushed); the waiter admitting, the
replay and a second waiter's tap; permissions, a rename, the split; the host moved by the waiter and then leaving; the
bill presented with an invitation live; a phantom turned away that frees the table, its replay and a refused admit; a
scan turned away on a waiter-seated table; a staff-opened tab (twice) hosted by the first phone; a booker let straight
onto a friend's waiting tab. Activity, outbox, feed, each tab's stream in sequence, the change stream in order, and the
end state of the tabs, participants, invitations, sittings and admission commands are unchanged byte for byte
(`Integration/Snapshots/TabLifecycleSideEffects.approved.txt`). One domain tightening, invisible there: `MoveHostTo`
checks the new host is approved before it touches anything, where `ReassignHost` used to repoint the tab first and
leave the refusal to the participant (the request failed and nothing was saved either way).

### Tables - migrated 2026-10-25

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `DiningTable.Occupy(sitting, how, booking, reason, clientCommandId, at)` (every seating in `TableStateService`: a waiter's walk-in, a booking, a held party; a QR scan; the booked party seating themselves from the app) | `TableSeated(Table, Sitting, From, How, Booking, Reason, ClientCommandId)` | `TableChangeStream`: the change stream's row (Free/Held to Occupied, naming the sitting and the booking; the reason as given, else "seated walk-in", "opened by QR scan", "seated reservation {code}", "seated by the diner from booking {code}", "seated held party"). `TableActivity`: `booking.seated` for a booking, `table.seated` otherwise (`source` walk-in, scan, booking, held). |
| `DiningTable.Vacate(sittingId, reservationId, tabId, outstandingAmd, reason, clientCommandId, at)` (`FreeTableAsync`, and the turn-away that frees a scan's table) | `TableFreed(Table, From, SittingId, ReservationId, TabId, TabClosed, OutstandingAmd, ...)` | `TableChangeStream`: Occupied to Free, naming the sitting, booking and tab. `TableActivity`: `table.freed` (the amount left owing, when any). |
| `DiningTable.PlaceHold(forReservationId, ...)` / `ReleaseHold(...)` | `TableHeld` / `TableHoldReleased` | `TableChangeStream`: to Held (naming the table's next booking) / back to Free. `TableActivity`: `table.held` / `table.hold-released`. |
| `DiningTable.MarkOutOfService(strandedBookings, ...)` / `ReturnToService(...)` | `TableTakenOutOfService` / `TableReturnedToService` | `TableChangeStream`: to OutOfService / back to Free. `TableActivity`: `table.out-of-service` (`strandedBookings`) / `table.back-in-service`. |
| `DiningTable.NoteHoldExtended(reservationId, holdExpiresAtUtc, ...)` (`RecordHoldExtendedAsync`, after the diner's extension is saved) | `TableHoldExtended` | `TableChangeStream`: a same-status row naming the booking. No History line here: `booking.hold-extended` is the booking's. |
| `TableSession.TransferServer(to, tabId, at)` (`TableTransferService.TransferAsync`: a table handed to another waiter, the sitting's `ServerStaffMemberId` moved from now on - listed with the Tabs before, moved here) | `TableTransferred(Sitting, TabId, FromStaffId, ToStaffId)` | `TableActivity`: `table.transferred` (who had it, who has it). `TableChangeStream`: the `TableTransferred` (7) signal naming the tab, through `TabAdmissions.Signal`. |

Handlers live in `Infrastructure/DomainEvents/Tables/`, registered after the takeaway orders' in the order the state
machine wrote them: the change stream's row, then the History's line (a hand-over: the line, then the signal). The row
names who acted as the request does (`TableStateActors.Resolve`, shared with the service's stored answer): the waiter,
the diner or their phone's participant, the system for a scan with no token. `TableStateService` and
`TableTransferService` dispatch before their save with their own `DomainEventDispatcher`, as the ledger does, so a
test composing them over a bare context (`SqlServerFixture.CreateService`) runs production's handlers for its caller.

What stayed in the services: who may do it, the replays (`ProcessedCommands`, the audit-row fallback), the
preconditions and refusals, the table lock, the row-version conflict, the booking marked seated or completed and its
unshown reminders dropped, the held pre-order put on the tab (`PreOrderSittings`, the pre-orders vertical's), closing
the sitting, and the tab's own fate when the table is freed. **A zero-balance tab closes with the table and raises
nothing of its own** (`Tab.Close`, unchanged): `TableFreed` names it (`TabId`, `TabClosed`), and freeing a table has
never told the tab's stream anything - a phone on an empty tab hears no `TabClosed`.

**Looked at 2026-10-26 and left so: the phones learn it from the tab read.** The diner's tab screen polls
`GET /api/tabs/{tabId}` every 8 seconds whatever the stream says (`useDinerTab(tabId, { pollMs })` in the frontend's
`app/tab/[tabId]/index.tsx`, "staff may close the table meanwhile"), and so do its people and waiting screens; the
screens pushed above it (menu, tray, settle) read the same cached query, which the tab screen beneath keeps polling.
The stream is only a faster reason to refetch: the app reads every event type, `tabClosed` included, as "refetch the
tab", and never acts on its payload. So within one poll the read answers `status: Closed` with `closedAtUtc` and
`canOrderNow: false`; the screen shows the closed tab's paid view and forgets it as the active tab, the menu says the
tab is closed instead of offering Add, and an order sent meanwhile is refused. Past the receipt grace (`Jwt:ParticipantReceiptGraceMinutes`, 120) the
token answers `tab-closed` and the app shows that access ended. The staff screens learn from the change stream
(`TableFreed` names the tab and that it closed); nothing on the web reads a tab's stream. Raising an event for it would
make the table state machine save through the ledger to number a stream row, for a phone that is told at most eight
seconds later anyway. Pinned by `TabEndpointTests.A_table_freed_with_an_empty_tab_closes_it_and_the_phones_read_of_the_tab_says_so`
(the read says closed, the stream carries no `TabClosed`); revisit if a client ever ends a tab screen from the stream
alone.
The transfer loads the caller, the target, the branch and the previous waiter tracked, so the handlers read names and
the venue from the tracker.

Test seeders that build a floor straight to the rows use `TestTableTransitions` (the transition with the event dropped);
the entity's events are tested in `DiningTableEventTests`.

**Proof it is the same.** `TableSideEffectsSnapshotTests`, committed on the code before the move: a walk-in seated, its
replay and a second seating refused; a hand-over to a waiter on shift by the table's waiter, one by a manager, one to the
cook refused and one already done; freed with a zero-balance tab, with a balance left and with no tab, the free
replayed and a double free refused; a booking seated by the waiter, a stale console row refused
(`table-sitting-changed`) and the booking's sitting finished; holds placed, released (and replayed), seated for a late
booking and for nobody's, a queued and a changed-and-changed-back command refused; out of service with a booking
stranded, a seating refused, back in service; a QR scan seating a table and its turn-away freeing it; the booked party
seating themselves from the app; a hold the diner extended. Every answer, the change stream in sequence, activity,
outbox and feed rows, the tabs' streams, the stored answers, and the end state of tables, sittings, tabs and bookings
(`Integration/Snapshots/TableSideEffects.approved.txt`) are unchanged byte for byte. The one race it cannot script
deterministically - two writers on one table, `table-state-conflict` - saves nothing either way (`TableStateServiceTests`).

### Guest messages - migrated

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `Conversation.WriteAsGuest(text, clientCommandId, at)` (`GuestMessageService.WriteAsGuestAsync`) | `GuestMessageWritten(Thread, Message)` | `GuestMessageStaffPush`: `staff.push` kind `guestMessages`, key `staff-push:message:{messageId}`. |
| `Conversation.Answer(answer, lateMinutes, heldUntil, at)` (`GuestThreads.AnswerAsync`, from "on my way" and "running late" in `PreOrderService` - the app and the manage link - and "not coming", the booking's cancel in `ReservationCancellation`) | `GuestAnswered(Thread, Answer, Status)` | `GuestMessageStaffPush`: the same push, for the answer (the status line after it is the server's, and is not pushed). |
| `Conversation.WriteAsVenue(text, staffId, staffName, clientCommandId, at)` (`GuestMessageService.WriteAsStaffAsync`) | `VenueMessageWritten(Thread, Message)` | `MessageActivity`: `message.sent` (the words, `replySeconds` - from the guest's oldest text the venue had not answered, read from what is saved; none when the thread is new in this save). `VenueMessageGuestNotice`: `message.to-guest` push (`VenueMessageNotice`, key `conversation:{id}:message:{messageId}`) and `venue-message` feed entry, under the account lock, for a live account. |
| `Conversation.AskComing(staffId, staffName, at)` (`GuestThreads.AskAsync`, from `PreOrderService.AskOnMyWayAsync`) | `VenueAskedComing(Thread, Ask)` | `MessageActivity`: `message.asked-on-my-way` (`notified`, `startUtc`). `VenueMessageGuestNotice`: the push (`ask: true`) and feed entry. |

Handlers live in `Infrastructure/DomainEvents/Messages/`, registered after the tabs': the History's line, the guest's
notice, the floor's push. `ConversationReads` (scoped, keyed by the message) reads the thread's subject
(`GuestThreads.SubjectAsync`) and takes the account's lock (`db.EnsureTransactionAsync`, then
`DinerAccountLock.IsLiveAsync`) once for every handler of a message, so the ask's `notified` and its notice agree and the
round trips stay what the hand-written code made (one more only when the venue writes to a subject with no live account:
the History's line now reads the subject itself). `GuestThreads.TellGuestAsync` is gone; `GuestMessageService` loses its
outbox; `StaffPushSignals` no longer looks at messages. Read marks (one conditional `UPDATE` each), the inbox, the
history page and `ConversationRetention` (a bulk delete) write no side effect and raise nothing.

What stayed in the services: who may write, the order of the refusals (text, subject, staff, settings, replay, open,
pace), the replay by `clientCommandId` and its 409, the account's and the subject's locks and finding or starting the
thread. In the booking services only the call changed: `PreOrderService.AskOnMyWayAsync` hands `AskAsync` the account
lock's answer (it took it before, and dropped it) and no longer records the ask's History line itself; "on my way",
"running late" and the cancel still call `AnswerAsync` and keep their own `booking.guest-answered` / `booking.cancelled`
lines. Test contexts that save a message: `SqlServerFixture.CreateContextWithHandlers` (a context whose own saves
dispatch, for a service that saves on the context), the host's scope, or `DequeueEvents()` on seeded history
(`ActivityHistoryTests`).

**Proof it is the same.** `MessageSideEffectsSnapshotTests`, committed on the code before the move: a guest's text from
the app with its replay, a reused id, a blank and an over-long text refused; the waiter reading and replying, its replay
and the manager reusing its id; the guest reading; five texts in a minute and the sixth slowed down; a takeaway order's
thread (the kitchen refused, the counter replying); a branch where guests may not type and waiters may not write (both
refused, a manager writing); "not coming" from the app into a thread that exists, a booking cancelled a day ahead that
gets none, and "not coming" from the manage link opening a thread; the venue writing first to a booking with no
account; "are you coming?" with and without an account; "running late" from the app and "on my way" from the manage
link (and its replay); a reply that answers nothing; a closed thread refusing both sides; read marks; the console's
history; and the retention deleting a thread. Activity, outbox (the guest's pushes, the staff pushes, the bookings' and
the order's own messages), feed, change stream and the end state of the threads, messages, bookings and order are
unchanged byte for byte (`Integration/Snapshots/MessageSideEffects.approved.txt`).

### Pre-orders - migrated

A pre-order and the booking's "are you on your way?" - the guest's answers and the venue's ask live on the booking, so
their events are raised by `Reservation`. Events in `Domain/Occupancy/PreOrderEvents.cs`.

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `PreOrder.Replace(lines, note, at)` (`PreOrderService`, the app and the link: the first save and every edit) | `PreOrderSaved(PreOrder)` | `OnYourWayQuestion`: the question moved to 45 minutes - push `reservation.on-your-way` and feed `booking-on-your-way` under the account's lock - unless the 45-minute key is already written or the question for this start went out. `PreOrderRailSignal`. |
| `PreOrder.Clear(at)` (new; the service then removes the row) | `PreOrderCleared(PreOrder)` | `OnYourWayQuestion`: back to 30 minutes, as every booking is asked. `PreOrderRailSignal`. |
| `PreOrder.MarkSent(staffId, sitting, at)` (the rail's send) | `PreOrderSent(PreOrder, ByStaffId, SittingId, TabId)` - the sitting and its tab as they stood before the send | `PreOrderActivity`: `order.pre-order-sent` (the tab's when the sitting had one, else the booking's; `seated`). `OnYourWayQuestion`: withdrawn. `PreOrderRailSignal`. |
| `PreOrder.MarkNotComing(staffId, at)` (the rail) | `PartyMarkedNotComing(PreOrder, ByStaffId)` | `PreOrderActivity`: `booking.guest-answered` (`not-coming`, `recordedByStaff`). `OnYourWayQuestion`: withdrawn. `PreOrderRailSignal`. |
| `PreOrder.MarkAttached(order, onNewTab, at)` (`PreOrderSittings`: the party seated with the ticket held - by the waiter, as a held party or opening with the booking code - or a send to a seated party) | `PreOrderArrivedOnTab(PreOrder, Order, OnNewTab)` | `PreOrderArrivalStream`: `OrderPlaced` then `OrderStatusChanged` New to where the kitchen had it, as the sending waiter's - numbered 1 and 2 on a tab opened for it, through the ledger on a tab the sitting had. `PreOrderRailSignal`. |
| `PreOrder.MoveKitchenStatus(next, at)` (the kitchen, `TabOrderService`, which now saves it through the ledger for its dispatch) | `HeldTicketMoved(PreOrder, From, To)` | `PreOrderRailSignal`. |
| `PreOrder.FollowMove(at)` (the bookings' `ReservationChanges`: the pre-order goes with a move) | `PreOrderFollowedMove(PreOrder)` | `PreOrderRailSignal`. |
| `PreOrder.VoidHeldBecauseBookingEnded(at)` (the bookings' `EndedBookingPreOrder`, a handler of a booking ending; the dispatcher's next round handles what it raised) | `HeldTicketVoided(PreOrder)` | `PreOrderRailSignal`. |
| `Branch.SetPreOrders(accepts, minutes, at)` (the console's settings) | `PreOrderSettingsSaved(Branch)` | `PreOrderRailSignal`: every booking whose card is or could be on a rail now, and the next one after them. |
| `Reservation.ConfirmOnMyWay(at)` (app and link; a replay raises nothing) | `GuestOnTheirWay(Reservation)` | `PreOrderActivity`: `booking.guest-answered` (`coming`, the guest's). `OnYourWayQuestion`: withdrawn. `PreOrderRailSignal`. |
| `Reservation.ReportRunningLate(minutes, at)` | `GuestRunningLate(Reservation, Minutes)` | `PreOrderActivity`: `booking.guest-answered` (`late`, `minutes`). `OnYourWayQuestion`: withdrawn. `PreOrderRailSignal`. |
| `Reservation.RecordStaffAskOnMyWay(at)` (the tablet's "are you coming?"; the thread's message and the guest's notice are the messages vertical's `VenueAskedComing`) | `OnMyWayAskRecorded(Reservation)` | `PreOrderRailSignal`. |
| `Reservation.Reschedule` / `KeepConfirmedSlot` to another start, when the guest had answered or the venue had asked | `OnMyWayAnswersForgotten(Reservation)` | `PreOrderRailSignal`. |

Handlers live in `Infrastructure/DomainEvents/PreOrders/`, registered by `PreOrderEventHandlers.Add` from
`AddDomainEventHandlers`, after the messages': the History's line, the question, the tab's stream, then signal 6.
`PreOrderEventReads` reads the booking and the table from the tracker first. `OnYourWayQuestion` re-takes the account's
lock (`db.EnsureTransactionAsync`, then `DinerAccountLock.IsLiveAsync`) the guest's save took first: one more round trip
on a guest's save, the only one the move adds. `PreOrderQuestions` stays the question's helper, shared with the
bookings' `ReservationReminders`.

**Signal 6 is told, no longer detected.** The tracker hook that looked for changed pre-orders and on-my-way columns is
gone: `PreOrderRailSignal` names the bookings (`YallaDbContext.SignalPreOrderRail`), and the save writes their rows
where the hook did, after signal 5's (`SignalPreOrderChangesAsync`, now a writer only). Written in the handler, a
signal 6 row would come before signal 5's in the stream and signal 5's "already signalled in this save" would take it
for its own and drop the booking's signal 5. Signal 5 is on the bookings' events now, and its handler no longer counts a
signal 6 row as its own - but the row still waits for the end of the save: see section 6, bookings.

What stayed in `PreOrderService`: who may act, the account's lock, the replays and the refusals in their order - the
booking still to come, already sent, **the two-hour lock** (`pre-order-locked`, checked before any domain call, so a
refused edit or clear raises and writes nothing), the branch taking pre-orders, the shape, the dishes - the table lock,
the tab race, the totals recomputed after the commit, and the settings' own History line (`settings.pre-orders`, one
path). Each of its saves dispatches first with the scope's dispatcher, as the ledger does
(`SqlServerFixture.CreatePreOrderService` composes it per caller). `PreOrderSittings` builds the tab and the order and
appends nothing. Bookings-owned: the booking's own question when it is made, moved or cancelled
(`ReservationReminders`, the cancel by prefix), signal 5 on the answers and the ask, and voiding a held ticket when its
booking ends - all on the bookings' events since (section 6, bookings).

**Proof it is the same.** `PreOrderSideEffectsSnapshotTests`, committed on the code before the move: pre-orders placed
with their bookings (and a replay), edited from the link, a send refused two days ahead, a booking moved with its
pre-order and cleared from the link, the edit refused inside the lock, the settings saved with cards on the rail, "are
you coming?" with and without an account, "on my way" from the app and the link, "running late", a clear of a booking
with no pre-order, "not coming" and its replay, held sends and a replayed one, the kitchen moving a held ticket, a
cancel voiding a held ticket and one with a pre-order never sent, sends to seated parties onto an open tab and onto one
the send opens, and held tickets arriving with the waiter's seating and the booker's code. Every answer, the change
stream in sequence (signal 6 after signal 5 within a save), activity, outbox, feed, each tab's stream, the stored
commands, and the end state of pre-orders, lines, bookings, sittings, tabs, orders and threads are unchanged byte for
byte (`Integration/Snapshots/PreOrderSideEffects.approved.txt`). The events are unit-tested in `PreOrderEventTests`.

**The one deliberate change**, in its own commit before the move: clearing a booking that has no pre-order no longer
rewrites its question - it withdrew and re-wrote the 30-minute question whatever it found, so a guest who had already
said they were on their way saw the feed entry again 30 minutes before the start
(`PreOrderTests.Clearing_a_booking_with_no_pre_order_does_not_ask_an_answered_guest_again`).

### Bookings - migrated 2026-10-25

The largest vertical: a booking is made, decided, moved, cancelled, let go and missed in a dozen places - the app, the
public page and its manage link, the floor, the console, the approval sweep, the guest before it answering for their
table, a no-show block. Events in `Domain/Occupancy/ReservationEvents.cs`. Each booking event carries the booking and
`Before` (`ReservationStanding`: its status, table, start, end, party, local date and time, reason to wait and answer
window as they were), which is what the change tracker used to compare against.

| Raised by | Event | Handlers and what they write |
|---|---|---|
| `Reservation.Book(at)` - new; `ReservationCreation`, after `Create` and the manage token, inside the table's lock | `ReservationMade(Reservation)` | `ReservationActivity`: `booking.created` (`channel`, `trigger`, the guest's). `BookingChangeStream`: signal 5 on its table. `ApprovalStaffPush`: `approvals` when it waits. |
| `TableShareRequest.Ask()` - new; `TableShares.Ask`, from the creation and a move, beside the later booking | `TableShareAsked(Request)` | `EarlierGuestNotice`: asked in the app, `reservation.table-request` to the guest before and their `table-request` feed entry; the floor's own question tells nobody. |
| `Reservation.Confirm(at, by, answeredRequest)` - staff (`ReservationDecisions`), the guest before saying yes (`ReservationAnswers`), or nobody when the question it waited on went away (`TableShares.DecideWithoutTheQuestion`) | `ReservationApproved(Reservation, Before, By, AnsweredRequest)` | `ReservationActivity`: `booking.approved` (`trigger`, `decisionSeconds`), or `booking.guest-answered` (`accepted: true`, the earlier booking), or nothing for nobody. `ReservationGuestNotice`: `reservation.decided` and `booking-confirmed` (key `reservation:{id}:approved`, timestamped when that is taken). `BookingChangeStream`. |
| `Reservation.Reject(at, reason, because, answeredRequest)`, `DeclineForNoAnswer(at, because)`, `DeclineChange(at, because, reason, answeredRequest)` - new, a waiting change whose old slot is gone - and `KeepConfirmedSlot(at, because, reason, answeredRequest)`; from `PendingDecline` (staff, the guest before, the sweep) and `TableShares.EarlierChangedAsync` (crowded out) | `ReservationDeclined(Reservation, Before, Because, Reason, WasChange, SlotKept, RequestedDate, RequestedTime, AnsweredRequest)` | `ReservationActivity`: `booking.declined` (staff, with the reason), `booking.auto-declined` (the sweep's, `deadlineUtc` from `Before`), `booking.guest-answered` (`accepted: false`), nothing when crowded out. `BookingReminders`: kept at the old slot, its reminders rewritten for it; otherwise withdrawn by prefix with the feed entries not yet shown. `ReservationGuestNotice`: `reservation.decided` and `booking-declined` (`expired`, `wasChange`, `originalKept`, the slot asked for) - not when it is the clock's decline of a booking that would already have started. `EarlierGuestNotice`: the guest before's own `table-request-decided` entry for their no. `BookingChangeStream`. `EndedBookingPreOrder`. |
| `Reservation.WaitInsteadFor(trigger, expires, at)` (`TableShares`: the question went away and something else still needs a human) | `ReservationWaitingInstead(Reservation, Before)` | `ApprovalStaffPush` when the reason changed. |
| `Reservation.Reschedule(..., viaManageLink)` (`ReservationChanges`: the app and the link, inside the target table's lock) | `ReservationRescheduled(Reservation, Before, ViaManageLink)` | `ReservationActivity`: `booking.moved` (before and after). `BookingReminders`: replaced for the new slot. `ReservationGuestNotice`: from the link, `reservation.changed-by-link` and its feed entry. `BookingChangeStream`: signal 5 on the new table and the old. `ApprovalStaffPush` when it moved into waiting, or to another time while waiting. |
| `Reservation.PromiseToFreeBy(request, at)` (`TableShares.ApplyYesAsync`: the guest before's yes, or the floor's for them) | `ReservationPromisedToFreeBy(Reservation, Before, Request)` | `EarlierGuestNotice`: `reservation.table-request-decided` and the `table-request-decided` entry. `BookingChangeStream` when its end was cut. |
| `Reservation.CancelByDiner(..., viaManageLink)` (`ReservationCancellation`), `CancelByVenue(at, reason)` (`ReservationRelease`; the default reason is now the domain's `ReleasedByVenueReason`), `CancelForNoShowBlock(at)` (`NoShowBlocks`) | `ReservationCancelled(Reservation, Before, By, Reason, ViaManageLink)` | `ReservationActivity`: `booking.cancelled` (`late`, `viaManageLink`, the guest's), `booking.released` (`countsTowardBlock`), `booking.cancelled` (`because: no-show-block`, the system's). `BookingReminders`: withdrawn. `ReservationGuestNotice`: from the link, `reservation.cancelled-by-link` and its entry; let go by the venue, the `booking-cancelled-by-venue` entry. `BookingChangeStream`: signal 5, or for a block's cancellation signal 4 with the block's words. `EndedBookingPreOrder`. |
| `Reservation.MarkNoShow(at, staffId, counts, reason)` (`ReservationRelease`) | `ReservationMarkedNoShow(Reservation, Before, Reason)` | `ReservationActivity`: `booking.no-show`. `BookingReminders`: withdrawn. `ReservationGuestNotice`: `reservation.no-show` (key with the instant) and `booking-no-show`. `BookingChangeStream`. |
| `Reservation.UndoNoShow(at, staffId, reason)` | `ReservationNoShowWithdrawn(Reservation, Before, Reason)` | `ReservationActivity`: `booking.no-show-undone`. `BookingChangeStream`. |
| `Reservation.ExtendHold(now, minutes)` | `ReservationHoldExtended(Reservation, Before, Minutes)` | `ReservationActivity`: `booking.hold-extended`. (The table's own row is the Tables vertical's `TableHoldExtended`.) |
| the pre-orders' `GuestOnTheirWay`, `GuestRunningLate`, `OnMyWayAskRecorded` (section 6, pre-orders) | - | `BookingChangeStream`: signal 5 on the booking's table, as the tracker wrote it for the answers and the ask. |
| `BookingBlock.Impose(cancelled, untilLocalDate)` - new; `NoShowBlocks`, after cancelling the guest's bookings | `BookingBlockImposed(Block, CancelledBookings, BlockedUntilLocalDate)` | `BookingBlockNotice`: `diner.bookings-blocked` and the `bookings-blocked-no-shows` entry, once for the whole block. |
| `BookingBlock.Lift(...)` (a no-show withdrawn, `ReservationRelease`; the platform, `BookingBlockService`) - only when it lifted | `BookingBlockLifted(Block)` | `BookingBlockNotice`: `diner.bookings-unblocked` and the `bookings-unblocked` entry. |

Handlers live in `Infrastructure/DomainEvents/Bookings/`, registered by `AddBookingHandlers` after the pre-orders': the
History's line, the reminders, the guest's push and feed entry, the guest before's, the block's notice, the change
stream, the floor's push, then the held pre-order. `BookingReads` reads the branch (with its venue), a table's label
and a booking from the tracker first; the sweep now loads its branch tracked so the handlers find it there.
`BookingActivity` stays the one shape of a booking's History line, shared with the pre-orders' `PreOrderActivity`, and
`ReservationNotices` the decided and link notices' shape, both called only from handlers now.

**The three tracker hooks are handlers.** Signal 5 (`BookingChangeStream`) is written for a booking made, and for a
change whose status, table, start, end or party differs from `Before` - the same rule the tracker applied against what
it had loaded - on the old table too for a move, and skipped where the save already names the booking on that table
(a block's signal 4, the table's own transition when it is seated or freed); seating and completing raise nothing of
their own, since `TableSeated` and `TableFreed` name the booking. The `approvals` push (`ApprovalStaffPush`) keeps its
key, `staff-push:approval:{id}:{n}` with a fresh `{n}` (`StaffPushSignals.Again`), and its rule: waiting now, and made so,
moved into waiting, moved in time or waiting for another reason. The held pre-order of a booking that ended
(`EndedBookingPreOrder`) is voided in the dispatch, so its `HeldTicketVoided` is handled in the next round instead of by a
second dispatch from the hook.

**Signal 6 stays a writer after dispatch.** The pre-orders' `PreOrderRailSignal` still only names bookings, and the save
writes their rows once every event is handled. Written per event, a signal 6 row would land before a signal 5 row of the
same save whenever a pre-order event is raised before the booking's - and a move raises `OnMyWayAnswersForgotten` before
its `ReservationRescheduled` - while tablets read the stream in sequence (`PreOrderSideEffectsSnapshotTests` holds that
order). Signal 5 no longer counts a signal 6 row as its own, so the order is the only reason, and it is enough.

**What stayed in the services**: who may act, the gates and refusals in their order, the replays (`ProcessedCommands`,
the client command id on the row), the locks - the table's, the account's, the previous table's for a declined change
(`ReservationWriter`) - the table's evening and the questions to the guest before (`TableShares` decides, accepts,
declines, withdraws and re-decides; it no longer tells anyone), the pre-order following a move, "not coming" in the
booking's thread (`GuestThreads.AnswerAsync`, the messages' event), the platform's audit of a lift, the hold given back
through the table state machine after a release, and:

- **A new booking's reminders**, written just after the insert commits, in a transaction of their own under the
  account's lock (`ReservationReminders.ScheduleRemindersAsync`): a web booking's reminder carries the manage link, whose
  plaintext only the creation holds and no event should carry. Replacing and withdrawing them are handlers'.
- **The no-show block's judgement.** `NoShowBlocks.BlockIfDueAsync` still runs in the services - after a booking's
  no-show (`ReservationRelease`) and a takeaway order's (`TakeawayService.MarkNoShowAsync`) - and cannot be a handler: it
  counts the guest's no-shows with a query that must read this one as saved, under the account's lock in its own
  transaction (handlers run before the save), and the holds of the bookings it cancels are given back through the table
  state machine after the commit (`ReleaseHoldsOfAsync`), where no handler runs. What the block writes is the events':
  each cancelled booking's `ReservationCancelled` (the system's History line, its reminders, signal 4, its held
  pre-order) and the block's `BookingBlockImposed`.

Every booking save goes through `ReservationWriter.SaveAsync`, which dispatches with the writer's dispatcher before
saving, as the table state machine and the ledger do: in a request it is the context's own, and a test composing the
booking services over a bare context (`SqlServerFixture.CreateReservationService`, `CreateApprovalExpirySweep`) runs
production's handlers for its caller. Test seeders that build a booking's history straight to the rows call
`DequeueEvents()` after the domain call; a booking built with `Create` and never `Book`ed raises nothing.

**Proof it is the same.** `BookingSideEffectsSnapshotTests`, committed on the code before the move: an evening at one
branch from 15:00 - made from the app (and its replay), the public page and the staff channel, eight tonight waiting for
the tablet; approved, declined with a reason (a second answer refused), expired by the sweep (a late yes refused); a
later booking asking the guest before in the app, a yes (and its replay) and a no, one the floor asks and says yes to,
one settled when the guest before cancels, one crowded out when the guest before moves; a party made smaller (and its
replay); confirmed bookings moved back to waiting - declined back to the slot they had, approved, declined with the slot
taken since, expired back by the sweep - a waiting one moved again, a move from the link (and its replay), a declined
one's move refused; cancelled in time, from the link (twice), late with the kitchen holding its pre-order (twice); let go
by the venue; "running late", "are you coming?", "on my way"; a party seated and freed; a hold extended (its replay and a
second refused); a no-show and its replay; a third no-show that blocks the guest and cancels their next booking,
withdrawn so the block is lifted (twice); another block lifted by the platform. Every activity entry, outbox row (the
`approvals` staff pushes among them), feed row, change-stream row (signal 5 and 4 among them), the platform's audit,
and the end state of the bookings, table requests, blocks, the pre-order and the threads' messages are unchanged byte
for byte (`Integration/Snapshots/BookingSideEffects.approved.txt`); the events are unit-tested in
`ReservationEventTests`.

**The one deliberate change**, in its own commit: a booking a test seeds straight to the rows (`Reservation.Create` on a
bare context, never booked) no longer gets a signal 5 row - the hook wrote one for any added booking, the events only
for one that was made. Production makes bookings only through `ReservationCreation`. The tables' and the pre-orders'
snapshots seed such bookings and lose exactly those rows (five and one); nothing else in them moved.

**Known, recorded, not changed.** A booking's `decisionSeconds` is measured from when it was made even for a change of a
confirmed booking (the slot it had is forgotten before the line is written, as before). Signal 5 for "running late" is
written on every answer, where the tracker skipped a second answer with the same instant. A no-show judged by
`NoShowBlocks` for a takeaway order still runs from `TakeawayService`, with the bookings' events doing its writing.

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
  service the fixture composes (`StaffPushTests.An_order_up_goes_to_the_tables_own_waiter_and_not_to_every_phone`). A
  service that saves on the context itself, not through the ledger, gets `fixture.CreateContextWithHandlers(clock,
  actor)`: a context whose own saves dispatch to production's handlers, as that caller.
- The rules themselves: `tests/Yalla.UnitTests/Architecture/` (`DomainEventRulesTests`, `HandWrittenSideEffectTests`,
  `SaveChangesHookTests`; `AGENTS.md`, "Architecture tests") hold sections 2 to 5 to the compiled code - sealed event
  records, `internal sealed` handlers registered by `AddDomainEventHandlers`, no handler saving, no service taking
  `IOutbox`/`IActivityLog` outside the allow-list, no tracker hook in the save. An allow-list entry needs a comment with
  its reason.

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
| **Settings, menu, staff, platform, reviews** | One activity call (or a few) per path | One path per change already: an event buys little. Move only when a second path appears | as needed |
