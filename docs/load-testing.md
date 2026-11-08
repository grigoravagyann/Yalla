# Load testing

`tests/load/load.mjs` drives the API the way a cafe's evening does and prints, per endpoint, how
many requests went through, how many were refused, and how long the slowest 5 % and 1 % took. It is
plain Node (20+), no packages, so it runs anywhere the frontend's tooling does, including CI.

```bash
# the API under test, on a throwaway database - never the real one: the tab scenario opens tabs and orders
pnpm dev:stack --backend ../Yalla --db Yalla_Load --only api --api-port 5610      # from the frontend checkout

node tests/load/load.mjs --base http://localhost:5610 --scenario public --vus 30 --duration 30
node tests/load/load.mjs --base http://localhost:5610 --scenario tab --vus 24 --duration 30 --qr <token>,<token>,...
```

| Flag | Default | Meaning |
| --- | --- | --- |
| `--scenario` | `public` | `public`: a guest reading the public page (venues, branch, menu, availability, day slots, reviews, rooms, search). `tab`: a phone at a table - scan once, then read the tab, order every third round, read events and shares for the rest of the sitting. `all`: both, half the users each. |
| `--vus` | 20 | Virtual users, each a loop of requests with no think time. |
| `--duration` | 30 | Seconds. |
| `--qr` | | The tables' QR tokens for the tab scenario, comma-separated: `SELECT QrToken FROM DiningTables` on the database under test. Users are spread over them round-robin, so 24 users on 8 tokens is three phones a table. |
| `--p95`, `--errors` | 500, 1 | The gate: exit code 1 when any endpoint's p95 is over this many milliseconds or its error rate over this percent. |
| `--venue`, `--branch` | `yalla-demo`, `yerevan-centre` | The slugs of the branch to read; the development seed's by default. |

A non-2xx answer is an error and its status is in the table, so a 403 or a 429 shows up as what it
is instead of disappearing into an average. Rate limiting is off in Development; in Production the
public routes allow 30 requests a minute per address and 300 per branch, so a run against a Production
configuration from one address is a test of the limiter, not of the service.

## What it found (2026-11-05, laptop, SQL Server Express on the same machine)

**Public page, 30 users, 30 s: 5 357 requests a second, p95 10 ms, no errors.** The slowest single
request was 415 ms (availability). Nothing to do here; a branch's whole public surface is served from
a handful of indexed reads.

**One phone scanning a free table: 27 ms.** A tab's reads once open: tab p95 20 ms, events 27 ms,
shares 60 ms with 24 phones reading at once; an order 91 ms p95.

**Several phones scanning the same table at the same moment** - the only contention the system has
by design (`TableLock`): the first phone seats the table, the others lose the race and join its tab.
A loser pays for the lost seating (lock wait, failed save, re-read) and sometimes a lost tab race on
top, which is what the retry loop in `TabOpener` is for. Measured warm:

| Phones at once | Per table | p50 | p95 | max |
| --- | --- | --- | --- | --- |
| 8 | 1 | 27 ms | 38 ms | 38 ms |
| 2 | 2 | 20 ms | 485 ms | 485 ms |
| 24 | 3 | 41-86 ms | 0.8-1.4 s | 1.9 s |
| 8 | 8 | 753 ms | 1.2 s | 1.2 s |

A table's second and third phones wait up to two seconds, once, while the first one's tab is created.
Accepted: it is one wait per sitting, the phone shows a spinner, and serialising seatings per table
is what keeps a table from being booked twice. A phone that fails outright (lock timeout, 5 s) is
answered with a retryable problem and the app sends the scan again.

**A cold process and a crowd.** Twenty-four phones scanning within the first second after the
API started waited **5.6-7.8 s each** (12.5 s on the first run of the day): the first request on each
path compiles its EF queries and JITs its code, and the other twenty-three queue behind it. One phone
on a cold process takes 230 ms; the stall is the restart *and* the crowd together. A ReadyToRun
publish was tried and measured at the same 5.7 s p50, so it is not the JIT and the Dockerfile stays
as it was. In production this is a deploy during service with a full room, which the deploy notes
already say not to do; a warm-up that runs the scan path once at startup would close it, and is the
next thing to add if a deploy ever has to happen at 20:00.

**A policy's refusal had no body.** Two in three phones at a table in the tab scenario are joiners
the host has not approved, and their orders came back `403` with an empty body - the framework's
default when an authorization policy says no, while every refusal a service throws is a problem
document with a code. `ForbiddenProblemHandler` now writes the `forbidden` problem (with the failed
requirement's name in `context.requirement`), so the app's `ForbiddenError` has a code to show and a
trace id to quote. `AuthorizationBoundaryTests` asserts it.

## What it did not cover

- Staff and tablet flows (sign-in, floor, kitchen) and bookings: they need a staff account or a
  verified phone, which the development seed does not provide without a Telegram proof. Add a
  scenario once `AuthTestData`-style seeding is reachable from outside the test host.
- Push delivery, SignalR fan-out and the Telegram bot: out of process.
- A real network and a real database server: these numbers are a laptop talking to itself. They
  bound the service's own cost, not the deployment's.
