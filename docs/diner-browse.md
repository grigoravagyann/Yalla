# Diner browse, orders and account: the real-data contract

What the diner app and the venue console read and write for Explore, search, the map, a place's
details, its reviews, the photo table view, the Orders tab and the diner's own account - the shapes,
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
| K4 | Managers limited to their home branch | B2 | In progress |
| K5 | Listing PUT: relocation is owner or platform admin only | B2 | In progress |
| K6 | Floor plan `version` / `expectedVersion` | B2 | In progress |
| K7 | `PUT /api/branches/{branchId}/table-photo-positions` | B2 | In progress |
| K8 | Review integrity, plus venue moderation and diner reports (extension) | B3 | In progress |
| K9 | App booking gate and booking note | B3 | In progress |
| K10 | Proxy, photo sweep and Staging rate-limit configuration | B4 | In progress |
| K11 | Favourites synced to the account | B6 | In progress |
| K12 | Diner notifications feed | B6 | In progress |

Backend migrations run one after another in that order (B1, B2, B3, B6), because the test fixture
builds the schema with `MigrateAsync`.

---

## Wire conventions

These apply to everything below.

- JSON camelCase. **Null fields are omitted**, not written as `null` - treat a missing key as null.
- Enums are **integers**:
  - `venueType`: 1 Cafe, 2 Restaurant.
  - `state` (`DerivedTableState`): 1 Free, 2 ReservedSoon, 3 Held, 4 Occupied, 5 OutOfService.
  - `kitchenStatus` (`TabOrderStatus`): 1 New, 2 InKitchen, 3 Ready, 4 Served, 5 Voided.
  - `day` (`DayOfWeek`): 0 Sunday … 6 Saturday.
- `TimeOnly` is `"HH:mm:ss"` (e.g. `"09:00:00"`). `DateTime` is ISO-8601 UTC.
- Photos are `PhotoView`: `{ photoId, thumbnailUrl, cardUrl, fullUrl, width?, height? }`. URLs are
  root-relative `/api/photos/{id}/{thumbnail|card|full}` - resolve them against the API origin -
  unless the photo is hosted elsewhere, in which case they are absolute.
- Errors use the unified envelope `{ type, title, status, code, detail, instance, traceId, context? }`.
  Codes a client branches on here: `invalid-request` (400), `unauthenticated` and `session-revoked`
  (401), `forbidden` and `phone-not-verified` (403), `not-found` (404), `conflicting-state` (409),
  `validation-failed` (422, `context.fields[]` with `field`, `message`, `bound`, `min`, `max`,
  `value`), `rate-limited` and `too-many-attempts` (429).

---

## Public (anonymous) routes - `/api/public`

### `GET /api/public/branches?lat=&lng=` → `PublicBranchListing[]`
### `GET /api/public/branches/search?q=&category=&lat=&lng=` → `PublicBranchListing[]`

