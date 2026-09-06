# Reports — what each one answers, and what is deliberately absent

Everything these read was already being logged and none of it was queryable. `TableSession` has
every occupancy with its source, party size and duration. `Reservation` has every booking and its
outcome. `Tab`, `TabOrder` and `TabOrderLine` have the money and the items.

This is the answer to "what am I paying for" in month three.

`GET /api/branches/{id}/reports/...`, `ManagerOrAbove` within scope.

---

## 1. Local dates, not UTC dates

**Every range is in the branch's own local dates**, converted with its `TimeZoneId`.

"Yesterday's covers" is a statement in the venue's clock. A report that used UTC midnight as the day
boundary would be wrong by four hours in Yerevan — every day, quietly, and in the direction that
moves the *late* sittings into the wrong day, which is exactly where a restaurant's interesting
numbers are.

The worked example, from `ReportTests`. Tuesday the 8th in Yerevan runs from 20:00 UTC on the 7th to
20:00 UTC on the 8th:

| Sitting | UTC instant | In a local Tuesday report? | In a UTC Tuesday report? |
| --- | --- | --- | --- |
| 00:30 local Tuesday | 20:30 on the **7th** | yes | **no** |
| 23:30 local Tuesday | 19:30 on the 8th | yes | yes |
| 01:30 local Wednesday | 21:30 on the 8th | **no** | **yes** |

Two errors, in opposite directions, on one ordinary Tuesday.

The conversion lives in `BranchTime` and nowhere else. A local day is half-open — from its own
midnight to the *next* day's midnight, exclusive — because there is no last instant of a day to be
inclusive of, and 23:59:59.999 leaves a millisecond of the evening unreported.

**A rollup uses the addressed branch's zone for the whole venue.** A chain with branches in two
zones would need a decision about which day boundary a rollup uses, and the one the caller named is
the only answer that is not arbitrary. Every Yalla venue is in Yerevan today, so the case is
theoretical — but a silent choice would not have stayed visible.

## 2. Turn time is a distribution

**The most useful number in the whole set**, and the reason it is not an average.

A cafe whose policy says 120 minutes and whose real median is 165 is refusing a 20:00 sitting
because it believes the 18:00 one ends at 20:00 — and half the time it does not. It is losing
bookings it has no other way to find out about. Nothing else in the product surfaces that.

An average hides it. A room with a fast lunch and a slow dinner averages to something plausible and
describes neither service. So the report returns the whole shape:

- `medianMinutes` and `p90Minutes`, nearest-rank — these are minutes somebody sat at a table, and an
  interpolated 137.4 implies a precision the underlying clock has not got.
- `buckets`, so the tail is visible rather than summarised away.
- `overPolicyFraction`, the single number to put beside the chart: the fraction of sittings that ran
  past what the policy assumes. **This is the one that says whether the policy needs changing.**

Sittings are counted in **every hour they span**, not only the one they started in. "How busy is the
room at eight" is a question about occupancy; counting arrivals makes a restaurant look empty at
exactly its busiest hour.

## 3. Web bookings without an app — the number that forces a decision

A diner who books from the public branch page has no app, so they have no push channel. The
reminder, the late nudge and one-tap cancel — **the entire no-show story from Prompt 9** — do not
reach them.

Two options were on the table. This ships **option 1**: web booking now, with an install prompt on
the confirmation screen framed as *get a reminder before your table*, plus an add-to-calendar link.
It costs nothing and converts some.

Option 2 is integrating SMS or Telegram. `INotificationChannel` has existed since Prompt 9 for
exactly this and the interface work is done; the open question was always commercial, not technical.

**`reservations.webBookingsWithoutAnApp` is what decides it.** It counts bookings where
`Reservation.Channel` is `Web` *and* the diner has no live `DinerDevice`. If that number is large,
the reminder is not reaching a meaningful share of bookings and a paid channel earns its cost. If it
is small, the install prompt is enough and nothing further is needed.

Two details worth knowing when reading it:

- **`Channel` is self-reported by the client**, because the server cannot tell an app's HTTPS request
  from a browser's. It is a metric and never a boundary: nothing is authorised on it and no booking
  is refused because of it, so a client that lies costs one wrong number.
- **The device check is live, not stored at booking time.** Somebody who installs the app a week
  later stops being counted as unreachable — which is the behaviour change the number exists to
  detect.

## 4. What is not built, and why

### No per-waiter leaderboard

The staff report is orders entered and tables turned, **aggregated per branch**.

The audit log has the per-person data. `TabOrder.PlacedByStaffId` is on every row and
`TableStateChange` names an actor for every transition, so building a ranking would be an afternoon's
work and an owner will ask for one.

It is not built because a ranked list of employees that renders itself every morning is a different
product from a report somebody requests. It changes how a shift feels to work whether or not anyone
acts on it, it rewards whoever keys in the orders rather than whoever runs the section, and the
software would be making that management decision on the venue's behalf without being asked to.

If a venue wants per-person numbers, they can be given a report. A ranking that arrives unbidden
every morning is a different thing, and the difference is the point.

### No rollup pipeline, yet

Everything is **queried live** against the operational tables, with indexes.

At pilot scale — twenty venues, a few thousand sittings a month — a range seek over an indexed
column answers in milliseconds, and a rollup pipeline would be a second copy of the truth that can
be wrong, be stale, or need backfilling after every schema change.

