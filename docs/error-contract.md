# Error contract — which refusals name their field

Prompt 8 Part 0 made `context` a real type on the wire for the named error families a client
branches on. It did not reach the field-level validation families, and this document is the record
of closing that.

---

## 1. The gap

The reservation-policy endpoints rejected an out-of-bounds value with prose and nothing else — no
`errors` map, no `context`, no field name. Worse, the bound check threw
`ArgumentOutOfRangeException` whose `ParamName` was an **English label**:

```csharp
throw new ArgumentOutOfRangeException("Turn time", value, "Turn time must be between ...");
```

So the console built a label-to-field lookup table keyed on English server text, while its own
labels were localised into Armenian and Russian. The moment either side was translated the table
silently stopped matching and every message fell back to form-level — no error, no test failure,
just an owner staring at "something is wrong" over a form with fourteen inputs.

## 2. The shape

Modelled on the floor plan's `duplicateLabels` (and the `tablesOutsideCanvas` it had while there was
a drawn canvas), which already did this correctly.

`FieldValidationException` (domain) → **HTTP 422**, code `validation-failed`.

```json
{
  "type": "https://docs.yalla.app/errors/validation-failed",
  "title": "Validation failed",
  "status": 422,
  "detail": "Turn time must be between 15 and 360 minutes; 5 minutes was given.",
  "code": "validation-failed",
  "traceId": "...",
  "errors": {
    "turnTimeMinutes": ["Turn time must be between 15 and 360 minutes; 5 minutes was given."]
  },
  "context": {
    "field": "turnTimeMinutes",
    "fields": [
      {
        "field": "turnTimeMinutes",
        "message": "Turn time must be between 15 and 360 minutes; 5 minutes was given.",
        "bound": "min",
        "min": 15,
        "max": 360,
        "value": 5
      }
    ]
  }
}
```

- **`context.field`** — the first offending property, for a form that can only highlight one input.
- **`context.fields`** — *every* violation. A request that breaks six bounds reports six. A form
  that surfaces one error at a time makes an owner submit six times.
- **`errors`** — the same complaints keyed by field, in the shape RFC 7807 defines, for generic
  tooling that knows nothing about Yalla.
- **`bound`** — `min` | `max` | `range` | `required` | `conflict`.

### Field names are wire names

`turnTimeMinutes`, not `TurnTimeMinutes` and not `"Turn time"` — the casing the OpenAPI schema
uses, so a form can key on it directly.

Where the payload is a bare array the name is **indexed**: `[2].closesAt`. The opening-hours body is
a list of blocks and the console renders a row per block, so the index is what puts the message on
the right row.

### Reporting all of them requires checking before constructing

`ReservationPolicy`'s constructor throws on the first bad number it meets. So
`ReservationPolicyCommand.ToPolicy()` now runs `ReservationPolicyLimits.Check` over the **raw
values** first and only then builds the policy. Validating a constructed policy could never have
reported more than one problem out of six.

## 3. Which endpoints carry `context.field`

**Typed, all violations, `errors` map — `validation-failed` / 422:**

| Endpoint | Fields it names |
| --- | --- |
| `PUT /api/branches/{branchId}/reservation-policy` | `turnTimeMinutes`, `bufferMinutes`, `graceMinutes`, `lateNudgeAfterMinutes`, `graceExtensionMinutes`, `minLeadMinutes`, `bookingWindowDays`, `cancellationDeadlineMinutes`, `walkInHoldbackMinutes`, `serviceChargePercent`, `maxSeatOverhang`, `approvalRequiredAbovePartySize` |
| `PUT /api/branches/{branchId}/opening-hours` | `[n].opensAt`, `[n].closesAt` |
| `PUT /api/branches/{branchId}/opening-hours/exceptions` | `expectedVersion` (`required`), `days` (`required`), `days[n].date` (`required`, `max`, `conflict`), `days[n].note` (`max`), `days[n].blocks` (`required`, `max`), `days[n].blocks[m].opensAt`, `days[n].blocks[m].closesAt` (`conflict`), `days[100]` (`max`). A day before today is ignored, not refused. A stale `expectedVersion` is 409 `concurrent-update` with `context.currentVersion` |
| `PUT /api/branches/{branchId}/menu/categories/order` and `PUT .../categories/{categoryId}/items/order` | `categoryIds` / `itemIds`, bound `conflict` — not every category (item) exactly once: the console's menu is stale |
| `PATCH /api/branches/{branchId}/menu/items/{itemId}` | `categoryId`, when it names a category on another branch |
| `POST /api/venues/{venueId}/staff` and `PATCH .../staff/{staffMemberId}` | `branchId` — a branch manager (`role` 2) with no branch: *"A branch manager works at one branch. Choose it."*; a general manager (`role` 6) with one: *"A general manager runs every branch, so leave the branch empty."* Checked on the resulting role and branch together |
| `POST /api/branches/{branchId}/floor-areas` and `PATCH .../floor-areas/{areaId}` | `name`, bound `conflict` — another room at the branch already has the name (trimmed, any case): *"Area names must be unique within a branch. Terrace is already used."* The rule the floor-plan PUT applies |
| `PUT /api/diner/me/password` | `currentPassword` — needed and not sent: the account has a password and this sign-in did not prove the phone in the last ten minutes. *"Enter your current password, or sign in again with a code to set a new one."* A `currentPassword` that is sent and wrong stays **401 `invalid-credentials`**, as before |

**Already correct, unchanged** — the shape the above was modelled on:

| Endpoint | Context |
| --- | --- |
| `PUT /api/branches/{branchId}/floor-plan` | `duplicateLabels`, `errors` — `floor-plan-invalid` / 422. The photo and pin rules (rooms with photos) answer `validation-failed` naming `areas[i].photoIds[j]` and `tables[i].photoId`/`photoX`/`photoY` |

