# Billing — the arithmetic, and what it is not

Every amount in Yalla is computed on the server. The client never adds up prices; it displays what
it is given. This document is the arithmetic in full, because a bill three people are looking at on
three phones has to be explainable, and because the parts that get decided by accident — rounding,
the order of operations, who carries the odd dram — are exactly the parts that produce an argument
at the table.

---

## Nothing here is a fiscal receipt

**Say this plainly to every venue.** A payment recorded through Yalla is a record of what the venue
took, so the tab balances and the cash drawer reconciles. It is **not** a fiscal receipt, and the
venue's registered cash register still issues one for every transaction exactly as it did before.

Nothing in this module may be described to a venue, in a contract or in a sales conversation, as
fiscal compliance. That is a separate piece of work with its own legal weight, and the difference
between "we record your takings" and "we satisfy your obligations" is the difference between a
useful product and a liability.

---

## 1. Money is whole dram, and only whole dram

`long`, counting whole Armenian dram, on every field, suffixed `Amd`. The dram has no subunit in
practice, and a `decimal` or a `double` invites drift that surfaces as a bill three people cannot
settle. `@yalla/format` on the frontend rejects a fractional dram outright, which enforces the same
rule from the other end.

The only decimals in the system are the **percentages**: `Tab.ServiceChargePercentSnapshot` and
`TabAdjustment.Percent`. Both are turned into whole dram by `Money`, which is the only place rounding
happens.

### Rounding is half-up, and happens once

```
round(x) = floor(x + 0.5)      for x >= 0
```

Half-up because it is what a person does on paper and what a diner checking the arithmetic expects.
.NET's default is banker's rounding, which is right for statistics and surprising on a receipt.

**Once**, at the service-charge line, never per item. Rounding each item and summing gives a
different answer from summing and rounding, and only one of the two can match what the guest is
shown.

---

## 2. The tab total

```
lineGross     = UnitPriceAmdSnapshot × Quantity        (0 if the line is voided)
lineNet       = lineGross − adjustments on that line
grossSubtotal = Σ lineNet
subtotal      = grossSubtotal − adjustments on the whole tab
serviceCharge = round(subtotal × ServiceChargePercentSnapshot ÷ 100)
total         = subtotal + serviceCharge
remaining     = total − paid
```

Five things worth stating outright, because each is a decision:

- **The service charge is computed on the post-discount subtotal.** A manager who comps a dish comps
  its service charge with it. Charging service on a dish you have just apologised for is the opposite
  of an apology.
- **A voided line counts zero and stays on the bill**, labelled as removed by staff with its reason.
  Nothing silently disappears from a bill somebody is watching on their phone: a line that vanishes
  reads as the venue editing the bill, and the guest cannot tell it apart from one.
- **An adjustment can never take off more than it applies to.** A 2,000 AMD comp on a 1,500 AMD dish
  takes off 1,500. Without the clamp a generous manager drives the subtotal negative and every number
  downstream becomes arithmetic nobody can explain.
- **Adjustments come off in the order they were given** (`CreatedAtUtc`, then id): each of a line's off what
  is left of the line, each tab-wide one off what is left of the sum. 10 % then 200 off a 13,100 bill is 1,310 + 200;
  the other way round it is 200 + 1,290. Until 2026-10-25 the order was whatever the change tracker enumerated - a
  just-added adjustment first - so a bill could change on its next recompute. `TabBilling.Reductions` answers
  what each took off, for the revenue report and Staff → Results.
- **Tips are outside the balance entirely.** `Payment.TipAmd` is excluded from `paid` and
  `remaining`. A 10,000 AMD bill settled with 12,000 AMD is not overpaid by 2,000, and folding the
  tip in makes every subsequent number — the balance, each share, whether the tab may close —
  incomprehensible to everyone including us. Where tips *are* reported, see section 6.

### Voiding a whole order

**A line is on the bill until it is voided, and that is the only rule.** Every total reads it: the tab's cached
totals and `TabBilling.Compute`, the staff and diner views, `/shares`, the balance a cash payment is reserved against,
the diner's order history, the revenue and menu reports, Staff → Results, the History's timeline, and every CSV of
them. None of them looks at the order's status.

