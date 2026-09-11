# Authentication

There are four identity types in this product. Not four implementations of one idea - four
genuinely different kinds of caller, with different constraints, and the most important of them
breaks if it is forced into the same model as the others.

Everything here uses JWT bearer tokens signed with HS256. The signing key comes from user secrets
in development and from `Jwt__SigningKey` everywhere else; startup fails, with instructions, if it
is missing or shorter than 32 bytes. It is never in `appsettings.json` and never committed.

## Why not ASP.NET Core Identity

Three of the four identity types below - device tokens, PINs, phone-only diners - do not fit its
user-and-role model at all, and its table set would be almost entirely dead weight. What is
actually wanted from it is one class: `PasswordHasher<T>`, for the secrets a person types. That is
what is used, through a small wrapper (`SecretHasher`), and nothing else.

Two hashing jobs exist here and they need different tools:

- **Low-entropy secrets a person chooses or types** - passwords, four-digit PINs, six-digit codes -
  are guessable by construction and need a deliberately slow, salted hash. `PasswordHasher<T>`.
- **High-entropy secrets a machine holds** - refresh handles, enrolment codes, reset links - are
  256 bits of randomness, so guessing is already impossible and the only requirement is that a
  database dump is useless. SHA-256, and being fast is a feature: these are looked up *by* their
  hash, which a salted hash could not do.

---

## 1. Tab participant — no account at all

**Someone scans the QR on table 7 and orders a coffee. If that asks them to register, the product
has failed.** Walk-ins are most of the traffic in a cafe, and a stranger will not create an account
to order. This is the flow the product lives on, and every other decision here bends around it.

So there is no user row. Scanning a table QR code, or redeeming a `TabJoinToken` passed round the
table, creates a `TabParticipant` and issues a token carrying **`participantId`, `tabId`,
`branchId`** and nothing else.

The token is valid for exactly one tab. Not "checked against" one tab - *incapable of naming*
another, because the claim does not contain one. Enforcement is the `TabParticipant` policy, which
compares the route's `tabId` to the claim before any handler runs. No handler checks it, so no
handler can forget to.

The policy also does one database read, which a claim cannot replace: it refuses a participant who
has since been removed from the tab, and a tab that closed longer ago than the receipt grace
period. A token is a statement about the past; those are questions about now.

**Lifetime tracks the tab.** The `exp` claim is a 12-hour ceiling, because at issue time nobody
knows when the tab will close. The real expiry is the tab closing plus
`Jwt:ParticipantReceiptGraceMinutes` (two hours), applied by the policy on every request - long
enough to look at the receipt, short enough not to be a session.

**Recognising someone again.** A participant is identified by a `deviceId` the app generates once
per install. Re-scanning after a phone locks returns the existing participant rather than putting
a second person on the bill. It is not a login and it is not an account.

**Display name.** A participant may set one, so the host sees "Ani" rather than "Guest 3". That is
a profile field on `TabParticipant`, optional, and costs nothing to skip.

One thing this flow deliberately does not do: it will not open a tab at a table where nobody is
seated. It answers 409 and says a member of staff seats the party first. Seating is a transition of
the table state machine - it opens a `TableSession`, writes an audit row and takes part in the
optimistic-concurrency handling that stops two waiters seating the same table. A QR scan that
seated its own party would be a second, unaudited way to occupy a table, which is exactly what that
machine exists to prevent. Whether diners should be able to self-seat is a product question, and
when it is answered the answer belongs in the state machine, not here.

## 2. Diner with a reservation — phone number, one-time code, no password

A booking needs a real phone number. It is what the reminder, the "still coming?" nudge and
no-show tracking across bookings all hang off, and a number that was never verified is worth
nothing to any of them. Once the number is verified, a password would add nothing except one more
thing to forget while standing outside a restaurant in the rain.

- `POST /api/auth/diner/request-code` — six digits, **five minute** lifetime, **five attempt**
  limit, single use, hashed at rest.
- `POST /api/auth/diner/verify-code` — returns access and refresh tokens, and creates the
  `DinerUser` on the first successful verification. There is no separate registration step, because
  a separate registration step is a step people abandon.