A plan that removes an area while a table still names it is refused the same way; the table and the
area are named in `errors` as `label -> area`, and nothing is written. Moving the tables to no area in
the same save is accepted and keeps every table - removing an area never removes a table, through the
plan or through `DELETE .../floor-areas/{id}`. Pinned by
`VenueAdminEndpointTests.Dropping_an_area_from_the_plan_keeps_its_tables_and_a_table_still_filed_under_it_is_refused_by_name`
(QA 2026-10-16, parity P2).

**The sweep: `context.field` on every remaining argument refusal.**

Every `Guard` call and every entity constructor in the domain already passes `nameof(theParameter)`,
which is already camelCase and already matches the schema. The mapper was simply throwing it away.
It now carries it — plus `context.value` where the exception has an `ActualValue` — so a single
change in one place gave `context.field` to every endpoint whose refusals come out of a guard,
including:

- `POST/PATCH /api/platform/venues` and `/api/platform/branches` — `name`, `slug`, `address`,
  `latitude`, `longitude`, `timeZoneId`
- `POST /api/branches/{branchId}/menu/categories/{categoryId}/items` and the item patch —
  `name`, `priceAmd`, `photoId`, `prepMinutes`, `ingredients`, `allergens`, `portionSize`,
  `description`, `displayOrder`
- `POST /api/venues/{venueId}/staff` and its patch — `fullName`, `phone`, `pin`, `email`
- `POST /api/venues/{venueId}/staff/{staffMemberId}/sign-in` — `email`
- `POST /api/branches/{branchId}/floor-areas` — `name`, `displayOrder`
- the reservation and tab commands — `partySize`, `guestName`, `guestPhone`, `quantity`, `note`,
  `amountAmd`, `tipAmd`, `displayName`, `deviceId`, and the rest

These stay **400 `invalid-request`** rather than becoming 422s: the status codes are a published
contract and nothing about what those refusals mean changed, only how much they say.

Two names are deliberately dropped rather than passed on:

- **Wrapper parameters** — `command`, `request`, `blocks`, `value`. A guard throwing `nameof(command)`
  is saying "something about this payload is wrong", which is genuinely form-level. Emitting
  `field: "command"` would send a console looking for an input that does not exist, which is worse
  than saying nothing.
- **Anything not camelCase.** A capitalised `ParamName` is a label, not a field — that is the exact
  bug being deleted — so it is dropped and the refusal stays form-level.

## 4. What the console can delete

The label-to-field lookup table. `context.field` is now authoritative on every endpoint listed
above, and `context.fields` lets a form show every problem at once instead of one per submission.

## 5. The declared contract, and who enforces it

The request records carry forty `[Required]`, `[StringLength]`, `[Range]`, `[MaxLength]` and
`[EmailAddress]` declarations. They reach the OpenAPI schema, which is what a generated client and a
gateway schema check read — and until `RequestValidationFilter` existed, **nothing enforced any of
them**. A client's schema check found that before the server ever did.

### Why a stock validation filter would have done nothing either

Every request here is a positional record:

```csharp
public sealed record OpenTabRequest([Required] string QrToken, ...);
```

An attribute on a positional parameter binds to the **parameter**, not to the generated property,
whenever the attribute permits both targets — which `RequiredAttribute` and its siblings all do.
`Validator.TryValidateObject` reads *properties*, finds an unannotated type, and passes everything.

Writing `[property: Required]` on all forty would also work, and would need every future author to
remember. `RequestValidationFilter` reads the rules from wherever the author put them — property or
primary-constructor parameter — so the declaration stays as it reads most naturally.

### What it costs a client when nothing enforces them

Not a missing refusal — the domain guards catch most bad input. A refusal **about the wrong thing**.
A required string goes missing, binds to `null`, travels down to a lookup, and the lookup answers
about the row it could not find:

| Request | Before | Now |
| --- | --- | --- |
| `POST /api/tabs/open` with no `qrToken` | **404** "That QR code does not belong to a table in service." | **422** naming `qrToken` |
| `POST /api/tabs/join` with no `joinToken` | **401** "That invitation is no longer valid. Ask the host for a new one." | **422** naming `joinToken` |
| `partySize` outside `[Range(1, 100)]` | not enforced | **422** naming `partySize` |

A diner was being told their QR code was bad when the client had sent none, and a guest was being
sent back to a host who had done nothing wrong.

### Two things it deliberately does not touch

**The sign-in flows are outside the filter, and must stay outside it.** A credential endpoint
answers the same 401 whether the email is unknown, the password is wrong or the body is malformed.
That uniformity is the anti-enumeration posture, not an oversight.

Validating the body first would answer *"that is not an email address"* **before the credentials are
ever checked** — and that is not merely a different status code, it is a **cheaper oracle than the
timing asymmetry this surface was already hardened against**. A 422 that arrives without a password
comparison is distinguishable from a 401 that arrives after one, by anybody with a stopwatch and far
less patience than timing analysis needs. Switching on a filter must not hand that back.

`POST /api/auth/diner/request-code` is out for a related reason: its `phoneE164` refusal already
answers **400** naming the field, so moving it to a 422 would be churn on something already right.

`RequestValidationTests.The_sign_in_flows_keep_their_uniform_refusal` pins all of this, because the
tidy-looking change is to fold the auth group into the validated one.

**`clientCommandId` keeps its own 400.** `ClientCommandIdFilter` answers with a sentence about
replays — *"Generate one per command and reuse it when retrying"* — which no schema attribute could
express, and which is the thing a client actually needs to read.

### The eighteen that cannot fire, and what actually guards each

`[Required]` on a **non-nullable value type** — `Guid`, `DateOnly`, `TimeOnly`, an enum — can never
fire. A missing one binds its default rather than null, and `RequiredAttribute` only rejects null.
There are nineteen of them.

A documented no-op and an unenforced attribute look identical from the outside, so each was checked
rather than assumed. **Probe** means a request was actually sent with the field omitted and the
answer recorded; **trace** means the guard was read in code and no probe was run.

