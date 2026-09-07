# The public surface

What a link anybody can open publishes, what it deliberately does not, and how somebody with no
app manages the booking they made on it.

Routes live under `/api/public`. There is no authentication in front of any of them, so everything
here is published to whoever has the URL — and the URL is meant to be pasted into an Instagram bio
and forwarded round a group chat.

---

## The branch page

`GET /api/public/branches/{venueSlug}/{branchSlug}`

Two halves, and the split matters. The **stable** half — address, coordinates, hours, the room,
the policy, `bookingWindowDays` — is cached for minutes. The **live** half — `freeTableCount`,
`isOpenNow`, each table's `isFree`, `acceptsWebBookings` and `phoneE164` — is read per
request and stamped with `asOfUtc`.

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

Opening hours trimmed to two days and the floor plan to two tables; nothing else is elided.

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
    "floorWidth": 1000,
    "floorHeight": 700,
    "areas": [
      {
        "id": "01a078ec-fcbf-7c91-b1dd-ab63ddd9cdf9",
        "name": "Windows",
        "displayOrder": 0
      }
    ],
    "tables": [
      {
        "label": "1",
        "seats": 4,
        "x": 50,
        "y": 100,
        "width": 90,
        "height": 90,
        "rotationDegrees": 0,
        "shape": 2,
        "areaName": "Windows",
        "isBookable": true,
        "isFree": true
      },
      {
        "label": "2",
        "seats": 4,
        "x": 100,
        "y": 100,
        "width": 90,
        "height": 90,
        "rotationDegrees": 0,
        "shape": 2,
        "areaName": "Windows",
        "isBookable": true,
        "isFree": true
      }
    ]
  },
  "phoneE164": "+37411223344",
  "acceptsWebBookings": true,
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

- `venueType`, `shape` and `day` are **integers**, not strings. The whole API serialises
  enums as numbers on purpose - see `docs/openapi.md` - and the schema carries `x-enum-varnames`
  so a generated client gets the names.
- `phoneE164` is **absent** when there is none, not null.
- `isFree` on a table and `freeTableCount` are true as of `asOfUtc`, not as of render time.
- `tableCount` counts **bookable** tables, so it is the denominator for `freeTableCount`.
- There is no `qrToken` on a public table and there never will be: it is the credential that opens
  a tab.

---

## Managing a booking without an account

`GET /api/public/bookings/{token}`
`POST /api/public/bookings/{token}/cancel`

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

Exactly what the confirmation screen renders: venue, branch, address, time zone, table label, local
date and time, party size, status, the code quoted at the door, the cancellation deadline as an
**instant**, and whether cancelling would still do anything.

**Nothing else.** No diner id, no phone number, no other bookings, no floor state, no table or
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
they had already given back.

### Rate limits

Three, chained:

| Limit | Partition | Default |
| --- | --- | --- |
| Global | Client address | 300 / min |
| Public | Client address | 30 / min |
| Public booking | Manage token (digest) | 20 / min |

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
`ReservationChannel.Web` bookings, built from `PublicWeb:ManageBookingUrlTemplate`.

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