Six digits is only 20 bits of entropy. That is safe **solely** because all three limits hold at
once: it dies after five minutes, after five wrong guesses, and on first success. Requesting a new
code retires the outstanding one, so five requests do not leave five live codes and quintuple the
guess budget.

**`request-code` answers identically for a registered and an unregistered number.** Not similarly -
identically: the method never reads the account table at all, so there is no field and no extra
query to time. Anything else would turn this endpoint into a way to ask a stranger's phone book who
uses Yalla.

**Rate limited on two axes.** Per address, by the pipeline limiter on the endpoint; and per phone
number, by a `PartitionedRateLimiter` inside the service. The second is not optional: a caller on
a phone network changes address for free, and codes cost money to send once a real provider is
wired in, so an unlimited request endpoint is an invoice generator. It is inside the service rather
than in the pipeline for a practical reason - the number is in the request body, and the middleware
runs long before anything has read it.

**Sending is abstracted, on purpose.**

```csharp
public interface IVerificationCodeSender
{
    Task SendAsync(string phoneE164, string code, string localeCode, CancellationToken ct);
}
```

Whether production sends these over SMS or over a Telegram bot is an open commercial question - SMS
to Armenian numbers is metered and a bot is free - and it is not a question that should have been
allowed to block sign-in from being built. One implementation ships: a development sender that
writes the code to the log and, in Development only, returns it in the response body so the flow is
testable with no provider. The message text comes from localised resources (`hy`, `ru`, `en`), so
whatever channel arrives later cannot invent its own wording.

The Development-only affordance is gated by the host from `IHostEnvironment`, not from
configuration, so `Auth__ReturnVerificationCodeInResponse=true` in a production environment does
nothing.

## 3. Staff — device-bound branch token plus a per-person PIN

The constraint that sets the shape of this one: **a waiter must never type an email address during
a Friday rush.** The tablet signs in once, in the morning, and stays signed in; a person becomes
present on top of that with four taps.

**Device enrolment.** A manager generates a one-time code for a branch from the admin panel. The
tablet redeems it once and gets a long-lived **device token** carrying `branchId` and `deviceId`.
The code is single use because it will be read out across a bar and overheard; a second redemption
is refused, and the manager then sees a tablet in the branch's device list they did not enrol. The
`RedeemedAtUtc` column is an EF concurrency token, so two tablets racing for the same code cannot
both win.

Enrolled devices live in `StaffDevice`, with a name, a last-seen timestamp and a revoked flag. **A
lost tablet is revocable from the admin panel and stops working on its next request** - not when
its year-long token expires. Every request carrying a device or session token costs one lookup on
a primary key to check that row. That is the price of statelessness, paid deliberately and only
where it is owed; diner and participant tokens do not pay it, because revoking their refresh chain
is enough.

The device token can do exactly one thing: offer a PIN. It names a branch but no person, so an
enrolled tablet with nobody signed in cannot seat a table.

**Who the PIN screen lists.** `GET /api/auth/staff/roster`, authenticated by the device token and
refused exactly as `GET /api/auth/staff/device` refuses (401 with no token, 401 `device-revoked` for a
revoked tablet), returns the people who may tap a PIN on this tablet, sorted by name:

```json
[{ "staffMemberId": "…", "fullName": "Anna Petrosyan", "role": 3 }]
```

"May tap a PIN here" is one rule, `StaffAuthService.SignsInOn`, read by both the roster and the PIN
exchange: **active, of the device's venue, and at the device's branch or at no particular branch.**
A platform admin has no venue and so is on no tablet. Without this the first sign-in on a fresh
tablet meant typing a staff member id by hand. The list carries those three fields and nothing
else - no phone, email or PIN state - because anybody holding the tablet can read it.

**Per-person PIN.** Each `StaffMember` has a four-digit PIN, hashed, never logged. Tapping it
exchanges the device token for a **staff session token** carrying `staffMemberId`, `role` and
`branchId`, good for 30 minutes.

**Thirty minutes of inactivity** is what it says, which a JWT cannot express on its own - a JWT only
knows the wall clock. So the session token is paired with a `StaffSession` row whose
`LastActivityAtUtc` moves forward on every renewal. Stop using the tablet for half an hour and the
next renewal is refused, so the next action needs the PIN again. An absolute 16-hour cap stops a
background poller from renewing forever.