| Field | Where | What actually answers | How checked |
| --- | --- | --- | --- |
| `clientCommandId` ×10 | reservation, tab, table-state commands | `ClientCommandIdFilter` — rejects `Guid.Empty`, **400** with its own sentence about replays | probe + every one of the ten records implements `IClientCommandRequest` and every endpoint taking one sits under a group carrying the filter |
| `settlementMode` | `SetSettlementModeRequest` | `Guard.Defined` in the domain — **400** naming `settlementMode`, because `(SettlementMode)0` is not a defined member | probe |
| `staffMemberId` | `StaffPinRequest` | The uniform **401**. `Guid.Empty` matches no staff member and answers exactly as a wrong PIN does — which is the anti-enumeration posture, and correct | trace |
| `newHostParticipantId` | `ReassignHostRequest` | `KeyNotFoundException("No such participant on that tab.")` — **404**. Accurate, if not diagnostic | trace |
| `reservationId` | `SeatReservationRequest` | The reservation lookup — **404** | trace |

That is fourteen of the nineteen genuinely covered. **Five were not**, and four still are not.

#### `outcome` — fixed here, because it was writing the wrong row

`ReleaseOutcome` has no zero member, so an absent `outcome` bound to `0`, missed the `== NoShow`
branch, and fell through to `CancelByVenue` — answering **200** the whole way.

Those two are not interchangeable. `NoShow` *"counts toward the rolling no-show threshold"*;
`CancelledByVenue` *"does not count against the diner"*. So a waiter's tablet that omitted the field
recorded a real no-show as the venue's own cancellation, silently, and the count that decides
whether that diner's next booking needs approval never moved.

The same shape as the settlement-mode bug: a client sends the wrong field name and the server picks
a branch instead of refusing. That one refused; this one wrote.

Fixed by declaring the parameter `ReleaseOutcome?`, which gives `RequiredAttribute` a null to
reject — so it now answers **422** naming `outcome`. That is the only fix available: the filter
cannot see a missing non-nullable enum, because binding has already replaced it with a real value.

#### `branchId`, `tableId`, `date`, `time` — not guarded, and not fixed here

All four are on `CreateReservationRequest`. Probed, each answers about something the caller never
sent:

| Omitted | Answer today |
| --- | --- |
| `branchId` | **404** `Branch 00000000-0000-0000-0000-000000000000 was not found.` |
| `tableId` | **404** `Table 00000000-0000-0000-0000-000000000000 was not found.` |
| `date` | **422** `reservation-lead-time-too-short` — `default(DateOnly)` is `0001-01-01`, which is in the past |
| `time` | **422** `reservation-outside-opening-hours` — `default(TimeOnly)` is midnight |

The last two are the worst of the four: they hand back a confident, plausible diagnosis about lead
time or opening hours to a client whose actual mistake was omitting a field.

The fix is the same as `outcome`'s — make them nullable so `[Required]` has a null to reject. It is
**not** applied here because it changes four fields of a request contract the diner clients are
mapping against right now, and this change was scoped to the filter. `time` is the one that cannot
be solved any other way: `00:00` is a legitimate booking time, so once it has bound to a
non-nullable `TimeOnly` there is no value left that means "absent".

### 410 `telegram-verification-expired`

The one code that answers 410. `POST /api/auth/diner/telegram/{challengeId}/claim` says it for a
challenge that is unknown, past its deadline, already claimed, or started under a different diner's
token than the one claiming it - deliberately one answer for all four, because the id is a
capability the app was handed moments ago and an anonymous poller with somebody else's id must not
learn which of those it is. Not 401: nothing was presented wrongly; the thing polled for is gone.
No `context`. The app's response is the same in every case: start again. See `docs/auth.md`,
"Telegram verification".

### 409 `pin-taken`

`POST /api/venues/{venueId}/staff` with a `pin`, and `POST /api/venues/{venueId}/staff/{staffMemberId}/pin`,
when somebody else who signs in on the same tablet already uses that PIN. `context.field` is `pin`;
the sentence is *"Somebody who signs in on this tablet already uses that PIN. Choose another."* "The
same tablet" is the roster: for somebody at a branch, everyone active at that branch plus the venue's
venue-wide people (owner, general manager, any branchless staff); for somebody venue-wide, everyone
active in every branch of the venue plus the other venue-wide people. Deactivated people do not count,
and your own current PIN is no clash. A conflict rather than a 422: the PIN is well formed, it is
somebody else's. PINs are hashed, so the check verifies the candidate against each roster hash;
duplicates already in the data are left alone. Minted by `PinTakenException`.

### Personal phones: `kitchen-phone-not-allowed`, `pairing-code-invalid`, `pairing-code-expired`

`POST /api/branches/{branchId}/staff/{staffMemberId}/phone-pairings` for kitchen staff, and
`POST /api/auth/staff/phone-pair` for somebody moved to the kitchen since their code was issued: **409
`kitchen-phone-not-allowed`**, `context.staffMemberId`, *"Kitchen staff work on the kitchen tablet, so a phone
cannot be paired to them."* Minted by `KitchenPhoneNotAllowedException`. The pairing code's own refusals are
sign-in reasons (401, no context): `pairing-code-invalid` - unknown, or a tablet's enrolment code - and
`pairing-code-expired` - past its ten minutes, or superseded by a newer code for the same person; retyping
never helps that one. A code already used is the generic `409 conflicting-state`, as a tablet's is. See
`docs/auth.md`, "Personal phones".

### 409 `table-has-future-bookings`

`DELETE /api/branches/{branchId}/tables/{tableId}`, and `PUT /api/branches/{branchId}/floor-plan`
when the plan leaves out a table, while that table holds a booking still to come: Confirmed or
PendingApproval and not yet ended. Deactivating it - what history otherwise earns a table - would
strand the booking on a table the floor no longer shows. `context.tables` lists the labels (label
order), `context.bookings` the booking codes (code order); the sentence is *"Table 5 has upcoming
bookings (K7Q2LM). Move or cancel them first."*, or *"Tables 5, 7 have ..."* for several. Nothing
is written. On the plan save it comes after `floor-plan-invalid`: a plan with errors of its own
answers those first. Past, cancelled, completed and no-show bookings are history and still mean
deactivation. A conflict rather than a 422: the request is well formed; the bookings are in the
way. Minted by `TableHasFutureBookingsException`.

