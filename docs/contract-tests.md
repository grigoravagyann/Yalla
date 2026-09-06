# Contract tests — why a green suite proved nothing

A client script pointed at the API with `DevActor__Enabled=false` found in an afternoon that a diner
could not place an order. The feature the product is built around had never worked outside the test
suite, and 522 tests were green.

This document is about the second fact, not the first.

---

## 1. The bug

A participant token carries `PrincipalType = TabParticipant` and a `participantId` claim, and **no
`dinerUserId`** — correctly. Prompt 3's whole argument is that somebody at a table needs no account:
asked to register, they put the phone down and wave at a waiter, and the product has failed at the
one moment it exists for.

`TabParticipantHandler` admitted the request and the tab view reported `canOrderNow: true`.
`TabOrderService` then did this:

```csharp
var participantId = actor.DinerUserId
                    ?? throw new TabPermissionException("Ordering", "somebody on this tab");
```

`ClaimsCurrentActor.DinerUserId` returns null unless the principal is a `Diner`. So with real auth,
every diner was refused — after being told they could order.

The fix is one line, plus the member it needed: `ICurrentActor` had **nowhere to put a participant
id**, so there was no correct thing for that line to read.

## 2. Why no test caught it

```csharp
public static TestActor Participant(Guid participantId) =>
    new(ActorType.Diner, null, null, participantId);   // ← into DinerUserId
```

The double put the participant id where production never puts it. Every ordering test asserted
against a shape the real actor cannot produce.

**That is not a bug in one double.** It is a hole in how the suite was built: nothing anywhere forced
a double and its production counterpart to agree, so any of them could drift and none of them would
fail. The double even carried a doc comment stating the fiction as a rule, three feet from the
production comment stating the opposite.

## 3. Contract tests

One abstract suite per interface, in `tests/Yalla.UnitTests/Contracts/`. Every implementation and
every double is a subclass. A double that cannot satisfy the real contract fails the build.

| Interface | Production | Doubles |
| --- | --- | --- |
| `ICurrentActor` | `ClaimsCurrentActor` (real tokens, minted and validated), `DevelopmentActorOrToken` | `TestActor`, `DevCurrentActor` |
| `IClock` | `SystemClock` | `TestClock`, `FakeClock` |
| `INotificationChannel` | `LoggingNotificationChannel` | `RecordingChannel`, `CountingChannel` |
| `IPhotoStorage` | `LocalDiskPhotoStorage` | *(none yet — the suite is what the first one will be held to)* |

Three rules that made these worth writing:

- **The contract asks for an identity, not for field values.** Each subclass is handed "give me an
  actor for a tab participant" and answers however it does that. If the case carried the expected
  values, a double could satisfy it by echoing them back.
- **The production subclass goes through the whole path.** `TokenIssuer` writes the claims, the token
  is signed, and it is validated back into a principal with the API's own
  `TokenValidationParameters`. A hand-built `ClaimsPrincipal` would let a claim name drift on one
  side unnoticed — which is the exact class of failure being guarded against.
- **Absent fields are asserted, not just present ones.** Every case checks that the ids belonging to
  *other* identity types are null. The failure being caught is a field that is set when it should
  not be.

### What failed on the first run

| Implementation | Result |
| --- | --- |
| `TestActor` | **Failed.** `Participant` populated `DinerUserId`. |
| `DevCurrentActor` | **Failed.** Could not represent a tab participant at all. |
| `DevelopmentActorOrToken` | **Failed.** Delegating composite with no member to forward. |
| `ClaimsCurrentActor` | **Failed.** `ICurrentActor` had no `ParticipantId`, so the production actor could not report one either. |
| `TestClock` | **Failed.** Stored whatever `DateTimeKind` it was handed. |

Four of four `ICurrentActor` implementations, and one of two clock doubles. The `ICurrentActor`
contract could not even be *written* against the old interface, which is the most useful thing the
exercise produced: the hole was in the abstraction, not only in the double.

`TestClock` is the smaller one and the same shape. It returned an `Unspecified` instant when
constructed from a bare `DateTime` literal, and `Guard.NotLocalTime` — the one check between a local
instant and a UTC column — passes on `Unspecified`. The real clock always returns `Utc`.

## 4. Running the real flows with real auth

Contract tests stop a double lying. They do not prove the wiring works, so
`RealAuthFlowTests` runs one flow per surface with `DevActor:Enabled=false` and no doubles anywhere
in the path:

- a participant scans, gets a token, and **orders**;
- a waiter signs in with a PIN on an enrolled tablet and takes cash;
- a diner verifies a phone number, books a table and cancels it;
- an owner signs in to the admin panel and edits a menu.

Plus a sweep: every endpoint a participant token can legitimately reach, called with one.

The general rule the file is an argument for: **a test that substitutes a stand-in for the thing that
is broken cannot notice that it is broken.** At least one path per surface has to run on the real
thing.

## 5. The `DinerUserId` sweep

Three call sites read `DinerUserId` on a path a tab participant can reach:

| Call site | Was | Now |
| --- | --- | --- |
| `TabOrderService.AuthoriseAndCreateAsync` | Refused every participant | Reads `ParticipantId` |
| `TabLedger.ResolveActor` | Wrote a **null actor** onto every diner-driven tab event | Participant first, then the account |
| `TableStateService.ResolveActor` | Same, on the table audit log | Same |

The two `ResolveActor` cases were not refusals, which is why nobody noticed: the event stream and the
audit log simply recorded "somebody" for nearly every diner action, and a log that cannot say who is
not an audit log.

Left alone deliberately, because they genuinely require an account:

- **`DinerDeviceService`** — registering a push token. Somebody with no account has no push channel.
- **`ReservationService`** — booking. A no-show has to be counted against somebody, which is the
  whole reason booking needs an account and ordering does not.
- **`TabService`** — records the account on the participant row *if the scanner has one*. Null is the
  ordinary case and is correct.
