# Platform admin — the tier above every venue

Until this task nothing in the system could create a venue: onboarding a pilot meant inserting
rows by hand in SQL, and nothing could be tested realistically end to end. This document is the
role hierarchy that fixes that, what the platform tier can do that a venue owner cannot, and why
a table with history is deactivated rather than deleted.

---

## 1. The role hierarchy

```
PlatformAdmin = 5        runs Yalla; belongs to no venue and no branch
  Owner = 1              runs a venue; every branch
    GeneralManager = 6   runs every branch of the venue; the row names no branch
      Manager = 2        a branch manager: runs one branch; the row always names it
        Waiter = 3       works the floor
        Kitchen = 4      works the pass
```

The numbers are storage, not rank (`StaffRoleRules.Seniority` is the rank; see `docs/auth.md` §4).

Lower numbers outrank higher ones. A `PlatformAdmin` is a `StaffMember` row like everyone else,
so the same sign-in flow (`POST /api/auth/venue/sign-in`), the same token type and the same
audit columns apply. What is different is enforced as a **domain invariant** on the entity:

| Role | `VenueId` | `BranchId` |
| --- | --- | --- |
| `PlatformAdmin` | **must be null** | must be null |
| every other role | **required** | optional (null = every branch of the venue) |

A platform admin with a venue id is refused at construction and at `SetRole`; a venue role
without a venue is refused the same way. There is no code path that produces a row the scope
policies would misread.

### How the policies treat the tier

| Policy | Platform admin |
| --- | --- |
| `PlatformAdminOnly` | the only role that passes |
| `ManagerOrAbove`, `WaiterOrAbove` | passes — "above" includes the platform |
| `BranchScoped` | passes for **any** branch, decided inside the handler |
| `VenueScoped` | passes for **any** venue, decided inside the handler |

The passthrough reads the explicit principal type *and* the role: a `PlatformAdmin` role claim on
a staff-session token — which no sign-in flow issues — is not enough. No call site has a role
check of its own; the handlers are the one place the rule lives.

### The first admin

There is no endpoint that creates a platform admin. The first one is **seeded from
configuration** on startup — `PlatformAdmin:Email` and `PlatformAdmin:Password`, from user secrets
in Development and from environment variables elsewhere:

```
dotnet user-secrets set "PlatformAdmin:Email"    "you@yalla.app"            --project src/Yalla.Api
dotnet user-secrets set "PlatformAdmin:Password" "<long random value>"      --project src/Yalla.Api
```

In Development the host **fails startup** when either is missing, with the commands above in the
message. Silently creating `admin / admin` is how a default account ends up in production. The
seed creates the row only when no account holds that address; it never resets an existing
admin's password - nor their name: both are the admin's to change from the console (§5).
`PlatformAdmin:SeedOnStartup=false` switches the seed off, which is what the test host does.

## 2. What the platform tier can do that a venue owner cannot

Everything under `/api/platform` is `PlatformAdminOnly`, and **every action writes a
`PlatformAuditLog` row in the same transaction as the change** — actor (id, role and name), action
slug, target (type, id and name), the venue it concerns, a JSON snapshot of what changed,
timestamp. The console reads it as its activity feed (§5). This is the tier that can delete a venue
and change what a customer pays; a change without its log entry cannot land, and a log entry for
a change that rolled back cannot either.

| Action | Endpoint | Notes |
| --- | --- | --- |
| Create a venue **with its first branch** | `POST /api/platform/venues` | A venue with no branch is useless. One `SaveChanges`: venue, branch and both audit rows commit together or not at all. `type` is `1` Cafe, `2` Restaurant or `3` Bar; the first branch ships that type's reservation-policy defaults (a 120-minute turn for a cafe or a bar, 90 for a restaurant). |
| List venues | `GET /api/platform/venues?search=&page=&pageSize=&status=&tier=&sort=&dir=` | The console's venue table: counts, `tierMix` and every branch's tier, rating, open tabs, the first readiness issue, the cover thumbnail - see §5. Deleted venues are hidden unless `status=deleted` or `all` (or the older `includeDeleted=true`). |
| Read one venue | `GET /api/platform/venues/{id}` | The venue and every branch under it. |
| Edit a venue | `PATCH /api/platform/venues/{id}` | Name, type (`1` Cafe, `2` Restaurant, `3` Bar), slug, active flag. Changing the type does not touch the policies existing branches already have. |
| **Suspend** / reactivate | `POST .../suspend`, `.../reactivate` | What non-payment does. The venue disappears from diner browsing but keeps every row and stays visible to its owner. |
| **Soft-delete** | `DELETE /api/platform/venues/{id}` | Never a hard delete. Refused, naming the blockers, while any tab is open or any confirmed booking is still in the future. |
| **Restore** | `POST /api/platform/venues/{id}/restore` | Undoes a soft delete; the venue comes back as it was - see below. |
| Add a branch | `POST /api/platform/venues/{id}/branches` | |
| Edit a branch | `PATCH /api/platform/branches/{id}` | Name, address, coordinates, time zone, active flag, **subscription tier**. Refused for a branch of a deleted venue, and a downgrade to `Free` is refused while any tab is open (see below). |

