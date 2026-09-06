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