**When that stops being true:** when a single branch's month exceeds roughly 50,000 `TableSession`
rows or 500,000 `TabOrderLine` rows, or when the menu report — the slowest — passes about two
seconds at p95. Either is a long way past a pilot.

**What would be rolled up first**, in order:

1. **Daily revenue per branch** — one row per branch per local day, written when a tab closes. The
   revenue report becomes a seek over a table of hundreds of rows rather than thousands.
2. **Daily item counts** — one row per branch per item per day. This is what makes the menu report
   cheap, and it is the report that scans the most.
3. **Daily occupancy buckets** — sittings by hour, already computed in memory here.

Reservations and staff would stay live: those tables are small and stay small.

The thing to preserve if that day comes is the local-date boundary. A rollup keyed on UTC days would
bake the four-hour error into stored rows, where it is far harder to notice and much harder to undo.

### Indexes

Added for the range scans these reports do:

| Index | For |
| --- | --- |
| `IX_TableSessions_BranchId_SeatedAtUtc_Reporting` — includes `ClosedAtUtc`, `Source`, `PartySize` | Every occupancy report. The included columns are what turn-time and the walk-in split need, so the range seek answers without a lookup per row. |
| `IX_Reservations_BranchId_StartUtc_Status` | The reservation group-by. |
| `IX_Tabs_BranchId_ClosedAtUtc`, filtered to closed tabs | Revenue. An open tab has no revenue to report and no reason to be in the index. |
| `IX_TabOrders_PlacedAtUtc` | Orders are the bridge between a date range and the lines it contains. |
| `IX_TabOrderLines_MenuItemId_TabOrderId_Reporting` — includes `Quantity`, `UnitPriceAmdSnapshot`, `VoidedAtUtc` | The menu group-by, from the line side. |

`IReportQuery.GetMenuReportSql` returns the menu report's SQL, for the same reason the availability
query exposes its own: "is this still a range seek" is worth being able to answer without attaching
a profiler.

### The slowest report, measured

The menu report, over a seeded month of a busy branch — **750 sittings, 750 tabs, 2,250 orders,
6,750 order lines, 40 tables, 60 menu items** — on LocalDB:

| Pass | Time |
| --- | --- |
| 1 (cold) | 86 ms |
| 2 | 16 ms |
| 3 | 16 ms |

The plan, estimated total subtree cost **0.61**:

```
Compute Scalar             rows=60      cost=0.609
  Hash Match (aggregate)   rows=60      cost=0.609
    Hash Match (join)      rows=6750    cost=0.528
      Index Seek           rows=750     cost=0.009   Tabs.IX_Tabs_BranchId_Status
      Hash Match (join)    rows=6750    cost=0.433
        Clustered Index Scan rows=2250  cost=0.049   TabOrders.PK_TabOrders
        Compute Scalar     rows=6750    cost=0.239
          Nested Loops     rows=6750    cost=0.239
            Nested Loops   rows=60      cost=0.017
              Clustered Index Scan rows=60          MenuItems.PK_MenuItems
              Clustered Index Seek rows=1           MenuCategories.PK_MenuCategories
            Index Seek     rows=112.5   cost=0.190   TabOrderLines.IX_TabOrderLines_MenuItemId_TabOrderId_Reporting
```

Two things worth saying about it plainly.

**The new covering index is doing its job.** `TabOrderLines` is an *Index Seek* on
`IX_TabOrderLines_MenuItemId_TabOrderId_Reporting`, driven once per menu item, and the included
columns mean the aggregate never leaves the index — no key lookup per line. Without it this is the
step that would dominate.

**`IX_TabOrders_PlacedAtUtc` is not chosen at this size**, and that is the optimiser being right
rather than the index being useless. 2,250 orders is small enough that scanning the clustered index
(cost 0.049) beats seeking a nonclustered index and looking up `TabId` for every row it finds. The
index earns its place as the table grows — the crossover is somewhere in the tens of thousands of
orders — and it costs one small write per order until then. It is reported here rather than quietly
left in, because "we added an index" and "the query uses it" are different claims.

## 5. Comparison against the previous period

Every numeric report carries `{ value, previous, changeFraction }`.

"Covers last night" means nothing without "and the Friday before".

- **The previous period is the same number of days, ending the day before this one starts** — counted
  in days, not calendar months, so a range crossing a month boundary compares against exactly as
  many days as it contains rather than against a February.
- **`previous` of zero and no prior data are different things**, and `changeFraction` is `null` for
  both zero and absent. Growth from nothing has no percentage, and every client that has tried to
  render one has printed something absurd about a venue's first week.

## 6. CSV export

`?format=csv` on any report.

Local dates and whole dram, so the file matches what the venue counted and what the screen said — an
export in UTC would disagree with the report it was exported from, and the person comparing them is
holding a till receipt.

It carries a **UTF-8 byte-order mark**. Excel opens a BOM-less UTF-8 CSV in the system code page,
which turns every Armenian venue name into mojibake, and the venue names are the part somebody reads.

Fields follow RFC 4180: quoted when they contain a comma, a quote or a newline, with inner quotes
doubled. A menu item called `Khachapuri, Adjarian` is not hypothetical.

## 7. The range cap

`ReportRange.MaxDays` is **366** — long enough to compare this December with last, short enough that
the query stays a range seek.

Past it: HTTP 400, code `report-range-too-long`, with `requestedDays` and `maxDays` in `context`. A
range nobody meant to ask for is a table scan nobody meant to run, and a refusal naming the limit is
more use than a request that times out.