An owner can do none of these. An owner configures the venue they have; the platform decides
which venues exist and what each branch pays for.

### The subscription tier is per branch

`Branch.SubscriptionTier` is `Free` or `Paid`. Billing is per branch, not per brand — a chain
with four locations is four paying customers — so the flag lives on the branch and the venue
list shows a **rollup**: `Paid` only when every branch is paid, plus the paid-branch count.

| Tier | Gets |
| --- | --- |
| `Free` | floor plan, reservations |
| `Paid` | adds tabs, ordering, payments, analytics |

The tab endpoints check the branch's tier inside the service, on every open, join, read and
mutation. A `Free` branch answers **409 `feature-not-enabled`** with the branch id and current tier
in `context` — not a 403, because the caller is allowed to be there; the branch has not paid for
what they asked. Nothing here bills anybody; it is a flag that gates features.

Because the gate covers reads and settlement as well as ordering, **a downgrade waits for the open
tabs.** Moving a branch to `Free` while a party is mid-meal would hide a live bill from the people
who owe it, so the request is refused and names the tables — the same shape as refusing to delete a
venue with an open tab. Upgrades are never blocked.

### Suspended versus deleted

| | Diner browsing | New bookings and tabs | Existing tabs | Owner's admin surface | Data | Reversible |
| --- | --- | --- | --- | --- | --- | --- |
| **Suspended** | gone (availability answers 404) | refused, `409 branch-unavailable` | readable and settleable | intact | intact | yes, `reactivate` |
| **Deleted** | gone | refused | readable and settleable | venue refuses every change | intact, `DeletedAtUtc` stamped | yes, `restore` |

**Hiding the venue is not the whole lever.** A diner's app caches branch ids and the QR sticker
stays on the table long after the invoice stops being paid, so the *entry points* refuse as well:
`POST /api/reservations` and `POST /api/tabs/open` and `/join` answer **409 `branch-unavailable`**
with the branch id and a readable reason. Without that, a booking made after a soft delete would
break the very invariant the delete had just checked.

What is deliberately **not** gated is an existing tab: reading it and settling it keep working.
Taking payment away from a party that is already seated strands real money on a real table and
punishes the diners for the venue's unpaid invoice. The same gate covers a branch whose own
`IsActive` is false, which is what that flag means.

A deleted venue refuses **every** change — name, type, slug, active flag, adding a branch, and
editing any branch it owns. Reservations, tabs and payments hang off a venue's branches and are
financial and occupancy records. They outlive the customer relationship, which is why there is no
hard delete anywhere.

**Restoring** (`POST /api/platform/venues/{id}/restore`) is the one change a deleted venue takes, and it
undoes the delete completely, because the delete removed nothing. The venue comes back **as it was
before**: `Venue.SoftDelete` records whether the venue was switched on (`ActiveBeforeDeletion`) and
`Venue.Restore` puts that back, and since deleting never touched the suspension, a venue that was
suspended comes back suspended. A venue deleted before that was recorded comes back **switched off**:
nobody knows how it was, so it comes back hidden and somebody switches it on on purpose.
Restoring a venue that is not deleted answers it as it is and writes nothing, so the console's undo
toast firing twice is harmless; a real restore is audited as `venue.restore`. The venue's slug stays
reserved while it is deleted - the unique index covers deleted venues, so nobody can take the address -
which is why a restore never finds it taken; should that ever change, the restore is refused with a
409 naming the venue that holds it.

## 3. Configuring a venue — what an owner or manager does

`ManagerOrAbove` within scope, or a platform admin.

**The venue itself** (`GET /api/venues/{id}/manage`) — the read the console opens a venue with:
name, type, slug, the suspended and deleted flags, and the branches the caller's own staff row
covers (id, name, slug, time zone, active flag, tier, table count), active ones first. An owner
and a general manager get every branch; a branch manager gets their own; a platform admin gets
everything. **Venue users never read `/api/platform`** — everything
there is `PlatformAdminOnly`, and this read carries none of what the platform keeps to itself
(tier rollup, paid-branch count, suspension timestamps).

**Reservation policy** (`GET`/`PUT /api/branches/{id}/reservation-policy`) — every field of the
owned `ReservationPolicy`, replaced as one form. Out-of-range values are **refused, never
clamped**: a turn time of 5 minutes or 12 hours gets a 400 that names the field and the value.
And **changing a policy never invalidates an existing reservation.** Shortening the window or the
turn time applies to future bookings; the ones already made that fall outside the new rules are
counted in `affectedExistingReservations` and listed by id, and are otherwise left exactly as
booked. A settings change never rewrites or cancels a booking as a side effect.

