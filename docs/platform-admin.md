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
| List venues | `GET /api/platform/venues?search=&page=&pageSize=` | Branch count, table count, paid-branch count, tier rollup. |
| Edit a venue | `PATCH /api/platform/venues/{id}` | Name, type, slug, active flag. |
| **Suspend** / reactivate | `POST .../suspend`, `.../reactivate` | What non-payment does. The venue disappears from diner browsing but keeps every row and stays visible to its owner. |
| **Soft-delete** | `DELETE /api/platform/venues/{id}` | Never a hard delete. Refused, naming the blockers, while any tab is open or any confirmed booking is still in the future. |
| Add a branch | `POST /api/platform/venues/{id}/branches` | |
| Edit a branch | `PATCH /api/platform/branches/{id}` | Name, address, coordinates, time zone, canvas size, active flag, **subscription tier**. |

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

### Suspended versus deleted

| | Diner browsing | Owner's admin surface | Data | Reversible |
| --- | --- | --- | --- | --- |
| **Suspended** | gone (availability answers 404) | intact | intact | yes, `reactivate` |
| **Deleted** | gone | venue refuses every change | intact, `DeletedAtUtc` stamped | no |

Reservations, tabs and payments hang off a venue's branches and are financial and occupancy
records. They outlive the customer relationship, which is why there is no hard delete anywhere.

## 3. Configuring a venue — what an owner or manager does

`ManagerOrAbove` within scope, or a platform admin.

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

Nobody changes their **own** role or deactivates their own account. The service decides from the
acting staff member's *stored* row, not from the token's claim.

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

### Why deletion deactivates

A `DiningTable` is the foreign-key target of every `Reservation`, `TableSession` and `Tab` that
ever happened at it. Those are the records that answer "who booked table 7 that Friday", "how
long did the party stay", "what did they owe". Deleting the table would orphan them — or, with
`Restrict` on the keys, fail with a constraint error the editor cannot act on.

So a table left out of the plan, or sent to `DELETE /api/branches/{id}/tables/{tableId}`, is
handled by its history:

| Has any reservation, session or tab | What happens | Response |
| --- | --- | --- |
| **No** — never used | removed outright | `deleted: true` |
| **Yes** | `IsActive = false`; it leaves the floor plan and takes no bookings, and every record that points at it still resolves | `deactivated: true`, and the message says why |

Nothing is lost either way, and the editor is told which happened rather than having to guess.

### The QR token

`QrToken` is generated for a new table, unique, and **never regenerated on edit**. The code is
printed and stuck to the physical table; renaming, moving or resizing the table must not break
it. Nothing in the replace path writes the token.

The one way it changes is `POST /api/tables/{tableId}/regenerate-qr`, for the case where a code
is compromised. It is explicit, `ManagerOrAbove` and branch-scoped (the branch is resolved from
the table), and **audited** to `PlatformAuditLog` with the old and new values, whoever did it.

## Out of scope

Billing, invoicing or payment collection from venues; photo storage; analytics and reports;
ordering; the late-nudge scheduler; SignalR.
