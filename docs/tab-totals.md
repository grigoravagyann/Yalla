# Tab totals — which commands write the tab row, and why ordering stopped

The money columns on `Tab` — `SubtotalAmd`, `ServiceChargeAmd`, `TotalAmd`, `PaidAmd`,
`RemainingAmd` — are a **cache**. The authoritative total is, and remains, the sum of the order
lines. `docs/billing.md` says so and `TabBilling.Compute` is the only thing that decides it.

They exist because the floor screen lists thirty tabs and cannot recompute each one. They are a
denormalisation exactly like `DiningTable.Status`, and they are not the truth.

This document is about a consequence of that which was not being taken seriously enough.

---

## 1. The failure

`TabLedger.TotalsRetryAttempts` was 3.

Placing an order wrote the order, its lines, the `TabEvent` **and the tab's totals cache** in one
transaction. The tab row carries a `rowversion`, so every concurrent order on one tab contended for
that single row. Three attempts absorbed a couple of writers. Six genuinely simultaneous orders
exhausted them and surfaced a `concurrent-update` error **to a diner**, for adding a coffee.

It reproduced reliably, and it is the worst-looking failure in the product: the fault is invisible
to the person it happens to, so the app simply appears broken. A diner who is told their coffee
could not be added does not retry — they put the phone down and wave at a waiter, which is the
exact behaviour the whole product exists to remove.

**Raising the number would have moved the ceiling rather than removed it.** Ten phones at one table
is a real table, not a stress test.

## 2. The fix

The order-insert transaction does not need to write the cache in order to be correct, because the
cache is not the truth. So it stopped.

- **Insert the order, its lines and the `TabEvent` in one transaction.** Nothing touches the tab
  row, so its `rowversion` is not taken and cannot be lost.
- **Recompute the cache immediately afterwards, in its own short transaction.**
- **Repair it on read if it is stale.**

Concurrent orders now write nothing in common, so they cannot collide. Test 9 in
`OrderingTests` races ten of them and expects ten successes; it used to race two, which was the
number the old design could survive.

### Why the recompute converges

The recompute *does* take the row version, and that is what makes it correct rather than what makes
it fragile.

Suppose one writer computes a total from nine orders while another commits the tenth. The first
loses the version check, reloads the tab **and re-reads its orders**, recomputes against ten, and
wins. Because a recomputation derives the whole answer from the lines rather than adding to what is
already there, repeating it is free and losing it costs nothing.

The last recompute to commit is therefore always one that saw everything: to commit at all its
version had to still match, which means no other recompute landed since it read — and if an order
was inserted since it read, that order's own writer runs a later recompute.

### Why it never throws

`RecomputeTotalsAsync` logs and gives up rather than raising. By the time it runs, the diner's order
is **already committed and already correct**. Telling them it failed would be a lie about work that
succeeded, and it is a lie about a cache. The next read repairs it.

## 3. Which commands write the tab row

This is the table to check before adding a new mutation.

| Command | Writes the tab row? | Why |
| --- | --- | --- |
| Place an order | **No** | The contended path. Insert only; cache refreshed afterwards in its own transaction. |
| Recompute totals (internal, after an order) | Yes | Its own short transaction. Retries on a lost version; never throws. |
| Void a line | Yes, inline | A manager on one tablet looking at one tab. Contention here is not a real phenomenon, and answering with the new total in the same breath is worth more than avoiding a collision that does not happen. |
| Add or void an adjustment | Yes, inline | Same. |
| **Record a cash payment** | **Yes, inline, and never retried** | See below. |
| Abandon a tab | Yes, inline | Manager action, one at a time. |
| Move an order along the kitchen rail | No | The rail does not touch money. It still goes through the ledger, because it appends an event and the numbering is what the stream rests on. |
| Read the split (`GET /shares`) | Only if the cache is stale | The read-path repair. Writes nothing when the numbers already agree, which is the overwhelmingly common case. |

### Payments are the exception, deliberately

**Nothing about payments changed, and nothing about them should.**

The atomic reserve against `RemainingAmd` takes the tab row under its `rowversion` and **does not
retry**. That is the one place in this system where a shared-row write is the entire point: two
people tapping Pay at once must not both reserve the same remaining dram. A retry there would
reserve a second time against a balance that has already moved, which is precisely the double-charge
the reserve exists to prevent.

So a payment that loses the race is reported to the caller — as `payment-exceeds-remaining` carrying
the *current* balance, because the waiter is standing at the table holding notes and needs the
number. Test 12 pins this: two concurrent cash payments resolve to exactly one success and one
refusal.

The asymmetry is the whole design, and it is worth stating plainly:

- **Two orders commute.** They insert different rows. Refusing one is a bug.
- **Two payments do not.** They both draw down one balance. Allowing both is a bug.

## 4. The event stream's own race

Orders no longer contend on the tab row, but ten simultaneous writers still contend for a place in
the tab's event stream: `TabEvent.Sequence` is a per-tab counter assigned as `MAX + 1` under a
unique index on `(TabId, Sequence)`.

That retry is a **renumbering**, not a re-application — nothing else in the unit of work is
touched — which is why it is safe even on the payment path. `SequenceRetryAttempts` went from 5 to
10 and gained a jittered backoff, for the plain reason that the number has to cover the worst case
rather than the common one: with ten writers reaching for the same position, the last to win has
lost nine times, and ten writers that all re-read the maximum on the same tick simply collide again
in the same order.

## 5. The invariant that guards all of this

`OrderingTests.Recomputing_a_complex_tab_from_its_lines_equals_the_cached_totals` builds a tab out
of orders, a void, and a percentage discount — a mix of the paths that write the cache inline and
the paths that refresh it afterwards — and asserts that recomputing from the rows in a context that
has never seen the cache written gives the same five numbers.

It matters **more** since the split, not less. If a future mutation forgets to refresh the cache,
that test is what says so.