**Opening hours** (`GET`/`PUT /api/branches/{id}/opening-hours`) — the whole week, replaced
atomically. `closesNextDay` is derived — a closing time at or before the opening time means after
midnight — and is not accepted from the client. Blocks on one day may touch but not overlap.

**Menu** (`/api/branches/{id}/menu/...`) — categories and items with ordering, and an
availability toggle that is not a delete: "we're out of khachapuri tonight" is not the same as
removing a dish. **Ingredients, allergens, portion size, prep minutes and a photo are required**
on create; most of what a diner asks a waiter is static data that belongs on the item, and
optional fields stay blank. An item any order line references cannot be deleted — order lines
snapshot name and price, so history is safe, but the reference must survive — so it is marked
unavailable instead, and the response says so. A price change never affects existing lines.

> **Photo storage is undecided.** `photoUrl` is accepted as a URL string for now. Where photos are
> uploaded and served — object storage, CDN, size limits, moderation — needs deciding before the
> venue admin panel ships an upload control.

**Staff** (`/api/venues/{id}/staff`) — CRUD within the venue, PIN set and reset, and the
enrolment-code and device-revocation endpoints from the authentication task. Who may give whom
which role is one table, `StaffRoleRules`:

| Actor | May create or edit |
| --- | --- |
| Manager (branch manager) | Waiter, Kitchen — at their own branch only |
| GeneralManager | Manager, Waiter, Kitchen — at any branch of the venue |
| Owner | Owner, GeneralManager, Manager, Waiter, Kitchen |
| Platform admin | any venue role — never another platform admin |

A branch manager names a branch and a general manager names none: anything else is 422
`validation-failed` on `branchId` (wording in `docs/auth.md` §4). A branch manager's staff list
shows their own branch's people only.

Nobody changes their **own** role, deactivates their own account, or **changes their own branch
assignment** — a branch-confined manager who could clear their own branch would sign in on every
branch's tablets, which is the same escalation by another route. The service decides from the
acting staff member's *stored* row, not from the token's claim.

**Only an owner, a general manager or a branch manager may hold an email and password.** A password mints a `VenueUser`
token, and that identity is venue-scoped rather than branch-scoped: giving one to a waiter would
hand them, from a browser with no enrolled tablet, the branches their PIN on the floor is
deliberately refused at. A waiter or kitchen hand taps a PIN on a device a manager enrolled. For
the same reason the venue-wide widening in `BranchScopedHandler` applies to `Owner` and
`GeneralManager` only, not to every venue-user token - and not to a branch manager, who passes on
their own branch alone.

One address is one account across the whole system, so a duplicate email is refused with a message
rather than a database error.

## 4. The rooms editor — and why a table is deactivated, not deleted

This is the tool the team uses during onboarding, so it is built to be used.

