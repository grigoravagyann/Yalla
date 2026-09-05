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

Four things worth stating outright, because each is a decision:

- **The service charge is computed on the post-discount subtotal.** A manager who comps a dish comps
  its service charge with it. Charging service on a dish you have just apologised for is the opposite
  of an apology.
- **A voided line counts zero and stays on the bill**, labelled as removed by staff with its reason.
  Nothing silently disappears from a bill somebody is watching on their phone: a line that vanishes
  reads as the venue editing the bill, and the guest cannot tell it apart from one.
- **An adjustment can never take off more than it applies to.** A 2,000 AMD comp on a 1,500 AMD dish
  takes off 1,500. Without the clamp a generous manager drives the subtotal negative and every number
  downstream becomes arithmetic nobody can explain.
- **Tips are outside the balance entirely.** `Payment.TipAmd` is excluded from `paid` and
  `remaining`. A 10,000 AMD bill settled with 12,000 AMD is not overpaid by 2,000, and folding the
  tip in makes every subsequent number — the balance, each share, whether the tab may close —
  incomprehensible to everyone including us.

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

## 6. Concurrency

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

## 7. What is deliberately not here

- **Any payment provider.** Idram, Telcell and cards are the next task; the reserve is the seam they
  plug into.
- **Refunds.** A void is refused once a tab has been paid against, precisely because reversing money
  that has changed hands is a different operation with its own audit and its own conversation with a
  manager.
- **Fiscal receipts**, as above.
- **Per-participant payment allocation.** `Payment.TabParticipantId` records who handed the money
  over, and each share reports what that person has settled — but a share is a **pre-payment**
  allocation of the total, which is what makes the sum invariant hold. Netting payments off shares is
  part of the multi-payer flow, not of this arithmetic.

---

## 8. Golden vectors, for the other implementation

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
