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

Modelled on the floor plan's `tablesOutsideCanvas` / `duplicateLabels`, which already did this
correctly.

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
| `PATCH /api/branches/{branchId}/menu/items/{itemId}` | `categoryId`, when it names a category on another branch |

**Already correct, unchanged** — the shape the above was modelled on:

| Endpoint | Context |
| --- | --- |
| `PUT /api/branches/{branchId}/floor-plan` | `tablesOutsideCanvas`, `duplicateLabels`, `errors` — `floor-plan-invalid` / 422 |

**The sweep: `context.field` on every remaining argument refusal.**

Every `Guard` call and every entity constructor in the domain already passes `nameof(theParameter)`,
which is already camelCase and already matches the schema. The mapper was simply throwing it away.
It now carries it — plus `context.value` where the exception has an `ActualValue` — so a single
change in one place gave `context.field` to every endpoint whose refusals come out of a guard,
including:

- `POST/PATCH /api/platform/venues` and `/api/platform/branches` — `name`, `slug`, `address`,
  `latitude`, `longitude`, `timeZoneId`, `floorWidth`, `floorHeight`
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
There are eighteen of them.

A documented no-op and an unenforced attribute look identical from the outside, so each was checked
rather than assumed. **Probe** means a request was actually sent with the field omitted and the
answer recorded; **trace** means the guard was read in code and no probe was run.

| Field | Where | What actually answers | How checked |
| --- | --- | --- | --- |
| `clientCommandId` ×9 | reservation, tab, table-state commands | `ClientCommandIdFilter` — rejects `Guid.Empty`, **400** with its own sentence about replays | probe + every one of the nine records implements `IClientCommandRequest` and every endpoint taking one sits under a group carrying the filter |
| `settlementMode` | `SetSettlementModeRequest` | `Guard.Defined` in the domain — **400** naming `settlementMode`, because `(SettlementMode)0` is not a defined member | probe |
| `staffMemberId` | `StaffPinRequest` | The uniform **401**. `Guid.Empty` matches no staff member and answers exactly as a wrong PIN does — which is the anti-enumeration posture, and correct | trace |
| `newHostParticipantId` | `ReassignHostRequest` | `KeyNotFoundException("No such participant on that tab.")` — **404**. Accurate, if not diagnostic | trace |
| `reservationId` | `SeatReservationRequest` | The reservation lookup — **404** | trace |

That is thirteen of the eighteen genuinely covered. **Five were not**, and four still are not.

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

### Which status, and why

**422, collecting every violation** — the rule §2 already states. A refusal that can report all the
fields at once is a `validation-failed` 422 carrying `context.fields`; the 400 `invalid-request`
answers are the single-field guard refusals that cannot collect. A body missing both `qrToken` and
`deviceId` names both, in one answer, in the same document shape as every other refusal in this API.