**Lockout.** Five consecutive wrong PINs lock the staff member out, reported as **403
`account-locked`** rather than another 401 - reporting a lockout as a wrong PIN leaves someone
standing at a tablet retyping digits that were correct all along. A manager clears it from the
admin panel, which is the path that actually gets used: a waiter who fat-fingered their PIN during
a rush cannot be made to wait out a timer.

**PINs are per person, and there is no shared-PIN option.** Venues will ask for one, because it is
faster. It also destroys the only thing that answers *"who gave away my reserved table"*, which is
the reason the audit log exists at all. Every `TableStateChange` written after a real sign-in
carries the `staffMemberId` of the person whose PIN was tapped.

## 4. Venue owner and manager — email and password

Conventional, and conventional is right here: this is a desktop browser session holding prices,
refunds and staff accounts, used from a chair rather than from a tray. The cost of typing an email
is nil and the value of a recovery story people already understand is high.

- Email plus password, hashed with `PasswordHasher<T>`.
- **Minimum length only, no composition rules.** "One capital, one symbol" measurably pushes people
  towards `Password1!` and towards a sticky note on the till.
- Password reset by emailed link: single use, one hour, stored hashed. Consuming one revokes every
  refresh token the account holds, because the usual reason to reset a password is that somebody
  else might have had it. The sender is abstracted behind `IPasswordResetSender` with a development
  implementation that logs, same pattern as the code sender, and no provider is integrated - which
  is why the link also travels by hand, below.

Unknown address, wrong password and deactivated account all answer identically, so the sign-in form
cannot be used to discover which addresses have accounts.

**These credentials live on `StaffMember`, not on a separate table.** A manager taps a PIN on the
floor and signs in to the panel from home on the same day, and the audit log has to name the same
person either way. Two tables would give one human two identities and quietly break the log.

---

### Waiters and kitchen never hold email credentials, on any surface

The question came up when the staff app moved to a web PWA: should Waiter and Kitchen be allowed
admin-panel email sign-ins, so `/staff` works in a browser? **No.** Two reasons, and neither of them
is about tablets.

- **A waiter must never type an email address during a Friday rush.** That is the constraint the
  whole staff model exists to satisfy. An email and a password is thirty seconds and two mistakes;
  four taps is four taps. Moving the surface to a browser does not make typing faster.
- **The PIN is what makes the audit log answer "who gave away my reserved table".** Per-person PINs
  on a shared device are the only mechanism that names a person on a floor where the device is shared
  and the people change every few hours. A shared email login erases exactly that.

**The browser is the device.** A PWA on a laptop at the counter enrols with a one-time code, holds a
device token, and exchanges a PIN for a session — the same three steps a tablet takes, because
nothing in that flow ever assumed a native app. What was missing was the redemption endpoint's
client-generated `deviceId` and a way for the PIN screen to say which venue it is bound to, not a
permission.

Email and password stay what they always were: the admin panel, for owners and managers, who do sit
down at a desk to do the things it is for.

### How a manager or owner gets their sign-in

**The person above them issues it: the server stores the address, mints a single-use reset link
good for 24 hours, returns it once, and they hand it over the way everything in this product
travels - pasted into a chat. The recipient opens it on the console's reset page and chooses a
password of their own.** `POST /api/venues/{venueId}/staff/{staffMemberId}/sign-in` with
`{ email }`; the anonymous `reset-password` endpoint that consumes the link stays the only thing
that ever sets a password.

Why it is shaped this way:

- **No provider delivers anything.** Outside Development the reset sender logs an error and drops
  the link, so "we'll email them" was a manager created from the console with no way to ever reach
  the panel - including the first owner of a new venue, whom only a platform admin can create. The
  console never sent a password at creation, and `UpdateStaffCommand` has no email, so nobody could
  repair it afterwards either. When a provider arrives, email becomes a second way to deliver the
  same token, not a replacement.
- **Not a downgrade.** The same actor may already create a manager and type a password *for* them,
  which is strictly more than a link the recipient completes with a password of their choosing; and
  the tablet enrolment code already returns a one-time secret to the same actor. What the mailed
  flow would add is proof of mailbox control, which is meaningless for an address the owner just
  typed.
