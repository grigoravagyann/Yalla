# The public surface

What a link anybody can open publishes, what it deliberately does not, and how somebody with no
app manages the booking they made on it.

Routes live under `/api/public`. There is no authentication in front of any of them, so everything
here is published to whoever has the URL — and the URL is meant to be pasted into an Instagram bio
and forwarded round a group chat.

---

## The browse list

`GET /api/public/venues`

Every active, non-suspended venue with its active branches. It is the web chooser, and the diner
app's Explore screen too: the app used to call `/api/venues`, which never existed, and showed no
restaurants at all.

A venue card carries `venueSlug`, `name`, `type` (1 Cafe, 2 Restaurant, 3 Bar) and `branches`. There is no
venue id on the public surface; the slug is the public identity. Each branch card:

| Field | Meaning |
| --- | --- |
| `branchId`, `branchSlug` | The id the menu and availability routes take, and the other half of a printed link |
| `name`, `address` | As a person would read them |
| `timeZoneId` | The branch's IANA zone. A slot a diner picks after browsing is a wall-clock time at the branch, and a phone set to another zone renders it in this one rather than guessing - the app guessed `Asia/Yerevan` |
| `isOpenNow` | Inside an opening block at this moment, in the branch's own zone - special opening days included: shut on a closed holiday, open late on a late one |
| `freeTableCount` | Tables with nobody at them, **walk-in-only stools included** - the same tables the page's `tableCount` counts and its plan draws. Zero means every seat is taken |

**Caching.** The estate - venues, branches, names, zones - is held for five minutes. Everything
live is held for fifteen seconds (`LiveFor`): the free counts, `isOpenNow`, the branch page's `specialDays` (from
the service date - yesterday while last night's special block still runs past midnight - through 60 days after
today, each replacing its date's weekly hours), and *which branches are
still published*. Between refreshes a read sends nothing to the database, which matters because
every Explore open and pull-to-refresh in the city lands here. So a suspended venue leaves the list
within fifteen seconds. The branch routes still check their one branch live, per request, because a
stale answer there would serve a whole page, booking button included.

