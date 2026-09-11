# Platform admin — the tier above every venue

Until this task nothing in the system could create a venue: onboarding a pilot meant inserting
rows by hand in SQL, and nothing could be tested realistically end to end. This document is the
role hierarchy that fixes that, what the platform tier can do that a venue owner cannot, and why
a table with history is deactivated rather than deleted.

---

## 1. The role hierarchy

```
PlatformAdmin = 0     runs Yalla; belongs to no venue and no branch
  Owner = 1           runs a venue; every branch
    Manager = 2       runs a venue or one branch; configures it
      Waiter = 3      works the floor
      Kitchen = 4     works the pass
```

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
admin's password. `PlatformAdmin:SeedOnStartup=false` switches the seed off, which is what the
test host does.

## 2. What the platform tier can do that a venue owner cannot

Everything under `/api/platform` is `PlatformAdminOnly`, and **every action writes a
`PlatformAuditLog` row in the same transaction as the change** — actor, action slug, target
type and id, a JSON snapshot of what changed, timestamp. This is the tier that can delete a venue
and change what a customer pays; a change without its log entry cannot land, and a log entry for
a change that rolled back cannot either.

| Action | Endpoint | Notes |
| --- | --- | --- |
| Create a venue **with its first branch** | `POST /api/platform/venues` | A venue with no branch is useless. One `SaveChanges`: venue, branch and both audit rows commit together or not at all. |
| List venues | `GET /api/platform/venues?search=&page=&pageSize=&includeDeleted=` | Branch count, table count, paid-branch count, tier rollup. Deleted venues are hidden unless `includeDeleted=true`. |
| Read one venue | `GET /api/platform/venues/{id}` | The venue and every branch under it. |
| Edit a venue | `PATCH /api/platform/venues/{id}` | Name, type, slug, active flag. |
| **Suspend** / reactivate | `POST .../suspend`, `.../reactivate` | What non-payment does. The venue disappears from diner browsing but keeps every row and stays visible to its owner. |
| **Soft-delete** | `DELETE /api/platform/venues/{id}` | Never a hard delete. Refused, naming the blockers, while any tab is open or any confirmed booking is still in the future. |
| Add a branch | `POST /api/platform/venues/{id}/branches` | |
| Edit a branch | `PATCH /api/platform/branches/{id}` | Name, address, coordinates, time zone, canvas size, active flag, **subscription tier**. Refused for a branch of a deleted venue, and a downgrade to `Free` is refused while any tab is open (see below). |

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
| **Deleted** | gone | refused | readable and settleable | venue refuses every change | intact, `DeletedAtUtc` stamped | no |

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

## 3. Configuring a venue — what an owner or manager does

`ManagerOrAbove` within scope, or a platform admin.

**The venue itself** (`GET /api/venues/{id}/manage`) — the read the console opens a venue with:
name, type, slug, the suspended and deleted flags, and the branches the caller's own staff row
covers (id, name, slug, time zone, active flag, tier, table count), active ones first. An owner
and a manager with no home branch get every branch; a manager whose row names a branch gets that
one; a platform admin gets everything. **Venue users never read `/api/platform`** — everything
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
| Manager | Waiter, Kitchen |
| Owner | Owner, Manager, Waiter, Kitchen |
| Platform admin | any venue role — never another platform admin |

Nobody changes their **own** role, deactivates their own account, or **changes their own branch
assignment** — a branch-confined manager who could clear their own branch would sign in on every
branch's tablets, which is the same escalation by another route. The service decides from the
acting staff member's *stored* row, not from the token's claim.

**Only an owner or a manager may hold an email and password.** A password mints a `VenueUser`
token, and that identity is venue-scoped rather than branch-scoped: giving one to a waiter would
hand them, from a browser with no enrolled tablet, the branches their PIN on the floor is
deliberately refused at. A waiter or kitchen hand taps a PIN on a device a manager enrolled. For
the same reason the venue-wide widening in `BranchScopedHandler` applies to `Owner` and `Manager`
only, not to every venue-user token.

One address is one account across the whole system, so a duplicate email is refused with a message
rather than a database error.

## 4. The floor plan — and why a table is deactivated, not deleted

This is the tool the team uses during onboarding, so it is built to be used.

`PUT /api/branches/{id}/floor-plan` **replaces the whole plan in one atomic call**: canvas size,
areas and tables together, in one transaction. A floor plan editor sends the finished layout,
not thirty individual table updates, and a partially applied plan is a broken room.

| Rule | Outcome |
| --- | --- |
| Table labels unique per branch | **error**, naming the label — a clear message, not a raw constraint violation |
| Every table inside the canvas | **error**, naming the offending tables |
| Overlapping tables | **warning**, not an error. Real rooms have benches against tables and stools tucked under bars; the plan is a map, not a physics simulation |
| Geometry changes | always safe, always allowed — moving table 7 across the room does not affect its bookings |
| Tables match by `id`, then by `label` | an editor that lost the ids still edits the same tables, so their QR codes survive |
| Two tables trading labels | **allowed** — renumbering a room is normal. The unique index is checked per statement, so the movers are parked on throwaway labels and renamed in a second pass, both inside one transaction |
| A table with a party seated at it | **error** — it cannot be dropped from the plan. A floor view that stops showing an occupied table is the failure the state machine exists to prevent; free it first |
| A table or area with no label or name | **error** naming how many, rather than a fault |

Areas can also be edited one at a time, which is what the editor's sidebar does:
`POST /api/branches/{id}/floor-areas`, `PATCH .../floor-areas/{areaId}`, `DELETE .../floor-areas/{areaId}`.
Deleting an area **keeps its tables** — they simply stop belonging to a zone.

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

## Out of scope

Billing, invoicing or payment collection from venues; photo storage; analytics and reports;
ordering; the late-nudge scheduler; SignalR.