| Query | Type | Notes |
| --- | --- | --- |
| `q` | string | Search only. Contains-match on venue name, branch name, cuisine and address; case-insensitive; blank means all; at most 100 characters |
| `category` | int | 1 Cafe, 2 Restaurant |
| `lat`, `lng` | double | Together or neither. Adds `distanceKm` and sorts nearest first. Without them: best rated first |

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
  "recentReviews": [ /* PublicReviewView, newest 3 */ ],
  "tableMarkers": [ /* PublicTableMarker */ ],
  "asOfUtc": "2026-09-14T10:00:00Z"
}
```

Several rows per `day` are possible (split service). `closesNextDay: true` means `closesAt` is after
midnight.

### `GET /api/public/branches/{branchId}/reviews?page=1` → `PublicReviewPage`

20 per page, newest revision first. 400 for `page < 1` or a page whose offset would overflow; 404 as
above. The aggregate is read live.

```json
{
  "branchId": "guid", "rating": 3.0, "reviewCount": 2, "page": 1, "pageSize": 20,
  "reviews": [
    { "reviewId": "guid", "authorName": "Anahit S.", "rating": 5, "text": "…",   // text absent if stars only
      "createdAtUtc": "…", "updatedAtUtc": "…" }
  ]
}
```

`authorName` is the first name and last initial, or `"Yalla diner"` when the account has no display
name. (K8 tightens this rule.)

### `GET /api/public/branches/{branchId}/table-markers` → `PublicTableMarkers`

Live, not cached beyond the request. Only tables a manager placed on the cover photo appear.

```json
{
  "branchId": "guid",
  "photo": { /* PhotoView: the cover the coordinates refer to; absent if no cover */ },
  "asOfUtc": "…",
  "tables": [
    { "tableId": "guid", "label": "5", "seats": 4, "isBookable": true, "state": 1, "photoX": 0.25, "photoY": 0.5 }
  ]
}
```

### Existing public routes

- `GET /api/public/branches/{branchId}/availability?partySize=&date=&time=` - every `tables[]` entry
  also carries `photoX` / `photoY` (absent when not placed).
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
| `POST /api/diner/branches/{branchId}/review` | `{ "rating": 5, "text": "…" }` | 201 `DinerReviewView`, `Location: /api/diner/branches/{id}/review` | 401; 403 `phone-not-verified`; 404; 409 `conflicting-state` (already reviewed); 422 `validation-failed` (`rating`, `text`) |
| `PUT /api/diner/branches/{branchId}/review` | `{ "rating": 4, "text": null }` | 200 (revised) or 201 (first write) `DinerReviewView` | 401; 403 `phone-not-verified`; 404; 422 |

`rating` is an integer 1-5; `text` is optional, trimmed, at most 1000 characters, blank clears it.
One review per diner per branch (unique index `UX_BranchReviews_BranchId_DinerUserId`). Phone-verified
accounts only, checked on the stored row. The list routes' `rating`/`reviewCount` catch up within
15 s; `/reviews` and the details route read the aggregate live.

`DinerReviewView`: `{ "reviewId", "branchId", "rating", "text"?, "createdAtUtc", "updatedAtUtc" }`

### Orders

| Route | Success | Errors |
| --- | --- | --- |
| `GET /api/diner/orders?status=active` or `?status=history` | 200 `DinerOrderView[]`, newest first, at most 100 | 400 (unknown status); 401 |
| `GET /api/diner/orders/{orderId}` | 200 `DinerOrderView` | 401; 404 (unknown, or somebody else's - the same answer) |

**Which orders a diner sees.** The query starts from the account's `TabParticipants` rows (by
`IX_TabParticipants_UserId`) and takes the union of:

1. orders **placed from the diner's phone** (`PlacedByParticipantId` is one of those rows),
2. orders a waiter keyed in **on the diner's behalf** (`OnBehalfOfParticipantId` is one of those rows),
3. orders a waiter keyed in **for the whole table** (neither set) on a tab where the diner may see the
   table's total.

**The hide-total rule** decides the third: a whole-table order is the table's bill, so it is shown
only to a participant `TabPermissions.MaySeeTableTotal` allows - **approved, and not hidden from by
the host** (`CanSeeTableTotal`). A guest the host hid the total from, a guest still pending approval
and a guest removed from the tab get none of the table's orders, on the list or by id. Their own
orders (1 and 2) they still see.

**The status filter.** `status=active` is kitchen New, InKitchen and Ready; `status=history` is Served
and Voided; absent or blank is both; anything else is 400.

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

Both routes carry `ManagerOrAbove` and `BranchScoped`.

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
  "address": "…",
  "latitude": 40.1843, "longitude": 44.5129
}
```

Response: `{ cuisine?, about?, priceLevel?, websiteUrl?, amenities[], address, latitude, longitude, gallery: PhotoView[] }`.

**Validation.** Everything is checked before anything is written, and the listing fields report every
bad field at once in `context.fields`.

| Field | Rule | Refusal |
| --- | --- | --- |
| `cuisine` | Trimmed; blank is null; at most 120 characters | 422 `validation-failed`, bound `max` |
| `about` | Trimmed; blank is null; at most 2000 characters | 422, bound `max` |
| `priceLevel` | 1-4, or null | 422, bound `range` (`min` 1, `max` 4) |
| `websiteUrl` | Trimmed; at most 2048 characters; an absolute `http` or `https` address | 422, bound `max` for length; no bound for a non-http(s) or relative address |
| `amenities` | `outdoorSeating`, `wifi`, `parking`, `cardPayment`, `vegan`; case-insensitive; repeats collapse | 422 per unknown key, `value` is the key |
| `galleryPhotoIds` | null leaves the gallery; `[]` clears it; at most 12; no repeats; each uploaded for this branch (`POST /api/branches/{branchId}/photos`) | 422 bound `max` or `conflict`; **404** for a photo not uploaded at this branch - nothing written |
| `latitude` / `longitude` | Together or neither | 422 naming the missing one, bound `required`; 400 `invalid-request` for a value out of range |
| `address` | Applied only together with `latitude` and `longitude` | - |

