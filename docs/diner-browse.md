# Diner browse, orders and account: the real-data contract

What the diner app and the venue console read and write for Explore, search, the map, a place's
details, its reviews, the rooms and their photos, the Orders tab and the diner's own account - the shapes,
the rules behind every derived number, the rate limits, the migrations and the Development seed.

It started as the hand-off contract for the frontend's move off mock data and now lives here, beside
the code it describes. [public-surface.md](public-surface.md) is the narrative for the anonymous
routes; [auth.md](auth.md) is the narrative for diner sessions and account deletion. This document is
the one to generate a client against.

---

## Status of the contract changes

The shapes below are what the backend serves today. The changes in
[Contract changes (K1-K12)](#contract-changes-k1-k12) are specified first so the clients can build
against them, and land one backend package at a time. Until a change says **implemented**, treat it
as **in progress**: the route or field is not served yet.

| Change | What | Package | Status |
| --- | --- | --- | --- |
| K1 | Revoking diner sessions (`sgen`, `session-revoked`) | B1 | **Implemented** |
| K2 | `DELETE /api/diner/me` | B1 | **Implemented** |
| K3 | `DELETE /api/diner/me/photo` deletes at once | B1 | **Implemented** |
| K4 | Managers limited to their home branch | B2 | **Implemented** |
| K5 | Listing PUT: relocation is owner or platform admin only | B2 | **Implemented** |
| K6 | Floor plan `version` / `expectedVersion` | B2 | **Implemented** |
| K7 | `PUT /api/branches/{branchId}/table-photo-positions` | B2 | **Removed** - superseded by K13 |
| K8 | Review integrity, plus venue moderation and diner reports (extension) | B3 | **Implemented** |
| K9 | App booking gate and booking note | B3 | **Implemented** |
| K10 | Proxy, photo sweep and Staging rate-limit configuration | B4 | **Implemented** |
| K11 | Favourites synced to the account | B6 | **Implemented** |
| K12 | Diner notifications feed | B6 | **Implemented** |
| K13 | Rooms with photos: pins on room photos, `GET /api/public/branches/{branchId}/rooms` | Rooms phase 1 | **Implemented** (backend) |

Backend migrations run one after another in that order (B1, B2, B3, B6), because the test fixture
builds the schema with `MigrateAsync`.

---

## Wire conventions

These apply to everything below.

- JSON camelCase. **Null fields are omitted**, not written as `null` - treat a missing key as null.
- Enums are **integers**:
  - `venueType`: 1 Cafe, 2 Restaurant, 3 Bar.
  - `state` (`DerivedTableState`): 1 Free, 2 ReservedSoon, 3 Held, 4 Occupied, 5 OutOfService.
  - `kitchenStatus` (`TabOrderStatus`): 1 New, 2 InKitchen, 3 Ready, 4 Served, 5 Voided.
  - `day` (`DayOfWeek`): 0 Sunday … 6 Saturday.
- `TimeOnly` is `"HH:mm:ss"` (e.g. `"09:00:00"`). `DateTime` is ISO-8601 UTC.
- Photos are `PhotoView`: `{ photoId, thumbnailUrl, cardUrl, fullUrl, width?, height? }`. URLs are
  root-relative `/api/photos/{id}/{thumbnail|card|full}` - resolve them against the API origin -
  unless the photo is hosted elsewhere, in which case they are absolute.
- Errors use the unified envelope `{ type, title, status, code, detail, instance, traceId, context? }`.
  Codes a client branches on here: `invalid-request` (400), `unauthenticated` and `session-revoked`
  (401), `forbidden`, `phone-not-verified` and `review-needs-visit` (403), `not-found` (404),
  `conflicting-state` and `bookings-not-accepted` (409), `validation-failed` (422, `context.fields[]`
  with `field`, `message`, `bound`, `min`, `max`, `value`), `rate-limited` and `too-many-attempts` (429).

---

## Public (anonymous) routes - `/api/public`

### `GET /api/public/branches?lat=&lng=` → `PublicBranchListing[]`
### `GET /api/public/branches/search?q=&category=&lat=&lng=` → `PublicBranchListing[]`

| Query | Type | Notes |
| --- | --- | --- |
| `q` | string | Search only. Contains-match on venue name, branch name, cuisine and address; case-insensitive; blank means all; at most 100 characters |
| `category` | int | 1 Cafe, 2 Restaurant, 3 Bar |
| `lat`, `lng` | double | Together or neither. Adds `distanceKm` and sorts nearest first. Without them: best rated first |

`q` matches the branch's `district` too, so "Arabkir" finds the places a venue says are in Arabkir.

Errors: 400 `invalid-request` (half a position, out of range, `q` too long). Cached 15 s (the estate
and the live numbers).

`PublicBranchListing`:

```json
{
  "branchId": "guid", "venueId": "guid", "venueSlug": "lumen", "branchSlug": "cascade",
  "venueName": "Lumen Coffee", "branchName": "Cascade", "venueType": 1,
  "cuisine": "Armenian & Mediterranean",   // absent until set
  "priceLevel": 2,                         // 1-4, absent until set
  "address": "12 Abovyan Street, Yerevan", "latitude": 40.1843, "longitude": 44.5129,
  "district": "Kentron",                    // the part of town; absent until set
  "distanceKm": 0.3,                        // only when lat/lng were sent
  "timeZoneId": "Asia/Yerevan", "isOpenNow": true, "freeTableCount": 4,
  "rating": 4.8,                            // absent when reviewCount is 0
  "reviewCount": 124,
  "badges": ["popular", "new"],             // derived, see Derived rules
  "coverPhoto": { "photoId": "...", "thumbnailUrl": "...", "cardUrl": "...", "fullUrl": "...", "width": 1200, "height": 800 }
}
```

### `GET /api/public/branches/{branchId}?lat=&lng=` → `PublicBranchDetail`

404 `not-found` when the branch is unknown or inactive, or its venue is suspended or deleted (read
live, per request).

```json
{
  "listing": { /* PublicBranchListing, identical to the list entry */ },
  "about": "A bright all-day cafe…",         // absent until set
  "websiteUrl": "https://…",                 // absent until set
  "phoneE164": "+374…",                      // absent until set
  "amenities": ["outdoorSeating", "wifi", "parking", "cardPayment", "vegan"],
  "openingHours": [ { "day": 1, "opensAt": "09:00:00", "closesAt": "23:00:00", "closesNextDay": false } ],
  "gallery": [ /* PhotoView, ordered, beyond the cover */ ],
  "tableCount": 12,
  "acceptsWebBookings": false,
  "acceptsAppBookings": false,               // K9: acceptsWebBookings AND the reservation policy saved
  "recentReviews": [ /* PublicReviewView, the newest 3 published, by when written */ ],
  "rooms": [ /* PublicRoom - exactly what GET …/rooms serves */ ],
  "asOfUtc": "2026-09-14T10:00:00Z"
}
```

Several rows per `day` are possible (split service). `closesNextDay: true` means `closesAt` is after
midnight.

### `GET /api/public/branches/{branchId}/reviews?page=1` → `PublicReviewPage`

20 per page, **newest written first** (`createdAtUtc` descending - a revision does not move a review
up). Hidden reviews are absent, and are left out of `rating` and `reviewCount` too (K8). 400 for
`page < 1` or a page whose offset would overflow; 404 as above. The aggregate is read live.

```json
{
  "branchId": "guid", "rating": 3.0, "reviewCount": 2, "page": 1, "pageSize": 20,
  "reviews": [
    { "reviewId": "guid", "authorName": "Anahit S.", "rating": 5, "text": "…",   // text absent if stars only
      "createdAtUtc": "…", "updatedAtUtc": "…",
      "edited": false }                     // updatedAtUtc differs from createdAtUtc
  ]
}
```

`authorName` (K8):

1. Take the first word of the display name.
2. If it contains `@`, a digit, `/` or `www.`, the name is `"Yalla diner"`.
3. Otherwise keep only letters (any script) and hyphens, at most 24 characters; nothing left is
   `"Yalla diner"` too.
4. Add `" X."` - the second word's initial - only when the second word starts with a letter.

So `"Anahit Sargsyan"` → `"Anahit S."`, `"Անահիտ Սարգսյան"` → `"Անահիտ Ս."`, `"Narek"` → `"Narek"`,
`"ani@example.test"` and `"+374 91 000 999"` → `"Yalla diner"`, and no display name → `"Yalla diner"`.

### `GET /api/public/branches/{branchId}/rooms` → `PublicBranchRooms`

Live, not cached beyond the request (K13). Rooms lowest `displayOrder` first; each room's photos in the
venue's order; on each photo the **active tables pinned there**, by label. A room with no photo is not
listed. A table with no pin is on no photo - it is not offered to diners. Nor is one whose pin is on a
photo that is not its own room's (a row written before the editor refused that): it is on no photo
here, left out of availability, and refused at booking, like an unpinned one.

```json
{
  "branchId": "guid",
  "asOfUtc": "…",
  "rooms": [
    {
      "roomId": "guid", "name": "Terrace", "displayOrder": 1,
      "photos": [
        {
          "photo": { /* PhotoView */ },
          "tables": [
            { "tableId": "guid", "label": "5", "seats": 4, "isBookable": true, "state": 1, "photoX": 0.25, "photoY": 0.5 }
          ]
        }
      ]
    }
  ]
}
```

`state`: 1 Free, 2 ReservedSoon, 3 Held, 4 Occupied, 5 OutOfService - what the floor derives now.
`isBookable: false` is a walk-in-only table: shown, not booked. 404 for a branch that is not published.
It replaces `GET …/table-markers`, which is gone.

### Existing public routes

- `GET /api/public/branches/{branchId}/availability?partySize=&date=&time=` - lists **pinned tables
  only**, and every `tables[]` entry carries `photoId`, `photoX`, `photoY` (always present). The
  floor-plan geometry (`x`, `y`, `width`, `height`, `rotationDegrees`, `shape`) and the branch's
  `floorWidth`/`floorHeight` are gone (K13).
- `GET /api/public/branches/{branchId}/availability/slots?partySize=&date=` - the time picker's day:
  every quarter of an hour whose sitting fits the opening hours, each `isAvailable` with its
  `freeTableCount`, `requestableTableCount`/`isRequestable` and `mustFreeByUtc`, judged exactly as the
  single-slot read above judges that time. Both take `excludeReservationId`, so a diner changing a
  booking sees their own table free. See
  [reservations.md](reservations.md#day-slots-one-day-every-quarter-of-an-hour).
- `GET /api/public/branches/{branchId}/menu` - `BranchMenuView`, complete items only.
- `GET /api/public/venues` and `GET /api/public/branches/{venueSlug}/{branchSlug}` - unchanged; see
  [public-surface.md](public-surface.md).

---

## Diner routes - `/api/diner`

Bearer diner token, policy `VerifiedDiner` (any diner account token; a tab participant is refused).
Every route here also refuses a token whose session has ended with **401 `session-revoked`** (K1).

### Reviews

| Route | Body | Success | Errors |
| --- | --- | --- | --- |
| `GET /api/diner/branches/{branchId}/review` | - | 200 `DinerReviewView` | 401; 404 (no review by this diner, or branch not published) |
| `POST /api/diner/branches/{branchId}/review` | `{ "rating": 5, "text": "…" }` | 201 `DinerReviewView`, `Location: /api/diner/branches/{id}/review` | 401; 403 `phone-not-verified` or `review-needs-visit`; 404; 409 `conflicting-state` (already reviewed); 422 `validation-failed` (`rating`, `text`); 429 `rate-limited` |
| `PUT /api/diner/branches/{branchId}/review` | `{ "rating": 4, "text": null }` | 200 (revised, or unchanged) or 201 (first write) `DinerReviewView` | 401; 403 `phone-not-verified`, or `review-needs-visit` on a first write; 404; 422; 429 |
| `POST /api/diner/reviews/{reviewId}/report` | `{ "reason": "spam", "note": "…" }` | 204 | 401; 404 (unknown, hidden, or at a branch not published); 409 `conflicting-state` (own review); 422 (`reason`, `note`); 429 |

`rating` is an integer 1-5; `text` is optional, trimmed, at most 1000 characters, blank clears it.
One review per diner per branch (unique index `UX_BranchReviews_BranchId_DinerUserId`). Phone-verified
accounts only, checked on the stored row. A **first** review - POST, or PUT with none yet - also needs
a visit in the last 180 days: a booking of this diner's at the branch that was Seated or Completed, or
an **approved** `TabParticipant` row on one of its tabs (a joiner still pending, or turned away, is not a
visit); otherwise **403 `review-needs-visit`** with
`context: { branchId, windowDays: 180 }`. Revising an existing review is always allowed. **A PUT with
the same rating and (trimmed) text is a 200 that writes nothing** - `updatedAtUtc` does not move. The
list routes' `rating`/`reviewCount` catch up within 15 s; `/reviews` and the details route read the
aggregate live.

`DinerReviewView`:
`{ "reviewId", "branchId", "rating", "text"?, "createdAtUtc", "updatedAtUtc", "publicAuthorName", "hidden" }`
- `publicAuthorName` is the name the public list shows it under; `hidden` is true when moderation took
it down (the diner still sees it here, nobody else does).

**Reporting.** `reason` is one of `spam`, `offensive`, `not-a-visit`, `personal-info`, `other` (422
naming `reason`: bound `required` when missing, `range` when not one of the five); `note` is optional,
trimmed, at most 500 characters (bound `max`). One report per diner per review - a repeat is a 204 that
writes nothing. Any diner account may report; a proved number is not needed. A report takes nothing
down. Review writes and reports, and both photo uploads, share the `diner-write` rate limit.

### Orders

| Route | Success | Errors |
| --- | --- | --- |
| `GET /api/diner/orders?status=active` or `?status=history` | 200 `DinerOrderView[]`, newest first, at most 100 | 400 (unknown status); 401 |
| `GET /api/diner/orders/{orderId}` | 200 `DinerOrderView` | 401; 404 (unknown, or somebody else's - the same answer) |

**Which orders a diner sees.** The query starts from the account's `TabParticipants` rows (by
`IX_TabParticipants_UserId`) and takes the union of:

1. orders **placed from the diner's phone** (`PlacedByParticipantId` is one of those rows),
2. orders a waiter keyed in **on the diner's behalf**: with an unsplit line that is theirs - named for
   them on the line (`TabOrderLines.OnBehalfOfParticipantId`), or by the order
   (`TabOrders.OnBehalfOfParticipantId`) on a line that names nobody. An order "for" them whose every
   line was put on somebody else, or that carries only the table's shared bottle, is not theirs; a
   voided line of theirs still counts, and shows labelled,
3. orders a waiter keyed in **for the whole table** (neither set) on a tab where the diner may see the
   table's total.

**The hide-total rule** decides the third: a whole-table order is the table's bill, so it is shown
only to a participant `TabPermissions.MaySeeTableTotal` allows - **approved, and not hidden from by
the host** (`CanSeeTableTotal`). A guest the host hid the total from, a guest still pending approval
and a guest removed from the tab get none of the table's orders, on the list or by id. Their own
orders (1 and 2) they still see.

**Which lines of it.** A diner who may see the table's total is shown every line of an order on their
list. Anybody else is shown **their own lines** of it - `TabProjection.IsOwnLine`, the tab's `myLines`
rule: what they ordered, what a waiter keyed in for them, and shared items they were at the table for.
Until a waiter could name a guest per line that was every line of every order on their list anyway;
a round with one guest's steak and another's coffee is not, and showing it whole would itemise the
other guest's dish, price and all, to somebody the host hid the total from. `totalAmd` is the sum of
the lines shown, and an order with nothing of theirs left on it is not listed.

**The status filter.** `status=active` is kitchen New, InKitchen and Ready **on a tab that is still
Open or Closing**; `status=history` is Served and Voided, **plus every order on a Closed or Abandoned
tab** whatever the kitchen rail says; absent or blank is both; anything else is 400. Settling the bill
ends the orders on it: most kitchens never tap "served" (the waiter carries the plate), and until
the tab had a say a paid-for order sat under Active for good (QA 2026-10-16, D-M4). The order's own
`status` and `kitchenStatus` are unchanged by this - a closed tab's order still reads `ready` if that
is where the rail left it; only which list it is on moves.

```json
{
  "orderId": "guid", "tabId": "guid", "branchId": "guid",
  "venueName": "Lumen Coffee", "branchName": "Cascade",
  "coverPhoto": { /* PhotoView, absent if none */ },
  "kind": "dineIn",
  "status": "preparing",              // confirmed | preparing | ready | completed | cancelled
  "kitchenStatus": 2,                 // integer TabOrderStatus
  "tableLabel": "5", "partySize": 2,
  "placedAtUtc": "…", "estimatedReadyAtUtc": "…",   // estimate absent if none
  "totalAmd": 2400,                   // non-voided lines, before service charge
  "items": [ { "lineId": "guid", "name": "Flat white", "quantity": 2, "unitPriceAmd": 1200,
               "lineTotalAmd": 2400, "note": "oat milk", "isVoided": false } ],
  "timeline": [ { "status": "confirmed", "atUtc": "…" }, { "status": "preparing", "atUtc": "…" } ],
  "canCancel": false
}
```

Status map (`kitchenStatus` integer → `status`): 1 New → `confirmed`, 2 InKitchen → `preparing`,
3 Ready → `ready`, 4 Served → `completed`, 5 Voided → `cancelled`. `inProgress` is never produced.
`timeline[0]` is always `confirmed` at `placedAtUtc`; later entries come from the tab's
`OrderStatusChanged` events.

**No cancel endpoint.** Voiding is a staff action on the kitchen rail (`POST /api/orders/{id}/status`,
Kitchen or above); the domain has no diner cancel, so none was invented. `canCancel` is always false.

### Account

`GET /api/diner/me`, `PUT /api/diner/me`, `PUT /api/diner/me/password`, `POST /api/diner/me/photo`
and `DELETE /api/diner/me/photo` are described in [auth.md](auth.md#the-password-door).
`DELETE /api/diner/me` and what ends a session are K1-K3 below, and in
[auth.md](auth.md#ending-a-diners-sessions).

---

## Venue console (manager, branch-scoped) - additions

Every route here carries `ManagerOrAbove` and `BranchScoped`, and the service checks the caller again
from the stored staff row (K4): a manager whose account names a home branch reaches that branch only.

### `GET /api/branches/{branchId}/listing` → `BranchListingView`
### `PUT /api/branches/{branchId}/listing` → `BranchListingView`

Body (`BranchListingCommand`). Every listing field is replaced; null or blank clears it.

```json
{
  "cuisine": "Armenian & Mediterranean",
  "about": "…",
  "priceLevel": 2,
  "websiteUrl": "https://…",
  "amenities": ["outdoorSeating", "wifi"],
  "galleryPhotoIds": ["guid", "guid"],
  "district": "Kentron",
  "address": "…",
  "latitude": 40.1843, "longitude": 44.5129
}
```

Response: `{ cuisine?, about?, priceLevel?, websiteUrl?, amenities[], address, latitude, longitude, gallery: PhotoView[], district? }`.

**Validation.** Everything is checked before anything is written, and the listing fields report every
bad field at once in `context.fields`.

| Field | Rule | Refusal |
| --- | --- | --- |
| `cuisine` | Trimmed; blank is null; at most 120 characters | 422 `validation-failed`, bound `max` |
| `about` | Trimmed; blank is null; at most 2000 characters | 422, bound `max` |
| `district` | The part of town, e.g. "Kentron". Trimmed; blank is null; at most 60 characters. A listing field like `cuisine` - a manager may set it, because naming the neighbourhood moves nobody's pin | 422, bound `max` |
| `priceLevel` | 1-4, or null | 422, bound `range` (`min` 1, `max` 4) |
| `websiteUrl` | Trimmed; at most 2048 characters; an absolute `http` or `https` address | 422, bound `max` for length; no bound for a non-http(s) or relative address |
| `amenities` | `outdoorSeating`, `wifi`, `parking`, `cardPayment`, `vegan`; case-insensitive; repeats collapse | 422 per unknown key, `value` is the key |
| `galleryPhotoIds` | null leaves the gallery; `[]` clears it; at most 12; no repeats; each uploaded for this branch (`POST /api/branches/{branchId}/photos`) | 422 bound `max` or `conflict`; **404** for a photo not uploaded at this branch - nothing written |
| `latitude` / `longitude` | Together or neither; sent together with `address` | 422 naming the missing one, bound `required`; 400 `invalid-request` for a value out of range |
| `address` | Trimmed; blank is absent; at most 400 characters; sent together with `latitude` and `longitude` | 422 naming `address` (bound `required` or `max`), or `latitude`/`longitude` when the address comes alone |
| Moving the branch | `address`, `latitude` or `longitude` present and different from the stored value | 403 `relocation-not-allowed` unless an owner or platform admin signed in to the panel - nothing on the form written (K5) |

The cover stays on `PUT /api/branches/{id}/public-profile`.

### `GET`/`PUT /api/branches/{branchId}/floor-plan` - the rooms editor

Rooms with their photos, and every table with its pin, replaced whole against `expectedVersion` (K6).
See K13 for the shapes and rules. The cover on `public-profile` has nothing to do with pins.

### `GET /api/branches/{branchId}/reviews?page=&pageSize=&filter=` → `ModeratedReviewPage`
### `PUT /api/branches/{branchId}/reviews/{reviewId}/visibility` → `ModeratedReviewView`

The venue's review moderation (K8 extension). Same policies and K4 guard as every route here. The
platform's twins are `GET /api/platform/branches/{branchId}/reviews` and
`PUT /api/platform/reviews/{reviewId}/visibility` (`PlatformAdminOnly`); both call one service.

`filter`: `all` (default; newest written first), `reported` (still visible, with at least one
undismissed report; most recently reported first - hiding a review answers its reports) or `hidden`. `page` from 1, `pageSize` 1-100, default 20. Out of range → 400
`invalid-request` naming `page`, `pageSize` or `filter`. 404 for an unknown branch.

```json
{
  "items": [
    { "reviewId": "guid", "branchId": "guid", "rating": 1, "text": "…",        // text absent if stars only
      "authorName": "Ani G.",                 // the public name, never the account's
      "dinerUserId": "guid",
      "createdAtUtc": "…", "updatedAtUtc": "…",
      "hidden": true, "hiddenReason": "…", "hiddenAtUtc": "…",   // reason and time absent when published
      "hiddenByPlatform": false,
      "reportCount": 2, "lastReportedAtUtc": "…",               // absent with no reports
      "hiddenByName": "Lilit Sargsyan",       // the venue moderator, read live; absent when shown or Yalla hid it
      "reply": { /* ReviewReplyView */ } }    // absent until the venue answers; shown here on hidden reviews too
  ],
  "page": 1, "pageSize": 20, "total": 1,
  "summary": {                                // the whole branch, whatever the filter and page
    "all": 7, "reported": 2, "hidden": 2, "visible": 5,
    "rating": 4.7,                            // shown reviews, one decimal; absent with none
    "ratingChange30Days": 0.1,                // today's rating of shown reviews minus the same for those older than 30 days,
                                              // from the unrounded averages, rounded once; absent when either has none
    "distribution": [ { "stars": 5, "count": 3 }, { "stars": 4, "count": 1 }, { "stars": 3, "count": 1 },
                      { "stars": 2, "count": 0 }, { "stars": 1, "count": 0 } ]   // always five rows, 5 first
  }
}
```

The visibility body is `{ "hidden": true, "reason": "…" }` or `{ "hidden": false }`. `hidden` is
required; `reason` is required when hiding, trimmed, at most 500 characters (422 naming `reason`,
bound `required` or `max`). A `reason` sent with `hidden: false` (a string or `null`) is accepted and
ignored: putting a review back stores no reason, and its `review.unhide` audit row records
`reason: null`. Answers the review as the list shows it. A request that changes nothing -
hiding what is already hidden for the same reason, putting back what is published - writes nothing.

- **The platform outranks the venue.** A venue putting back a review the platform hid is 403
  `forbidden`; a venue hiding it again changes nothing, and it stays the platform's. The platform can
  put back anything, and takes ownership of what it hides. A platform admin calling the venue route is
  answered as the platform.
- **Audited** in the same transaction: `PlatformAuditLogs` `Action` `review.hide` or `review.unhide`,
  `TargetType` `BranchReview`, `ActorStaffMemberId` the moderator, `ChangesJson`
  `{ actorType: "platform"|"venue", branchId, reason, before: { hidden, hiddenByPlatform, reason } }`.
- 404 `not-found` for a review that is not at this branch (or, on the platform route, not at all).

### `PUT`/`DELETE /api/branches/{branchId}/reviews/{reviewId}/reply` → `ModeratedReviewView`

The venue's **one public reply** under a review (venue console phase 4). Body of the PUT
`{ "text": "…" }`: required, trimmed, at most 500 characters (422 naming `text`, bound `required` or
`max`). Text that shows nothing - white space, zero-width and other invisible characters only - is blank
(`required`). A second PUT replaces the text; the same text again writes nothing. DELETE takes it down and is
a no-op when there is none. Both answer the review as the list shows it.

```json
"reply": {
  "text": "Thank you, Arthur…",
  "authorName": "Aram Hakobyan",   // console: the writer's full name as it was when they last saved it
  "authorRole": 1,                 // 1 Owner, 2 Manager (branch manager), 6 GeneralManager
  "createdAtUtc": "…",             // first published; a reply removed and written again starts over
  "updatedAtUtc": "…",             // last change of the text
  "edited": false
}
```

- **The public reads sign it short.** The public reviews, `recentReviews` and the author's own review
  carry `PublicReviewReplyView`: the same fields, with `authorName` as `"Aram H."` - the rule diners'
  names follow (`BranchReview.PublicReplyAuthorName`), absent when the name looks like contact details
  (the role then signs alone). Only the consoles read the full name.
- **Who.** The venue's own ranks only: the owner, a general manager, or this branch's manager - the
  same `ManagerOrAbove` + `BranchScoped` + K4 guard as the rest of these routes, then the stored role.
  **A platform admin is 403 `forbidden`**: the platform does not speak for a venue. **The last writer
  signs it** - a general manager editing the owner's reply makes it theirs.
- **Hidden reviews are not answered.** A PUT to a review that is taken down, by either tier, is 409
  `conflicting-state`; DELETE is always allowed. The reply follows the review's visibility: it is on
  the public reviews page and `recentReviews` only while the review is, and the venue (on this list)
  and the author (on `GET /api/diner/branches/{id}/review`) still see it while it is hidden. Written
  under the same row lock as a takedown, so the two serialise. A change that writes into the author's
  feed (a first reply, a removal, a takedown) takes the author's account lock first and the review's
  second - the order account deletion takes them in - and writes no notice into an account that is not
  live.
- **`CK_BranchReviews_Reply`** holds the six reply columns all null or all set, with `ReplyAuthorRole`
  1, 2 or 6.
- **The author is told once.** The first reply on a review writes a `review-replied` feed entry
  (no push, like `review-hidden`); an edit writes none. Removing the reply deletes that entry if the
  author has not read it yet - the console's undo is a DELETE seconds later.
- **Audited** in the same transaction: `review.reply`
  `{ actorType: "venue", branchId, edited, before }` (the previous text) and `review.remove-reply`
  `{ actorType: "venue"|"platform", branchId, before: { text, authorName } }`. A request that changes
  nothing writes nothing. 404 `not-found` for a review that is not at this branch. The platform's
  activity feed leaves out `review.reply` and a venue's `review.remove-reply`.

**The platform takes a reply down** with `DELETE /api/platform/reviews/{reviewId}/reply`
(`PlatformAdminOnly`, re-checked from the stored row; 404 for an unknown review): the same removal, on a
shown or hidden review, the author's unread notice taken back, audited `review.remove-reply` with
`actorType: "platform"` - and that row is shown in the activity feed. The platform never writes a reply.

---

## Rate limits

On by default in Production and off elsewhere; `RateLimiting:Enabled` overrides either way (K10
switches it on for Staging). Every value below is configurable under `RateLimiting:*`. A refusal is
429 `rate-limited` in the unified envelope, with `Retry-After`.

"Per caller" means per principal when the request carries a token - the diner account, the tab
participant, the tablet, the staff member - and per remote address otherwise.

| Routes | Endpoint policy (per caller) | Also |
| --- | --- | --- |
| Every request | - | Global limiter: 300 per 60 s per caller |
| `GET /api/public/branches`, `/api/public/branches/search`, `/api/public/venues` | `public-browse`: 120 per 60 s | One city-wide ceiling for the three: 6000 per 60 s |
| `GET /api/public/branches/{branchId}`, `…/reviews`, `…/rooms` | `public-place`: 120 per 60 s | Per-branch ceiling: 300 per 60 s, whoever asks |
| Other `/api/public` routes (branch page, menu, meta, availability, bookings) | see [public-surface.md](public-surface.md#rate-limits) | |
| `GET /api/diner/branches/{id}/review`, `/api/diner/orders`, `/api/diner/me` reads and edits, `GET /api/diner/favorites`, `GET /api/diner/notifications`, `POST /api/diner/notifications/read` | none - the global limiter only | |
| `POST`/`PUT /api/diner/branches/{id}/review`, `POST /api/diner/reviews/{id}/report`, `POST /api/diner/me/photo`, `POST /api/branches/{id}/photos`, `PUT /api/diner/favorites`, `PUT`/`DELETE /api/diner/favorites/{branchId}` | `diner-write`: 10 per 60 s per caller, one budget shared by all of them (`RateLimiting:DinerWritePermitLimit`, `DinerWriteWindowSeconds`) | |
| `DELETE /api/diner/me` | `auth`: 10 per 60 s | Per account, in the service: 10 attempts per 15 min, right or wrong → 429 `too-many-attempts` |
| `POST /api/auth/diner/request-code`, `/register` | `auth-code-request`: 5 per 300 s | `request-code` is also limited per phone number in the service |
| `POST /api/auth/diner/verify-code`, `/login`, `/refresh` | `auth`: 10 per 60 s | `login` is also limited per identifier: 10 per 15 min |

---

## Derived rules

- **rating** = average of `BranchReviews.Rating`, rounded to 0.1 away from zero; absent with no reviews.
- **new** = the branch row's `CreatedAtUtc` is within the last 30 days.
- **popular** = at least 20 `TableSessions` seated (walk-ins and bookings) in the last 30 days, **or**
  at least 5 reviews averaging at least 4.5. Absolute thresholds, so badges do not flicker with other
  venues' traffic.
- **distanceKm** = great-circle (haversine) distance from the sent `lat`/`lng`, to 0.1 km. The app may
  also compute it from `latitude`/`longitude`.
- **isOpenNow** = inside an opening block now in the branch's zone, including yesterday's
  past-midnight block, with special opening days replacing their date's weekly hours (the same rule as
  the web branch page). The detail's `specialDays` lists them from the service date (yesterday while
  last night's special block still runs past midnight) through 60 days after today - show today's from
  there when today is one - and the day's slots carry `specialDay` for the date asked.
- **marker state** = `TableStateProjection.Derive(physical status, next Confirmed/PendingApproval
  booking, now, branch buffer)` - the staff floor's rule.
- **opening a tab by booking code** (`POST /api/tabs/open-by-booking`) joins only the booking's own
  sitting. Another party's open sitting on the booked table answers `409 booking-table-occupied`
  ("Table 1 still has another party seated. Ask a member of staff to free it."); on the booking's own
  sitting the booker joins an existing tab **approved**, never pending. See [tabs.md](tabs.md).

---

## Migrations

| Migration | What it adds |
| --- | --- |
| `20260913224239_BranchListingReviewsGalleryAndPhotoMarkers` | `DiningTables.PhotoX`, `PhotoY` (nullable); `Branches.Cuisine`, `About`, `PriceLevel`, `WebsiteUrl`, `AmenityKeys`; table `BranchGalleryPhotos` (`BranchId`, `PhotoId`, `Position`; unique per branch and photo, and per branch and position); table `BranchReviews` (`BranchId`, `DinerUserId`, `Rating` 1-5 by check constraint, `Text` ≤ 1000, `UpdatedAtUtc`; `UX_BranchReviews_BranchId_DinerUserId`, and indexes on `(BranchId, UpdatedAtUtc)` and `DinerUserId`) |
| `20260913235419_TabParticipantsByDinerAccount` | `IX_TabParticipants_UserId`, including `TabId`, `Status` and `CanSeeTableTotal`, for the Orders tab's first read - see [SCHEMA.md](../SCHEMA.md#tabparticipant-and-tabjointoken) |
| `20260914102955_DinerSessionGenerationAndDeletion` (K1, K2) | `DinerUsers.SessionGeneration int NOT NULL DEFAULT 0`; `DinerUsers.DeletedAtUtc datetime2 NULL`; `DinerUsers.PhoneE164` nullable, with `UX_DinerUsers_PhoneE164` filtered to `PhoneE164 IS NOT NULL` so a deleted account gives its number back |
| `20260914110522_BranchFloorPlanVersion` (K6) | `Branches.FloorPlanVersion int NOT NULL DEFAULT 0`, an EF concurrency token |
| `20260914145450_ReviewModerationReportsAndReservationNote` (K8, its extension, K9) | `BranchReviews.HiddenAtUtc datetime2 NULL`, `HiddenReason nvarchar(500) NULL`, `HiddenByStaffMemberId uniqueidentifier NULL`, `HiddenByPlatform bit NOT NULL DEFAULT 0`; the `(BranchId, UpdatedAtUtc)` index replaced by `(BranchId, CreatedAtUtc)`; table `BranchReviewReports` (`ReviewId` cascading from its review, `DinerUserId` Restrict, `Reason nvarchar(32)` held to the five slugs by `CK_BranchReviewReports_Reason`, `Note nvarchar(500)`; `UX_BranchReviewReports_ReviewId_DinerUserId`, `IX_BranchReviewReports_DinerUserId`); `Reservations.Note nvarchar(500) NULL` |
| `20261016130309_RoomsWithPhotos` (K13) | table `FloorAreaPhotos` (`FloorAreaId` cascading from its room, `PhotoId` Restrict, `Position`; unique `(FloorAreaId, PhotoId)`, index `(FloorAreaId, Position)`); `DiningTables.PhotoId uniqueidentifier NULL` (Restrict to `Photos`); every existing `PhotoX`/`PhotoY` **cleared** - they were places on the cover; drops `DiningTables.X`, `Y`, `Width`, `Height`, `RotationDegrees`, `Shape` and `Branches.FloorWidth`, `FloorHeight`. Down restores those columns (canvas 1000x700, tables 80x80 rectangles at 0,0) and clears the pins |
| `20261017150829_BranchDistrict` | `Branches.District nvarchar(60) NULL` - every existing branch reads as not said until the venue sets it |
| `20260914154849_FavoritesAndNotifications` (K11, K12) | table `DinerFavorites` (`DinerUserId` cascading from the account, `BranchId` with no foreign key; `UX_DinerFavorites_DinerUserId_BranchId`); table `DinerNotifications` (`DinerUserId` cascading from the account, `Kind nvarchar(40)` held to the six kinds by `CK_DinerNotifications_Kind`, `ParamsJson nvarchar(2000)`, nullable `BranchId`, `ReservationId`, `TabId`, `OrderId` with no foreign keys, `ReadAtUtc`, `Sequence bigint IDENTITY`; indexes `(DinerUserId, CreatedAtUtc)`, the same filtered to `ReadAtUtc IS NULL`, `CreatedAtUtc`, and `ReservationId` filtered to non-null) |

---

## Development seed data

With `DevSeed:Enabled` true - on in `appsettings.Development.json`, and never registered outside
Development - every start seeds, idempotently:

- **The demo venue and branch**: venue `yalla-demo` ("Yalla Demo Cafe", a cafe), branch
  `yerevan-centre` ("Yerevan Centre", 12 Abovyan Street, `Asia/Yerevan`, on the Paid tier so tabs and
  ordering work), a waiter and a manager, eight tables (labels 1-4 seat two, 5-7 seat four, 8 seats
  ten) and opening hours.
- **The listing** (`DevListingSeeder`), filled only while every listing field - amenities included -
  is still empty: cuisine "Armenian & Mediterranean", an about text, price level 2, website
  `https://example.com/yalla-demo`, district "Kentron", no amenities. A database seeded before the
  district existed keeps its listing as it is - the seed does not write over a listing somebody has.
- **Five reviews** by five development reviewer accounts - "Dev Reviewer 1" to "Dev Reviewer 5", on the
  +374 99 000 051-055 test range, phone verified - rated 5, 4, 5, 3 and 4, dated 1 to 25 days back. A
  review is added only for a reviewer who has none at the branch.
- **A cover and a two-picture gallery** (B4), only while the branch has no cover: three JPEGs embedded
  in `Yalla.Infrastructure` (`DevSeed/cover.jpg`, `gallery-1.jpg`, `gallery-2.jpg`), drawn by code for
  this repository - no photograph or third-party artwork, see `DevSeed/README.md` - and stored through
  `IPhotoStorage` like an upload, so the three variants exist and every URL answers. The gallery is
  added only when the branch has none. A manager's own cover is never replaced; a cover somebody
  cleared comes back on the next start.
- **Bookings open** (B4), only while nobody has saved the reservation policy: `acceptsWebBookings` on
  and the policy marked reviewed, so `acceptsAppBookings` is true (K9). Once a manager saves the policy,
  the booking switch is theirs, off included.
- **Room photos and pins** (K13), only while no room at the branch has a photo: Windows gets the cover
  picture (the same photo row) with tables 1-4 pinned where they are drawn, Terrace gets `gallery-2.jpg`
  with tables 5-7 pinned on its three tables and 8 between them. A manager who takes a table off a photo
  keeps it off; one who removes every room photo gets the seed's back on the next start.

`DevListingSeedTests` proves that a dropped database and an empty photo folder start with the demo
branch in the browse list with a cover whose variants load, both rooms on the public rooms read with
every table pinned, a gallery whose pictures load and app bookings open - without the test inserting a
photo; that a second run adds nothing; that a manager's cover, amenities, saved policy and a table they
took off the photo survive the next seed; that seeding runs with `DevActor:Enabled` off; and that nothing is seeded outside
Development.

---

## Contract changes (K1-K13)

All errors use the existing problem shape. New error codes across the changes: `session-revoked`
(401, K1), `relocation-not-allowed` (403, K5), `floor-plan-changed` (409, K6), `review-needs-visit`
(403, K8), `bookings-not-accepted` (409, K9). `cover-changed` (409, K7) was removed with its route by
K13.

### K1. Revoking diner sessions - **implemented (B1)**

- Diner access tokens carry the claim **`sgen`**: `DinerUser.SessionGeneration` when the token was
  minted, as a string. A token with no `sgen` (minted before the claim) reads as `0`.
- **Every diner-authenticated route** answers **401 `session-revoked`** with
  `WWW-Authenticate: Bearer error="invalid_token"` when the account is inactive or deleted, or the
  token's `sgen` differs from the stored value. The check runs on token validation, before any handler.
- The check reads the account through a 5 s cache measured on the application clock.
  `InvalidateDiner(id)` takes effect at once on the process that bumped the generation (called after
  the commit), and a token *newer* than the cached generation bypasses the cache.
- **The generation is bumped by:** the number's owner proving it and displacing a registrant
  (`DinerUser.ProveNumber`); setting or changing a password; `SetActive(false)`;
  `DELETE /api/diner/me`. A password rehash at sign-in does not bump it.
- `PUT /api/diner/me/password` without a current password also compares the token's `sgen` with the
  stored row, past the cache. On an account that has a password, leaving it out is allowed only within
  ten minutes of a code or Telegram sign-in (the token's `phone_proved_at`; `docs/auth.md` §2) and is
  otherwise 422 `validation-failed` on `currentPassword`. `DinerPhoneGate` (bookings, holds, tabs from a booking, reviews) also
  requires an active, undeleted account.
- **Refresh behaviour.** Refresh tokens are revoked on displacement and on deletion, as before. A
  password change revokes the refresh tokens of **every other sign-in** (reason `password-changed`)
  and keeps the chain the caller's access token names in its **`rch`** claim (the refresh-token chain
  it was issued with; a token without it keeps nothing). Refresh refuses an inactive or deleted account.
- **Client rule:** on `session-revoked`, try the refresh token once; if refresh is refused too, sign
  out. After a password change - which ends the caller's own access token as well - the refresh
  succeeds on the device that made the change and the app carries on; other devices' refresh is
  refused (401 `refresh-token-invalid`) and they sign out. No request or response shape changed.

### K2. `DELETE /api/diner/me` - **implemented (B1)**

- **Policy:** `VerifiedDiner`; the `auth` rate-limit policy on the route, plus 10 attempts per 15 min
  per account in the service.
- **Body:** `{ "password": string | null, "code": string | null }`.
  - The account has a password → `password` is required.
  - It has none → `code` is required: a one-time code from `POST /api/auth/diner/request-code` for the
    account's own phone.
- **204 on success.**
- **Errors:**
  - 422 `validation-failed` naming `password` or `code`, bound `required` (an empty `{}` body included)
  - 401 `invalid-credentials` - a wrong password, or a wrong, expired or missing code
  - 429 `too-many-attempts` - the account's attempts are spent, or the code's five are
- **Effects, in one transaction:**
  - `DeletedAtUtc` is set and `IsActive` is false.
  - `Username`, `Email`, `PhoneE164`, `DisplayName`, `PasswordHash`, `PhoneVerifiedAtUtc` and `PhotoId`
    are nulled, so the phone, username and email can register again.
  - `SessionGeneration++`; every refresh token revoked (`account-deleted`); push devices deleted.
  - Every `PhoneVerificationCodes` row for the account's number is deleted, the code that proved the
    deletion included; the audit row counts them as `phoneCodes`.
  - A favourite, review, review report, profile edit, password, picture or booking written for the
    account while it is being deleted (another phone, past the token cache) is refused with 401
    `session-revoked` instead of outliving it; one that committed first is removed or detached with
    the rest. A booking reminder or order-ready feed entry written in that window is left out.
  - Every `Photo` the account owns and its files are deleted - the current picture and any replaced
    one still waiting for the sweep.
  - Every `BranchReview` by the diner is deleted; the aggregates recompute on the next read.
  - `TabParticipants.UserId` → null and its display name → **`"Guest"`** (the column is NOT NULL; the
    app already shows an account-less participant this way).
  - `Reservations.DinerUserId` → null. The guest name and phone on the reservation stay as the venue's
    record.
  - Orders are kept.
  - One `PlatformAuditLogs` row, `Action` `diner.delete`, `TargetType` `DinerUser`, `TargetId` the
    account. `ActorStaffMemberId` holds the diner's id (no staff member acted); `ChangesJson` carries
    `actorType: "diner"` and the counts removed and detached, and nothing identifying.
- Review reports (K8 extension) are removed by the same transaction: the ones the diner filed, and every
  report about one of the diner's reviews. The audit row counts them as `reviewReports`. Favourites
  (K11) and the notifications feed (K12) are removed the same way, one line each in
  `DinerAccountDeletion.RemoveRowsOwnedByAsync`, counted as `favorites` and `notifications`.

### K3. `DELETE /api/diner/me/photo` - **implemented (B1)**

No shape change. It now deletes the Photo row and its files immediately
(`IPhotoService.DeleteAsync`). Afterwards `GET /api/photos/{id}/{variant}` returns 404.

### K4. Managers limited to their home branch - **implemented (B2)**

- **Refused:** on every route with the `BranchScoped` policy, a VenueUser **Manager** whose
  `StaffMember.BranchId` is set and differs from the route's branch gets 403 `forbidden`.
- **Enforced twice.**
  - `BranchScopedHandler`, from the token's `branchId` claim.
  - Again in the services through `IStaffBranchGuard.RequireAtBranchAsync`, which reads the stored staff
    row, so a reassignment, demotion or deactivation takes effect before the token expires. That covers:
    - the listing (read and save)
    - the settings: public profile, reservation policy, opening hours, floor plan, floor areas, table
      delete, QR regeneration, pins
    - photo upload, and the branch booking list, approve and reject
- The same rule holds in `StaffBranchGuard.RequireAsync`, the check under the tab, order, payment and
  service-request routes that are addressed by a bare id.
- A StaffSession (PIN) token is confined to its tablet's branch whatever the role.
- **Unchanged:** owners (whether or not their row names a branch), managers with no branch, and platform
  admins.
- `GET /api/venues/{venueId}/manage` lists the same set of branches a manager is allowed.

### K5. `PUT /api/branches/{branchId}/listing`: relocation - **implemented (B2)**

- **Body:** the existing fields, with `address: string | null`. `GET /listing` returns `address`.
- **A relocation** is `latitude`, `longitude` or `address` present **and** different from the stored
  value. Repeating the stored values is not one. A blank address counts as absent.
- **Who may relocate:** an active Owner of this venue or an active platform admin, **signed in to the
  admin panel** (a VenueUser token, read from the principal type; roles read from the stored row).
  - Anyone else gets 403 `relocation-not-allowed` (with `context.branchId`) and **nothing** is written:
    the save is atomic, other fields included.
  - That includes any PIN session, an owner's too.
  - The refusal is decided before the field rules, so a manager is not asked to complete a move they
    would then be refused.
- **Validation:** coordinates without an address, or an address without coordinates, is 422
  `validation-failed` naming `address` (or `latitude` and `longitude`), bound `required`. The address is
  at most 400 characters (bound `max`). Out-of-range coordinates stay 400 `invalid-request`.
- **Audit:** `PlatformAuditLogs` `Action="branch.relocate"`, `TargetType="Branch"`,
  `TargetId=branchId`, `ActorStaffMemberId` the owner or admin, `ChangesJson`
  `{ "old": {address, latitude, longitude}, "new": {…} }`, in the same transaction as the move. The log
  line carries the actor id only.

### K6. Floor plan version - **implemented (B2)**

- `GET /api/branches/{branchId}/floor-plan` adds `version: string`, opaque (today the decimal
  `Branches.FloorPlanVersion`, an int concurrency token; do not parse it).
- **PUT body** adds a required `expectedVersion: string`. (Pins were taken off this form here and put
  back on it by K13, where they belong to rooms rather than to the cover.)
- **Errors:**
  - missing or blank `expectedVersion` → 422 naming it, bound `required`
  - stale or unreadable → 409 `floor-plan-changed` with `context.currentVersion`, nothing written
- **Success:** 200 with the existing body; `plan.version` is the new revision. A successful
  floor-plan PUT bumps the version, and so do `POST`, `PATCH` and `DELETE …/floor-areas` - each changes
  the plan, and an editor that loaded it before must get 409 rather than resurrect a deleted room or
  delete a new one. Reload the plan after any of them.
- The replace takes an update lock on the branch row (shared with the room add, rename and delete)
  and compares the version inside it. Another save holding that lock past 5 s answers 409 `concurrent-update` - reload
  and retry.

### K7. `PUT /api/branches/{branchId}/table-photo-positions` - **removed (K13)**

Pins on the cover photo had their own route and their own `409 cover-changed`. Rooms with photos (K13)
replaced both: a pin is on a room photo and is saved with the rooms, so the route, its models and the
error code are gone, and changing the cover no longer touches any table.

### K13. Rooms with photos - **implemented (rooms phase 1, backend)**

A room (`FloorArea`) has 0-10 ordered photos; a table is pinned on one of **its own room's** photos -
`photoId` plus `photoX`/`photoY`, fractions 0-1 from the left and the top - or on none. The drawn floor
plan is gone: no canvas, no table geometry, no `TableShape`.

**Room photos at 4:3 (venue console phase 3, 2026-10-19).** The console and the apps show room photos in
a 4:3 frame, like dish and cover photos - but the server never crops: every photo is stored whole at its own
aspect (resized only, upright from EXIF), `PhotoView.width`/`height` are its real size, and a pin is a fraction
of that whole image. Cropping on upload would move every pin already placed. So a pin is drawn over the photo
*contained* in the frame (letterboxed when it is not 4:3) at `left + x * width`, `top + y * height` of the
image's own rect, and placing one inverts that. Drawn with `object-fit: cover`, a pin on any non-4:3 photo
would be shown - and saved - somewhere else. No migration: existing pins are already fractions of the whole
image.

**`GET /api/branches/{branchId}/floor-plan`** → `FloorPlanView`
`{ branchId, areas: [ { id, name, displayOrder, photos: [PhotoView] } ], tables: [ { id, label, seats,
floorAreaId?, isBookable, isActive, qrToken, status, isDeletable, photoId?, photoX?, photoY? } ], version }`.
Inactive tables included. Null fields are omitted.

**`PUT /api/branches/{branchId}/floor-plan`** - body `ReplaceFloorPlanCommand`
`{ areas: [ { id?, name, displayOrder, photoIds?: [uuid] } ], tables: [ { id?, label, seats,
floorAreaName?, isBookable, photoId?, photoX?, photoY? } ], expectedVersion }`. Replace-whole: rooms,
their photos (in the order sent; null or absent = none) and every pin. Kept: the branch lock, `version`
/ `409 floor-plan-changed`, history-aware delete vs deactivate, staging labels, QR tokens never
changed. 200 → `FloorPlanReplaceResult { plan, warnings, deactivatedTables, removedTables }`.
`deactivatedTables` lists only tables **this save** took out of service (active before, inactive
after); an inactive table the editor leaves out, as it always does, is not reported again.
`removedTables` lists the rows this save deleted.

| Rule | Answer (nothing written) |
| --- | --- |
| Missing/repeated table label, missing/repeated room name, unknown `floorAreaName`, table or room `id` sent by two entries, omitted occupied table, a label held by an inactive table or by one this save deactivates | 422 `floor-plan-invalid`, `context.errors[]`, `context.duplicateLabels[]` (`tablesOutsideCanvas` is gone) |
| More than 10 photos in a room | 422 `validation-failed`, field `areas[i].photoIds`, bound `max`, `max` 10 |
| A photo twice in one room, or in a second room | 422, field `areas[i].photoIds[j]`, bound `conflict` |
| An empty photo id | 422, field `areas[i].photoIds[j]`, bound `required` |
| A photo not uploaded for this branch | 404 `not-found` (the gallery's answer) |
| A pin half sent (any of the three without the others) | 422, fields `tables[i].photoId` / `photoX` / `photoY`, bound `required` |
| A coordinate outside 0-1 | 422, `tables[i].photoX` or `photoY`, bound `range` |
| A pin on a photo that is not the table's room's (another room's, or the table has no room) - unless the photo was the table's room's before this save (below) | 422, `tables[i].photoId`, bound `conflict` |
| `expectedVersion` missing / stale | 422 `expectedVersion` `required` / 409 `floor-plan-changed` |

**Matching rooms:** every entry's `id` is matched to a stored room first; only then are names matched,
and only against rooms no `id` claimed. So renaming Hall to Lounge and adding a new Hall in one save
makes two rooms, in either order. An `id` this branch does not have is matched by name, else new.

**Matching tables:** the same order - every entry's `id` first (active or inactive), then labels,
and only against **active** tables no `id` claimed. Renumbering 5 to 13 and adding a new 5 in one save
keeps 13's id, QR code and history and creates a new 5; swapping 5 and 13 still works (every renamed
table is parked on a staging label first). A label is never matched to an inactive table: a new entry
with an inactive table's label is 422 `floor-plan-invalid`, "Table 5 is out of service. Put it back
instead of adding a new table with the same label." - putting it back means sending its `id`. Renaming
another table onto it gets "Table 5 is out of service and keeps its label. Put it back, or give table
13 another label."

**Taken off, not refused:** a pin on a photo the save drops from every room comes off (the table is
saved unpinned, and `warnings` says so); so does a pin on a photo the save **moves out of the table's
room** - it was that room's before the save and is another room's after it - with its own warning
("taken off photos this plan moved to another room"). Removing and moving a photo are the manager's
decision about the rooms, and answer the same way. A bookable table with no pin is saved with a warning.
Dropping a room takes its photos (cascade) and its tables' pins. **`DELETE …/floor-areas/{areaId}`**
keeps the room's tables, with no room and no pin. `POST`/`PATCH …/floor-areas` answer `FloorAreaView`
with `photos` (empty for a new room), and refuse a name another room at the branch has (trimmed, any
case) with 422 `validation-failed`, field `name`, bound `conflict` - the rule the PUT applies.

**The orphan sweep and a save:** the PUT holds (`UPDLOCK`) the photo rows it checked until it commits,
so the sweep cannot delete a just-uploaded photo out from under it; a photo swept before the check is
404 as above.

**Diners are only offered pinned tables** - pinned on a photo of the table's own room; a pin on another
room's photo counts as none. `GET /api/public/branches/{id}/rooms` (above),
`GET /api/branches/{id}/availability` and its public twin list pinned tables only; a booking naming an
unpinned table is refused like a walk-in-only one (422 `reservation-table-not-bookable`). The public branch page's
`floorPlan` lists every active table, with `isBookable` false for an unpinned one. Staff are not
limited: `GET /api/branches/{id}/tables/floor` lists every active table with `floorAreaId`,
`floorAreaName`, `floorAreaDisplayOrder` and its pin (no geometry, no `floorWidth`/`floorHeight`).

**Platform:** `floorWidth`/`floorHeight` are gone from create/update branch and from `BranchSummary`.
Readiness rules are unchanged. The orphan photo sweep keeps photos a room shows or a table is pinned on.

### K8. Reviews - **implemented (B3)**

**Eligibility.** A **first** review - `POST /api/diner/branches/{branchId}/review`, or `PUT` when the
diner has none yet - needs, within the last 180 days (`BranchReview.VisitWindowDays`), a reservation of
this diner's at the branch whose status is Seated or Completed (measured by its `StartUtc`), or a
`TabParticipant` with `UserId` = the diner on one of the branch's tabs that was **approved** - the host,
or a joiner the host let on - measured by `ApprovedAtUtc`. A place still `PendingApproval`, or rejected
without ever being approved, does not count: joining needs only the host's shareable invitation link.
A place approved and later removed still counts.
Otherwise 403 `review-needs-visit` with `context: { branchId, windowDays: 180 }`. It is checked after
the phone gate (`phone-not-verified` first) and, on POST, after "already reviewed" (409). Revising the
diner's own existing review is always allowed.

**Rate limit.** `diner-write`: 10 requests per 60 s per principal, one budget shared by review POST and
PUT, `POST /api/diner/reviews/{id}/report`, `POST /api/diner/me/photo` and `POST /api/branches/{id}/photos`
(K11's writes join it). Over it: 429 `rate-limited`. `RateLimiting:DinerWritePermitLimit` and
`DinerWriteWindowSeconds` configure it.

**Identical PUT.** Same rating and trimmed text → 200, nothing written, `updatedAtUtc` unchanged.

**Public review list.** `/reviews` and `recentReviews` are ordered by `createdAtUtc` descending (index
`(BranchId, CreatedAtUtc)`); each item adds `edited: boolean` (`updatedAtUtc` differs from
`createdAtUtc`) and `reply`, the venue's `PublicReviewReplyView` - signed `"Aram H."` (absent until the
venue answers; see the reply route above). Hidden reviews are left out of the list, `rating`, `reviewCount` and so the badges, on
the list, search and details routes alike.

**`authorName`.** The rule under the reviews route above (`BranchReview.PublicAuthorName`). The
moderation lists use the same name.

**The diner's own review.** `DinerReviewView` adds `publicAuthorName: string`, `hidden: boolean` and
`reply` (the venue's answer as `PublicReviewReplyView`, absent until there is one - shown to the author
even while hidden).

**Platform moderation** (`PlatformAdminOnly`; the service re-checks an active platform admin from the
stored row; audited):
- `GET /api/platform/branches/{branchId}/reviews?page=1&pageSize=20&filter=all` → `ModeratedReviewPage`,
  the shape under the venue console's review routes above - for any branch, published or not.
- `PUT /api/platform/reviews/{reviewId}/visibility`, body `{ "hidden": boolean, "reason": string | null }`:
  `hidden` required; `reason` required when hiding, trimmed, at most 500 characters, else 422 naming
  `reason`; with `hidden: false` a `reason` is accepted and ignored; 200 with the item; 404
  `not-found`; audit `review.hide` or `review.unhide` with `actorType: "platform"`.
- As built, items carry `hiddenByPlatform`, `reportCount` and `lastReportedAtUtc` beyond the fields first
  specified, and the list accepts `filter`: one shape and one set of parameters for both tiers.

#### K8 extension: venue moderation and diner reports - **implemented (B3)**

The user's decision: moderation and reporting are built now.

- **Venue moderation.** `GET /api/branches/{branchId}/reviews?page=&pageSize=&filter=all|reported|hidden`
  and `PUT /api/branches/{branchId}/reviews/{reviewId}/visibility` `{ hidden, reason }` - policy
  `ManagerOrAbove` + `BranchScoped` + the K4 guard; audited `review.hide`/`review.unhide` with
  `actorType: "venue"`. Shapes and rules are under the venue console's review routes above. A review the
  platform hid cannot be put back by a venue (403 `forbidden`), and a venue hiding it again changes
  nothing; one a venue hid can be put back by the platform.
- **Diner report.** `POST /api/diner/reviews/{reviewId}/report`, body
  `{ reason: "spam"|"offensive"|"not-a-visit"|"personal-info"|"other", note: string|null (≤ 500) }` →
  204. One report per diner per review (a repeat is a 204 no-op); reporting one's own review is 409
  `conflicting-state`; a hidden or unknown review, or one at a branch that is not published, is 404. An
  unknown reason is 422 naming `reason` with bound `range`. Entity
  `BranchReviewReport { ReviewId, DinerUserId, Reason, Note, CreatedAtUtc }`, unique
  `(ReviewId, DinerUserId)`, `Reason` held to the five slugs by `CK_BranchReviewReports_Reason`. Policy:
  any diner token (verified number not required); `diner-write`.
- The public list shape is unchanged: the app shows "Report" on every review not written by the
  signed-in diner.
- **Account deletion (K2)** deletes, in its transaction, the reports the diner filed and every report
  about the diner's reviews; the audit row counts them as `reviewReports`.
- Stored as `BranchReviews.HiddenAtUtc`, `HiddenReason`, `HiddenByStaffMemberId` and `HiddenByPlatform`,
  in migration `20260914145450_ReviewModerationReportsAndReservationNote`.

### K9. Booking gate and booking note - **implemented (B3)**

- **New field:** `PublicBranchDetail` and `PublicBranchPage` carry
  `acceptsAppBookings: boolean` = `AcceptsWebBookings && ReservationPolicyReviewedAtUtc != null`, read live
  on both.
- **Gate:** `POST /api/reservations` with channel App (`1`) when `!acceptsAppBookings` → 409
  `bookings-not-accepted` with `context.branchId`. Checked after the replay lookup - a retry of a booking
  that already exists still returns it - and after the Web rule. Web (`2`) behaves as before; Unknown
  (`0`) and Staff (`3`) are not gated.
- **Note on create:** `CreateReservationRequest` adds `note: string | null`, trimmed, blank → null (absent
  on reads), over 500 characters → 422 `validation-failed` naming `note`, bound `max`, checked before
  anything else about the booking. The note is sent to the venue. The schema declares no `maxLength` for
  it, because the request validation filter reports every attribute failure with bound `required`.
- **Note on reads:** `note` (absent when none) on every `ReservationView` - the branch list and approval
  queue (`GET /api/branches/{id}/reservations`), `GET /api/reservations/mine`, and the create, cancel,
  approve and reject answers - and on `GET /api/public/bookings/{token}`. There is no separate diner
  booking-detail route; `/mine` is that read.

### K10. Configuration - **implemented (B4)**

- `ForwardedHeaders:KnownProxies: string[]`, `ForwardedHeaders:KnownNetworks: string[]` (CIDR),
  `ForwardedHeaders:ForwardLimit: int = 1`. `X-Forwarded-For` and `X-Forwarded-Proto` are read only on
  a connection from a listed proxy or network, and only the last `ForwardLimit` hops. Empty trusts no
  proxy: the middleware is not added at all, so the header is ignored from every address, loopback
  included. In Production with rate limiting on and nothing listed, a warning is logged at startup. A
  value that is not an address, a CIDR block or a limit of at least 1 stops startup. Called first in the
  pipeline, before the Swagger allowlist, authentication and the rate limiter. See README.md, "Behind a
  reverse proxy".
- `PhotoStorage:SweepIntervalMinutes: int = 60` (0 turns the sweep off; negative stops startup).
  `PhotoSweepHostedService` runs the orphan sweep that often, first one interval after start, each pass
  in its own scope; a failed pass is logged and the next still runs. The sweep takes a photo nothing
  references once it is more than 24 hours old, so a replaced picture's URL keeps working for at least
  a day and stops within one interval after that.
- `PhotoStorage:RootPath` starting with `~` (or blank) resolves under the user's local application data
  folder: Development's `~/photos` is `%LOCALAPPDATA%\Yalla\photos`, shared by every worktree that
  shares the database. The absolute root is logged at startup.
- `appsettings.Staging.json`: `RateLimiting:Enabled = true`.
- `GET /api/branches/{id}/readiness` is unchanged; only the frontend starts using it.

### K11. Favourites - **implemented (B6)**

The user's decision: favourites are synced to the account, not kept on the phone.

- **Entity** `DinerFavorite { DinerUserId, BranchId, CreatedAtUtc }`, unique `(DinerUserId, BranchId)`
  (`UX_DinerFavorites_DinerUserId_BranchId`, which leads with `DinerUserId` and is the index on it),
  foreign key cascading from the account. K2 deletion also removes them explicitly (`favorites` in the
  audit row's counts). As built there is **no foreign key to the branch**: a heart is checked against a
  published branch when added and read back through the same rule.
- **Policy:** any diner token (`VerifiedDiner`, which admits an account whose number is not proved). No
  diner id in any route.
- `GET /api/diner/favorites?lat=&lng=` → `DinerFavoriteList`
  `{ items: [ { branchId, createdAtUtc, listing: PublicBranchListing } ] }`, newest first. A branch that
  is not published (inactive, or its venue suspended or deleted) is left out and its row kept, so it comes
  back if the branch reopens. `lat`/`lng` as the public list - together or neither, else 400 - adding
  `distanceKm`. The branch rows are read live; rating, count, badges, open-now and free tables are the
  list's 15 s cached numbers.
- `PUT /api/diner/favorites/{branchId}` → 204, idempotent (re-sending a kept place is 204, even at the
  limit); 404 `not-found` for an unknown or unpublished branch; a new heart past 500 → 409
  `conflicting-state`.
- `DELETE /api/diner/favorites/{branchId}` → 204, idempotent, including for an unknown or closed place.
- `PUT /api/diner/favorites`, body `{ branchIds: uuid[] }` → 200 with the GET shape (and the same
  `lat`/`lng`). Adds every published place not already kept and **removes nothing**; unknown, unpublished
  and duplicate ids are skipped, not refused. `branchIds` missing → 422 `validation-failed` naming
  `branchIds`, bound `required`; more than 500 → the same, bound `max`. A merge that would take the account
  past 500 → 409 `conflicting-state`, and nothing is written. A bad position is refused before anything
  is written. Used once at sign-in to upload hearts made signed out.
- **Rate limit:** `diner-write` on the three writes; the GET is on the global limiter only.
- Tests: `DinerFavoriteTests`, and `DinerAccountDeletionTests` for the deletion.

### K12. Notifications feed - **implemented (B6)**

The user's decision: the feed is built now. As built:

- **Entity** `DinerNotification { Id, DinerUserId, Kind, ParamsJson, BranchId?, ReservationId?, TabId?, OrderId?, CreatedAtUtc, ReadAtUtc?, Sequence }`.
  No `Title` column: the server writes no prose, and the app builds the title and body from `kind` and
  `params`. `Sequence` is a database identity that breaks ties for the cursor and is never sent.
  `CreatedAtUtc` is when the entry **appears**.
- **Where each kind is written**, always in the unit of work that saves the change, whether or not the
  diner has a device:

  | `kind` | Written when | Beside the push |
  | --- | --- | --- |
  | `booking-reminder` | A booking with an account is created and its reminder enqueued. `createdAtUtc` is the reminder's due time; the feed shows it from then | `reservation.reminder` |
  | `booking-confirmed`, `booking-declined` | A manager approves or rejects a pending booking | `reservation.decided` |
  | `booking-cancelled-by-venue` | Staff release a booking with outcome 2 (`CancelledByVenue`) | none exists - written with the release |
  | `order-ready` | An order moves to Ready at a branch with `NotifyOnOrderReady` on, for the ordering participant's account | `tab.order-ready` |
  | `review-hidden` | The platform or the venue takes down a review that was shown (not again when a hidden review is re-hidden) | none exists - written with the takedown |
  | `review-replied` | The venue first replies to the diner's review (not on an edit). Deleted while unread if the venue removes the reply | none - written with the reply |
  | `booking-changed-by-link` | Somebody holding the booking's manage link changes it - never the diner's own change in the app. `params` add `status` (the `ReservationStatus` number: 1 waiting for the venue, 2 confirmed); `date`/`time`/`partySize` are where it stands now | `reservation.changed-by-link` |
  | `booking-cancelled-by-link` | Somebody holding the booking's manage link cancels it - never the app's cancel | `reservation.cancelled-by-link` |

  The late nudge and "participant approved" pushes write no entry. Cancelling a booking - by the diner,
  through the manage link, by rejection or by release - deletes its entries that have not appeared yet
  (the reminder), alongside its unsent outbox messages.
- **`params`** (all string values, as they were when written): booking kinds `venueName`, `branchName`,
  `date` (`yyyy-MM-dd`, local), `time` (`HH:mm`, local), `partySize`, `reservationCode`; `order-ready`
  `venueName`, `branchName`, `tableLabel`; `review-hidden` and `review-replied` `venueName`, `branchName`,
  `reviewId`.
- `GET /api/diner/notifications?before=<cursor>&limit=20` → `DinerNotificationPage`
  `{ items: [ { notificationId, kind, params, branchId, branchName, reservationId, tabId, orderId, createdAtUtc, read } ], nextCursor, unreadCount }`,
  newest first by `(createdAtUtc, sequence)`. Only entries that have appeared. `branchName` is the
  branch's name now, or as written if the branch is gone. `nextCursor` is opaque and absent on the last
  page. `limit` 1-50, else 400; a `before` this feed did not issue → 400 `invalid-request`. `unreadCount`
  covers the whole feed, not the page.
- `POST /api/diner/notifications/read`, body `{ upTo: notificationId | null, ids: uuid[] | null }` → 204.
  `upTo` marks that entry and every older one; `ids` (at most 200, else 422 naming `ids`, bound `max`)
  marks those; both may be sent. **Both absent marks every entry read** ("mark all read"). An unknown id,
  or one that is not the caller's, is skipped rather than refused. An entry that has not appeared is
  never marked.
- **Policy:** any diner token; no rate limit beyond the global one.
- **Retention:** `DinerNotificationRetention` deletes entries older than 90 days, run by the outbox's
  background loop (`OutboxHostedService`) on its wake-up pass and then about hourly. It rides on that
  loop, so it does not run while `Outbox:Enabled` is false.
- **Account deletion (K2)** removes them (`notifications` in the audit row's counts).
- Tests: `DinerNotificationFeedTests`, and `DinerAccountDeletionTests` for the deletion.

The specification as first written:

- Source: the existing outbox and push pipeline. Entity
  `DinerNotification { Id, DinerUserId, Kind, Title?, BodyKey/params JSON, BranchId?, ReservationId?, TabId?, OrderId?, CreatedAtUtc, ReadAtUtc? }`,
  `Kind` one of `booking-reminder`, `booking-confirmed`, `booking-declined`,
  `booking-cancelled-by-venue`, `order-ready`, `review-hidden`. Written **where the push is enqueued**,
  in the same transaction, whether or not the diner has a push device. The app writes the words from
  `kind` and `params`; the server sends no prose.
- `GET /api/diner/notifications?before=<cursor>&limit=20` →
  `{ items: [ { notificationId, kind, params, branchId, branchName, reservationId, tabId, orderId, createdAtUtc, read } ], nextCursor, unreadCount }`.
- `POST /api/diner/notifications/read`, body `{ upTo: notificationId | null, ids: uuid[] | null }` → 204.
- Retention: rows older than 90 days are deleted by the existing sweep or hosted service. K2 deletion
  removes the diner's notifications.

---

## App model → API field map

### `Place` (`apps/diner/src/places/model.ts`)

| App field | Source |
| --- | --- |
| `id` | `listing.branchId` |
| `venueId` | `listing.venueId` |
| `name` | `listing.venueName` (the card title; `listing.branchName` is the location - show "Venue · Branch" when a venue has several) |
| `type` | `listing.venueType`: 1 → `'cafe'`, 2 → `'restaurant'`, 3 → `'bar'` |
| `cuisine` | `listing.cuisine` - absent → `''`, hide the line |
| `distanceKm` | `listing.distanceKm` when `lat`/`lng` were sent; else computed from `latitude`/`longitude` and the device position; no position → unknown, hide |
| `rating` | `listing.rating` - absent → no rating yet |
| `ratingCount` | `listing.reviewCount` |
| `badges` | `listing.badges` (`"popular"`/`"new"`, the same values as `PlaceBadge`) |
| `openState` | computed client-side by `openStateFor(hours, now, timeZoneId)`; `listing.isOpenNow` is the server's answer (15 s cache) |
| `photos` | `[listing.coverPhoto.cardUrl or fullUrl, ...detail.gallery[].cardUrl]`, resolved against the API origin. The first, the cover, carries the markers |
| `coords` | `{ latitude: listing.latitude, longitude: listing.longitude }` |
| `address` | `listing.address` |
| `district` | `listing.district` - absent → the app's old fallback; the area in "type · cuisine · area" |
| `phone` | `detail.phoneE164` (absent → undefined) |
| `website` | `detail.websiteUrl` (absent → undefined) |
| `timeZoneId` | `listing.timeZoneId` |
| `hours` | `detail.openingHours[]`: `day` → `day` (0 = Sunday on both sides), `opensAt`/`closesAt` `"HH:mm:ss"` → `"HH:mm"`. Split service gives several entries for one weekday; `hoursOn` picks the first |
| `amenities` | `detail.amenities` (the same keys as `place.amenity.*`) |
| `about` | `detail.about` - absent → `''` |
| `menu` | `GET /api/public/branches/{id}/menu` (`BranchMenuView`): categories → `section`, items → `{ name, price: priceAmd, description }` |
| `reviews` | `detail.recentReviews[]` (or `/reviews?page=`): `authorName` → `author`, `rating`, `text` (absent → `''`), `updatedAtUtc` (or `createdAtUtc`) → `date` as `YYYY-MM-DD` |
| `rooms` | `detail.rooms[]` or `GET …/rooms` - see below |

Repository methods: `listNearby()` → `GET /api/public/branches?lat&lng`; `search(q, filter)` →
`GET /api/public/branches/search?q&category` (`filter.type` → `category`; `filter.badge` filtered
client-side on `badges`); `getById(id)` → `GET /api/public/branches/{id}` (404 → `null`) plus the menu;
`rooms(id)` → `GET /api/public/branches/{id}/rooms`.

### `TablePhotoMarker`

One entry of `rooms[].photos[].tables[]`; the photo it is drawn on is that entry's `photo`.

| App field | Source |
| --- | --- |
| `tableId` | `tableId` |
| `label` | `label` |
| `status` | `state`: 1 Free → `'free'`; 2 ReservedSoon, 3 Held → `'reserved'`; 4 Occupied, 5 OutOfService → `'occupied'` |
| `capacityMin` | **unsupported** - tables have no minimum party size; use `1` |
| `capacityMax` | `seats` |
| `x`, `y` | `photoX`, `photoY` |

### `Order` (`apps/diner/src/orders/model.ts`)

| App field | Source |
| --- | --- |
| `id` | `orderId` |
| `placeId` | `branchId` |
| `placeName` | `venueName` (optionally with `branchName`) |
| `placePhoto` | `coverPhoto.thumbnailUrl` or `cardUrl`; absent → placeholder |
| `kind` | `kind` - always `'dineIn'` |
| `status` | `status` (already the app's word) |
| `tableLabel` | `tableLabel` |
| `partySize` | `partySize` |
| `placedAt` | `placedAtUtc` |
| `items[]` | `items[]`: `lineId` → `id`, `name`, `quantity`, `unitPriceAmd` → `unitPriceDram`, `note`. A voided line has `isVoided` true and `lineTotalAmd` 0 - drop it or strike it through |
| `totalDram` | `totalAmd` (non-voided lines, before service charge) |
| `timeline[]` | `timeline[]`: `status`, `atUtc` → `at` |

Repository: `list()` → `GET /api/diner/orders` (both segments; `splitOrders` still applies the 24 h
window client-side); `getById(id)` → `GET /api/diner/orders/{id}` (404 → `null`); `cancel(id)` →
**unsupported** (reject with `OrderNotCancellableError`; `canCancel` is always false).

---

## Not backed by data, and why

- **The `inProgress` order status and the `takeaway` kind.** The domain has no takeaway; every order is
  on a table's tab. Never produced.
- **A diner cancelling an order.** Voiding is a staff action on the kitchen rail. Not invented;
  `canCancel` is always false and there is no endpoint.
- **`TablePhotoMarker.capacityMin`.** Tables have only `seats`.
- **Markers on a photo other than the cover.** Coordinates are defined against the cover only (the
  app's first photo). Changing the cover takes every table off it; there is no separate floor-photo
  entity.
- **Several hero photos per table view.** Not modelled.
- **A diner deleting a single review.** Not built; deleting the account deletes all of them (K2).
  Moderation hides a review rather than deleting it, and a diner can report somebody else's (K8
  extension).
- **Distance without a position.** The server does not know where the diner is; `distanceKm` is present
  only when `lat` and `lng` are sent.
