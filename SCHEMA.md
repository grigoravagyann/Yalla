# Yalla — database schema

Table reservation and in-app ordering for restaurants and cafes, launching in Yerevan. Three
clients consume this API: the diner app, the staff tablet, and the owner's admin panel.

Two facts shape everything below:

- **Ordering is never gated behind a reservation.** A QR code on the table opens a tab for
  anyone — booked, phoned ahead, or walked in off the street. In a cafe, walk-ins are most of the
  traffic. So the booking flow and the walk-in flow converge on shared occupancy and tab
  entities instead of running as two parallel worlds.
- **Billing is per branch, not per brand.** A chain with four locations is four paying customers,
  so every table, booking, tab and payment carries a `BranchId` from day one.

## Conventions

| Concern | Decision |
| --- | --- |
| Primary keys | `Guid` generated in application code with `Guid.CreateVersion7()`, mapped `ValueGeneratedNever()`. UUIDv7 sorts by creation time, so the clustered index stays append-ordered; a random Guid clustered key fragments SQL Server badly. |
| Money | `long` count of whole Armenian dram, every property suffixed `Amd`. The dram has no subunit in practice, and decimal or floating-point money invites rounding drift across a split bill. |
| Instants | UTC `datetime2`, applied as a model-wide convention so no configuration class can forget it. |
| Wall-clock values | `DateOnly` / `TimeOnly` (`date` / `time`), stored *alongside* the UTC instant and never converted to it. Opening hours and the local date and time on a booking must not move when a time zone setting changes. `Branch.TimeZoneId` holds the IANA zone (`Asia/Yerevan`). |
| Enums | `int`. Strings would let a member rename orphan existing rows. |
| Mapping | One `IEntityTypeConfiguration<T>` per entity in `Yalla.Infrastructure`, applied by `ApplyConfigurationsFromAssembly`. No data annotations, no fluent configuration inline in `OnModelCreating`, and no EF Core dependency in `Yalla.Domain`. |
| Deletes | `Restrict` everywhere except four genuinely dependent child collections. Historical financial and occupancy records must not disappear because someone deactivated a branch. |

## Aggregates

### Venue

A brand. Owns branches and the staff accounts that work across them. Nothing is operated or
billed here: the venue exists so a chain has one identity, one slug, and one place for
`StaffMember` rows whose `BranchId` is null (an owner works everywhere). `Slug` is unique.

### Branch

One physical location, and the operational and commercial unit of the whole system: a floor plan
canvas, opening hours, a menu, a reservation policy, a time zone. `(VenueId, Slug)` is unique.
**`ReservationPolicy`** is an owned entity stored as extra columns on the Branches row — every
reservation rule (turn time, buffer, grace, lead time, booking window, cancellation deadline,
auto-confirm, service charge, VAT, seat overhang, approval threshold) is a per-branch setting
with a shipped default, never a constant in code. "How long do we hold a table?" has a different
answer in a breakfast cafe and a tasting-menu restaurant, and the only default that varies by
venue type is the turn time: 120 minutes for a cafe, 90 for a restaurant.

### OpeningHours

When a branch is open on one day of the week, as wall-clock `TimeOnly` values. `ClosesNextDay`
marks a closing time after midnight (10:00–01:00); without it, a closing time earlier than the
opening time is indistinguishable from bad data. Not unique per day, because a branch may close
for the afternoon and reopen. This is the one child collection that cascades from its branch.

### FloorArea and DiningTable

`FloorArea` is a named zone — Windows, Bar, Terrace. `DiningTable` (named that, not `Table`,
which collides with too much persistence vocabulary) is one table positioned on the branch's
canvas so the diner app can draw the room from above: `X`, `Y`, `Width`, `Height`,
`RotationDegrees`, `Shape`, `Seats`. `(BranchId, Label)` is unique — table 7 exists once per
branch. `QrToken` is a stable, unguessable, unique string: it is what a walk-in scans, so it must
survive the table being renamed or moved.

`Status` (`Free | Held | Reserved | Occupied | OutOfService`) is a **denormalised cache** so the
floor plan renders in one query rather than joining sessions and reservations for every square on
the canvas. `TableSession` is authoritative; when the two disagree, the cache is wrong and is to
be recomputed. `CurrentSessionId` is a pointer, not a foreign key — `TableSession` already points
at the table, and an opposing key would make the pair circular and uninsertable. `RowVersion`
stops two waiters seating a walk-in on the same table at the same moment.

### Reservation

A promise that one specific table is held for one party over one interval. A reservation is an
**interval, not a point in time**: `EndUtc` is derived at creation from the branch's turn time.
Without an end, two bookings on the same table cannot be tested for overlap at all — the schema
would permit double-booking and no application code could detect it. The branch's buffer is
applied when *checking* overlaps, not baked into `EndUtc`, so the stored interval stays the one
the diner booked and sees while turnaround padding remains a setting the owner can change
tomorrow without rewriting history. `(DiningTableId, StartUtc, EndUtc)` is the index every
overlap check hits. `Code` is unique — it is what the diner quotes at the door. `HoldExpiresAtUtc`
covers the brief hold while a booking is being completed; `GraceExtensionsUsed` stops "just five
more minutes" being granted indefinitely.

### TableSession

One physical occupancy of one table: this party, this table, from this moment until they left.
This is where the two flows converge — a booked party and a stranger who scanned the QR code
produce the same kind of row, differing only in `Source` (`Reservation | WalkIn`). It is the
authoritative answer to "is table 7 free?", and because one row is written per seating and closed
on departure, it is also the raw data for turnover analytics: covers per night, minutes per
seating, table utilisation. A filtered index on `ClosedAtUtc IS NULL` keeps the "who is sitting
down right now" query off the years of closed history behind it.