The cover stays on `PUT /api/branches/{id}/public-profile`. K5 changes who may send `address`,
`latitude` and `longitude`.

### `PUT /api/branches/{branchId}/floor-plan` - per table, optional `photoX`, `photoY`

Each `tables[]` entry accepts `"photoX": 0.25, "photoY": 0.5` - fractions 0-1 of the cover photo, both
or neither; omitted or null takes the table off the photo. One without the other is 422 bound
`required`; out of range is 422 bound `range`. `GET /api/branches/{id}/floor-plan` returns them on each
table. Saving the public profile with a different cover, or none, takes every table off the photo.
(K6 and K7 move pin writes off this route.)

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
| `GET /api/public/branches/{branchId}`, `…/reviews`, `…/table-markers` | `public-place`: 120 per 60 s | Per-branch ceiling: 300 per 60 s, whoever asks |
| Other `/api/public` routes (branch page, menu, meta, availability, bookings) | see [public-surface.md](public-surface.md#rate-limits) | |
| `GET`/`POST`/`PUT /api/diner/branches/{id}/review`, `/api/diner/orders`, `/api/diner/me` reads and edits | none - the global limiter only | K8 adds `diner-write` (10 per minute) to review writes and photo uploads |
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
  past-midnight block (the same rule as the web branch page).
- **marker state** = `TableStateProjection.Derive(physical status, next Confirmed/PendingApproval
  booking, now, branch buffer)` - the staff floor's rule.

---

## Migrations

| Migration | What it adds |
| --- | --- |
| `20260913224239_BranchListingReviewsGalleryAndPhotoMarkers` | `DiningTables.PhotoX`, `PhotoY` (nullable); `Branches.Cuisine`, `About`, `PriceLevel`, `WebsiteUrl`, `AmenityKeys`; table `BranchGalleryPhotos` (`BranchId`, `PhotoId`, `Position`; unique per branch and photo, and per branch and position); table `BranchReviews` (`BranchId`, `DinerUserId`, `Rating` 1-5 by check constraint, `Text` ≤ 1000, `UpdatedAtUtc`; `UX_BranchReviews_BranchId_DinerUserId`, and indexes on `(BranchId, UpdatedAtUtc)` and `DinerUserId`) |
| `20260913235419_TabParticipantsByDinerAccount` | `IX_TabParticipants_UserId`, including `TabId`, `Status` and `CanSeeTableTotal`, for the Orders tab's first read - see [SCHEMA.md](../SCHEMA.md#tabparticipant-and-tabjointoken) |
| `20260914102955_DinerSessionGenerationAndDeletion` (K1, K2) | `DinerUsers.SessionGeneration int NOT NULL DEFAULT 0`; `DinerUsers.DeletedAtUtc datetime2 NULL`; `DinerUsers.PhoneE164` nullable, with `UX_DinerUsers_PhoneE164` filtered to `PhoneE164 IS NOT NULL` so a deleted account gives its number back |

Planned: B2's floor-plan version column (K6); `ReviewModerationReportsAndReservationNote` (K8
extension, K9); B6's `DinerFavorites` and `DinerNotifications` (K11, K12).

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
  `https://example.com/yalla-demo`, no amenities.
- **Five reviews** by five development reviewer accounts - "Dev Reviewer 1" to "Dev Reviewer 5", on the
  +374 99 000 051-055 test range, phone verified - rated 5, 4, 5, 3 and 4, dated 1 to 25 days back. A
  review is added only for a reviewer who has none at the branch.
- **Table pins** on the cover photo, mapped from each table's floor-plan position into the middle of
  the picture - only when the branch has a cover and no active table has a pin yet, so a manager who
  takes a table off the photo keeps it off. **The seed sets no cover photo**, so a fresh database has
  no markers until a cover is set.

`DevListingSeedTests` proves that a second run adds nothing, that a manager's amenities and a table
they took off the photo survive the next seed, that seeding runs with `DevActor:Enabled` off, and that
nothing is seeded outside Development.

---

## Contract changes (K1-K12)

All errors use the existing problem shape. New error codes across the changes: `session-revoked`
(401, K1), `relocation-not-allowed` (403, K5), `floor-plan-changed` (409, K6), `cover-changed` (409,
K7), `review-needs-visit` (403, K8), `bookings-not-accepted` (409, K9).

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
  (`DinerUser.ProveNumberByCode`); setting or changing a password; `SetActive(false)`;
  `DELETE /api/diner/me`. A password rehash at sign-in does not bump it.