### Takeaway and "I'm coming"

Each refusal has its own code (2026-10-22), so the app and the tablet say what is true rather than "conflict":

| Code | Status | `context` | When |
|---|---|---|---|
| `takeaway-not-accepted` | 409 | `branchId` | the branch does not take takeaway |
| `takeaway-closed` | 409 | `branchId`, `opensAtUtc`, `opensAt` (local `HH:mm`) when it opens again within a week | shut now, or past the last order |
| `takeaway-busy` | 409 | `branchId`, `maxInProgress` | ASAP while the kitchen has as many orders sent or accepted as the venue allows |
| `takeaway-slot-unavailable` | 409 | `branchId`, `at`, `slots` (on offer now) | the pickup time asked for is not one on offer |
| `takeaway-items-unavailable` | 422 | `items[]` = `{ menuItemId, reason }` (1 NotOnMenu, 2 SoldOut, 3 NotForTakeaway) | dishes that cannot be ordered to take away, every one |
| `takeaway-status-conflict` | 409 | `orderId`, `status` (where the order is now) | a move the order is not in the state for - cancel after the venue answered, accept a cancelled order |
| `takeaway-no-show-too-early` | 409 | `orderId`, `noShowFromUtc` | "no-show" less than thirty minutes after ready |
| `booking-blocked-no-shows` | 403 | `blockedUntilUtc` | a takeaway order from a guest blocked for no-shows - the booking's own code (2026-10-23), so the app says the same thing at both checkouts |
| `conversation-closed` | 409 | `subjectKind` (`reservation`/`takeaway`), `subjectId`, `closedAtUtc` | writing to a guest-message thread two hours after its booking or order ended (2026-10-23, [messages.md](messages.md)) |
| `guests-cannot-write` | 403 | `branchId` | a guest's typed message where the venue switched them off - the answer buttons still work |
| `waiters-cannot-write` | 403 | `branchId` | a waiter writing to a guest where the venue keeps messages to managers |
| `messages-too-fast` | 429 | `conversationId`, `perMinute` (5), `perConversation` (60), `retryAfterUtc` (with `Retry-After`) while waiting helps | a guest writing faster than five a minute, or past sixty in a thread |
| `ask-on-my-way-too-early` | 409 | `reservationId`, `opensAtUtc` | the venue asks more than two hours before the start |
| `ask-on-my-way-not-open` | 409 | `reservationId`, `status`, `startUtc` | the venue asks about a booking not confirmed, or started |
| `guest-already-answered` | 409 | `reservationId`, `answeredAtUtc`, `runningLateMinutes` (when late) | the guest said on my way or running late |
| `ask-on-my-way-asked-recently` | 409 | `reservationId`, `askedAtUtc`, `nextAllowedAtUtc` | asked within the last 15 minutes |

### Activity history and transfers

2026-10-24, [activity.md](activity.md).

| Code | Status | `context` | When |
|---|---|---|---|
| `transfer-target-ineligible` | 422 | `staffMemberId`, `reason` (`not-found`, `inactive`, `other-branch`, `does-not-serve`) | `POST /api/tabs/{tabId}/transfer` to somebody who cannot take the table |
| `tab-not-transferable` | 409 | `tabId`, `status` | a transfer of a bill closed or written off, or whose party has gone |
| `activity-export-too-large` | 422 | `rows` (at least), `maxRows` (20,000) | `GET .../activity.csv` over a window with more entries than a file carries |

The History's and the results' queries are ordinary 422 `validation-failed` naming their fields: `date`, `from`, `to`,
`staffMemberId`, `actorKind`, `category`, `q`, `cursor`, `limit`, `list`, `branchId`.
Reading them without the role is 403 `forbidden`.

The shape refusals are ordinary 422 `validation-failed` naming their fields: `lines`, `lines[i].quantity`, `note`,
`pickup`, `pickup.at`, `readyAt`, `reason`, `method`, `prepMinutes`, `lastOrderMinutes`, `maxInProgress`, `minutes`.

### Branch features: `feature-not-enabled`

2026-10-27, [platform-admin.md](platform-admin.md) "Branch features". The Free/Paid tier is gone; the platform
allows or blocks ten `BranchFeature`s per branch.

| Code | Status | `context` | When |
|---|---|---|---|
| `feature-not-enabled` | 409 | `feature` (the `BranchFeature` number), `branchId` | a new action on a feature the platform has not allowed at the branch |
| `branch-not-ready` | 409 | `branchId`, `incompleteMenuItemCount` | `PUT /api/platform/{branches,venues}/{id}/features` turning table service (5) on with unfinished dishes |

