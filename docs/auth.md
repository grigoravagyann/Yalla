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
- A wrong code answers **401 `verification-code-invalid`** with `context.attemptsRemaining`, the tries
  left on the number's live code. **Zero** when nothing is live - none asked for, already used, or
  replaced by a newer one - which is the cue to ask for a new code rather than retry. The app read
  a count the server never sent, defaulted it to zero, and told a diner their code was spent after
  one slip, so they asked for another and spent the hourly per-number budget. Whether a number has a
  code outstanding was already visible to an anonymous caller - that case always had its own
  sentence, and a live code answers 429 after five tries - so the count publishes nothing new.

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

The question this interface was written to defer - SMS, which is metered to Armenian numbers, or a
Telegram bot, which is free - now has a second answer beside it rather than instead of it. The SMS
code path above is untouched and still has only the development sender, which writes the code to
the log and, in Development only, returns it in the response body. The Telegram bot is a **second
door**, not a second sender: it never sends a code at all, because Telegram already holds the
verified number - see [Telegram verification](#telegram-verification). The message text for both
comes from the same localised resources (`hy`, `ru`, `en`), so neither channel invents its own
wording.

The Development-only affordance is gated by the host from `IHostEnvironment`, not from
configuration, so `Auth__ReturnVerificationCodeInResponse=true` in a production environment does
nothing.

### The password door

The same `DinerUser` row, with four optional things on it: a username, an email, a password hash
and a profile photo. Two routes under `/api/auth` open it and five under `/api/diner/me` live
behind it. Both doors mint the same token and the same refresh chain, so nothing downstream knows
which one a diner came in by.

- `POST /api/auth/diner/register` — `{ username, email, password, phoneE164, displayName,
  localeCode? }`, answers **201** with exactly what `verify-code` returns (`isNewAccount` true).
  Username 3-30 characters, letters, digits, `.` and `_`, starting with a letter or digit;
  email a real address; both **stored trimmed and lowercased** and unique among the rows that
  have one; password 8-128 characters and not the username or the email - length is the only rule,
  for the reason §4 gives. A malformed field is **400 `invalid-request`** with `context.field`
  naming it. A taken one is **409** - `username-taken`, `email-taken`, or `phone-in-use` - with
  `context.field` as well. The rules are three pure functions in `DinerAccountRules`, called by
  registration and by the profile edit, so the two cannot disagree.
- **Registering does not verify the number.** The phone is stored as typed and
  `PhoneVerifiedAtUtc` stays null until the diner passes a code once; the profile says
  `phoneVerified: false` and the app shows a *verify* link into the code flow. The code flow, when
  it meets a registered number, **signs that account in and marks it verified** rather than creating
  a second row - the number is the account either way. `phone-in-use` is therefore not a dead end:
  the app's answer is *log in with a code instead*, which the number's owner can do and a stranger
  typing it cannot. Every row that existed before this door was created by a code coming back from
  its number, so the migration backfilled their stamp from the last sign-in.
- **The first code to a registered number displaces the registrant.** Anybody can type anybody's
  number into the sign-up form, so until a code comes back the password on that row is only the
  form-filler's. When `verify-code` succeeds for an account whose `PhoneVerifiedAtUtc` was null and
  which has a password, the person holding the phone has proved the number and the registrant never
  did: the **password hash is cleared**, **every refresh token for the account is revoked**
  (`RefreshTokenStore.RevokeAllForSubjectAsync`, reason `phone-proved-by-another`), and only then is
  the number marked verified and the verifier signed in. The squatter's password now answers 401
  `invalid-credentials` and their refresh token is refused. **The honest registrant keeps
  everything** by verifying with the token `register` gave them: `verify-code` stays anonymous but
  reads a valid diner bearer token when one is sent, and when it names the very account whose number
  is being proved, the token shows the verifier holds the password and the code shows they hold the
  phone - one person - so the number is marked verified and the password and sessions stay. That
  cannot shelter a squatter: the squatter holds the token but never receives the code, and the
  owner receives the code but never holds the squatter's token. Anonymous, or under a different
  diner's token, the displacement above applies; an invalid token is ignored rather than refused,
  and a displaced verifier sets a password through `PUT /api/diner/me/password` with no current
  one. An account whose number was already proved
  keeps its password and its sessions when it verifies again. The rule lives in
  `DinerUser.ProveNumber`, which also moves the account's session generation on: the access
  token the registrant still holds is refused on its very next request with **401
  `session-revoked`** - see [Ending a diner's sessions](#ending-a-diners-sessions).
- **An unproved number cannot book.** Creating a booking (`POST /api/reservations`), extending its
  hold (`POST /api/reservations/{id}/extend-hold`) and opening a tab from one
  (`POST /api/tabs/open-by-booking`) answer **403 `phone-not-verified`** while
  `PhoneVerifiedAtUtc` is null, because each of them puts reminders and a no-show record on that
  number. The check reads the row, not the token, and lives in `ReservationService` and
  `TabService` through `DinerPhoneGate`, so no caller of those services skips it. Reading your own
  bookings and cancelling one stay open.
- `POST /api/auth/diner/login` — `{ identifier, password, localeCode? }`, where `identifier` is a
  username or an email, case-insensitively. Unknown identifier, wrong password, an account with
  no password yet and a deactivated account all answer **401 `invalid-credentials`** - and all
  cost the same time, because the hash is always paid, against a decoy when there is nothing else
  (`SecretHasher.DecoyHash`, shared with the admin panel's sign-in). Rate limited on two axes like
  `request-code`: per address by the pipeline, and per identifier by `PasswordAttemptLimiter` -
  ten attempts in fifteen minutes, right or wrong, after which **429 `too-many-attempts`**. A
  window and not a lockout, because a diner has no manager to clear one and a lockout anybody could
  trigger by typing a username is a way to stop any diner signing in.
- `GET /api/diner/me` and `PUT /api/diner/me` — the account behind the token and nothing else:
  there is no id in the route, so there is no parameter through which one diner reaches another's
  row. The edit changes only the fields sent, under registration's rules and 409s, and nothing can
  be cleared - the name is read out at the door and the other two are how the person signs in.
- `PUT /api/diner/me/password` — `{ currentPassword?, newPassword }`. An account the code flow
  created sets its first password with no current one: the bearer token is the proof, and there is
  nothing to compare against anyway - which is why that token's session generation is compared with
  the row once more here, past the token check's cache. One that has a password must send it, and a
  wrong one is 401 `invalid-credentials` - **unless this sign-in proved the phone in the last ten
  minutes.** A diner who forgot their password signs in again with a code (`verify-code`) or the
  Telegram claim, and those two sign-ins - and only they, through `DinerPhoneSignIn` - stamp the
  access token with **`phone_proved_at`** (Unix seconds). For ten minutes after that instant
  (`DinerProfileService.PhoneProofWindow`, read off the injected clock) `currentPassword` may be left
  out; the session generation is compared with the row as for a first password. The stamp is on the
  access token rather than a column on the refresh session, on purpose: a refresh proves nothing about
  the phone and does not re-mint it, so the window cannot be stretched by refreshing, and nothing is
  stored. Left out with no recent proof - a password sign-in, a refreshed token, a proof gone stale -
  is **422 `validation-failed`** on `currentPassword`: *"Enter your current password, or sign in again
  with a code to set a new one."* (Before, leaving it out was the same 401 as a wrong one; the app
  never sent that shape, and a wrong one is still the 401 it maps.) **Setting or changing it ends every access token the
  account holds, the caller's included, and revokes the refresh tokens of every other sign-in**
  (reason `password-changed`). The sign-in the caller's token names in its `rch` claim keeps its
  refresh token, so the app that made the change refreshes and carries on.
- `POST /api/diner/me/photo` and `DELETE /api/diner/me/photo` — the profile picture, through the
  same pipeline as a branch photo: sniffed, stripped of EXIF, three WebP variants, same size cap,
  same **409 `unsupported-image`** (which branch uploads now answer too, in place of the generic
  conflict). A `Photo` now belongs to exactly one owner - a branch or a diner, enforced by a check
  constraint - and is stored under `diner-{id}/` rather than a branch id. Replacing a picture leaves
  the old one for the orphan sweep, which excludes anything a `DinerUsers.PhotoId` still points at.
  **Removing one deletes it at once**, row and files (`IPhotoService.DeleteAsync`), so its
  `/api/photos/{id}/…` links answer 404 from the next request: somebody taking a picture of
  themselves down means now, not after the sweep's day of grace.

The `VerifiedDiner` policy keeps its name and admits any diner token, including one issued by
`register` before the number is proved. The policy says "has an account, and is not a tab
participant"; `DinerUser.PhoneVerifiedAtUtc` says whether the number is real, and anything that
needs a real number reads that - today, the three booking routes above.

### Ending a diner's sessions

A refresh token lives in the database and can be revoked there. An access token cannot: it is a
signed statement that stays valid for its fifteen minutes. That used to be accepted as short enough,
and for one case it was not. A squatter displaced by the number's owner kept a working access token,
and `PUT /api/diner/me/password` with no current password would have handed them the account back.

So every diner access token carries **`sgen`** - the account's `DinerUsers.SessionGeneration` when it
was minted - and every request carrying one is checked by `ITokenAuthorityCheck.CheckDinerSessionAsync`
on token validation, before any handler: the account must exist, be active, not be deleted, and be on
the token's generation. Otherwise the answer is **401 `session-revoked`** with
`WWW-Authenticate: Bearer error="invalid_token"`, on every diner-authenticated route. A token minted
before the claim existed reads as generation zero.

The generation moves on - and every access token of the account ends - on exactly four events:

| Event | Where | Refresh tokens |
|---|---|---|
| The number's owner proves it and displaces a registrant | `DinerUser.ProveNumber` | All revoked (`phone-proved-by-another`) |
| A password is set or changed | `DinerUser.SetPassword` | Every other sign-in's revoked (`password-changed`); the caller's chain, named by the token's `rch` claim, kept |
| The account is deactivated | `DinerUser.SetActive(false)` | Kept; refresh refuses an inactive account, and `verify-code` and `login` both answer 401 `invalid-credentials` |
| The account is deleted | `DinerUser.MarkDeleted` | All revoked (`account-deleted`) |

Storing a stronger hash of the same password at sign-in (`RehashPassword`) is not a change and moves
nothing.

**The client's rule** is one line: on `session-revoked`, try the refresh token once; if that is
refused too, sign out. After a password change the refresh succeeds on the device that made it and
the app carries on; on every other device, and after a displacement or a deletion, it cannot.

**Which sign-in made the change.** A diner access token carries **`rch`**, the refresh-token chain it
was issued with - at sign-in, registration, code verification and every refresh. An admin-panel
(`VenueUser`) token carries it too, from sign-in and every refresh; the admin-panel password change
does not need it, because it answers the caller a fresh pair instead (§4). A password change
revokes every refresh token of the account except that chain's, so a refresh token somebody else holds
(a lost phone, a copied backup) cannot mint a token under the new generation. A token minted before the
claim existed names no chain, and then nothing is kept: that device signs in again as well.

**The cache.** The account read goes through the same five-second cache as a device's, with two
differences. The window is measured on `IClock`, so a test and a deployment agree about when it
lapses. And a token *newer* than the cached generation bypasses the cache: generations only go up, so
that token proves the cache stale, and refusing it would sign the rightful owner out in the moment
they signed in. Every write in this process that bumps the generation calls `InvalidateDiner`
**after its commit** - before it, another request could re-read and re-cache the old value. A write
that announces nothing, such as an operator's `UPDATE` or another instance, is noticed within the
window. `DinerPhoneGate` and the first-password path read the row directly as well.

### Deleting an account

`DELETE /api/diner/me` under `VerifiedDiner`, body `{ "password": string | null, "code": string | null }`.
An account with a password proves it with `password`; one without proves it with `code`, a one-time
code from `POST /api/auth/diner/request-code` for the account's own number.

- The proof the account needs, missing, is **422 `validation-failed`** naming `password` or `code`
  (bound `required`).
- A wrong password, or a wrong, expired or missing code, is **401 `invalid-credentials`**. A wrong
  code spends one of the code's five attempts.
- A spent code, or more than ten attempts on the account in fifteen minutes (the password sign-in's
  `PasswordAttemptLimiter`, keyed on the account), is **429 `too-many-attempts`**. The route also
  carries the `auth` pipeline policy.
- Success is **204**.

It is one transaction (`DinerAccountDeletion.EraseAsync`), and the retention rule is the product
decision: **what is the person's is deleted; what is the venue's record is kept, with the link to the
person cut.**

| Row | What happens |
|---|---|
| `DinerUsers` | A tombstone: `DeletedAtUtc` set, `IsActive` false, `PhoneE164`, `Username`, `Email`, `DisplayName`, `PasswordHash`, `PhoneVerifiedAtUtc` and `PhotoId` cleared, `SessionGeneration` bumped. The number, username and email can make a new account; the id is never reused |
| `RefreshTokens` | Every one revoked, reason `account-deleted` |
| `DinerDevices` | Deleted |
| `PhoneVerificationCodes` for the account's number | Deleted, the code that proved the deletion included (counted as `phoneCodes`). Keyed by the number, so clearing `DinerUsers.PhoneE164` would not reach them |
| `DinerFavorites` | Deleted (K11) |
| `DinerNotifications` | Deleted, including a reminder that has not appeared yet (K12) |
| `Photos` the account owns | Deleted, files first - the current picture and any replaced one still waiting for the sweep |
| `BranchReviews` by the account | Deleted; rating and count are computed on read |
| `TabParticipants` | `UserId` cleared and `DisplayName` set to `Guest`, which is what an account-less scan looks like. The row stays: order lines and shares point at it |
| `Reservations` | `DinerUserId` cleared. `GuestName` and `GuestPhone` stay - they are what the venue was given for that booking |
| `TabOrders` and their lines | Untouched |
| `PlatformAuditLogs` | One row: `Action` `diner.delete`, `TargetType` `DinerUser`, `TargetId` the account. `ActorStaffMemberId` holds the diner's id because no staff member acted; `ChangesJson` says `actorType: diner` and counts what was removed and detached, and carries nothing that identifies the person |

**Writes racing the deletion.** Another phone's request can be past the token check - cached for five
seconds, and per process - when the account is deleted. The deletion takes an update lock on the
`DinerUsers` row first (`DinerAccountLock`) and re-reads the row and the account's photos under it.
Every writer that keeps something of the person's takes the same lock and reads the row under it -
adding or merging favourites, writing or revising a review, reporting one, editing the profile,
setting a password, uploading a picture, booking a table: one that got there first commits before the
deletion reads what to remove, and one that comes second is refused with 401 `session-revoked`. The
two feed entries that are not written by the diner's own request - a booking's reminder, saved just
after the booking commits, and a kitchen marking an order ready - take the lock as well, and leave the
entry (and the order-ready push) out when the account is no longer live.

**A new table with a `DinerUserId` needs one line** in `DinerAccountDeletion`:
`RemoveRowsOwnedByAsync` if its rows are the person's, `DetachVenueRecordsAsync` if they are the
venue's record. A foreign key will not catch the omission, because the account row is a tombstone
rather than gone. Favourites (`DinerFavorites`), the notifications feed (`DinerNotifications`) and
review reports are in the first of the two - review reports above the reviews line, and including the
reports filed against the diner's own reviews.

### Telegram verification

The second way to prove a number, for the people who would rather not wait for an SMS - and for
a product whose SMS bill is a line item. **The SMS code path is untouched**; this sits beside it
and ends in exactly the same place.

**Flow.**

1. `POST /api/auth/diner/telegram/start` `{ localeCode? }` → `200 { challengeId, deepLinkUrl,
   expiresInSeconds, pollAfterMs }`. The app opens `deepLinkUrl`
   (`https://t.me/{bot}?start={nonce}`), which starts the bot with a single-use nonce.
2. The bot (`/start nonce`) binds the challenge to the Telegram account that opened it and shows
   one button: *share my phone number*. Telegram answers that button with the account's own
   contact card.
3. The contact verifies the challenge. The bot says so - *go back to the Yalla app* - and takes
   the keyboard away. (No button back into the app: Telegram's inline buttons accept only
   http(s) and tg:// URLs and refused `yalla://`. An HTTPS universal link can be added once
   yalla.am hosts the `.well-known` association files.)
4. The app polls `POST /api/auth/diner/telegram/{challengeId}/claim` `{ localeCode? }` every
   `pollAfterMs`: `200 { state: "pending" }` until the contact arrives, then once
   `200 { state: "verified", signIn, phoneE164 }` where `signIn` is **exactly** `verify-code`'s
   `DinerSignInResult` and `phoneE164` is the number Telegram handed over. After that the
   challenge is spent.

The claim runs the account path `verify-code` runs after its code check - one class,
`DinerPhoneSignIn`, shared by both doors: find or create the `DinerUser` by number, refuse a
deactivated one with `401 invalid-credentials`, prove the number (`DinerUser.ProveNumber`, with
the same displacement rule for a registered number and the same *send the account's own bearer
to keep its password* escape), mint the refresh chain and the access token. Nothing downstream
can tell which door a diner came in by.

**Security rules.**

- The nonce is 256 random bits, base64url (43 characters, inside Telegram's 64-character
  start-parameter limit), stored **only as a SHA-256 hash** with a unique index. A dump of
  `TelegramVerifications` holds no usable link.
- A challenge **binds to the first Telegram account that opens it**. The same account opening
  the link again is fine; any other account gets *link expired* and can neither bind nor verify
  it. A forwarded link is dead on arrival.
- A contact verifies only when it is **the sender's own card**: Telegram's `contact.user_id`
  must equal `message.from.id`. A forwarded card, or one for a number with no Telegram account,
  answers *that is not your own contact card* and changes nothing. The number is normalised
  through the same `PhoneNumber.Normalise` as every other number (Telegram sends it with or
  without the plus).
- Links last **five minutes**; a verified challenge stays claimable for **two more**, so a
  contact that arrives at 4:58 is not verified and unclaimable in the same breath.
- `claim` answers **410 `telegram-verification-expired`** for an unknown id, an expired or
  spent challenge, and a challenge started under one diner's token and claimed under another's -
  one answer for all of them. A claim before verification answers `pending` and reveals nothing.
- The webhook requires `X-Telegram-Bot-Api-Secret-Token` to equal the configured secret,
  compared in constant time; anything else is a bare 401 with no body. Once the header is right
  it always answers 200 - a body it cannot read is logged and ignored - because Telegram retries
  anything else for a day.
- Rate limits: `start` shares `request-code`'s per-address policy; `claim` has its own
  `auth-telegram-claim` (60 a minute per address, sized for one app polling every two seconds);
  the webhook has `telegram-webhook` (600 a minute), a ceiling on the bot's whole traffic.
- Logs carry challenge ids, Telegram user ids and outcomes. Never the number, never the token:
  the Bot API puts the token in the URL path, and the client scrubs it from every exception and
  log line, which a test asserts.

**Configuration** (`Telegram` section; secrets via user secrets or `Telegram__*` variables):

| Key | Meaning |
|---|---|
| `Enabled` | Off by default. **Off, the three routes are not mapped at all** - no `/api/telegram/webhook`, no `/api/auth/diner/telegram/*` - rather than routes that answer "not configured". |
| `BotToken` | From @BotFather. SECRET. |
| `BotUsername` | Without the `@`; goes into the deep link. |
| `WebhookSecret` | What Telegram sends back on every webhook call. SECRET. Required in `Webhook` mode. |
| `Mode` | `Webhook` (default) or `LongPolling` (Development only). |
| `WebhookPublicUrl` | When set in `Webhook` mode, the host calls `setWebhook` with it and the secret at startup. Blank means somebody registered the webhook by hand. |
| `ApiBaseUrl`, `ChallengeLifetime`, `PollAfterMs`, `LongPollTimeoutSeconds` | `https://api.telegram.org`, five minutes, 2000, 25. |

Enabled without a token, a username or (in webhook mode) a secret fails startup with
instructions, exactly as a missing `Jwt:SigningKey` does.

**Local development: long polling.** A laptop has no public address for Telegram to call, so
`Mode=LongPolling` has a hosted service ask Telegram for updates in a loop and feed them to the
same `ITelegramUpdateHandler` the webhook uses. The host refuses it outside Development, whatever
the setting says: two deployed instances polling one bot would each receive half the
conversation. `appsettings.Development.json` does **not** switch the door on, because the test
host reads that file with no token; switch it on in your own user secrets:

```
dotnet user-secrets set "Telegram:Enabled"      "true"            --project src/Yalla.Api
dotnet user-secrets set "Telegram:BotToken"     "<from @BotFather>" --project src/Yalla.Api
dotnet user-secrets set "Telegram:BotUsername"  "<name without @>"  --project src/Yalla.Api
dotnet user-secrets set "Telegram:Mode"         "LongPolling"       --project src/Yalla.Api
```

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
where it is owed. Participant tokens pay it against their tab, and diner tokens against their
account's session generation - a displaced squatter's token showed that fifteen minutes is not short
(see [Ending a diner's sessions](#ending-a-diners-sessions)). Venue-user tokens pay it against the staff
row's session generation (§4, "Ending admin-panel sessions").

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

**And a PIN is unique on its tablet.** Setting one - creating somebody with a PIN, or resetting it -
is refused with **409 `pin-taken`** (`context.field = "pin"`) when anybody else who signs in on the
same tablet already uses it: for somebody at a branch, everyone active at that branch plus the
venue-wide people (owner, general manager, branchless staff); for somebody venue-wide, every active
person in the venue. PINs are hashed, so the check verifies the candidate against each of those hashes
(a roster is a few dozen people at most). Deactivated people do not count, your own current PIN is no
clash, and duplicates already in the data are left alone. Moving a person to another branch does not
re-check their PIN; two PINs set at the same instant are not serialised against each other.

## 4. Venue owner and manager — email and password

### Ending admin-panel sessions

Every admin-panel (`VenueUser`) access token - owner, general manager, branch manager, platform admin -
carries **`sgen`**, the staff row's `SessionGeneration` when it was minted, at sign-in and every refresh.
**Every request with such a token** compares it with the row, through the same five-second cache as a
diner's (bypassed for a token newer than the cached read), and a token on an older generation - or whose
row is gone - is refused with `401 session-revoked`. All of them rather than only the platform's routes:
the reason to end a session is the same whichever door the token is used on. Deactivation is not part of
this check; the services still refuse an inactive staff member from the stored row, with 403.

The generation moves (`StaffMember.EndSessions`) when the password is **reset** from a link, when the
platform admin or a venue user **changes** theirs, and when a venue user **signs out everywhere else**.
A reset and a change also revoke every refresh token; the change spends any reset link still unused and
answers the caller a fresh pair under the new generation, so the console carries on
(`docs/platform-admin.md` §5, and "The venue console's own account" below). A rehash at sign-in does
not move it. **A token minted before `sgen`
existed reads as generation 0**, which is where every row starts (migration `StaffSessionGeneration`), so
those keep working until they expire unless a bump ends them first.

### The venue console's own account

`/api/venue/me` is Իմ հաշիվը for an **owner, general manager or branch manager signed in with email and
password** (policy `VenueStaffAccount`: the `VenueUser` principal type and one of the three venue ranks).
The platform admin has `/api/platform/me`; a manager's PIN session on a tablet is refused, because a
tablet is not where a password is changed. No route takes an id: the row is the token's, re-checked from
the store (active, a venue rank, a sign-in).

| Route | What it does |
|---|---|
| `GET /api/venue/me` | `{ staffMemberId, fullName, email, role, venueId, branchId? }` - `branchId` only for a branch manager |
| `PATCH /api/venue/me` | `{ fullName }`, 1-200 after trimming; audited `account.rename`, nothing written for the same name |
| `POST /api/venue/me/password` | `{ currentPassword, newPassword }` - the platform admin's change: every problem in one 422 (`currentPassword` `conflict` when wrong - a form error, not a 401), ten wrong ones in 15 minutes lock it with `429 too-many-attempts` and `Retry-After`, the `auth` rate limit on top; ends every session and answers a fresh pair. Audited `account.password-change` / `account.password-change-failed` |
| `GET /api/venue/me/sessions` | `{ sessions: [{ id, signedInAtUtc, lastActiveAtUtc, isCurrent }] }`, current first |
| `POST /api/venue/me/sessions/sign-out-others` | `{ endedSessions }`; audited `account.sign-out-others` |

- **The email is not theirs to change.** Whoever outranks them issues a new sign-in link for a new
  address; an owner's is the platform's.
- **One set of rules.** `OwnAccountChanges` holds the name bounds, the proof of the current password, the
  lockout counted from the audit rows and the end of every session; the platform admin's account and this
  one each pass their own audit slugs. The feed leaves out both `-failed` slugs: they are the lockout's
  counter, not a change.
- **A sign-in is a refresh chain**, the one per-session record there is. A session row is a chain with a
  token that is neither revoked, rotated nor expired; `signedInAtUtc` is the chain's first token,
  `lastActiveAtUtc` its live one - the last renewal, about every fifteen minutes while the console is in
  use - and `isCurrent` compares the chain with the token's `rch` claim. **Nothing records the browser,
  the device or the place**, so none of that is on the wire.
- **Signing out everywhere else** revokes every other chain (reason `signed-out-elsewhere`) and moves the
  generation, so their access tokens stop now rather than within fifteen minutes. The caller's access
  token stops with them and **the caller's chain is kept**: the console refreshes and carries on. It
  answers no fresh pair on purpose - an access token alone must never turn itself into a thirty-day
  refresh token; the password change, which proves the password, is the one that does. A token with no
  `rch` claim cannot say which sign-in to keep, so it is refused with `409 sign-in-unknown` and nothing
  changes; signing in again gives a token that names its chain.
- **The profile's `branchId`** is a branch manager's home branch only. An owner's row may name a branch,
  but an owner runs every branch, so the profile leaves it out - as it does for a general manager.
- **The wrong guesses are counted one at a time.** The change takes the account's lock (below) before it
  counts, so guesses sent at once queue on it and the tenth is the last one heard.
- **The tablet PIN** stays where it was: a staff member sets their own through
  `POST /api/venues/{venueId}/staff/{staffMemberId}/pin` with the ids `GET /api/venue/me` answers - 4 to 8
  digits, `409 pin-taken` when somebody else on the same tablet taps it.

### The roles, and who reaches which branch

| Role | Wire value | Branch on the row | Reaches |
|---|---|---|---|
| `Owner` | 1 | normally none | every branch of the venue |
| `GeneralManager` | 6 | **always none** | every branch of the venue |
| `Manager` (a **branch manager**) | 2 | **always one** | that branch only |
| `Waiter`, `Kitchen` | 3, 4 | one, or none | the tablet's branch |
| `PlatformAdmin` | 5 | none, and no venue | everything |

Rank is `PlatformAdmin > Owner > GeneralManager > Manager > Waiter, Kitchen`, declared in
`StaffRoleRules.Seniority` rather than read off the numbers (6 sorts after Kitchen by value).

- **Who adds and changes whom.** An owner: co-owners, general managers, branch managers, waiters,
  kitchen. A general manager: branch managers, waiters and kitchen at any branch of the venue - never
  an owner or another general manager, whether adding, editing, deactivating, re-PINning or issuing a
  sign-in. A branch manager: waiters and kitchen **at their own branch**, and nobody they would put
  anywhere else (another branch, or no branch - a branchless waiter is on every tablet). Refused with
  403 `forbidden`; `context.requiredRole` is 6 when the refusal is reach rather than rank. A sign-in
  is issued only by somebody strictly above, so a general manager issues for branch managers and an
  owner for general managers. Owner, general manager and branch manager may hold an email and
  password; waiters and kitchen may not.
- **The branch suits the role.** A branch manager without a branch, at creation or after any role or
  branch change, is 422 `validation-failed` with `context.field = "branchId"` and *"A branch manager
  works at one branch. Choose it."*; a general manager with one is the same 422 with *"A general
  manager runs every branch, so leave the branch empty."* Promotion from branch manager to general
  manager therefore sends `role: 6, setBranch: true, branchId: null` in one `PATCH`. The entity
  enforces it (`StaffMember.Reassign`) and so does the table (`CK_StaffMembers_RoleBranch`).
- **The migration.** `GeneralManagerRole` turned every manager whose row named no branch - the
  venue-wide manager of the old rules - into a general manager, then added the constraint. Every
  manager with a branch stayed a branch manager. Down reverses both.
- **Server-side confinement.** `BranchScoped` passes an owner or general manager on any branch of
  their venue and a branch manager only on the branch their token names; `IStaffBranchGuard` decides
  the same from the stored row; the managed-venue read lists the same set; the staff list shows a
  branch manager their own branch's people; a report rollup across the venue is owner, general
  manager or platform admin. Tokens follow the row: a general manager's names no branch, a branch
  manager's names theirs.
- **Tablets.** A general manager, like an owner, is on the roster of every branch tablet of the venue
  and may PIN-sign there; that session is confined to the tablet's branch like anybody's. A branch
  manager is on their own branch's tablet only.

### Email and password

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

### Refreshing and revoking are serialised per account

A refresh reads its token and then rotates it; a revocation - a password reset or change, signing out
everywhere else, a sign-out, a number proved by its owner, a deletion - reads the account's tokens and then
revokes them. Nothing on the row says it changed in between, so a refresh that read its token just before
a revocation committed used to mint a successor the revocation never saw, and the sign-in that was meant
to end carried on for thirty days. Diner, venue user and platform admin alike.

So `RefreshTokenStore` does both under **the account's lock**: an update lock on the `StaffMembers` or
`DinerUsers` row, held by the transaction to its commit, taken before any token is read (and the token
read again under it). Whichever side comes second waits for the other's commit: a refresh that waited finds
its token revoked and is refused; a revocation that waited finds the successor and ends it too. Every
rotation and revocation therefore runs in a transaction - the refresh, sign-out, reset, password-change and
sign-out-elsewhere paths open one, and the store refuses without one. `RefreshRevocationRaceTests` holds
each side open while the other runs, in both orders, for a staff member and a diner, and checks the
second side really waited.

**What a change decides, it decides under the lock.** Taking the lock only around the revocation is not
enough when the change was decided from a read taken before it. So the password reset reads its link, takes
the account's lock and reads the link and the staff row again before it checks the link is still usable -
a link a password change spent meanwhile, or a second use of the same link, is refused, and the generation
it moves is the one that stands. The phone proof that displaces a registrant locks and re-reads the diner
before it clears the password and moves the generation, so a password the registrant set while it waited
- and the token minted with it - is displaced too. Issuing a new sign-in link takes the person's lock
before it retires their unused links, so an old link being redeemed at that moment either reset the
password first or waits and finds itself retired. The password change takes the lock before it counts the
wrong guesses. The slow part, hashing a new password, is done before the lock is taken; checking the current
password stays after the lockout count, under it. `AccountLockRaceTests` forces each interleaving.

A rotation is handed the caller's transaction (`RefreshTokenStore.RotateAsync(..., transaction, ...)`) and
commits it only when the reuse alarm fires, because the caller then throws; any other transaction on the
context is refused rather than committed early.

**Reuse detection is strict, and clients single-flight their refresh.** Two refreshes sent with the same
token - two tabs, or a retry racing the first attempt - are indistinguishable from a thief replaying a
stolen copy: the second finds the token already rotated and revokes the whole chain, so both sign in again.
This is deliberate and stays. The clients therefore send one refresh at a time per sign-in; the console
coordinates across its tabs with the Web Locks API, so one tab refreshes and the others take its result.

A revocation ends only the tokens that could still be exchanged - unrevoked, unrotated, unexpired, one per
live sign-in. Rotated rows are refused already and are **never pruned**, so a long-lived account
accumulates one row per renewal; a periodic prune of rows long past their expiry is a follow-up, not built.
The reuse alarm and sign-out still mark their whole chain.

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
  able to *read* the bill for those two hours - and add nothing to it.
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

`TabParticipantAccess` - the tab's status, the participant's status, their `CanOrder` flag - is a
database fact that changes when somebody changes it. The decision that follows depends on the clock:
whether a closed tab is still inside its receipt grace is a different answer at 20:00 and at 22:01.
Caching the decision froze that grace for five seconds at a time, which the token-authority tests
caught: a tab whose grace had just expired went on accepting reads.

Five seconds is chosen against the thing being protected. A revoked tablet is not usable for five
seconds by anyone who is not already holding it, and the alternative - a database read per request
per participant - costs a busy venue far more than that window is worth. Where the delay is *not*
acceptable, the cache is invalidated directly instead of waited out:

| Event | Invalidation |
|---|---|
| A device is revoked | `InvalidateDevice` on the revoking path - immediate, not five seconds later |
| A participant is approved, removed, or loses `CanOrder` | `InvalidateParticipant` |
| A tab is closed or moved to closing | `InvalidateTab`, which drops **every** participant's entry through a per-tab `CancellationChangeToken` |
| A diner's session generation moves on: the number proved by its owner, a password set, the account deleted | `InvalidateDiner`, after the commit. A token newer than the cached read bypasses the cache regardless |

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
| `TabParticipantMutating` | The above, and the tab is genuinely open - the receipt grace allows reading a closed tab, never adding to it |
| `PlatformAdminOnly` | The platform operator, for venue creation, suspension and the audit log |
| `WaiterOrAbove` | A staff session or admin-panel identity whose role is Waiter, Manager, GeneralManager, Owner or PlatformAdmin - a PIN session on a tablet and the console's email sign-in alike |
| `KitchenOrAbove` | As `WaiterOrAbove`, and the Kitchen role too. Only the kitchen queue and the order rail carry it; on the rail the service then limits Kitchen to `InKitchen -> Ready`. The floor, tabs and service requests stay `WaiterOrAbove` |
| `ManagerOrAbove` | Role is Manager (a branch manager), GeneralManager, Owner or PlatformAdmin, on a staff session or an admin-panel identity. Says nothing about which branch - always paired with a scope policy, which passes the platform admin for every branch |
| `BranchScoped` | The token's `branchId` claim matches the route's branch id |
| `VenueScoped` | An owner, general manager or branch manager acting inside their own venue; the services narrow a branch manager to their branch |
| `VenueStaffAccount` | An owner, general manager or branch manager signed in to the admin panel: principal type `VenueUser` and one of the three venue ranks. The venue console's own account (`/api/venue/me`); not a tablet session, not the platform admin |
| `VerifiedDiner` | The token's principal type is `Diner` - there is an account to hold bookings against. A tab participant is deliberately not one. Since the password door, the name overstates it: an account that registered with a password holds this token before its number is proved, and `DinerUser.PhoneVerifiedAtUtc` is the fact about the number. The token must also belong to a live session - an active, undeleted account on the token's `sgen` - which is checked on token validation, before the policy runs |

Two of these are the real security of this system, and both have tests:

- **A staff token for branch A must not act on branch B.** Chains have several branches and staff
  belong to one. `BranchScoped` is combined with every staff endpoint **addressed by branch**,
  including the table-state endpoints. The branch claim on a session is copied from the enrolled
  device, so there is nothing a waiter can send that changes it.

  Two booking routes are addressed by reservation id instead - `POST /api/reservations/{id}/approve`
  and `/reject` - so there is no `branchId` route value for the policy to compare against, and it
  fails closed rather than passing. They carry `WaiterOrAbove` - a short-notice request is the
  floor's to decide, within fifteen minutes - and the reservation service resolves the booking's own
  branch and checks it against the acting staff member's **stored** row: a waiter or branch manager
  at their own branch, an owner or general manager at any branch of their venue, a platform admin
  anywhere. `no-show` and `release` are addressed and checked the same way.
  `ReservationEndpointTests` proves a manager of another venue is refused, and
  `ConsoleBookingActionsTests` that a waiter or manager of the sibling branch is.

  The photo upload `POST /api/branches/{branchId}/photos` is addressed by branch and carries
  `BranchScoped` like the rest; it once carried only `ManagerOrAbove`, which let any manager put
  images into any venue's branch. `PhotoService` also checks the stored staff row - active, and of
  the branch's venue, platform admin exempt - so a deactivated account's still-valid token is
  refused and the lock holds if a route ever loses the policy. The photo id a menu item is given
  is caller-supplied, so `MenuService` refuses one uploaded for another branch as not found there,
  the same answer a foreign category gets. `PhotoScopeTests` proves all of it.

  The approval queue those two act on, `GET /api/branches/{branchId}/reservations?status=1`, is
  addressed by branch and carries `ManagerOrAbove` and `BranchScoped` - and then the manager-or-above
  check from the stored row (`IStaffBranchGuard`), not the floor's. So **listing is narrower than
  deciding**: the console's queue is read by a branch manager at their own branch, an owner or
  general manager at every branch of their venue and a platform admin anywhere, while the branch's
  waiters - who may approve and reject the same bookings - read the floor's own list,
  `GET /api/branches/{branchId}/reservations/awaiting` (`WaiterOrAbove` + `BranchScoped`). Sorted by
  local date and start time, capped at 200 rows with no paging.
  `BranchReservationListTests` proves a manager of a sibling branch, another venue's manager, a
  waiter and an anonymous caller are refused.

  The console acts on bookings through the **floor's own routes** - `seat-reservation` and `free`
  on the table, `no-show`, `release`, `approve` and `reject` on the booking - with no route of its
  own and no policy change: `WaiterOrAbove` admits an admin-panel (`VenueUser`) token of an owner,
  general manager or branch manager as it admits a PIN session, and the branch is drawn as before
  (`BranchScoped` on the table routes, the stored row on the booking ones). So an owner or general
  manager acts at any branch of their venue, a branch manager at their own, and a waiter keeps
  exactly their tablet's branch. `ConsoleBookingActionsTests` sends every action as every kind of
  caller and checks the refused ones leave the booking as it was; the Overview that drives that
  menu, `GET /api/branches/{branchId}/overview`, is `ManagerOrAbove` + `BranchScoped` and a row of
  `AuthorizationMatrixTests`.
- **A tab participant token must not touch any other tab.** Two adjacent tables must not be able to
  order on each other's bill.

`BranchScoped` widens in exactly one direction: an owner or general manager signed in to the admin
panel is venue-scoped rather than branch-scoped - managing every branch is the point of those
accounts - so when their token carries no matching branch claim, the handler asks whether the branch
belongs to their venue. **A branch manager is not widened**: they pass on the branch their token
names and nowhere else, and a branch manager's token with no branch claim passes nowhere. A staff
session with a branch claim is still confined to it, and a venue user is still confined to their own
venue. `BranchScopeEnforcementTests` walks floor plan, menu, staff and reports for both kinds of
manager on both branches.

`VenueScoped` guards the venue read `GET /api/venues/{venueId}/manage` and the staff routes under
`/api/venues/{venueId}/staff`, which are addressed by venue because staff belong to one rather
than to a branch; the handler passes a platform admin for every venue. `VenueAdminEndpointTests`
proves a manager of the neighbouring venue is refused.

The venue read is what the console opens a venue with, and **which branches it lists is decided
from the caller's stored staff row, never from the token** - a token names at most one branch and
an owner's names none, which is the shape the console once mistook for "no branch". An owner
covers every branch, with or without a home branch; a general manager covers every branch; a branch
manager is listed their own branch only; a platform admin is listed everything. The query checks the
row itself, so it refuses a neighbour and a deactivated account even if the policy were lost from
the route (`ManagedVenueQueryTests`). What it lists and what the server allows are the same set in
both directions: `BranchScoped` and `IStaffBranchGuard` confine a branch manager to the branch listed.

`TabParticipantCanOrder` is defined and carries no endpoint yet - it has nothing to guard until
ordering exists - and is covered by `PolicyHandlerTests` rather than shipped unexercised.

## The actor stub

`ICurrentActor` is now implemented from the JWT claims (`ClaimsCurrentActor`), so every
`TableStateChange` carries a real `staffMemberId`. The pre-authentication stub is still registered
in Development when `DevActor:Enabled` is true, where its later registration replaces the real one -
it is what the Prompt 2 integration tests use, and it is still the quickest way to exercise a
manager-only path locally without minting a token. It is not registered in any other environment.

The demo data the stub takes its staff member from is a separate switch, `DevSeed:Enabled` - on by
default in Development, never registered anywhere else. Seeding used to ride on
`DevActor:Enabled`, so turning the stub off for real sign-in also left the database without the demo
venue, tables, listing and reviews. Now the two are independent: the stub off still seeds, and the
stub on with seeding off reports no seeded staff member unless `DevActor:StaffMemberId` pins one.

## Out of scope

No SMS or email provider, no shared staff PINs, no biometric unlock, no social login, no admin
panel UI, and nothing touching menu, orders or payments. (The Telegram bot above is the one
provider-shaped thing that exists, and it is a second door rather than an SMS provider.)