So voiding a whole order (`POST /api/orders/{id}/status` with `Voided`, from the rail or the floor) **voids every line
of it still on the bill** (`TabOrder.Void`): `VoidedAtUtc` the moment of the void, `VoidedByStaffId` whoever voided it,
`VoidReason` "The whole order was voided." A line voided on its own earlier keeps its own stamp and reason. The tab's
totals are written inline in the same save, as for a line void. The lines stay on the bill marked and at zero, like
any voided line.

Until 2026-10-25 the order's status moved and its lines did not, so a voided order was still charged in full - live QA
found a voided cappuccino on a bill of 3,762 - while the timeline and the Staff → Results split, which alone skipped
voided orders, said 0. Voiding the lines, rather than teaching the ledger a second rule, was chosen because every
reader already followed the line, and because a second rule would have re-priced bills that were already paid (below).
The timeline and the split no longer skip voided orders either; they follow the line like everything else.

- **The stream says it once.** One `OrderStatusChanged` to 5, as before - no `LineVoided` per line. Phones refetch on
  it and find the lines voided.
- **The History says it once.** One `order.voided` entry, its amount what the order came to on the bill. Staff →
  Results counts **each line** it took off as a void for whoever voided it, as `Voids` always counted lines.
- **Refused once money has changed hands.** On a tab with a `Reserved` or `Succeeded` payment, a void that would take
  something off the bill is `409 line-already-paid` (`LineAlreadyPaidException`), exactly as a line's void is: taking
  food off a bill somebody has paid against is a refund (section 8), and a void cannot leave the tab overpaid. An order
  whose every line was already voided can still be voided - nothing comes off. A manager who wants a paid-against
  guest not to pay for something comps it (an adjustment), which is recorded as what it is.

**Rows voided the old way** were put right by the `ActivityHistory` migration (`VoidedOrderLinesBackfillSql`): on
every tab **nobody has paid against**, a voided order's live lines are voided when its `OrderStatusChanged` event
says the order was, by the staff member it names, and the tab's totals cache is recomputed where it is the plain sum
(no standing adjustment; nothing paid). A tab with a standing adjustment is repaired by its next write or read of
`/shares` (`TabLedger.EnsureTotalsFreshAsync`) - a payment always reserves against the lines, never the cache.

**A tab already paid against keeps the bill it was paid on.** Its voided order's lines are left on it, charged, and
every reader shows them charged, consistently - the bill, the reports and the timeline all agree with the money that
changed hands. Taking them off now would silently turn a settled bill into an overpaid one; if the guest is owed
something, that is a refund, with a manager, outside this arithmetic.

### VAT is not a line

Armenian menu prices are VAT-inclusive and `Branch.PricesIncludeVat` is true by default, so the
displayed price is the price and no decomposition appears anywhere on the bill.

> **Note for the fiscal module.** It *will* need the VAT decomposition — a fiscal receipt itemises the
> tax even when the menu does not. That is a receipt concern, not a bill concern, and it is recorded
> here so nobody has to rediscover it: the rate is not stored per item today, and adding it is part
> of that task rather than a gap in this one.

---

## 3. The stored totals are a cache

`Tab.SubtotalAmd`, `ServiceChargeAmd`, `TotalAmd`, `PaidAmd` and `RemainingAmd` are **denormalised**,
exactly like `DiningTable.Status`. The authoritative total is, and remains, the sum of the lines.

They exist because the floor screen lists thirty tabs and cannot recompute each one. They are written
by `TabLedger` inside the **same `SaveChanges`** as the mutation that changed them, so there is no
state in which the wine was ordered and the total does not include it.

`OrderingTests.Recomputing_a_complex_tab_from_its_lines_equals_the_cached_totals` builds a tab with
orders from three surfaces, a void, a shared line and a percentage discount, then recomputes it from
the rows in a context that has never seen the cache written. If a future mutation forgets to
recompute, that is the test that fails.

---

## 4. Who owes what

For each participant, in join order with the **host first**:

```
ownItems    = Σ lineNet of their own unshared lines
sharedItems = for each shared or table-attributed line, an integer split
              across the participants snapshotted on that line
absorbed    = the share of anyone removed from the tab (host only)
personal    = (ownItems + sharedItems + absorbed), reduced pro rata by any tab-wide discount
share       = personal + (their pro-rata slice of serviceCharge)
```

### The rules that decide whether this works

- **Every split is integer and the shares sum to the total, to the dram.** An off-by-one is not a
  rounding curiosity: the shares add up to less than the total, the last person to pay is short a
  dram, the tab will not close, and a waiter sorts it out by hand while the table watches.
- **Service charge splits pro rata.** Order 30% of the food, pay 30% of the service.
- **Shared lines snapshot who was there.** A friend who joins for dessert is not on the starters, and
  one who leaves early still owes for what they ate. This is why `TabOrderLineShare` rows are written
  at order time rather than the split being computed off the current roster.
- **Table-attributed lines split like shared ones.** When a waiter keys in a spoken order and cannot
  say who asked for it, the line belongs to the table. `IsTableAttributed` keeps that distinguishable
  from a deliberate share: identical arithmetic, completely different facts, and a venue whose bills
  are full of table-attributed lines has a training problem its own numbers should show it.
- **A removed participant's items fall to the host** and are reported in `absorbedFromRemovedAmd`
  rather than folded in silently. They stay on the bill — the food was eaten — and nobody else can
  pay for them. It is logged.

### Remainders, and why the obvious rule is wrong

Splitting 1,000 AMD three ways gives 334 + 333 + 333, not 333 three times. The extra dram goes to the
**host**: somebody has to carry it, and the host is the one person who has agreed to be responsible
for the tab.

For the pro-rata splits — the tab-wide discount and the service charge — the rule is the
**largest-remainder method**: everybody gets the whole-dram floor of their exact slice, and the few
dram left over go one each to whoever was cut by the most, ties broken by position so the host is
first.

> The obvious alternative — round every slice and give the residue to the host — is wrong in a way
> that only shows up on a real bill. Half-up rounding can hand out several dram *more* than there are
> to give, and subtracting that from a host who ordered one coffee produces a **negative share**. A
> diner shown "you owe −3 AMD" has found a bug, whatever the totals say.

### Ordering is join order, not id order

After the host, participants are ordered by when they joined. Sorting by `ParticipantId` would look
equivalent and is not: those are UUIDv7 values whose leading bytes are all the same millisecond, so
the comparison falls through to random bits and the odd dram lands on a different guest each time the
same bill is computed. Join order is both deterministic and explainable to a table.

---

## 5. A worked example, to the dram

Three people at table 3. The branch charges **10%** service.

| Who | What | Amount |
| --- | --- | ---: |
| Aram (host) | 2 × flat white @ 3,200 | 6,400 |
| Nune | khachapuri | 4,500 |
| Vahe | salad | 2,800 |
| the table | one bottle of Areni, **shared** | 9,500 |

### The bill

```
grossSubtotal = 6,400 + 4,500 + 2,800 + 9,500 = 23,200
subtotal      = 23,200                            (no adjustments)
serviceCharge = round(23,200 × 10 ÷ 100)          = 2,320
total         = 23,200 + 2,320                    = 25,520
```

### The bottle

9,500 three ways: `9,500 ÷ 3 = 3,166` each, remainder `9,500 − 9,498 = 2`, and the remainder goes to
the host.

| | Aram | Nune | Vahe |
| --- | ---: | ---: | ---: |
| share of the bottle | **3,168** | 3,166 | 3,166 |

### Personal totals

| | Aram | Nune | Vahe | sum |
| --- | ---: | ---: | ---: | ---: |
| own items | 6,400 | 4,500 | 2,800 | 13,700 |
| shared | 3,168 | 3,166 | 3,166 | 9,500 |
| **personal** | **9,568** | **7,666** | **5,966** | **23,200** |

### Service charge, pro rata

Exact slices are `2,320 × personal ÷ 23,200`:

| | exact | floor | fraction |
| --- | ---: | ---: | ---: |
| Aram | 956.8 | 956 | .8 |
| Nune | 766.6 | 766 | .6 |
| Vahe | 596.6 | 596 | .6 |
| | | **2,318** | |