**The slug and the status did not change; the context did.** Before 2026-10-27 `feature` was a sentence ("Tabs and
ordering") and the context carried the branch's `currentTier`; now `feature` is the number and there is no tier. Every
feature answers it for its new actions - what each one refuses, and the work that runs on, is in
[branch-features.md](branch-features.md); a blocked staff phone or kitchen session gets it on every authorised call. The features routes refuse a set as an ordinary 422 `validation-failed` on
`features` (missing, a number that is not a feature, or pre-orders without app or web bookings), and turning table
service off while tabs are open as a 409 `conflicting-state` whose `context` names the branch (`branchId`) and how many
tabs hold it (`openTabCount`), the tables in `detail` - on the venue route, the first branch that refused.

### Which status, and why

**422, collecting every violation** — the rule §2 already states. A refusal that can report all the
fields at once is a `validation-failed` 422 carrying `context.fields`; the 400 `invalid-request`
answers are the single-field guard refusals that cannot collect. A body missing both `qrToken` and
`deviceId` names both, in one answer, in the same document shape as every other refusal in this API.

## The catalogue: every problem code

Generated - do not edit by hand. Every code is declared once, on its constant in
`src/Yalla.Api/Errors/ErrorCodes.cs`: `[Problem(status, meaning, Title = ...)]` and a
`[ProblemContext(name, type)]` per `context` member. `ProblemCatalogue` reads those; the exception mapper
takes each code's status from it, the OpenAPI document publishes it as the root `x-problem-codes`
extension (the frontend generates `packages/api/src/generated/problemCodes.ts` from that), and this table
is written from it. `ProblemCatalogueTests` fails when the table is stale - regenerate with
`YALLA_UPDATE_DOCS=1 dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~ProblemCatalogueTests"` -
and when a code is thrown anywhere without being declared.

A member marked *optional* is left out of some contexts that are there; every other member is present
whenever `context` is. Some codes answer without a context on some routes (`not-found` for a missing row,
`reservation-overlaps-own` on a manage link): branch on the code, then read the context when it is there.
Enums are their integer values. `unauthenticated` is declared for clients and not sent today: a route
refusing a missing token answers a bare 401 with no body.

<!-- problem-codes:begin -->
| Code | Status | Title | Meaning | `context` |
|---|---|---|---|---|
| `account-inactive` | 401 | Account inactive | The account or staff member behind the credential is no longer active. | - |
| `account-locked` | 403 | Account locked | The account is locked, not the credential wrong. | `lockedUntilUtc` date-time (nullable) |
| `activity-export-too-large` | 422 | Export too large | A History export would carry more rows than a file may. | `rows` int32, `maxRows` int32 |
| `ask-on-my-way-asked-recently` | 409 | Asked a moment ago | "Ask: are you coming?" within fifteen minutes of the last ask. | `reservationId` uuid, `askedAtUtc` date-time, `nextAllowedAtUtc` date-time |
| `ask-on-my-way-not-open` | 409 | Booking not expecting anybody | "Ask: are you coming?" for a booking that is not confirmed, or has started. | `reservationId` uuid, `status` ReservationStatus, `startUtc` date-time |
| `ask-on-my-way-too-early` | 409 | Too early to ask | "Ask: are you coming?" more than two hours before the start. | `reservationId` uuid, `opensAtUtc` date-time |
| `booking-blocked-no-shows` | 403 | Booking paused after missed bookings | The guest missed three bookings in three days and cannot book until the block ends. | `blockedUntilUtc` date-time |
| `booking-ended` | 409 | Booking has ended | The booking is over: past its end, or its sitting has finished. | `reservationId` uuid, `status` ReservationStatus, `startUtc` date-time, `endUtc` date-time, `earliestUtc` date-time, `tableLabel` string (nullable) |
| `booking-not-active` | 409 | Booking not active | The booking is not expecting its party: awaiting approval, cancelled, or a no-show. | `reservationId` uuid, `status` ReservationStatus, `startUtc` date-time, `endUtc` date-time, `earliestUtc` date-time, `tableLabel` string (nullable) |
| `booking-not-found` | 404 | Booking not found | None of the caller's bookings carries that code. | - |
| `booking-table-occupied` | 409 | Booked table still occupied | The booked table still has another party's sitting open; staff must free it. | `reservationId` uuid, `status` ReservationStatus, `startUtc` date-time, `endUtc` date-time, `earliestUtc` date-time, `tableLabel` string (nullable) |
| `booking-too-early` | 409 | Too early for this booking | The booking's table is not being held for the party yet; context says from when. | `reservationId` uuid, `status` ReservationStatus, `startUtc` date-time, `endUtc` date-time, `earliestUtc` date-time, `tableLabel` string (nullable) |
| `bookings-not-accepted` | 409 | Bookings not accepted | A booking from the diner app at a branch that is not taking app bookings. | `branchId` uuid |
| `branch-not-ready` | 409 | Branch is not ready for diners | The branch's menu has unfinished dishes, so table service cannot be turned on. | `branchId` uuid, `incompleteMenuItemCount` int32 |
| `branch-unavailable` | 409 | Branch not open for business | The branch is not open for business: venue suspended or deleted, or branch switched off. | `branchId` uuid, `reason` string |
| `client-command-id-in-use` | 409 | Command id already used | The clientCommandId already belongs to another caller's command; start again with a fresh one. | - |
| `concurrent-update` | 409 | Concurrent update | Someone else changed the row first: re-read and retry. | `currentVersion` string |
| `conflicting-state` | 409 | Conflicting state | The operation is not legal in the current state; a staff order's unorderable guest, or the branch whose open tabs keep table service on, is named in context. | `field` string (optional), `participantId` uuid (optional), `participantStatus` ParticipantStatus (optional), `branchId` uuid (optional), `openTabCount` int32 (optional) |
| `conversation-closed` | 409 | Conversation closed | Writing to a guest-message thread that has closed. | `subjectKind` string, `subjectId` uuid, `closedAtUtc` date-time |
| `credentials-invalid` | 401 | Credentials invalid | An admin-panel email and password did not match an account. | - |
| `device-revoked` | 401 | Device revoked | The tablet or device is no longer enrolled; ask a manager to enrol it again. | - |
| `email-taken` | 409 | Email address taken | The email address on a diner sign-up or profile edit is somebody else's. | `field` string |
| `enrolment-code-expired` | 401 | Enrolment code expired | The tablet enrolment code has expired; ask a manager for a new one. | - |
| `enrolment-code-invalid` | 401 | Enrolment code invalid | The tablet enrolment code is not valid. | - |
| `extensions-not-offered` | 409 | Hold extensions not offered | The branch offers no hold extensions. | `reservationId` uuid, `startUtc` date-time |
| `feature-not-enabled` | 409 | Feature not enabled for this branch | The platform has not allowed this feature at the branch. | `feature` BranchFeature, `branchId` uuid |
| `floor-plan-changed` | 409 | Floor plan changed | The floor plan was saved by somebody else since the editor loaded it. | `currentVersion` string |
| `floor-plan-invalid` | 422 | Floor plan invalid | The floor plan cannot be applied; every reason is listed. | `errors` string[], `duplicateLabels` string[] |
| `forbidden` | 403 | Forbidden | The caller is not allowed to do this. | `operation` string (optional), `requiredRole` StaffRole (optional), `requirement` string (optional), `subject` string (optional) |
| `guest-already-answered` | 409 | The guest has answered | "Ask: are you coming?" when the guest already said on my way or running late. | `reservationId` uuid, `answeredAtUtc` date-time, `runningLateMinutes` int32 (optional) |
| `guests-cannot-write` | 403 | This venue takes no written messages | A guest's typed message where the venue switched guest messages off. | `branchId` uuid |
| `hold-already-extended` | 409 | Hold already extended | The booking's one hold extension is already used. | `reservationId` uuid, `startUtc` date-time |
| `hold-not-active` | 409 | No table being held yet | "Keep my table" with no table being held yet. | `reservationId` uuid, `startUtc` date-time |
| `internal-error` | 500 | Internal error | Anything unhandled; quote the traceId when reporting it. | - |
| `invalid-credentials` | 401 | Invalid credentials | A diner's identifier and password, or current password, did not match a usable account. | - |
| `invalid-request` | 400 | Invalid request | The request violated a domain rule or arrived malformed; context names the field when one is to blame. | `field` string, `value` any (optional) |
| `invalid-table-transition` | 422 | Invalid table transition | The table cannot make that transition from its current status. | `tableId` uuid, `tableLabel` string, `fromStatus` TableStatus, `attemptedToStatus` TableStatus, `allowedFromHere` TableStatus[] |
| `join-token-invalid` | 401 | Join token invalid | The tab invitation is no longer valid; ask the host for a new one. | - |
| `kitchen-phone-not-allowed` | 409 | No phone for kitchen staff | A phone cannot be paired to kitchen staff: the kitchen works on its tablet. | `staffMemberId` uuid |
| `line-already-paid` | 409 | Line already paid for | Removing this line would reverse money already taken; that is a refund. | `tabId` uuid, `lineId` uuid |
| `menu-item-unavailable` | 409 | Item unavailable | A dish on the order has sold out; context names it. | `menuItemId` uuid, `itemName` string |
| `messages-too-fast` | 429 | Messages too fast | A guest writing more than five messages a minute, or sixty in one thread. | `conversationId` uuid, `perMinute` int32, `perConversation` int32, `retryAfterUtc` date-time (optional) |
| `no-show-undo-window-closed` | 409 | Too late to withdraw this no-show | A no-show can no longer be withdrawn: its service day ended. | `undoableUntilUtc` date-time |
| `not-found` | 404 | Not found | The thing addressed does not exist; a staff order's missing guest is named in context. | `field` string, `participantId` uuid |
| `on-my-way-not-open` | 409 | Booking not expecting you | "On my way" for a booking that is not expecting anybody now. | `reservationId` uuid, `status` ReservationStatus, `opensAtUtc` date-time |
| `pairing-code-expired` | 401 | Pairing code expired | The phone pairing code is past its ten minutes, or a newer one replaced it. | - |
| `pairing-code-invalid` | 401 | Pairing code not valid | The phone pairing code is unknown: mistyped, or a tablet's enrolment code. | - |
| `payment-exceeds-remaining` | 409 | Payment exceeds the balance | More was offered than the tab still owes; context carries the balance. | `tabId` uuid, `requestedAmd` int64, `remainingAmd` int64 |
| `phone-has-active-booking` | 409 | This number holds as many bookings as it can | The booker's proved number already holds as many active bookings as one number may. | `maxActiveBookings` int32, `reservationId` uuid (optional), `code` string (optional), `startUtc` date-time (optional), `localDate` date (optional), `localStartTime` time (optional), `venueName` string (optional), `branchName` string (optional) |
| `phone-in-use` | 409 | Phone number already has an account | The phone number on a diner sign-up already has an account; log in with a code instead. | `field` string |
| `phone-not-verified` | 403 | Phone number not verified | The diner's phone number has never been proved, and the request would land on it. | - |
| `pin-invalid` | 401 | Pin invalid | The staff PIN was not recognised. | - |
| `pin-taken` | 409 | PIN already in use on this tablet | The PIN is already used by somebody else who signs in on the same tablet. | `field` string |
| `pre-order-already-sent` | 409 | Pre-order already sent to the kitchen | The kitchen already has the pre-order, so it no longer changes. | `reservationId` uuid, `sentAtUtc` date-time |
| `pre-order-items-unavailable` | 422 | Dishes cannot be pre-ordered | Dishes that cannot be pre-ordered, every one named with why. | `items` PreOrderItemRefusalShape[] |
| `pre-order-locked` | 409 | Pre-order closed | The pre-order can no longer be changed by the guest: two hours before the start or later. | `reservationId` uuid, `lockedAtUtc` date-time |
| `pre-order-not-sendable` | 409 | Pre-order cannot be sent now | Not the time or the state to send a pre-order to the kitchen, or mark the party not coming. | `reservationId` uuid, `status` ReservationStatus, `sendableFromUtc` date-time, `endUtc` date-time |
| `pre-orders-not-accepted` | 409 | Pre-orders not accepted | The branch does not take pre-orders. | `branchId` uuid |
| `precondition-failed` | 409 | Precondition failed | A queued command describes a table that has since moved; resolve it from the conflict list. | `tableId` uuid, `tableLabel` string, `expectedFromStatus` TableStatus, `currentStatus` TableStatus, `failure` PreconditionFailure, `clientCommandId` uuid |
| `rate-limited` | 429 | Rate limited | Too many requests in the window; wait for Retry-After. | - |
| `refresh-token-invalid` | 401 | Refresh token invalid | The refresh token is unknown, expired or revoked; sign in again. | - |
| `refresh-token-reused` | 401 | Refresh token reused | A refresh token already rotated was presented again; its whole chain is revoked. | - |
| `relocation-not-allowed` | 403 | Relocation not allowed | Moving a branch (address or map pin) is for an owner or platform admin; nothing was saved. | `branchId` uuid |
| `report-range-too-long` | 400 | Report range too long | The report range is longer than any report will run. | `field` string, `requestedDays` int32, `maxDays` int32 |
| `reservation-approval-expired` | 409 | Too late to answer this booking | Approving or declining a booking whose chance to be answered has gone. | `reservationId` uuid, `status` ReservationStatus, `startUtc` date-time, `approvalExpiresAtUtc` date-time (nullable) |
| `reservation-awaiting-earlier-guest` | 409 | Waiting for the guest before to answer | Staff deciding a booking that waits on the guest before it on the table to answer. | `reservationId` uuid, `requestId` uuid, `expiresAtUtc` date-time |
| `reservation-cancelled-by-block` | 409 | Cancelled: the guest is blocked for no-shows | Approving or rejecting a booking its guest's no-show block cancelled. | `reservationId` uuid, `cancelledAtUtc` date-time (nullable) |
| `reservation-free-by-required` | 422 | Free the table by the time required | The next booking cuts the sitting short and the booking did not accept the time to free the table by. | `field` string, `requiredFreeByUtc` date-time, `requiredFreeByLocal` string, `nextBookingStartUtc` date-time, `tableId` uuid, `tableLabel` string |
| `reservation-lead-time-too-short` | 422 | Reservation lead time too short | The slot starts sooner than the branch's minimum lead time. | `reason` ReservationRejectionReason, `requestedStartUtc` date-time, `earliestStartUtc` date-time, `minLeadMinutes` int32 |
| `reservation-local-time-does-not-exist` | 422 | Reservation local time does not exist | The wall-clock time picked does not exist in the branch's zone (a clock change skips it). | `reason` ReservationRejectionReason, `localDate` date, `localTime` string, `timeZoneId` string |
| `reservation-lock-timeout` | 503 | Reservation lock timeout | The booking could not get the table's lock in time; retry with the same clientCommandId. | `tableId` uuid, `tableLabel` string, `timeoutMilliseconds` int32, `retryable` boolean |
| `reservation-not-changeable` | 409 | Booking can no longer be changed | A change to a booking that is no longer pending or confirmed, or has started. | `reservationId` uuid, `status` ReservationStatus, `startUtc` date-time |
| `reservation-not-started` | 409 | Booking has not started yet | A no-show recorded against a booking whose start has not come yet. | `reservationId` uuid, `startUtc` date-time |
| `reservation-outside-booking-window` | 422 | Reservation outside booking window | The date is further ahead than the branch takes bookings. | `reason` ReservationRejectionReason, `requestedDate` date, `lastBookableDate` date, `bookingWindowDays` int32 |
| `reservation-outside-opening-hours` | 422 | Reservation outside opening hours | The sitting does not fit inside one opening block of the branch's day. | `reason` ReservationRejectionReason, `localDate` date, `localStartTime` string, `localEndTime` string |
| `reservation-overlaps-own` | 409 | You already have a booking then | The sitting overlaps another of the diner's own live bookings; context names it (not on a manage link). | `reservationId` uuid, `code` string, `localDate` date, `localStartTime` time, `tableLabel` string, `venueName` string (nullable), `branchName` string (nullable) |
| `reservation-party-exceeds-capacity` | 422 | Reservation party exceeds capacity | More guests than the table has seats. | `reason` ReservationRejectionReason, `tableId` uuid, `tableLabel` string, `partySize` int32, `seats` int32 |
| `reservation-seat-overhang-exceeded` | 422 | Reservation seat overhang exceeded | The party would leave more seats empty than the branch allows. | `reason` ReservationRejectionReason, `tableId` uuid, `tableLabel` string, `partySize` int32, `seats` int32, `seatsLeftEmpty` int32, `maxSeatOverhang` int32 |
| `reservation-table-not-bookable` | 422 | Reservation table not bookable | The table never takes bookings: a bar stool, a counter seat. | `reason` ReservationRejectionReason, `tableId` uuid, `tableLabel` string |
| `reservation-table-out-of-service` | 422 | Reservation table out of service | The table is withdrawn from service. | `reason` ReservationRejectionReason, `tableId` uuid, `tableLabel` string |
| `reset-token-invalid` | 401 | Reset token invalid | The password reset link is unknown, spent or expired; ask for a new one. | - |
| `review-needs-visit` | 403 | Review needs a visit | A first review of a branch from a diner with no visit there in the window. | `branchId` uuid, `windowDays` int32 |
| `scan-turned-away` | 409 | Ask a waiter | A scan refused for a while after a waiter turned this phone or table away. | `retryAfterUtc` date-time |
| `service-request-rate-limited` | 429 | Too many requests from this table | This table has asked for staff too many times too quickly. | `tabId` uuid, `limit` int32, `windowMinutes` int32 |
| `session-expired` | 401 | Session expired | The staff PIN session timed out; tap the PIN again. | - |
| `session-invalid` | 401 | Session invalid | The staff PIN session is no longer valid; tap the PIN again. | - |
| `session-revoked` | 401 | Session revoked | The token's session has ended; refresh once, then sign out. | - |
| `sign-in-unknown` | 409 | Sign-in not identified | The token names no sign-in, so "sign out everywhere else" cannot tell which to keep. | - |
| `sms-unavailable` | 503 | Codes by SMS unavailable | No SMS provider is configured, so no code can be sent; prove the number with Telegram. | - |
| `tab-admission-decided` | 409 | Already decided | Admitting a tab already turned away, or turning away one already admitted. | `tabId` uuid, `admission` TabAdmission |
| `tab-awaiting-admission` | 409 | Waiting for a waiter | The tab waits for a waiter to let it order. | `tabId` uuid |
| `tab-not-accepting-orders` | 409 | Tab not accepting orders | The tab takes no more orders: the bill has been asked for, or it is closed. | `tabId` uuid, `status` TabStatus |
| `tab-not-transferable` | 409 | Nothing to hand over | A table whose bill is closed or written off, or whose party has gone, has nobody to hand over. | `tabId` uuid, `status` TabStatus |
| `table-already-booked` | 409 | Table already booked | The table was booked by somebody else between seeing it free and confirming. | `reason` ReservationRejectionReason, `tableId` uuid, `tableLabel` string, `requestedStartUtc` date-time, `requestedEndUtc` date-time, `conflictingStartUtc` date-time, `conflictingEndUtc` date-time, `conflictingReservationId` uuid, `availability` BranchAvailability (optional) |
| `table-currently-occupied` | 409 | Table currently occupied | A party seated at the table now runs into the requested slot; it may free up early. | `reason` ReservationRejectionReason, `tableId` uuid, `tableLabel` string, `requestedStartUtc` date-time, `requestedEndUtc` date-time, `seatedAtUtc` date-time, `projectedFreeAtUtc` date-time, `tableSessionId` uuid, `availability` BranchAvailability (nullable) |
| `table-has-future-bookings` | 409 | Table has upcoming bookings | A table with a booking still to come cannot be deleted, nor left out of a floor-plan save. | `tables` string[], `bookings` string[] |
| `table-lock-timeout` | 503 | Table busy | A table write waited too long for another writer; retry, nothing was decided. | `tableId` uuid, `tableLabel` string, `timeoutMilliseconds` int32, `retryable` boolean |
| `table-not-seated` | 409 | Nobody seated at this table | Staff tried to open a tab on a table with nobody seated at it. | `tableId` uuid, `tableLabel` string, `status` TableStatus |
| `table-request-not-pending` | 409 | This request no longer needs an answer | An answer to a request for the table that no longer waits. | `reservationId` uuid, `requestId` uuid, `status` TableShareRequestStatus, `expiresAtUtc` date-time |
| `table-sitting-changed` | 409 | Table sitting changed | A free that named a booking found another sitting, or none, open at the table. | `tableId` uuid, `tableLabel` string, `reservationId` uuid, `tableStatus` TableStatus, `currentSessionId` uuid (nullable) |
| `table-state-conflict` | 409 | Table state conflict | Somebody else changed this table first; redraw it from the current state. | `tableId` uuid, `tableLabel` string, `attemptedFromStatus` TableStatus, `currentStatus` TableStatus, `currentSessionId` uuid (nullable) |
| `takeaway-busy` | 409 | Kitchen busy | ASAP while the kitchen has as many takeaway orders in progress as the venue allows. | `branchId` uuid, `maxInProgress` int32 |
| `takeaway-closed` | 409 | Takeaway closed now | Closed for takeaway now: shut, or past the last order. | `branchId` uuid, `opensAtUtc` date-time (optional), `opensAt` string (optional) (nullable) |
| `takeaway-items-unavailable` | 422 | Dishes not for takeaway | Dishes that cannot be ordered to take away, every one named with why. | `items` TakeawayItemRefusalShape[] |
| `takeaway-no-show-too-early` | 409 | Too early for a no-show | "No-show" for a takeaway order before thirty minutes past ready. | `orderId` uuid, `noShowFromUtc` date-time |
| `takeaway-not-accepted` | 409 | Takeaway not offered | The branch does not take takeaway orders. | `branchId` uuid |
| `takeaway-slot-unavailable` | 409 | Pickup time unavailable | The pickup time asked for is not one on offer; context lists the slots on offer now. | `branchId` uuid, `at` string, `slots` string[] |
| `takeaway-status-conflict` | 409 | Order has moved on | The takeaway order is not in the state for that move; context says where it is now. | `orderId` uuid, `status` TakeawayStatus |
| `telegram-verification-expired` | 410 | Telegram verification no longer available | A Telegram verification challenge that can no longer be claimed; start again. | - |
| `time-full` | 409 | Time full | There is no room for that many guests at that time. | `branchId` uuid, `partySize` int32, `guestsLeft` int32 |
| `too-many-attempts` | 429 | Too many attempts | A one-time credential is out of attempts, or a per-identity limit is spent; ask for a new code. | `retryAfterSeconds` int32 |
| `transfer-target-ineligible` | 422 | Cannot take this table | The waiter named cannot take the table. | `staffMemberId` uuid, `reason` string |
| `unauthenticated` | 401 | Not authenticated | No usable credential was presented (declared for clients; a bare 401 carries no body). | - |
| `unsupported-image` | 409 | Unsupported image | An upload is not a JPEG, PNG or WebP, or is too large to store. | `detectedFormat` string |
| `username-taken` | 409 | Username taken | The username on a diner sign-up or profile edit is somebody else's. | `field` string |
| `validation-failed` | 422 | Validation failed | Field-level validation failed; every offending field is named. | `field` string, `fields` FieldViolationShape[] |
| `venue-deletion-blocked` | 409 | Venue cannot be deleted | The venue has open tabs or future bookings and cannot be deleted. | `openTabs` string[], `futureReservations` string[] |
| `verification-code-expired` | 401 | Verification code expired | The one-time code has expired; ask for a new one. | - |
| `verification-code-invalid` | 401 | Verification code invalid | A one-time code that was not accepted; context says how many tries are left. | `attemptsRemaining` int32 |
| `waiters-cannot-write` | 403 | Only managers write to guests here | A waiter writing to a guest where the venue keeps messages to managers. | `branchId` uuid |
| `web-bookings-not-accepted` | 409 | Web bookings not accepted | The branch does not take bookings from its public page. | `branchId` uuid |
<!-- problem-codes:end -->