### Tab

The running bill for one seating. A tab hangs off a `TableSession`, never off a reservation,
because most tabs belong to people who never booked. `SettlementMode`
(`HostPaysEverything | EveryonePaysOwnItems | AnyonePaysAnyAmount`) is frozen by
`SettlementModeLockedAtUtc` once settlement begins. `ServiceChargePercentSnapshot` copies the
branch policy as it stood when the tab opened, so changing the policy tomorrow cannot alter a
bill already presented. The five money fields are **server-computed only** — the client may
display a total but must never calculate one — and `RowVersion` is what makes concurrent payments
safe: two people tapping Pay at once cannot both claim the same remaining dram.

### TabParticipant and TabJoinToken

A participant is one person on a tab, identified by device as well as by account, because the
common case is a table of six where two people have the app and four scanned a QR code. Three
permissions travel with them: `CanOrder`, `CanSeeTableTotal`, `CanPay`. **`CanPay` implies
`CanSeeTableTotal`** — nobody pays toward a total they are not allowed to see — and that is
enforced as a domain invariant in the entity, not as a UI rule, because three separate clients
consume this API and a rule living in one of them is a rule the other two will forget.

`TabJoinToken` is a short-lived invitation to join an *open* tab (the table's QR code is stable
and opens a *new* one). Tokens expire 30 minutes after issue so a screenshot from last Tuesday
cannot get a stranger onto a live tab. `Token` is unique system-wide, because it is looked up on
its own when someone follows an invitation.

### TabOrder, TabOrderLine and TabOrderLineShare

`TabOrder` is one round sent to the kitchen. Exactly one of `PlacedByParticipantId` and
`PlacedByStaffId` is set — diners order from their own phone, waiters key in spoken orders on the
tablet — and both land in the same place, so the kitchen queue and the bill never care which
happened.

`TabOrderLine` snapshots the item's `NameSnapshot` and `UnitPriceAmdSnapshot`. A menu edit next
week — a price rise, a rename, a deletion — must never change what a closed bill says the guest
agreed to pay; `MenuItemId` is kept only to link back for reporting. Lines are voided, never
deleted, with a reason and an actor.

`TabOrderLineShare` records **who was at the table when the item was ordered**, not who is on the
tab now. Someone who joins for dessert must not be charged a share of the starters they never
saw, and someone who leaves early must not stop owing for what they ate. Splitting off the
current participant list gets both cases wrong.

### Payment

One attempt to settle part of a tab, by `Idram`, `Telcell`, `Card` or `Cash`. Schema only at this
stage — no provider is called and Armenian fiscal-receipt handling is a separate unresolved
question that will shape its own module. `Reserved` is the state that matters: the amount is held
against the tab's remaining balance under the tab's row version *before* anything is sent to a
provider, so two simultaneous payers cannot both claim the same dram and leave the venue
refunding one of them.

### MenuCategory and MenuItem

The menu belongs to the **branch**, not the venue: two locations of one brand routinely price
differently and stock different things, and pretending otherwise forces an override mechanism
later. On `MenuItem`, `Ingredients`, `Allergens`, `PortionSize`, `PrepMinutes` and `PhotoUrl` are
**required, not optional**. Nearly every question a diner puts to a waiter — what is in it, how
big is it, how long will it take, does it have nuts — is static data that belongs on the item,
and nullable columns here would simply stay empty while the app stayed unable to answer.

### StaffMember

Someone who works for a venue and signs in to the tablet or the admin panel, with a coarse
`Role` (`Owner | Manager | Waiter | Kitchen`). A null `BranchId` means all branches of the venue.
Authentication is a later module; `PinHash` exists so the schema will not have to change when it
arrives, and nothing reads it yet.

### TableStateChange

An append-only log of every transition of every table's status: from, to, why, who, when, and the
reservation or tab involved. Cheap to write and impossible to reconstruct afterwards. It settles
the argument that actually happens on a Friday night — "somebody gave away my reserved table" —
and it is the raw material for the turnover reporting sold to owners later: how long tables sat
empty between covers, how often bookings were released as no-shows, which areas turn fastest.
None of that can be backfilled, which is why the log starts on day one.

## Why reservation, session and tab are three entities and not one

They have three different lifetimes, and most rows only ever participate in one or two of them.

A **reservation** may never become anything at all. A no-show produces a booking that occupied a
slot, blocked other diners, and generated no occupancy and no money. Folding it into a tab would
mean either inventing an empty tab for every no-show or losing the record that the slot was ever
taken.

A **tab** usually has no reservation. In a cafe, walk-ins are most of the traffic: someone sits
down, scans the QR code on the table, and starts ordering. If ordering required a reservation
row, the system would have to fabricate a fake booking for the majority of its own customers —
and every availability query, every overlap check and every report would then have to learn to
ignore them.

A **session** is the only thing both of those have in common: a party is physically at a table
between two instants. Making it its own entity is what lets the booked party and the stranger who
walked in be handled by exactly one code path for occupancy, floor-plan state and billing. It is
also the only clean place to hang turnover analytics, because it is the only record that measures
the thing owners are actually paying for — how long each table was earning.

Collapsing the three would force one row to carry a booking's interval, an occupancy's start and
end, and a bill's balance and concurrency token all at once, with most columns null most of the
time and no way to express "booked but never arrived" or "arrived without booking". Keeping them
separate costs two joins and buys a model where every one of those cases is just a row that
exists or does not.