**Its own rate limits** - see [Rate limits](#rate-limits). It used to share the page budget per
address and, having no branch in its route, one partition under the per-branch ceiling: three
hundred a minute for every phone in the city. A 429 there is the no-restaurants screen again.

---

## The diner app's browse routes

`GET /api/public/branches` · `GET /api/public/branches/search?q=&category=&lat=&lng=` ·
`GET /api/public/branches/{branchId}` · `GET /api/public/branches/{branchId}/reviews?page=` ·
`GET /api/public/branches/{branchId}/rooms`

Branch-first rather than venue-first, because the app's place *is* a branch: Explore, search, the
map, the details screen and the rooms screen all key on the branch id the booking flow takes.
The list and search return `PublicBranchListing`; the details route returns `PublicBranchDetail`,
whose `listing` is that same card. `/api/public/venues` is unchanged for the web chooser.

- **Nothing is a constant.** `rating`/`reviewCount` come from `BranchReviews`; `badges` are
  `BranchBadgeRules` over sittings and reviews; `isOpenNow` is the same opening-hours rule the branch
  page uses (`BranchOpenNow`); `cuisine`, `priceLevel`, `about`, `amenities`, `websiteUrl`, `district`
  and the gallery are what the venue wrote through `PUT /api/branches/{id}/listing`. A field the venue
  never set is **absent**, and an unreviewed branch has no `rating` at all rather than a zero.
- **The district** - "Kentron", "Arabkir" - is the part of town the app prints after the venue type
  and the cuisine, and search's `q` matches it. Free text the venue writes, never guessed from the
  address: a street address names its district in no fixed place, and a guess prints the wrong
  neighbourhood with confidence. A manager may set it like the cuisine line; it moves nobody's pin.
- **Where a branch is** - its `address`, `latitude` and `longitude` - is the one part of that form a
  manager cannot change. Moving it is for an owner of the venue or a platform admin signed in to the
  admin panel, and is audited as `branch.relocate`. Anyone else sending a different location gets
  `403 relocation-not-allowed`, and nothing on the form is saved. Sending the stored location back
  unchanged is not a move.
- **Distance** is computed server-side only when the caller sends `lat` and `lng` together, and then
  orders the list nearest first. Without a position the list is best rated first.
- **Rooms** (`GET /api/public/branches/{id}/rooms`, and `rooms` on the details route) are the branch's
  rooms, lowest `displayOrder` first, each with its photos in the venue's order and, on each photo, the
  active tables a manager pinned there - `photoX`/`photoY` as fractions of that photo - with the derived
  `state` the staff floor shows. A room with no photo is not listed. **A table with no pin is not on
  any photo and is not offered to diners**: availability leaves it out and a booking naming it is
  refused as not bookable. A pin counts only on a photo of the table's own room; one on another room's
  photo is treated as no pin, everywhere a diner looks or books. Pins are set through the rooms editor (`PUT /api/branches/{id}/floor-plan`).
  The cover photo is the card's picture only; changing it moves no pin.
- **Caching**: the list and search hold the estate and every live number for fifteen seconds; the id
  routes check their branch live, so a suspended venue is a 404 at once. The reviews route and the
  details route read the review aggregate live, since they are the screens somebody opens right after
  writing one and the details card sits beside `recentReviews`, which is live too.

Writing a review is `POST`/`PUT /api/diner/branches/{branchId}/review` under the diner policy - one
review per diner per branch, a phone-verified account only, and a **first** review only after a
visit: a booking of the diner's at the branch that was Seated or Completed, or a place on one of its
tabs, in the last 180 days (`403 review-needs-visit` otherwise). Revising a review the diner already
has is never refused for the visit, and re-sending it unchanged writes nothing.

**What is published.** The list and `recentReviews` are newest first by when each review was
*written* - a revision does not lift an old review back to the top - and each carries `edited`. The
name a review is published under is derived from the display name, never stored and never the full
name:

1. Take the first word of the display name.
2. If it contains `@`, a digit, `/` or `www.`, the name is `Yalla diner` - that first word is where
   people type an email address, a phone number or a link.
3. Otherwise keep only letters (any script) and hyphens, at most 24 of them; if nothing is left, the
   name is `Yalla diner` too.
4. Add the second word's initial and a full stop only when that word starts with a letter:
   `Anahit S.`, `Անահիտ Ս.`, `Narek`.

**Takedown.** A platform admin (`PUT /api/platform/reviews/{reviewId}/visibility`) or an owner or
manager who covers the branch (`PUT /api/branches/{branchId}/reviews/{reviewId}/visibility`) hides a
review with a reason. A hidden review stays on its row and leaves every public read at once - the
list, `recentReviews`, `rating`, `reviewCount`, and so the badges. A venue cannot put back what the
platform hid. Every change is audited as `review.hide` or `review.unhide`.

**The venue's reply.** The owner, a general manager or the branch's manager may publish one reply under a
shown review (`PUT /api/branches/{branchId}/reviews/{reviewId}/reply`, at most 500 characters), edit it
and take it down (`DELETE` on the same route); the platform can take it down too
(`DELETE /api/platform/reviews/{reviewId}/reply`). It appears as `reply` on each item of the public
list and `recentReviews`, signed with the writer's role and their first name and surname initial
(`Aram H.`, the rule diners' names follow; the full name stays in the consoles), and leaves with the
review when the review is hidden. Diners flag reviews with
`POST /api/diner/reviews/{reviewId}/report`; a report takes nothing down on its own.

The full contract for these routes - every shape, the app's field maps, the rate limits, the Orders
tab, the Development seed and the contract changes in progress - is
[diner-browse.md](diner-browse.md).

---

## The branch page

`GET /api/public/branches/{venueSlug}/{branchSlug}`

Two halves, and the split matters. The **stable** half — address, coordinates, hours, the room,
the policy, `bookingWindowDays` — is cached for minutes. The **live** half — `freeTableCount`,
`isOpenNow`, each table's `isFree`, `acceptsWebBookings`, `acceptsAppBookings`, `phoneE164` and
`venueBranchCount` — is read per request and stamped with `asOfUtc`.

`venueBranchCount` is how many of the venue's branches the chooser (`GET /api/public/venues`) lists:
the ones switched on, this one included. A branch switched off is neither listed there nor counted
here. It is for the page's "All N branches" link, so the page does not download every venue to count
one venue's branches; it rides on the live published-check query as a correlated count.

`acceptsAppBookings` (K9) is `acceptsWebBookings` **and** a reservation policy somebody at the venue has
saved: the diner app books only where both hold, and `POST /api/reservations` from the app channel is
`409 bookings-not-accepted` otherwise. The web channel's rule is unchanged.

`acceptsWebBookings` and `phoneE164` are live despite looking stable. They gate and populate the
booking UI, and the rule behind `acceptsWebBookings` is enforced live in the reservation service —
a cached copy would mean a venue switches bookings off and the page goes on offering a button that
is refused for the next five minutes. They cost nothing extra: the published check was already a
query against that row.

`asOfUtc` exists because this page is cached for seconds and shared for days. A count with no
timestamp implies it is live; the page shows the staleness instead of pretending there is none.

### There is no `status`, on purpose

An earlier draft carried `status` (`Open`/`Closed`) beside `isOpenNow`. It was computed as
`isOpenNow ? Open : Closed` — the same fact under a second name, and a name that implied venue
lifecycle rather than opening hours. Two fields meaning one thing is how the next mapper picks the
wrong one, so it is gone.

It could not have been made to mean lifecycle either. Every query behind this route filters to
`IsActive && !Suspended && !Deleted`, so an active/suspended/deleted field here would only ever hold
one value. That is not an accident — a public page that distinguished a suspended venue from a wrong
slug would publish a customer's billing status to anybody who guessed one.

So the two states a client needs are carried where they actually live:

| The diner asks | The API says |
| --- | --- |
| "Is it worth going tonight?" | **200** with `isOpenNow: false` — closed now, still has hours and a menu to read |
| "Is this place still on Yalla?" | **404** — suspended, deleted, switched off, or never existed, indistinguishably |

### `policy` — what is published, and what is not

`policy` is a hand-picked subset, not a projection of `ReservationPolicy`:

| Published | Why a diner needs it |
| --- | --- |
| `turnTimeMinutes` | How long the table is held — the answer to "can we linger?", and finding out at the table is worse |
| `minLeadMinutes` | How far ahead a booking must be made; greys out the next slot |
| `cancellationDeadlineMinutes` | A deadline nobody was told about produces no-shows, not cancellations |

Withheld, deliberately:

| Withheld | Why |
| --- | --- |
| `serviceChargePercent` | Commercial. What the venue adds to a bill is not a browsing diner's business |
| `walkInHoldbackMinutes` | Operational. A scraper reading this across the estate learns how every venue in the city runs its floor |
| `approvalRequiredAbovePartySize` | Tells a stranger exactly which party size slips past staff review |
| `maxSeatOverhang` | Same: publishes the rule so it can be gamed |
| `autoConfirm` | Whether a venue vets its bookings is internal |
| `pricesIncludeVat`, `bufferMinutes`, `graceMinutes`, `graceExtensionMinutes`, `lateNudgeAfterMinutes`, `reminderHoursBefore` | Nothing a diner can act on before booking |

`PublicReservationPolicy` is its own record rather than a filtered `ReservationPolicyView` so that a
setting added to the policy upstream **cannot** appear here by accident. The record physically
cannot carry it. The test asserts on the serialised body, not the type, because a test that read
the record's properties would still pass on the day somebody serialised the full policy through it.

### `acceptsWebBookings` — why it defaults to false

A venue that has never been asked has not agreed to take bookings from strangers on the internet.
Defaulting it to true would opt every existing branch, and every branch created by the platform API,
into something nobody consulted them about — and the first a venue would learn of it is a party
arriving that it never accepted.

The cost of the default being wrong is one switch during onboarding. The cost of the opposite
default being wrong is a venue discovering it has been taking bookings it did not know about. The
first pilot venues are onboarded by hand anyway.

When it is false the page shows the room, the menu and the hours and offers no booking. **That is a
perfectly good page**, and the one most venues will start with.

It appears on the branch readiness checklist as a **reported line, not a blocker**. A venue that
does not want web bookings is not an unfinished venue. It is on the list so that leaving it off is
something somebody saw and chose rather than a default they never knew they had inherited — and a
line that blocked going live would teach every onboarder to switch it on without reading it, which
is the opposite of the point.

Editing it: `GET`/`PUT /api/branches/{branchId}/public-profile`, manager or above, branch-scoped.

The same form carries the venue card's picture. `PUT` takes `coverPhotoId` — a photo uploaded for
**this** branch through `POST /api/branches/{branchId}/photos`, or null to clear it — and both
`GET` and `PUT` answer with
`coverPhoto` as the three-variant photo view, absent when there is none. A photo uploaded for another
branch is not found here, and the whole form is refused before anything is written, the same answer a
menu item gets for a foreign photo. It is what `imageUrl` on the public venue list and `coverPhoto` on
the public branch page are read from. The page carries it inside the cached plan rather than the live
stitch — the picture changes once during onboarding, and a five-minute-old cover is not a claim about
the room — so a new cover shows on the public page within the plan's cache window, not instantly.

Photo links everywhere on this surface are **server-relative** (`/api/photos/{id}/card`): the API does
not know what host it is reachable at. Every client resolves them against the base url it calls the
API with; a page that used one as a root-relative `<img src>` would ask its own web host for a picture
only the API has.

### Null fields are absent, not null

The API serialises with `DefaultIgnoreCondition = WhenWritingNull`, so a branch with no published
number has **no `phoneE164` key at all** rather than `"phoneE164": null`. A client that checks for
null will not find it. This is true across the API, not only here, and is worth stating because the
public page is the first surface with an optional string on it.

### `phoneE164`

Nullable. A branch created through the platform API has no way to supply one, and a venue is usable
before anybody types its number in — but a public page with no phone number is close to useless to a
diner who wants to ask about a high chair, a wheelchair ramp or a dog. The page has no messaging and
is not going to grow any.

Normalised through `PhoneNumber.Normalise`, the same E.164 rule the diner sign-in uses. One
canonical form matters: `+374 11 22 33 44` and `+37411223344` are one number, and three spellings in
the database are three venues to a client trying to dial one.

---

## A whole branch page, as served

Seeded branch, phone published and bookings switched on. Regenerate with
`PublicBranchPageShapeTests`, which prints this and fails if a key is renamed or dropped -
the mapper that started this whole exercise was written against a guess, and this is the antidote.

Opening hours trimmed to two days and the rooms to one room of one photo and two tables; nothing
else is elided.

```json
{
  "venueSlug": "test-venue-530a90dd59e4",
  "branchSlug": "test-branch-530a90dd59e4",
  "branchId": "01a078ec-fc61-7ae9-9ad3-ccad7dd53bc0",
  "venueName": "Test Venue 530a90dd59e4",
  "branchName": "Test Branch 530a90dd59e4",
  "venueType": 2,
  "address": "1 Test Street, Yerevan",
  "latitude": 40.18,
  "longitude": 44.51,
  "timeZoneId": "Asia/Yerevan",
  "openingHours": [
    {
      "day": 0,
      "opensAt": "10:00:00",
      "closesAt": "23:00:00",
      "closesNextDay": false
    },
    {
      "day": 1,
      "opensAt": "10:00:00",
      "closesAt": "23:00:00",
      "closesNextDay": false
    }
  ],
  "isOpenNow": false,
  "freeTableCount": 2,
  "tableCount": 2,
  "floorPlan": {
    "areas": [
      {
        "id": "01a078ec-fcbf-7c91-b1dd-ab63ddd9cdf9",
        "name": "Windows",
        "displayOrder": 0,
        "photos": [
          {
            "photoId": "01a078ec-fcc0-7a52-8d0e-3c2f4c1b9a77",
            "thumbnailUrl": "https://cdn.example.test/room.jpg",
            "cardUrl": "https://cdn.example.test/room.jpg",
            "fullUrl": "https://cdn.example.test/room.jpg"
          }
        ]
      }
    ],
    "tables": [
      {
        "label": "1",
        "seats": 4,
        "areaName": "Windows",
        "isBookable": true,
        "isFree": true,
        "photoId": "01a078ec-fcc0-7a52-8d0e-3c2f4c1b9a77",
        "photoX": 0.05,
        "photoY": 0.5
      },
      {
        "label": "2",
        "seats": 4,
        "areaName": "Windows",
        "isBookable": true,
        "isFree": true,
        "photoId": "01a078ec-fcc0-7a52-8d0e-3c2f4c1b9a77",
        "photoX": 0.14,
        "photoY": 0.5
      }
    ]
  },
  "phoneE164": "+37411223344",
  "acceptsWebBookings": true,
  "venueBranchCount": 3,
  "bookingWindowDays": 14,
  "policy": {
    "turnTimeMinutes": 90,
    "minLeadMinutes": 30,
    "cancellationDeadlineMinutes": 120
  },
  "asOfUtc": "2026-09-06T22:53:12.1013041Z"
}
```

Notes for a mapper:

- `venueType` (1 Cafe, 2 Restaurant, 3 Bar) and `day` are **integers**, not strings. The whole API serialises
  enums as numbers on purpose - see `docs/openapi.md` - and the schema carries `x-enum-varnames`
  so a generated client gets the names.
- `phoneE164` is **absent** when there is none, not null.
- `isFree` on a table and `freeTableCount` are true as of `asOfUtc`, not as of render time.
- `freeTableCount` is **counted off the plan the page draws**: the cached plan's tables, less the
  ones a live read says are taken. It is not the estate-wide free count the browse cards carry. The
  plan is held for minutes and the live read for seconds, so a table a manager adds in between used
  to be counted free before it was drawn - "9 tables free" over a room of 8 (QA 2026-10-16, P-L5).
  Now a new table reaches the number and the room together, when the plan refreshes; a card on the
  browse list may show it up to five minutes sooner, because a card draws no room.
- `tableCount` counts **every active table**, bookable or walk-in only, and so does
  `freeTableCount`: the count is exactly the tables drawn with `isFree: true`, out of the tables
  drawn. It once read "3 of 2 free", when the denominator took bookable tables only. Narrowing the
  free count to bookable tables instead said "no tables free" beside an empty stool on the plan -
  to a walk-in, who is who the live count is for.
- There is no `qrToken` on a public table and there never will be: it is the credential that opens
  a tab.
- A table's pin - `photoId`, `photoX`, `photoY` - is absent when it has none, and `isBookable` is false
  for such a table whatever the venue set: a diner is only offered a table pinned on one of its own
  room's photos.
  Every active table is still listed, pinned or not, so the counts above stay out of the whole branch.
- There is no drawn floor plan any more - no canvas, no `x`/`y`/`width`/`height`/`rotationDegrees`/
  `shape`. A room is its photos and the pins on them.

---

## Booking needs a verified number

There is **no anonymous booking door**. The page books only after the visitor proves their number
(Telegram - `GET /api/auth/diner/phone-proof` says which ways are on): with the diner token from the proof,
`POST /api/reservations` with `channel: 2`, the app's own route and rules. Yalla#54 added
`POST /api/public/branches/{branchId}/bookings` for a visitor who would not verify; the user withdrew it the
same day (2026-10-20) and the route is gone (404), with its `deviceId`, its per-network budget and its branch
ceiling, and the three `policy` members that described it. See
[reservations.md](reservations.md#3g-booking-needs-a-proved-number-the-per-number-rules-the-guest-record).

**Tell the visitor before they book** that venues they book with see their record - how many of their bookings
in the last 12 months they kept, missed or cancelled late, never where - and that three no-shows in three days
pause booking for a week. Deleting the account resets the record, but a pause in force stays with the number until
it ends; a number taken over by someone else who proves it takes the account - and its record - with it. See
[reservations.md](reservations.md#guest-record).

## Managing a booking without an account

`GET /api/public/bookings/{token}`
`POST /api/public/bookings/{token}/cancel`
`POST /api/public/bookings/{token}/reschedule`

A visitor who books from the web has no app, so no push reminder and no one-tap cancel reach them.
Without a way to cancel, this is a **no-show generator** — the whole reservation product rests on
cancelling being easier than not turning up, and for a web booking this link is the only way that is
true.

### The token

Minted at reservation creation and returned **once**, in `manageToken` on the creation response.
Never returned again by any read.

Stored as a **hash only** — hex SHA-256, the same treatment as refresh handles and enrolment codes,
per the split documented in `Secrets.cs`. 256 bits from a CSPRNG, so guessing is already impossible
and the only remaining job is making a database dump useless. A fast unsalted digest is correct
here precisely because the row is looked up *by* the hash, which a salted one could not do.

It matters more here than for a refresh handle. This is a bearer capability that lives in a URL, and
that URL gets pasted into WhatsApp, left in browser history, and read by whoever picks the phone up.

It expires at the end of the booking plus `Reservation.ManageTokenGraceDays` (7). Not zero: somebody
who booked on Friday may open the link on Sunday to check what time they went. Not forever: a link
in a WhatsApp thread should stop working once nobody could act on it.

### What the link shows

Exactly what the confirmation screen renders: venue, branch, address, time zone, table label and
the room the table is in (`roomName`, absent for a table in no room), local date and time, party
size, status, the code quoted at the door, the cancellation deadline as an **instant**, and whether
cancelling would still do anything. A booking waiting on a short-notice answer, or on the guest
with the table before it, also carries `approvalExpiresAtUtc` - the page counts down to it and reads
the link again until the booking is decided - and `awaitsEarlierGuestAnswer`, true while that guest is
asked in the app (only they answer) and false when the venue decides; a declined one carries
`declinedBecause` (1 the venue said no, 2 nobody at the venue answered in time, 3 the guest before said
no, 4 they did not answer), so the page apologises for the right thing. See [reservations.md](reservations.md#3d-short-notice-tonight-is-the-floors-to-decide).

The branch page's public address, `venueSlug` and `branchSlug`: the link a reminder carries is
`/booking/{token}` (below), which names no branch, so the page needs them to link back to the branch
and open its booking card there. They are the page's own address, public already.

`phoneUnverified` says the booking was made without a verified number - only the few made while Yalla#54 allowed
it; `canChange` says whether `reschedule` would be considered at all (still to come, and an account with a proved
number to land on) - false for a booking with no account behind it, whose link cancels but never changes it
(`403 phone-not-verified`).

Two ids, for changing the booking from the link: `reservationId`, which the change screen sends as
`excludeReservationId` on the public availability and day-slot reads so the table the visitor already
holds reads as free to them, and `tableId`, the booked table - table ids are no secret, those reads
publish every pinned table's; the booked one is named even if it has since been unpinned or taken out,
and a move to it is then refused like any unbookable table. Neither opens anything: every route keyed by a booking id wants the booking's diner or the
venue's staff, and the reads that take one say no more about it than that its table is free.

**Nothing else.** No diner id, no guest name or phone number, no other bookings, no floor state, no
branch id. `PublicBookingView` is its own record for the same reason `PublicReservationPolicy` is:
a field added to the authenticated shape must not be able to leak onto an anonymous route by being
added upstream.

The deadline is absolute rather than a number of minutes so the page does not reimplement the
arithmetic, and so a later policy edit cannot silently move a deadline the diner has already been
shown.

### Failures do not answer questions

An **unknown** token, an **expired** token and a valid token whose booking is **gone** are three
different facts on the server and exactly one answer on the wire: the same status, the same code and
the same sentence, from both routes.

Anything less lets somebody with a list of candidate tokens learn which ones are real, and "real" is
the only thing standing between them and a stranger's booking. This is why
`ManageBookingFailure.Message` is a constant and `Raise()` takes no arguments — a sentence carrying
the token, the reason, or the date it expired is the oracle rebuilt in prose.

A **cancelled, missed or completed** booking is *not* a failure. It answers 200 with its state.
Somebody opening a three-week-old link should learn what happened to their table; a 404 teaches them
only that something is broken, and they ring the venue to ask.

### Cancelling

Free before the branch's `cancellationDeadlineMinutes`, allowed after it and recorded as late,
**never blocked**. A diner who cannot cancel simply does not turn up, and a no-show costs the venue
the same table plus the chance to resell it.

This is `IReservationService.CancelByManageTokenAsync`, which shares `CancelCoreAsync` with the
app's cancel. The **only** difference between the two entry points is how the caller proved they
may: a signed-in diner is checked against `DinerUserId`, a link holder by holding an unguessable
token. Everything after that — the deadline rule, the lateness record, cancelling the booking's
outbox messages, the save — happens once, in one place. A second cancellation path is how a web
cancel would eventually stop cancelling the reminder, and the diner would get a push about a table
they had already given back. The link's cancel adds one thing the app's does not: the booking's
account is told it was cancelled from the link (see "Changing the booking" below).

### Changing the booking

`POST /api/public/bookings/{token}/reschedule` takes the app's body unchanged - `date`, `time`,
`partySize`, `diningTableId` (required: `tableId` from the read, or another table at the branch),
`clientCommandId`, and `acceptsFreeByUtc` when the new slot's table asks for it - and answers `200` with
the booking as the read shows it, re-read through the same projection: the same `code` and link at the
new date, time, party size, table and status.

It is `IReservationService.RescheduleByManageTokenAsync`, which finds the booking exactly as the read
and the cancel do (unknown, expired and gone answering identically) and then runs the app's move - see
[reservations.md](reservations.md#3c-changing-a-booking). The same rules and the same `reservation-*`
answers, the status decided again (a confirmed party keeping its table and start without growing stays
confirmed; a move into tonight, a party grown past the threshold, a strict branch or a no-show history
waits for the venue), the same table lock, the same replaced reminders - a web booking's carry the same
manage link - and the same idempotency record. The only difference is the proof.

The decisions:

- **No phone proof is asked of the visitor.** The link already proves possession of the booking, as it
  does for cancelling. The phone gate that applies is the **booking's account's**: the moved booking's
  reminder and no-show record land on that account, so it must still be live with a proved number,
  whoever holds the link - `403 phone-not-verified` otherwise. In practice that is an account the
  platform **deactivated**, or one the diner **deleted** (the deletion cuts `DinerUserId`, so there is no
  account to land on). An account that stops being live between the gate and the move's own lock is the
  same 403, never the lock's `401 session-revoked` - there is no session on a link. The message is for
  the link holder, who has no account to verify: "This booking can no longer be changed online. Call the
  venue, or cancel it and book again." (`ManageLinkAccountUnavailableException`). Reading and cancelling
  stay open.
- **The booking's account is told.** The link goes round group chats and into the calendar file, so
  whoever changes or cancels the booking from it may not be the diner - and a booking moved without
  their knowing becomes a no-show on their account. A change from the link (not a retry) and a cancel
  from the link (not of an already-cancelled booking) each write, in the same save, a push to the
  account (`reservation.changed-by-link` / `reservation.cancelled-by-link`, Armenian, Russian or English
  by the account's device) and a feed entry (`booking-changed-by-link` / `booking-cancelled-by-link`) -
  see [notifications.md](notifications.md). The app's own change and cancel write neither. The log says
  which way in: "changed from its manage link, not the app".
- **The change is recorded as the diner's.** `ProcessedCommands` holds the booking's account as the
  actor, so a retry is a retry whichever way it arrives, and the same id on another booking - another
  link, the same diner's other booking, a stranger's - or on a different change is
  `409 client-command-id-in-use`, revealing nothing. A link cannot reach a second booking: the token
  names the booking, and the body has no id to point elsewhere.
- **One refusal is narrower than the app's.** A move onto a sitting that overlaps another of the diner's
  live bookings at the branch is `409 reservation-overlaps-own` as in the app, but with **no `context`**
  and a message that names no time, table or code (`ManageLinkOverlapsOwnException`). The app may name
  the other booking - the diner is signed in and it is theirs; the link's refusal does not name it to
  whoever opened the link from a group chat. **It does not hide that it exists**: the writer checks the
  diner's own bookings before the table's clash, so aiming at a taken table answers
  `reservation-overlaps-own` when the diner holds another table around then and `table-already-booked`
  when not, and nothing moves either way. The order is shared with the app and was left as it is: the
  link holder is already in the diner's circle, at the same venue.

### Rate limits

Chained, so a request passes every one that applies to it:

| Limit | Partition | Default |
| --- | --- | --- |
| Global | The caller: the principal where the request has one, the client address otherwise | 300 / min |
| Public | Client address — these routes are anonymous | 30 / min |
| Public branch ceiling | One branch, whoever is asking | 300 / min |
| Browse list, per caller | The caller, on `GET /api/public/venues`, `/api/public/branches` and `/api/public/branches/search` - replaces the page budget there | 120 / min |
| Browse list ceiling | The whole city: one partition for those three lists, sized for them rather than for one branch | 6,000 / min |
| Place, per caller | The caller, on `GET /api/public/branches/{id}`, `…/reviews` and `…/rooms` - replaces the page budget there; the branch ceiling still applies | 120 / min |
| Public booking | Manage token (digest) - the read, the cancel and the change share it | 20 / min |

The browse list is the diner app's Explore as well as the web chooser, and a carrier puts thousands
of phones behind one address, so the page budget was spent before dinner. The page budget exists to
stop one client walking the estate page by page; the list *is* the estate, in one cached response,
so there is nothing to walk. Configured as `PublicBrowsePermitLimit` / `PublicBrowseWindowSeconds`
and `PublicBrowseCeilingPermitLimit` / `PublicBrowseCeilingWindowSeconds`.

The global row said "client address" and meant it, and for the public routes it is still what
happens, because nobody arrives at them with a token. Everywhere else it was wrong in a way worth
recording: the key read `Identity.Name`, which is the `StaffMemberId` claim, and a tab participant,
a diner and an enrolled tablet carry no such claim. All three keyed on the empty string, so three
of the five identity types shared **one** budget across the whole platform — three hundred requests
a minute for every diner and every open tab there is. The key now names the principal per identity
type, and a test walks `PrincipalType` and requires each member to produce its own.

The per-address limit bounds somebody *guessing* tokens — every guess spends their own budget. What
it does not bound is a link that went round a group chat being hammered from forty phones, which is
what the per-token limit is for. The partition key is a digest rather than the token itself: the
limiter holds its keys in memory for the life of the window, and a capability that opens somebody's
booking has no business sitting in that dictionary.

They are chained rather than three endpoint policies because an endpoint carries one policy — a
second `RequireRateLimiting` replaces the first rather than composing with it.

---

## The manage URL in the reminder

`ScheduleRemindersAsync` writes `manageUrl` into the reminder and late-nudge payloads for
`ReservationChannel.Web` bookings, built from `PublicWeb:ManageBookingUrlTemplate` with the token
URL-escaped into `{token}`. A change of the booking - from the app or the link - reads that URL back
off the old reminder (`ManageUrlIn`) and writes it into the new one unchanged.

**The template points at the web's token-only page, `https://{host}/booking/{token}`** - in
`deploy/.env.example`, in `appsettings.Development.json` (`http://localhost:5173/booking/{token}`) and
as the `PublicWebOptions` default; `DeployEnvExampleTests` pins all three to that path. The web serves
`/booking/:token` and reads the branch from the answer's `venueSlug`/`branchSlug`. `booking` is a
reserved slug (`SlugText`) so no venue can take the address. The web's older
`/{venueSlug}/{branchSlug}/booking/{token}` route is the page's own link after booking and is not
what the reminder carries.

Written now, before any channel exists that could send it, because **the token is knowable only at
creation**. It is stored as a hash and returned once; a dispatcher added later that needed to build
this URL would find that it cannot. Putting it in the payload now is what stops the SMS or Telegram
work having to revisit reservation creation.

Only for `Web`. A capability that opens somebody's booking belongs in as few rows as possible, and
an app booking already has a better cancel route.

**This is the one place the plaintext token is persisted**, and it is deliberate: a message that has
to carry a link has to contain the link. The row is deleted when the message is sent or when the
booking is cancelled. Everywhere else — including `Reservations` itself — holds only the hash.

---

## What a self-reported channel can and cannot do

`ReservationChannel` is supplied by the client. The `acceptsWebBookings` gate refuses a booking that
declares itself `Web` at a branch that has not switched web bookings on.

That stops the public page offering a booking the venue never agreed to. It does **not** stop a
hand-written client claiming to be the app. That is the right trade for what the flag is — a venue's
stated preference about its own public page, not an access control — and making it one would mean
authenticating the channel, which a page reachable by anybody with a URL cannot do.

In practice the page reads `acceptsWebBookings` and hides its booking UI, so the gate fires for a
stale page whose branch was switched off while somebody had it open.