- `PUT /api/diner/me/password` without a current password also compares the token's `sgen` with the
  stored row, past the cache. `DinerPhoneGate` (bookings, holds, tabs from a booking, reviews) also
  requires an active, undeleted account.
- **Refresh behaviour.** Refresh tokens are revoked on displacement and on deletion, as before. They are
  **not** revoked by a password change, and refresh refuses an inactive or deleted account.
- **Client rule:** on `session-revoked`, try the refresh token once; if refresh is refused too, sign
  out. After a password change - which ends the caller's own access token as well - the refresh
  succeeds and the app carries on.

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
- Favourites (K11), notifications (K12) and review reports (K8) are removed by the same transaction
  once those tables exist - one line each in `DinerAccountDeletion.RemoveRowsOwnedByAsync`.

### K3. `DELETE /api/diner/me/photo` - **implemented (B1)**

No shape change. It now deletes the Photo row and its files immediately
(`IPhotoService.DeleteAsync`). Afterwards `GET /api/photos/{id}/{variant}` returns 404.

### K4. Managers limited to their home branch - in progress (B2)

- **Refused:** on every route with the `BranchScoped` policy, a VenueUser **Manager** whose stored
  `StaffMember.BranchId` is set and differs from the route's branch gets 403 `forbidden`.
- **Enforced** in `BranchScopedHandler` from the token claim, and again in services through
  `IStaffBranchGuard`, which reads the stored row - so a reassignment takes effect before the token
  expires. The same rule applies to a StaffSession token acting as manager.
- **Unchanged:** owners, managers with no branch, and platform admins.

### K5. `PUT /api/branches/{branchId}/listing`: relocation - in progress (B2)

- **Body:** the existing fields, with `address: string | null`. `GET /listing` returns `address`.
- **A relocation** is `latitude`, `longitude` or `address` present **and** different from the stored
  value. Repeating the stored values is not one.
- **Who may relocate:** an Owner (admin-panel VenueUser token, stored row active, same venue) or a
  platform admin. Anyone else gets 403 `relocation-not-allowed` and **nothing** is written - the save
  is atomic, other fields included.
- **Validation:** coordinates without an address, or an address without coordinates, is 422
  `validation-failed` naming `address` (or `latitude`/`longitude`), bound `required`. The address is
  non-blank and at most 400 characters.
- **Audit:** `PlatformAuditLogs` `Action="branch.relocate"`, `EntityType="Branch"`, `EntityId=branchId`,
  details `{ "old": {address, latitude, longitude}, "new": {…} }`.

### K6. Floor plan version - in progress (B2)

- `GET /api/branches/{branchId}/floor-plan` adds `version: string`, opaque, from
  `Branches.FloorPlanVersion` (an int concurrency token).
- **PUT body** adds a required `expectedVersion: string`. `tables[].photoX`/`photoY` are **removed**
  from `FloorTableInput`: ignored if sent, never written; GET still returns them. New tables have no
  pin; kept tables keep theirs.
- **Errors:** missing `expectedVersion` → 422 naming it, bound `required`; stale → 409
  `floor-plan-changed` with `context.currentVersion`.
- **Success:** 200 with the existing body plus `version`. Only a successful floor-plan PUT bumps the
  version; pin saves and cover changes do not.

### K7. `PUT /api/branches/{branchId}/table-photo-positions` - in progress (B2, new route)

- **Policy:** `ManagerOrAbove` + `BranchScoped` + the K4 guard.
- **Body:** `{ "coverPhotoId": "uuid", "positions": [ { "tableId": "uuid", "photoX": 0.42, "photoY": 0.61 } ] }`.
  Only the listed tables change; `photoX: null, photoY: null` takes a table off the photo. One
  transaction holding an update lock on the Branch row.
- **200:** `{ "coverPhotoId": "uuid", "tables": [ { "tableId", "label", "photoX", "photoY" } ] }` for all
  active tables.
- **Errors (nothing written for any):** 409 `cover-changed` with `context.currentCoverPhotoId` (uuid or
  null) when `coverPhotoId` is not the branch's cover, including when it has none; 404 `not-found` for a
  `tableId` that is not an active table here; 422 `validation-failed` naming `positions[i].photoX` or
  `photoY` (bound `required` for one half, `range` outside 0..1) or `positions` (duplicate table ids);
  403 `forbidden`. Never changes label, seats, x/y, area or active state.