- **Returned once, and that is the only copy.** The server keeps the token's hash and never logs
  the link; an audit row (`staff.sign-in-issued`) records who gave whom a way in, to which address
  and from which, and never the credential. A lost link is replaced by issuing another.
- **24 hours, not the mailed link's hour.** A link somebody opens from a chat after their shift is
  not a link somebody is sitting at a screen waiting for; the tablet enrolment code sets the same
  onboarding window. `PasswordResetToken.InvitationLifetime` is separate from `Lifetime` so the
  mailed flow keeps its hour.
- **A new link retires the old one.** Every unused link for the person is superseded - not
  consumed; no password changed - so "send a new one" is a way to take a lost one back. Two links
  issued at the same instant can both be live until one is used; stated and accepted.
- **Nothing else moves until the link is used.** A person who already has a password keeps it, and
  their sessions stay open, until consumption replaces the one and ends the other. The *address*
  does change the moment it is issued, so the console warns before re-pointing a working sign-in.
- **Only somebody strictly above.** An owner issues for managers, a platform admin for owners and
  managers; a co-owner is refused, because issuing a peer's sign-in is taking over a peer's account
  and the audit log would then name the wrong person for whatever came next. Oneself is refused,
  because a bearer holding a fifteen-minute access token must not be able to turn it into a
  password. A waiter or kitchen hand is refused for the reason above, and a deactivated person is
  refused until they are reactivated - the deactivation *was* the revocation.
- **Rate limited as a credential.** The route carries the `auth` policy, ten a minute per caller,
  rather than the global budget sized for a busy floor.

**The link must point at the console in every deployment.** `Auth:PasswordResetUrlTemplate`
defaults to the local console, and outside Development the host refuses to start on that value -
the same guard `PublicWeb:ManageBookingUrlTemplate` has, for the same reason: the link is minted
once and handed to a person, and one that points at `localhost` sends them nowhere. The token rides
in the URL fragment, so it never reaches the static host's access log - and the same guard refuses a
template that puts `{token}` anywhere else, so a deployment cannot quietly move it back into the
query string.

**Reserved slugs.** The console owns the first path segments `assets`, `dev`, `fonts`, `platform`,
`reset-password`, `sign-in`, `staff` and `venue`; the web app boots the console for those and the
public page for everything else. `SlugText` refuses them as a venue or branch slug, mirroring
`RESERVED_FIRST_SEGMENTS` in the frontend, so no venue can ever sit unreachable under the address a
reset link points at.

## Tokens

| Token | Lifetime | Renewal |
|---|---|---|
| Access (diner, venue user) | 15 minutes | Rotating refresh token, 30 days |
| Tab participant | Tab close + 2h grace, 12h ceiling | None - rejoin the tab |
| Staff device | 365 days | None - re-enrol |
| Staff session | 30 minutes | Renewal handle, dies after 30 minutes idle |

### What a device token can and cannot do

Four properties, and the fourth is the one that makes the first three safe to leave in a browser on
a counter for a year:

| | |
|---|---|
| **Bearer only** | No cookie, no session affinity. It is sent as a header and nothing else. |
| **Branch-scoped** | It carries one `branchId`, copied onto every session opened on it. A waiter at branch A cannot act on branch B however the request is addressed. |
| **Revocable** | `StaffDevice.RevokedAtUtc` is checked on **every** request, so a laptop left in a taxi stops working on its next call rather than when its year-long token expires — and so does any PIN session already open on it. |
| **Grants nothing** | It carries a branch and a device and **no role claim at all**, so every staff policy fails on it. The tablet is not a person; it can offer a PIN, read which venue it is bound to, and read the names of who may sign in on it, and that is the whole list. |

`StaffAuthTests.A_device_token_alone_can_do_nothing_but_offer_a_pin` asserts the last one across
reads and a write, because "by construction" is exactly the kind of claim that stops being true the
first time somebody adds a convenience.

**Refresh tokens rotate.** Each use retires the token and issues its successor, so a stolen copy is
only useful until the real client next refreshes. Every token descended from one sign-in shares a
`ChainId`, and **presenting a token that has already been rotated revokes the whole chain**: two
parties hold the same secret and there is no way to tell which is the thief, so both are made to
sign in again. Revoking only the reused token would leave whichever party refreshed last in
possession of a live session, and that is as likely to be the attacker.