Two dram are left over. They go to the two largest fractions — Aram (.8), then Nune (.6, ahead of
Vahe on position):

| | Aram | Nune | Vahe | sum |
| --- | ---: | ---: | ---: | ---: |
| service | **957** | **767** | 596 | **2,320** |

### What each person owes

| | Aram | Nune | Vahe | sum |
| --- | ---: | ---: | ---: | ---: |
| personal | 9,568 | 7,666 | 5,966 | 23,200 |
| service | 957 | 767 | 596 | 2,320 |
| **share** | **10,525** | **8,433** | **6,562** | **25,520** |

**10,525 + 8,433 + 6,562 = 25,520**, which is the total exactly.

> This example is a test. `TabBillingTests.The_documented_three_person_example_comes_out_to_the_dram`
> asserts every number in the tables above. If this document and that test ever disagree, one of them
> is lying to a venue.

### And the property test

`TabBillingTests.Shares_always_sum_exactly_to_the_total` generates three thousand random tabs — any
number of participants, shared and unshared lines, voids, percentage and flat adjustments, any
service-charge percent — and asserts on every one that the shares sum to the total and that nobody is
handed a negative share. That test is worth more than any amount of review here, because the bug it
looks for is invisible in every example a person writes by hand.

---

## 6. Tips, and the bill a diner asks for

### The tab's tips

`tipsAmd` is the sum of `Payment.TipAmd` over the tab's **succeeded** payments. A wallet hold still
sitting in `Reserved`, or one that `Failed`, put nothing in anybody's hand, so its tip is not counted.

- **`TabView.tipsAmd`** goes where the table total goes: present when `tableTotalVisible` is true,
  **absent** otherwise — a tip is money the table handed over, and a guest the host has hidden the
  total from does not see the table's tips either, even their own.
- **`TabStaffView.tipsAmd`** is always present. It is what the drawer holds beyond the bill.
- **Neither is inside `TabTotalsView`.** `tableTotal.paidAmd` and `remainingAmd` never include a tip;
  `tipsAmd` sits beside them.

Summed in the same statement that reads the tab (`TabQuery`), not cached on the `Tab` row: the five
cached totals are the balance, and a tip is not part of the balance.

### Asking for the bill from the pay sheet

`POST /api/tabs/{tabId}/service-requests` with `preset: 3` (TheBill) may say **whose part** and **what
tip**: `billScope` (`1` WholeBill, `2` MyShare) and `tipPercent` (0 to 25). The server prices the
request from the tab's own lines — `TabLedger.Compute`, the same bill `/shares` and the cash endpoint
read — and stores the price with it (`ServiceRequest.BillScope`, `BillAmountAmd`, `TipPercent`,
`TipAmd`; `Domain.Billing.BillQuote`). The waiter walks over with the number the diner was shown.

**The diner's pay sheet computes the same figure from the same rules**, so these are the rules both
sides implement, to the dram:

```
percentOf(a, p)    = a × p ÷ 100, rounded half-up (away from zero) to whole dram    Money.PercentOf
proRata(a, n, d)   = a × n ÷ d,   rounded half-up; 0 when d ≤ 0, n ≤ 0 or a = 0   Money.ProRata

WholeBill  (only for somebody who can see the table total, else 403)
  amount  = tab remaining
  base    = proRata(subtotal, amount, tab total)

MyShare
  owed    = share − paid                     (the requester's row of the shares view)
  amount  = canSeeTableTotal ? min(owed, tab remaining) : owed
  base    = proRata(personal, amount, share)  (0 when share ≤ 0)

tip = percentOf(base, tip%)          amount ≤ 0 → 422 on billScope
```

- **The service charge is inside `billAmountAmd`; the tip is outside it**, for the reason above.
- **The tip is on the unpaid part of the items.** The items the scope covers — the subtotal before
  service for the whole bill, the asker's `personalAmd` for their share — in the proportion the amount
  bears to what those items come to with service. A tip on the service charge is a tip on a tip, and a
  tip on a coffee somebody already settled is a tip paid twice. **With nothing paid the base is the
  items exactly** (`proRata(x, t, t) = x`), so the first request of an evening is a plain percentage of
  the subtotal or of `personalAmd`.