### K8. Reviews - in progress (B3)

**Eligibility.** `POST /api/diner/branches/{branchId}/review` needs, within the last 180 days, a
reservation for this diner at the branch that was Seated or Completed, or a `TabParticipant` with
`UserId` = the diner on a tab at the branch. Otherwise 403 `review-needs-visit`. `PUT` on the diner's
own existing review is always allowed.

**Rate limit.** A new policy `diner-write`, 10 requests per minute per principal, on review POST and
PUT, `POST /api/diner/me/photo`, `POST /api/branches/{id}/photos`, and the K8-extension and K11 writes.
Over it: 429 `rate-limited`.

**Identical PUT.** Same rating and text → 200 with `updatedAtUtc` unchanged.

**Public review list.** `/reviews` and `recentReviews` are ordered by `createdAtUtc` descending; each
item adds `edited: boolean`; hidden reviews are left out of the list, `rating`, `reviewCount` and badges.

**`authorName`.** The first word of the display name; if it contains `@`, a digit, `/` or `www.`, the
name is `"Yalla diner"`; otherwise only letters (any script) and hyphens, at most 24 characters; then
`" X."` only when the second word starts with a letter.

**The diner's own review.** `DinerReviewView` adds `publicAuthorName: string` and `hidden: boolean`.

**Platform moderation** (`PlatformAdminOnly`, audited):
- `GET /api/platform/branches/{branchId}/reviews?page=1&pageSize=20` →
  `{ items: [ { reviewId, branchId, rating, text, authorName, dinerUserId, createdAtUtc, updatedAtUtc, hidden, hiddenReason, hiddenAtUtc } ], page, pageSize, total }`.
- `PUT /api/platform/reviews/{reviewId}/visibility`, body `{ "hidden": boolean, "reason": string | null }`:
  `reason` required when hiding, at most 500 characters, else 422 naming `reason`; 200 with the item;
  404 `not-found`; audit `review.hide` or `review.unhide`.

#### K8 extension: venue moderation and diner reports - in progress (B3)

The user's decision: moderation and reporting are built now.

- **Venue moderation.** `GET /api/branches/{branchId}/reviews?page=&pageSize=&filter=all|reported|hidden`
  and `PUT /api/branches/{branchId}/reviews/{reviewId}/visibility` `{ hidden, reason }` - policy
  `ManagerOrAbove` + `BranchScoped` + the K4 guard; audited `review.hide`/`review.unhide` with actor type
  venue. Items as the platform list plus `reportCount` and `lastReportedAtUtc`. A review the platform
  hid cannot be unhidden by a venue (403 `forbidden`); one a venue hid can be unhidden by the platform.
- **Diner report.** `POST /api/diner/reviews/{reviewId}/report`, body
  `{ reason: "spam"|"offensive"|"not-a-visit"|"personal-info"|"other", note: string|null (≤ 500) }` →
  204. One report per diner per review (a repeat is a 204 no-op); reporting one's own review is 409
  `conflicting-state`; a hidden or unknown review is 404. Entity
  `BranchReviewReport { ReviewId, DinerUserId, Reason, Note, CreatedAtUtc }`, unique
  `(ReviewId, DinerUserId)`. Policy: any diner token (verified number not required); `diner-write`.
- The public list shape is unchanged: the app shows "Report" on every review not written by the
  signed-in diner.
- The migration for K8 and K9 is `ReviewModerationReportsAndReservationNote`.

### K9. Booking gate and booking note - in progress (B3)

- **New field:** `PublicBranchDetail` and `PublicBranchPage` add
  `acceptsAppBookings: boolean` = `AcceptsWebBookings && ReservationPolicyReviewedAtUtc != null`.
- **Gate:** `POST /api/reservations` with channel App when `!acceptsAppBookings` → 409
  `bookings-not-accepted` with `context.branchId`. The Web channel behaves as today.
- **Note on create:** `CreateReservationRequest` adds `note: string | null`, trimmed, blank → null, over
  500 characters → 422 naming `note`, bound `max`. The note is sent to the venue.
- **Note on reads:** `note: string | null` on the staff `ReservationView` (branch reservation lists and
  pending approvals), `GET /api/reservations/mine` items and the diner booking detail, and
  `GET /api/public/bookings/{token}`.

### K10. Configuration - in progress (B4)