`PUT /api/branches/{id}/floor-plan` **replaces the whole plan in one atomic call**: the rooms, each
room's photos in order, and every table with its pin on one of its room's photos, in one
transaction. The editor sends the finished layout, not thirty individual table updates, and a
partially applied plan is a room whose pins point at pictures it no longer shows. There is no drawn
canvas any more: a room is its photos (up to 10), and a table is a pin on one of them. The full
contract is K13 in [diner-browse.md](diner-browse.md#k13-rooms-with-photos---implemented-rooms-phase-1-backend).

| Rule | Outcome |
| --- | --- |
| Table labels unique per branch | **error**, naming the label — a clear message, not a raw constraint violation |
| At most 10 photos a room; a photo in one room only; each photo this branch's | **422** naming `areas[i].photoIds…`, or **404** for another branch's photo |
| A pin is a photo and both coordinates (0-1), on one of the table's own room's photos | **422** naming `tables[i].photoId`/`photoX`/`photoY` |
| A photo dropped from every room | its tables come **off** the photo, with a warning - not an error |
| A bookable table with no pin | **warning**: staff seat it, diners are not offered it |
| Moving a pin or a table to another room | always safe, always allowed — it does not affect the table's bookings |
| Tables match by `id`, then by `label` | an editor that lost the ids still edits the same tables, so their QR codes survive |
| Two tables trading labels | **allowed** — renumbering a room is normal. The unique index is checked per statement, so the movers are parked on throwaway labels and renamed in a second pass, both inside one transaction |
| A table with a party seated at it | **error** — it cannot be dropped from the plan. A floor view that stops showing an occupied table is the failure the state machine exists to prevent; free it first |
| A table or area with no label or name | **error** naming how many, rather than a fault |

Areas can also be edited one at a time, which is what the editor's sidebar does:
`POST /api/branches/{id}/floor-areas`, `PATCH .../floor-areas/{areaId}`, `DELETE .../floor-areas/{areaId}`.
Deleting an area **keeps its tables** — they simply stop belonging to a room, and come off its
photos, which go with it.

### Why deletion deactivates

A `DiningTable` is the foreign-key target of every `Reservation`, `TableSession` and `Tab` that
ever happened at it. Those are the records that answer "who booked table 7 that Friday", "how
long did the party stay", "what did they owe". Deleting the table would orphan them — or, with
`Restrict` on the keys, fail with a constraint error the editor cannot act on.

So a table left out of the plan, or sent to `DELETE /api/branches/{id}/tables/{tableId}`, is
handled by its history:

| Has any reservation, session, tab **or state-change audit row** | What happens | Response |
| --- | --- | --- |
| **No** — never used | removed outright | `deleted: true` |
| **Yes** | `IsActive = false`; it leaves the floor plan and takes no bookings, and every record that points at it still resolves | `deactivated: true`, and the message says why |

The audit log counts, and it is the easy one to forget. A table that was held for a late party, or
marked out of service for a wobbly leg, has no reservation, no session and no tab — but it does
have `TableStateChange` rows, and that foreign key restricts. Leaving it out of the history check
would turn a delete into a database constraint error nobody can act on.

Nothing is lost either way, and the editor is told which happened rather than having to guess.

### The QR token

`QrToken` is generated for a new table, unique, and **never regenerated on edit**. The code is
printed and stuck to the physical table; renaming, moving or resizing the table must not break
it. Nothing in the replace path writes the token.

The one way it changes is `POST /api/tables/{tableId}/regenerate-qr`, for the case where a code
is compromised. It is explicit, `ManagerOrAbove` and branch-scoped (the branch is resolved from
the table), and **audited** to `PlatformAuditLog`, whoever did it.

The audit row records the **previous** token only. That one is dead the moment the change commits,
and recording it is what lets somebody answer "which code stopped working". The new token is a live
credential — anyone holding it can open a tab on that table anonymously — so it does not go into an
append-only log that reporting and backups can reach.

## Creating a venue: which refusals collect, and which do not

`POST /api/platform/venues` answers **two different shapes**, and the split is deliberate rather
than an accident of where the check happens.

| What is wrong | Status | Reports |
| --- | --- | --- |
| A required field is missing or blank | **422** `validation-failed` | **Every** missing field, in `context.fields` |
| A field is present but out of range | **400** `invalid-request` | The **first** offending field only |
| The body is not JSON, or a scalar sits where an object goes | **400** | Nothing field-level — the deserialiser refused it |

An empty body therefore names `name`, `slug` and `firstBranch` together, and a body whose branch has
no name names `firstBranch.name` — nested fields are dotted, the same way the opening-hours refusal
already names `[1].opensAt`.

### Why the missing half collects and the range half does not

`CreateVenueCommand.Validate()` asks one question — "is this null or blank?" — which needs no
knowledge of any rule, so it can be asked before construction without a second copy of anything.

Bounds are different. A latitude of 200 or a name of 500 characters is refused by the `Venue` and
`Branch` constructors through `Guard`, and **the bounds exist nowhere else**. Collecting them the
way the reservation policy does would mean either duplicating every limit in a validator — two
places that must agree, and will not — or extracting them into a `VenueLimits` class the way
`ReservationPolicyLimits` exists for the policy form. That second option is the right end state and
is a real change to how venue creation validates; it is not a contained one, so it has not been
made here.

The practical effect is small: missing fields are what an empty or half-filled form produces, and
out-of-range coordinates are what one typo produces. The first case is the one that made somebody
submit three times to discover three problems.

### The 500 this replaced

Until this change a body with no `firstBranch` reached `ArgumentNullException.ThrowIfNull` and came
back as a **500, logged as a server error**. `ApiExceptionMapper` maps `ArgumentNullException` to a
500 on the grounds that a null where the domain requires an object is a `Venue` or a
`ReservationPolicy` we failed to build — correct for every other call site, and wrong for this one,
because `command.FirstBranch` arrives over HTTP. Client input was going through the server-fault
path: the endpoint contradicted its own documented contract, every malformed request was filed as
our fault, and the caller was told to quote a `traceId` instead of which field to fix.

The mapper is unchanged. The throw site is what moved.

## 5. The platform console's reads

The console redesign (Home, the venue table, a venue's bookings, the activity feed, Settings) reads
these. Every one is `PlatformAdminOnly` and checked again inside the service. **Booking numbers are
aggregates only**: no response here carries a guest's name, number or booking code at any depth -
the queries group and count, so nothing about a guest ever leaves the database. The restaurant sees
its guests; the platform sees numbers.

| Route | What it answers |
| --- | --- |
| `GET /api/platform/overview` | Home in one read |
| `GET /api/platform/venues/{venueId}/bookings-summary?days=7\|30` | One venue's bookings as counts, for 7 or 30 days |
| `GET /api/platform/activity?limit=&venueId=` | The newest audit rows, newest first |
| `POST /api/platform/reviews/{reviewId}/dismiss-reports` | Leave a reported review up and set its reports aside |
| `POST /api/platform/venues/{venueId}/restore` | Undo a soft delete (§2, "Suspended versus deleted") |
| `GET` / `PATCH /api/platform/me`, `POST /api/platform/me/password` | The admin's own name and password |
| `POST /api/platform/reviews/{reviewId}/restore-reports` | Undo the last dismissal of a review's reports |
| `GET /api/platform/venues` (extended) | The venue table's rows, filters and sorts |

There is **no outbox retry endpoint**, by decision: a dead letter is a decision to stop (see
`docs/notifications.md` §5). Home's alert links to `GET /api/platform/outbox` for the details.

### The tier is per branch; the console shows a mix

`Branch.SubscriptionTier` stays the real flag, set with `PATCH /api/platform/branches/{id}`. Every
venue-level answer derives a **`tierMix`** from its branches - `1` Free (no branch paid, and a venue
with no branch at all), `2` Mixed (some), `3` Paid (every one) - alongside `paidBranchCount` and
`branchCount`, which is everything "Paid" or "Mixed · 1/2" needs. The venue row also lists every
branch's own tier in `branches`. The older `subscriptionTier` rollup on a venue row is kept for the
callers that read it; it reads `Free` for a mixed venue. A button that changes a whole venue's tier
is the console sending one `PATCH` per branch; each is refused on its own terms (`branch-not-ready`
going Paid, open tabs going Free).

### `GET /api/platform/overview`

```json
{
  "generatedAtUtc": "2026-10-18T10:32:00Z",
  "venues": { "total": 8, "paid": 4, "mixed": 1, "free": 3, "active": 6, "suspended": 1, "inactive": 1,
              "deleted": 1, "branches": 10, "paidBranches": 6 },
  "bookings": { "last7Days": 312, "previous7Days": 278, "changePercent": 12.2,
                "labelTimeZoneId": "Asia/Yerevan",
                "perDay": [ { "date": "2026-09-19", "count": 31 }, "… 30 entries, today last" ] },
  "pendingApprovals": { "total": 7, "venues": [ { "venueId": "…", "name": "Cascade Cafe", "count": 3 } ] },
  "openTabs": { "total": 41, "venues": [ { "venueId": "…", "name": "Cascade Cafe", "count": 15 } ] },
  "notReady": { "total": 4, "branches": [ { "venueId": "…", "venueName": "Riverside Restaurant",
                "branchId": "…", "branchName": "Main", "issues": [ { "code": "incomplete-dishes", "count": 3 } ] } ] },
  "recentVenues": [ { "venueId": "…", "name": "Riverside Restaurant", "slug": "riverside-restaurant", "type": 2,
                      "status": 1, "tierMix": 3, "branchCount": 1, "paidBranchCount": 1, "createdAtUtc": "…" } ],
  "outbox": { "pending": 0, "due": 0, "failed": 0, "deadLettered": 2, "sentLastDay": 118, "deadLetteredLastDay": 2 }
}
```

| Part | Rule |
| --- | --- |
| `venues` | Venues not deleted, split by `tierMix` and by `status` (1 Active, 2 Suspended, 3 Inactive - switched off); `deleted` is counted apart and is in no other number. `branches` and `paidBranches` are the branches of the venues in `total`. |
| `bookings` | Bookings by the **day of the visit** (`Reservation.LocalDate`, the branch's own date), **cancelled ones left out** (diner, venue, and a rejected request, which is a venue cancellation). Each branch counts on its own calendar - "so many days before that branch's today" - and the `date` labels are in the zone most branches share. `perDay` is 30 entries, oldest first, today last; **the 14-day chart is the last fourteen**. `last7Days` is today and the six days before it; `previous7Days` the seven before those - a rolling week, so "this week" on a Monday is not one day long. `changePercent` is to one decimal and absent when the previous week had none. Deleted venues are left out. |
| `pendingApprovals` | Requests still `PendingApproval` **whose visit is ahead and whose answer window is open** (`ApprovalExpiresAtUtc` null or ahead) - the predicate the venue's own approval queue lists by; one whose time or window has passed is not something anybody can approve. Per venue, the most first. |
| `openTabs` | Tabs `Open` or `Closing` right now, per venue, the most first. |
| `notReady` | Branches **a diner can reach** (venue switched on, not suspended, not deleted; branch switched on) that a guest would find unfinished - the guest-facing codes below. The newest venues first - those are the ones somebody is setting up - at most 20, with the `total`. Not the venue table's set; see below. |
| `recentVenues` | The five newest venues not deleted. |
| `outbox` | The same numbers `GET /api/platform/outbox` serves, plus **`deadLetteredLastDay`**: messages given up on in the last 24 hours, which is what Home's "N messages did not reach guests" counts. The outbox endpoint now carries that number too. |

### Readiness issue codes

The readiness checklist (`GET /api/branches/{id}/readiness`) now returns **`issues`** beside its
`blockers` sentences - one code per sentence, in the same order, from the same place in
`BranchReadinessQuery`, so they cannot drift. A count rides on the one line that has one.

| Code | Line |
| --- | --- |
| `no-tables` / `tables-unlabelled` | The floor plan has no active table / some table has no label |
| `no-menu` / `menu-empty` | No menu category / categories and no dishes |
| `incomplete-dishes` (`count`) | Dishes missing a photo, description, ingredients, allergens, portion or prep time |
| `no-hours` | No opening hours on any day |
| `policy-not-reviewed` | Nobody has saved the reservation policy |
| `no-staff` | No active staff member works at the branch (a venue-wide owner counts) |
| `no-device` | No tablet enrolled |
| `no-photo` | **The platform console only, never a checklist blocker**: the branch has no cover photo |

**Home and the venue table ask different questions, deliberately.** Home's `notReady` is "what are
diners seeing unfinished right now", so it is only the branches a diner can reach: the venue switched
on, not suspended, not deleted, and the branch switched on. The venue table's `readinessIssue`,
`notReadyBranchCount` and `branches[].issues` are **setup progress**: every *active* branch of a venue
that is not deleted - a suspended or switched-off venue included, because somebody is still finishing
it. Neither ever names a switched-off branch: its `issues` are empty and it is skipped when the first
issue is chosen. A deleted venue has none at all.

**The console shows what a guest would find unfinished**, and nothing else
(`PlatformRollups.GuestFacingIssues`): `no-tables` (with `tables-unlabelled` folded in), `no-menu`
(with `menu-empty` folded in), `incomplete-dishes`, `no-hours`, then `no-photo`. An unsaved
reservation policy, no staff or no tablet are the venue's own work - on the branch's readiness page,
not in the venue table or on Home. The checklist for many branches is
`IBranchReadinessQuery.GetManyAsync`, a fixed handful of grouped queries however many branches there
are; the single-branch read is that call with one id.

### The venue table: `GET /api/platform/venues`

New query parameters, all optional: `status` = `live` (default: every venue not deleted), `all`,
`active`, `suspended`, `inactive` or `deleted` - the older `includeDeleted=true` means `all` when no
`status` is sent, and a `status` wins when both are; `tier` = `free`, `mixed` or `paid` (the
`tierMix`); `sort` = `name` (default), `rating`, `tables` or `created`; `dir` = `asc` (default) or
`desc` - a venue with no rating sorts below every rated one. Name and id break every tie. `search` now
also matches each branch's name and district. A blank value (`tier=`, as an empty select sends it) is
no filter. A value it does not know is a **400** naming the parameter in `context.field`.

Each row (`VenueSummary`, also the `venue` of `GET /api/platform/venues/{id}`) gained these, named as
the console's gateway maps them:

| Field | Meaning |
| --- | --- |
| `status` | 1 Active, 2 Suspended, 3 Inactive, 4 Deleted |
| `tierMix`, `branches` | See above; `branches` is every branch - `branchId`, `name`, `subscriptionTier`, and also `isActive`, its `issues` and its own `district` (absent when it has none, so the console can search every branch's) - active ones first, then by name. With `paidBranchCount` and `branchCount` it is "Paid" or "Mixed · 1/2" |
| `averageRating`, `reviewCount` | Over the **published** reviews of every branch - the public pages' rule, venue-wide. `averageRating` is absent with none |
| `openTabCount` | Tabs open or closing now |
| `readinessIssue` | `{ code, count, branchName }`: the first thing a guest would find missing, at the first branch in `branches` that has one - `no-tables`, `no-menu`, `incomplete-dishes` (with `count`), `no-hours` or `no-photo`. Absent when nothing is; never worked out for a deleted venue |
| `notReadyBranchCount` | How many branches have any such issue |
| `coverThumbnailUrl` | The first branch's cover thumbnail (in `branches` order): root-relative `/api/photos/{id}/thumbnail` for an uploaded photo, absolute for a migrated one |
| `district`, `cuisine` | The first branch's that has one, as the listing form saved it |
| `createdAtUtc` | When the venue was created |

`tableCount` now counts **active** tables only, here and on each branch of a venue's detail - a table
kept only for its history is not one the room has, which is the checklist's rule. Each branch of
`GET /api/platform/venues/{id}` also carries `openTabCount` and `district`.

### `GET /api/platform/venues/{venueId}/bookings-summary?days=7|30`

```json
{
  "venueId": "…",
  "days": 7,
  "total": 115,
  "byStatus": { "completed": 89, "upcoming": 9, "pendingApproval": 3, "cancelled": 10, "noShow": 4 },
  "byWeekday": [ { "dayOfWeek": 0, "count": 15 }, "… all seven, 0 Sunday to 6 Saturday" ],
  "averagePartySize": 3.2,
  "byBranch": [ { "branchId": "…", "name": "Kaskad", "total": 74, "completed": 57, "upcoming": 5,
                  "pendingApproval": 2, "cancelled": 7, "noShow": 3, "todayCount": 18 } ]
}
```

`days` is `7` or `30` (default 30; anything else is a 400 naming `days`). Unlike Home, the window
counts **every status, cancelled ones included**, so `byStatus` adds up to `total`: `completed` is
Seated or Completed (they came), `upcoming` is confirmed and not seated - still ahead, or late and not
yet marked - `pendingApproval`, `cancelled` (either side, rejections included) and `noShow`. The window
is each branch's own today and the `days - 1` days before it, by the branch's local date of the visit,
and `byWeekday` is that date's weekday as `System.DayOfWeek` (0 Sunday … 6 Saturday), all seven listed.
`averagePartySize` is over the bookings that were not cancelled, to one decimal, and **written as
`null`** with none. `byBranch` lists every branch of the venue, by name, even with none, with the same
split flat and `todayCount` - its bookings for its today, cancelled ones left out - for the venue
page's branch table. A deleted venue's numbers are still readable; an unknown venue is 404.

### The activity feed: `GET /api/platform/activity`

`PlatformAuditLog` - the log every platform action already wrote in the same transaction as the
change - gained the four columns a feed needs, copied **when the row is written** so a history reads
with the names things had then: `ActorRole` and `ActorDisplay` (from the actor's stored row, not the
token), `TargetDisplay`, and `VenueId`, the venue the row concerns. Migration
`PlatformActivityAndReportDismissal` fills them for the rows already there from what their ids name
today. A review's row is named by **its branch, never its author**; the diner deleting their own
account writes the diner's id as the actor and keeps a null role and name, which is how the feed
leaves it out.

```json
{ "items": [ {
  "id": "…", "atUtc": "2026-10-18T08:10:00Z", "action": "branch.tier-change",
  "actor": { "staffMemberId": "…", "role": 5, "displayName": "Grigor Avagyan" },
  "target": { "type": "Branch", "id": "…", "displayName": "Kaskad" },
  "venue": { "venueId": "…", "name": "Luna Cafe" },
  "detail": { "venueId": "…", "from": 1, "to": 2 }
} ] }
```

`limit` is 1-100 (default 20; out of range is 400 naming `limit`); `venueId` narrows it to one venue.
The feed is every row a member of staff wrote - the platform admin's (`role` 5) and the venues' own
teams' - so Home can say "Aram Hakobyan · Cascade Cafe added Mariam Avetisyan as branch manager".
`venue` is the venue's **current** name (the link goes there); the actor and target names are the
ones they had then. `detail` is the row's JSON as written:

| Action | Written by | `detail` |
| --- | --- | --- |
| `venue.create`, `venue.update` | platform | `after` (and `before`): name, type, slug, active flag |
| `venue.suspend`, `venue.reactivate`, `venue.delete` | platform | the timestamps; `snapshot` on delete |
| `venue.restore` | platform | `wasDeletedAtUtc`, `after` (active flag, suspended) |
| `branch.create`, `branch.update` | platform | `after` (and `before`): the branch's fields, tier included |
| `branch.tier-change` | platform | `venueId`, `from`, `to` (1 Free, 2 Paid) |
| `branch.relocate` | owner or platform | `old`, `new` address and coordinates |
| `staff.create` | owner, manager or platform | `role`, `branchId`, `canSignIn` - never a PIN or password |
| `staff.sign-in-issued` | owner, manager or platform | `email`, `emailBefore`, `expiresAtUtc`, `replacedExistingSignIn` - never the link |
| `review.hide`, `review.unhide` | venue or platform | `actorType`, `branchId`, `reason`, `before` |
| `review.dismiss-reports` | platform | `actorType`, `branchId`, `dismissed`, `reasons` (count per report reason) |
| `review.restore-reports` | platform | `actorType`, `branchId`, `restored`, `dismissedAtUtc` (the dismissal it undid) |
| `table.regenerate-qr` | manager or platform | `branchId`, `label`, `previousQrToken` (dead the moment it was replaced) |
| `admin.rename` | platform | `before`, `after` |
| `admin.password-change` | platform | `otherSignInsEnded`, `resetLinksEnded` - nothing about either password |
| `admin.password-change-failed` | platform | `reason` (`current-password-wrong`) - never what was typed; these rows are the lockout's counter |

**A tier change is its own entry.** `PATCH /api/platform/branches/{id}` that moves the tier writes
`branch.tier-change`; anything else the same request changed is a `branch.update` beside it; a request
that changes nothing still writes the `branch.update` it always has.

### Dismissing review reports

`POST /api/platform/reviews/{reviewId}/dismiss-reports` (no body) is the platform looking at a
reported review and finding nothing wrong. The review stays exactly as it is - shown or hidden - and
**stops counting as reported** for both tiers: `BranchReviewReport` gained `DismissedAtUtc` and
`DismissedByStaffMemberId`, and the moderation lists' `reportCount`, `lastReportedAtUtc` and the
`reported` filter read only the reports nobody has dismissed. The reports are kept - each is still a
diner's word - and a report filed afterwards counts again. **Idempotent**: under the same row lock a
visibility change takes, a review with no open report is answered as it is and nothing is written.
Audited as `review.dismiss-reports`. Platform only; a venue cannot dismiss reports. Answers the review
as the moderation list shows it.

**Undo: `POST /api/platform/reviews/{reviewId}/restore-reports`** (no body). The console dismisses at
once and undoes with this. One dismissal stamps every report it sets aside with the same
`DismissedAtUtc`, so the latest instant names the latest dismissal, and exactly its reports count again
(`DismissedAtUtc` and `DismissedByStaffMemberId` cleared); reports an earlier dismissal set aside stay
dismissed, and reports filed since are untouched. It undoes one dismissal, **once**: when the review was
last restored after its latest dismissal, a second call writes nothing - it never reaches back to an
older one. A dismissal after the undo is undoable in its turn. Same row lock, platform only, audited as
`review.restore-reports`, answers the review as the list shows it.

### The admin's own account

`GET /api/platform/me` answers `{ staffMemberId, fullName, email }` for the signed-in admin, checked
from the token and the stored row (active, platform admin). `PATCH /api/platform/me` with
`{ fullName }` renames them (1-200 characters after trimming; 422 naming `fullName` otherwise; an
unchanged name writes nothing; audited `admin.rename`). **The email is fixed**: it is
`PlatformAdmin:Email`, the address the seed finds the account by, and nothing here changes it.

`POST /api/platform/me/password` with `{ currentPassword, newPassword }` answers **200 with a fresh
token pair** - `{ accessToken, refreshToken, expiresInSeconds }`, the refresh endpoint's shape. **The
console must store it**: the change ends every session the account has, the one it was sent from
included.

- **Rules.** The new password follows the one rule every console password follows - at least
  `Auth:MinimumPasswordLength` (12) characters, length only. Everything wrong is one **422** naming
  each field in `context.fields`: `currentPassword` missing (`required`) or wrong (`conflict`),
  `newPassword` missing or too short (`min`). A wrong current password is a form error, **not a 401**,
  so the console stays signed in and shows it under the field.
- **Every session ends, now.** `StaffMember.SessionGeneration` moves on, so every admin-panel access
  token the account holds is refused from its next request with `401 session-revoked` (`docs/auth.md`
  §4); every refresh token is revoked (`password-changed`); every reset link not yet used is spent. The
  response's pair is minted under the new generation on a new refresh chain, and is how the sign-in
  that made the change carries on.
- **Wrong passwords are counted per account.** Each one is audited as `admin.password-change-failed`,
  and those rows are the counter: with `Auth:PasswordAttemptsPerIdentifier` (10) of them inside
  `Auth:PasswordAttemptWindowMinutes` (15) and since the last successful change, the route answers
  **429 `too-many-attempts`** with a `Retry-After` header and `context.retryAfterSeconds` - until the
  oldest of them leaves the window - before the password is looked at, so a right one does not get
  through either. Stored, so it holds across restarts and instances. A successful change starts the
  count again.
- **Rate limited** by the `auth` policy as well (per account, since the caller is signed in), on top of
  the counter.
- Audited `admin.password-change`, with nothing about either password.

### The seed never overwrites the admin

`PlatformAdminSeeder` runs on every start and **only creates**: when an account already holds the
configured `PlatformAdmin:Email` it returns that account's id and writes nothing. So a name changed in
Settings and a password changed in Settings both survive every restart; `PlatformAdmin:FullName` and
`PlatformAdmin:Password` name the account the first time and never again. To reset a forgotten
admin password, use the reset link (`POST /api/auth/venue/request-password-reset`), not the
configuration. Changing `PlatformAdmin:Email` itself would make the next start create a second
admin, which is why the console cannot change the address.

## Out of scope

Billing, invoicing or payment collection from venues; photo storage; analytics and reports;
ordering; the late-nudge scheduler; SignalR; retrying dead letters.