Staff sessions deliberately do not use this mechanism. They expire on inactivity rather than
rotating for thirty days, which is a different thing, so they have their own record.

## Why a stateless token still needs an authority check

A JWT is a **signed statement about the past**. It says who this was, and that nobody has tampered
with the claim since. It cannot say whether that is still true, and for three of the four identities
here it stops being true well inside the token's own lifetime:

- A tab closes. The token stays valid for the two-hour receipt grace, and the participant should be
  able to *read* the bill for those two hours {M} and add nothing to it.
- A manager revokes a stolen tablet. Its device token still has months to run.
- A participant is removed from a tab, or has their ordering taken away. The claim minted at join
  time says nothing about it.

Signature validation alone would let all three keep working. "The token is short-lived" is not an
answer either: fifteen minutes is a long time to hold a tablet somebody just reported stolen, and
the tab grace is deliberately hours long.

So `ITokenAuthorityCheck` runs **on token validation**, before any handler, and is what turns a
revoked device or a closed tab into a 401 carrying a reason (`device-revoked`, `tab-closed`) rather
than a confusing 403 from a policy further down.

### What gets cached, and what deliberately does not

Checking the database on every request would put a round trip in front of every read of a busy tab,
so the check caches for **five seconds**. What it caches matters:

> The **read** is cached. The **decision** is not.

`TabParticipantAccess` {M} the tab's status, the participant's status, their `CanOrder` flag {M} is a
database fact that changes when somebody changes it. The decision that follows depends on the clock:
whether a closed tab is still inside its receipt grace is a different answer at 20:00 and at 22:01.
Caching the decision froze that grace for five seconds at a time, which the token-authority tests
caught: a tab whose grace had just expired went on accepting reads.

Five seconds is chosen against the thing being protected. A revoked tablet is not usable for five
seconds by anyone who is not already holding it, and the alternative {M} a database read per request
per participant {M} costs a busy venue far more than that window is worth. Where the delay is *not*
acceptable, the cache is invalidated directly instead of waited out:

| Event | Invalidation |
|---|---|
| A device is revoked | `InvalidateDevice` on the revoking path {M} immediate, not five seconds later |
| A participant is approved, removed, or loses `CanOrder` | `InvalidateParticipant` |
| A tab is closed or moved to closing | `InvalidateTab`, which drops **every** participant's entry through a per-tab `CancellationChangeToken` |

The last one is the subtle case. A tab has many participants and closing it changes the answer for
all of them at once; without a per-tab token, each entry would expire on its own schedule and the
table would disagree with itself for a few seconds. Both of these were real bugs found by tests
rather than reasoning, which is the argument for the tests existing.

### Read and mutate are separate policies

`TabParticipant` and `TabParticipantMutating` exist because the receipt grace is precisely a window
where reading is right and writing is not. Splitting them puts that distinction in the route table,
where it can be seen, instead of inside seven handlers that each have to remember it.

## Authorization policies

Named policies, applied to endpoints. **Nothing re-checks identity inside a handler** - a check
inside a handler is a check the next handler can forget, and the failure is silent.

| Policy | Rule |
|---|---|
| `TabParticipant` | The token's `tabId` claim matches the route's tab id, the participant is still approved, and the tab has not closed beyond the grace period |
| `TabParticipantCanOrder` | The above, plus the participant's `CanOrder` flag |
| `TabParticipantMutating` | The above, and the tab is genuinely open {M} the receipt grace allows reading a closed tab, never adding to it |
| `PlatformAdminOnly` | The platform operator, for venue creation, suspension and the audit log |
| `WaiterOrAbove` | A staff session or admin-panel identity whose role is Waiter, Manager or Owner |
| `ManagerOrAbove` | Role is Manager or Owner |
| `BranchScoped` | The token's `branchId` claim matches the route's branch id |
| `VenueScoped` | An owner or manager acting inside their own venue |
| `VerifiedDiner` | The token's principal type is `Diner` - a phone number was verified, so there is an account to hold bookings against. A tab participant is deliberately not one |

