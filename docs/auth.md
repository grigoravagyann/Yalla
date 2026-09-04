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
  implementation that logs, same pattern as the code sender, and no provider is integrated.

Unknown address, wrong password and deactivated account all answer identically, so the sign-in form
cannot be used to discover which addresses have accounts.

**These credentials live on `StaffMember`, not on a separate table.** A manager taps a PIN on the
floor and signs in to the panel from home on the same day, and the audit log has to name the same
person either way. Two tables would give one human two identities and quietly break the log.

---

## Tokens

| Token | Lifetime | Renewal |
|---|---|---|
| Access (diner, venue user) | 15 minutes | Rotating refresh token, 30 days |
| Tab participant | Tab close + 2h grace, 12h ceiling | None - rejoin the tab |
| Staff device | 365 days | None - re-enrol |
| Staff session | 30 minutes | Renewal handle, dies after 30 minutes idle |

**Refresh tokens rotate.** Each use retires the token and issues its successor, so a stolen copy is
only useful until the real client next refreshes. Every token descended from one sign-in shares a
`ChainId`, and **presenting a token that has already been rotated revokes the whole chain**: two
parties hold the same secret and there is no way to tell which is the thief, so both are made to
sign in again. Revoking only the reused token would leave whichever party refreshed last in
possession of a live session, and that is as likely to be the attacker.

Staff sessions deliberately do not use this mechanism. They expire on inactivity rather than
rotating for thirty days, which is a different thing, so they have their own record.

## Authorization policies

Named policies, applied to endpoints. **Nothing re-checks identity inside a handler** - a check
inside a handler is a check the next handler can forget, and the failure is silent.

| Policy | Rule |
|---|---|
| `TabParticipant` | The token's `tabId` claim matches the route's tab id, the participant is still approved, and the tab has not closed beyond the grace period |
| `TabParticipantCanOrder` | The above, plus the participant's `CanOrder` flag |
| `WaiterOrAbove` | A staff session or admin-panel identity whose role is Waiter, Manager or Owner |
| `ManagerOrAbove` | Role is Manager or Owner |
| `BranchScoped` | The token's `branchId` claim matches the route's branch id |
| `VenueScoped` | An owner or manager acting inside their own venue |

Two of these are the real security of this system, and both have tests:

- **A staff token for branch A must not act on branch B.** Chains have several branches and staff
  belong to one. `BranchScoped` is combined with every staff endpoint, including the table-state
  endpoints. The branch claim on a session is copied from the enrolled device, so there is nothing
  a waiter can send that changes it.
- **A tab participant token must not touch any other tab.** Two adjacent tables must not be able to
  order on each other's bill.

`BranchScoped` widens in exactly one direction: an owner or manager signed in to the admin panel is
venue-scoped rather than branch-scoped - managing every branch is the point of that account - so
when their token carries no matching branch claim, the handler asks whether the branch belongs to
their venue. A staff session with a branch claim is still confined to it, and a venue user is still
confined to their own venue.

Two policies are defined but carry no endpoint yet: `TabParticipantCanOrder` has nothing to guard
until ordering exists, and `VenueScoped` has no venue-level route yet. Both are covered by
`PolicyHandlerTests` rather than shipped unexercised.

## The actor stub

`ICurrentActor` is now implemented from the JWT claims (`ClaimsCurrentActor`), so every
`TableStateChange` carries a real `staffMemberId`. The pre-authentication stub is still registered
in Development when `DevActor:Enabled` is true, where its later registration replaces the real one -
it is what the Prompt 2 integration tests use, and it is still the quickest way to exercise a
manager-only path locally without minting a token. It is not registered in any other environment.

## Out of scope

No SMS or email provider, no Telegram, no shared staff PINs, no biometric unlock, no social login,
no admin panel UI, and nothing touching menu, orders or payments.