- **MyShare is capped at what the tab owes — for somebody who can see the total.** Cash a waiter took
  against the table without naming anybody lowers the balance without lowering any share, and the cash
  keypad would refuse the excess. A guest the host hid the total from is quoted their own share
  uncapped: the cap would quote them the table's balance whenever it was the smaller figure, and that
  balance is what the projection keeps from them. The keypad still refuses more than the tab owes when
  the waiter takes the cash.
- **Nothing to pay is refused.** A scope that leaves 0 — a share already settled, a closed tab — is a
  422 `validation-failed` on `billScope`, rather than a waiter sent across the room for nothing.
- **WholeBill needs sight of the total.** A guest the host hid the total from would otherwise read it
  back off their own request, so they get 403 and may ask for their own share.
- **The shape is checked first, every field at once** (`ServiceRequest.ValidateBillFields`): either
  field on another preset, a tip with no scope, an undefined scope or a tip outside 0 to 25 is a 422
  naming each field.
- A plain `{ "preset": 3 }` is still a request for the bill, with the four fields absent.
- **Asking again replaces the earlier ask.** A request with a `billScope` supersedes the same
  participant's own open request(s) for the bill on that tab — scoped or plain — by stamping
  `ServiceRequest.SupersededAtUtc` (`ServiceRequest.Supersede`) in the same save. A superseded request
  is not open: it leaves the waiter's list and cannot be acknowledged, though nobody acknowledged it
  either. A tablemate's request is theirs and stays; a plain "the bill, please" replaces nothing.

Worked against the three-person example in section 5 (subtotal 23,200, total 25,520, shares 10,525 /
8,433 / 6,562 on personal parts of 9,568 / 7,666 / 5,966):

| Request | `billAmountAmd` | `tipAmd` |
| --- | ---: | ---: |
| Guest A, WholeBill, 10% | 25,520 | 2,320 |
| Guest A, MyShare, 5% | 8,433 | 383 (383.3) |
| Guest B, MyShare, 15% | 6,562 | 895 (894.9) |
| Guest A, MyShare, 10%, after paying 3,000 | 5,433 | 494 (base 4,939) |
| Host, WholeBill, 10%, after guest A paid 3,000 | 22,520 | 2,047 (base 20,473) |
| Guest B, MyShare, 15%, after paying 2,000 and 5,000 paid against the table | 4,562 | 622 (base 4,148) |
| Host, MyShare, 10%, sees the total, after 20,000 paid against the table | 5,520 (capped) | 502 (base 5,018) |
| Guest A, MyShare, 10%, total hidden, after 20,000 paid against the table | 8,433 (not capped) | 767 |

`BillRequestTests` asserts every row, and that with nothing paid the tip is `percentOf` the items
exactly for every participant, scope and tip from 0 to 25.

**Who sees the price.** The response to the asker and the waiter's list
(`GET /api/branches/{branchId}/service-requests`) carry all four fields, so the staff tablet can open
the cash keypad prefilled with the amount and the tip. **The `ServiceRequested` event does not carry
the amounts** — its payload is `serviceRequestId`, `preset`, `note`, `requestedByParticipantId`,
`billScope` and `tipPercent`, nothing else (`TabEventContractTests` pins the member names). Every
phone at the table reads the stream, including a guest the total is hidden from, and somebody else's
priced request is their share or the table's balance.

`CK_ServiceRequests_Bill` holds the columns together in the database: none at all, or preset 3 with
`BillScope IN (1, 2)` and a positive amount, the tip a percentage from 0 to 25 and a non-negative
amount, or neither. Every comparison carries its own `IS NOT NULL`, because a CHECK passes on UNKNOWN
as well as on TRUE and `[BillAmountAmd] > 0` alone would let a missing amount in.
`SupersededAtUtc` is outside it. `PaymentAndServiceTests` writes the bad rows with raw SQL and expects
each to be refused.

---

## 7. Concurrency

Two rules that point in opposite directions, which is the whole of the subtlety.

### Orders retry. This is the one place in Yalla where retrying is correct