Two of these are the real security of this system, and both have tests:

- **A staff token for branch A must not act on branch B.** Chains have several branches and staff
  belong to one. `BranchScoped` is combined with every staff endpoint **addressed by branch**,
  including the table-state endpoints. The branch claim on a session is copied from the enrolled
  device, so there is nothing a waiter can send that changes it.

  Two booking routes are addressed by reservation id instead - `POST /api/reservations/{id}/approve`
  and `/reject` - so there is no `branchId` route value for the policy to compare against, and it
  fails closed rather than passing. They carry `ManagerOrAbove`, and the reservation service
  resolves the booking's own branch and checks it against the acting staff member's branch and
  venue. `ReservationEndpointTests` proves a manager of another venue is refused.

  The photo upload `POST /api/branches/{branchId}/photos` is addressed by branch and carries
  `BranchScoped` like the rest; it once carried only `ManagerOrAbove`, which let any manager put
  images into any venue's branch. `PhotoService` also checks the stored staff row - active, and of
  the branch's venue, platform admin exempt - so a deactivated account's still-valid token is
  refused and the lock holds if a route ever loses the policy. The photo id a menu item is given
  is caller-supplied, so `MenuService` refuses one uploaded for another branch as not found there,
  the same answer a foreign category gets. `PhotoScopeTests` proves all of it.

  The approval queue those two act on, `GET /api/branches/{branchId}/reservations?status=1`, is
  addressed by branch and carries `ManagerOrAbove` and `BranchScoped` - and then the same service
  check as approve and reject, because `BranchScoped` widens a manager with a home branch to the
  whole venue (below). So a manager lists exactly the bookings they may decide: their home branch
  only if their record names one, every branch of their venue if it names none, anything for a
  platform admin. Sorted by local date and start time, capped at 200 rows with no paging.
  `BranchReservationListTests` proves a manager of a sibling branch, another venue's manager, a
  waiter and an anonymous caller are refused.
- **A tab participant token must not touch any other tab.** Two adjacent tables must not be able to
  order on each other's bill.

`BranchScoped` widens in exactly one direction: an owner or manager signed in to the admin panel is
venue-scoped rather than branch-scoped - managing every branch is the point of that account - so
when their token carries no matching branch claim, the handler asks whether the branch belongs to
their venue. A staff session with a branch claim is still confined to it, and a venue user is still
confined to their own venue.

`VenueScoped` guards the venue read `GET /api/venues/{venueId}/manage` and the staff routes under
`/api/venues/{venueId}/staff`, which are addressed by venue because staff belong to one rather
than to a branch; the handler passes a platform admin for every venue. `VenueAdminEndpointTests`
proves a manager of the neighbouring venue is refused.

The venue read is what the console opens a venue with, and **which branches it lists is decided
from the caller's stored staff row, never from the token** - a token names at most one branch and
an owner's names none, which is the shape the console once mistook for "no branch". An owner
covers every branch, with or without a home branch; a manager whose row names no branch covers
every branch; a manager whose row names a branch is listed that branch only; a platform admin is
listed everything. The query checks the row itself, so it refuses a neighbour and a deactivated
account even if the policy were lost from the route (`ManagedVenueQueryTests`). Note that the
branch manager's narrowing is **in the console only**: `BranchScoped` still widens their token to
the whole venue on the server, as above, and whether that should change is a decision not taken
here. What is guaranteed is the safe direction - the console never lists a branch the server
would refuse.

`TabParticipantCanOrder` is defined and carries no endpoint yet - it has nothing to guard until
ordering exists - and is covered by `PolicyHandlerTests` rather than shipped unexercised.

## The actor stub

`ICurrentActor` is now implemented from the JWT claims (`ClaimsCurrentActor`), so every
`TableStateChange` carries a real `staffMemberId`. The pre-authentication stub is still registered
in Development when `DevActor:Enabled` is true, where its later registration replaces the real one -
it is what the Prompt 2 integration tests use, and it is still the quickest way to exercise a
manager-only path locally without minting a token. It is not registered in any other environment.

## Out of scope

No SMS or email provider, no Telegram, no shared staff PINs, no biometric unlock, no social login,
no admin panel UI, and nothing touching menu, orders or payments.