- `ForwardedHeaders:KnownProxies: string[]`, `ForwardedHeaders:KnownNetworks: string[]` (CIDR),
  `ForwardedHeaders:ForwardLimit: int = 1`. Empty trusts no proxy. In Production with rate limiting on,
  a warning is logged at startup.
- `PhotoStorage:SweepIntervalMinutes: int = 60` (0 turns the sweep off).
- `appsettings.Staging.json`: `RateLimiting:Enabled = true`.
- `GET /api/branches/{id}/readiness` is unchanged; only the frontend starts using it.

### K11. Favourites - in progress (B6)

The user's decision: favourites are synced to the account, not kept on the phone.

- Entity `DinerFavorite { DinerUserId, BranchId, CreatedAtUtc }`, unique `(DinerUserId, BranchId)`,
  foreign key cascading on diner delete (K2 also removes them explicitly), index on `DinerUserId`.
- `GET /api/diner/favorites?lat=&lng=` - any diner token, verified number not required →
  `{ items: [ { branchId, createdAtUtc, listing: PublicBranchListing } ] }`, newest first; inactive
  branches omitted; `lat`/`lng` as the public list, for distance.
- `PUT /api/diner/favorites/{branchId}` → 204, idempotent; 404 `not-found` for an unknown or inactive
  branch; more than 500 favourites → 409 `conflicting-state`.
- `DELETE /api/diner/favorites/{branchId}` → 204, idempotent.
- `PUT /api/diner/favorites`, body `{ branchIds: uuid[] }` (≤ 500) → merges - adds what is missing,
  never removes - and returns the GET shape. Used once at sign-in to upload hearts made signed out.
- Rate limit: `diner-write`.

### K12. Notifications feed - in progress (B6)

The user's decision: the feed is built now.

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
| `type` | `listing.venueType`: 1 → `'cafe'`, 2 → `'restaurant'` |
| `cuisine` | `listing.cuisine` - absent → `''`, hide the line |
| `distanceKm` | `listing.distanceKm` when `lat`/`lng` were sent; else computed from `latitude`/`longitude` and the device position; no position → unknown, hide |
| `rating` | `listing.rating` - absent → no rating yet |
| `ratingCount` | `listing.reviewCount` |
| `badges` | `listing.badges` (`"popular"`/`"new"`, the same values as `PlaceBadge`) |
| `openState` | computed client-side by `openStateFor(hours, now, timeZoneId)`; `listing.isOpenNow` is the server's answer (15 s cache) |
| `photos` | `[listing.coverPhoto.cardUrl or fullUrl, ...detail.gallery[].cardUrl]`, resolved against the API origin. The first, the cover, carries the markers |
| `coords` | `{ latitude: listing.latitude, longitude: listing.longitude }` |
| `address` | `listing.address` |
| `phone` | `detail.phoneE164` (absent → undefined) |
| `website` | `detail.websiteUrl` (absent → undefined) |
| `timeZoneId` | `listing.timeZoneId` |
| `hours` | `detail.openingHours[]`: `day` → `day` (0 = Sunday on both sides), `opensAt`/`closesAt` `"HH:mm:ss"` → `"HH:mm"`. Split service gives several entries for one weekday; `hoursOn` picks the first |
| `amenities` | `detail.amenities` (the same keys as `place.amenity.*`) |
| `about` | `detail.about` - absent → `''` |
| `menu` | `GET /api/public/branches/{id}/menu` (`BranchMenuView`): categories → `section`, items → `{ name, price: priceAmd, description }` |
| `reviews` | `detail.recentReviews[]` (or `/reviews?page=`): `authorName` → `author`, `rating`, `text` (absent → `''`), `updatedAtUtc` (or `createdAtUtc`) → `date` as `YYYY-MM-DD` |
| `tables` | `detail.tableMarkers[]` or `GET …/table-markers` - see below |

Repository methods: `listNearby()` → `GET /api/public/branches?lat&lng`; `search(q, filter)` →
`GET /api/public/branches/search?q&category` (`filter.type` → `category`; `filter.badge` filtered
client-side on `badges`); `getById(id)` → `GET /api/public/branches/{id}` (404 → `null`) plus the menu;
`tables(id)` → `GET /api/public/branches/{id}/table-markers`.

### `TablePhotoMarker`

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
  Review moderation and reporting are in progress (K8 extension).
- **Distance without a position.** The server does not know where the diner is; `distanceKm` is present
  only when `lat` and `lng` are sent.