Prompt 2 forbade automatic retry, and was right about the table state machine: a retried "seat this
table" seats a party at a table that went while the request was in flight, and the second attempt is a
different, wrong action.

**Order placement is the opposite case.** Two participants tapping "add" at the same moment both
legitimately succeed — they insert different rows and the operations commute. The only thing that
collides is the totals cache on the shared `Tab` row. So `TabLedger.SaveWithTotalsAsync` catches the
row-version clash, reloads, recomputes from what is now there, and tries again, up to three times with
a short backoff. Refusing one of the two would be the bug: a diner is told their order failed when
nothing was wrong with it.

`OrderingTests.Two_participants_ordering_at_the_same_moment_both_succeed_and_the_total_is_right` is
the test that fails if somebody applies the no-retry rule here.

### Payments do not retry

A concurrency failure on a payment is reported to the caller, with the current balance. Reserving
twice against the same remaining dram is precisely the failure the reserve exists to prevent, and a
retry would do exactly that against a balance that has already moved.

### The reserve

```
BEGIN
    read the tab under its RowVersion
    recompute remaining FROM THE LINES, not from the cached column
    refuse if the amount exceeds it, carrying the real balance in the refusal
    insert the payment as Reserved
    mark it Succeeded            (cash only - the money is already on the table)
    recompute and store the totals
    close the tab if remaining reaches zero
COMMIT
```

The two-step `Reserved → Succeeded` exists so Idram and Telcell can sit in `Reserved` while the
provider is called. It is built now, with one rail, because it is far easier to get right with one
than with three.

**Closing the tab closes its `TableSession` and does not free the table.** Physical state and
financial state are independent: a party that has paid usually sits on for another twenty minutes, and
a floor plan that frees their table the moment the bill settles is lying to whoever is seating the
next walk-in. Freeing stays an explicit waiter action.

---

## 8. What is deliberately not here

- **Any payment provider.** Idram, Telcell and cards are the next task; the reserve is the seam they
  plug into.
- **Refunds.** A void - of a line or of a whole order - is refused once a tab has been paid against,
  precisely because reversing money that has changed hands is a different operation with its own audit
  and its own conversation with a manager. So a void can never leave a tab overpaid.
- **Fiscal receipts**, as above.
- **Per-participant payment allocation.** `Payment.TabParticipantId` records who handed the money
  over, and each share reports what that person has settled — but a share is a **pre-payment**
  allocation of the total, which is what makes the sum invariant hold. Netting payments off shares is
  part of the multi-payer flow, not of this arithmetic.

---

## 9. Golden vectors, for the other implementation

The mobile app carries its own port of `TabBilling.Compute`, so it can show an offline bill that
matches the one the server will send. A property test on that port proves it is **self-consistent**,
which is not the same as proving it agrees with us: two implementations can both be internally
coherent, both green, and quietly disagree about what three people owe.

So the side that owns the arithmetic publishes its answers.

**`docs/billing-vectors.json`** holds fourteen worked cases — input tab, expected subtotal, service
charge, total, and every participant's share — produced by `TabBilling.Compute` itself and committed.
Test the client against it.

```json
{
  "name": "residue-does-not-divide-by-three",
  "input":    { "serviceChargePercent": 0, "lines": [ ... ], "participants": [ ... ] },
  "expected": { "subtotalAmd": 1000, "serviceChargeAmd": 0, "totalAmd": 1000,
                "shares": [ { "shareAmd": 334 }, { "shareAmd": 333 }, { "shareAmd": 333 } ] }
}
```

The cases are chosen for the disagreements a re-implementation actually has — where rounding lands,
whether comping a dish comps its service charge, what happens to a removed guest's food, whether a
pending joiner is on the bill. Even splits of round numbers agree by accident and prove nothing.

**The file is generated, never hand-edited.** `GoldenBillingVectorTests` compares it against what the
arithmetic produces today and fails, naming the vector that moved, if they disagree. Regenerating is
deliberate:

```
YALLA_WRITE_BILLING_VECTORS=1 dotnet test --filter GoldenBillingVectorTests
```

Then read the diff, and tell whoever ships the client — their bill has just started disagreeing with
the server, and the failing test is the only warning either side gets.
