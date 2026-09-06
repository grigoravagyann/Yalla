# Yalla backend

[![backend](https://github.com/grigoravagyann/Yalla/actions/workflows/backend.yml/badge.svg)](https://github.com/grigoravagyann/Yalla/actions/workflows/backend.yml)

Table reservation and in-app ordering for restaurants and cafes, launching in Yerevan. ASP.NET
Core 9, EF Core, SQL Server, minimal APIs. Three clients consume this API: the diner app, the staff
tablet, and the owner's admin panel.

- `SCHEMA.md` — the database schema and the decisions behind it.
- `docs/` — one document per subsystem: `auth.md`, `reservations.md`, `tabs.md`,
  `platform-admin.md`, `openapi.md`, `billing.md`, `notifications.md`,
  `menu-completeness.md`, `tab-totals.md`, `error-contract.md`, `contract-tests.md`.

## Running locally

Requirements: .NET 9 SDK, SQL Server reachable as `localhost` (the connection string is in
`src/Yalla.Api/appsettings.Development.json`).

Two secrets are required in Development and the API **refuses to start without them**:

```
dotnet user-secrets set "Jwt:SigningKey"          "<48 random bytes, base64>"  --project src/Yalla.Api
dotnet user-secrets set "PlatformAdmin:Email"     "you@yalla.app"             --project src/Yalla.Api
dotnet user-secrets set "PlatformAdmin:Password"  "<long random value>"       --project src/Yalla.Api
```

Then:

```
dotnet run --project src/Yalla.Api
```

On first start the database is migrated, the demo branch is seeded, and the platform admin above
is created. Swagger UI is at `/swagger`.

```
dotnet test
```

The integration tests run against a throwaway SQL Server database and **skip** when no server is
reachable; set `YALLA_TEST_SQL_SERVER` to point them at one.

## Reaching the API from a phone on the same wifi

In Development the API listens on **all interfaces**, on two ports:

| | URL | For |
| --- | --- | --- |
| HTTP | `http://<LAN-IP>:5086` | **Local device testing only.** Phones and the Expo app - they will not trust the ASP.NET dev certificate, and installing it on Android is not worth the trouble. |
| HTTPS | `https://<LAN-IP>:7289` | The browser on this machine, and anything that trusts the dev certificate. |

`<LAN-IP>` is **logged at startup** - look for "Reachable from other devices on the local network"
in the console. It lists every adapter, real wifi first, with virtual adapters (VirtualBox,
Hyper-V, WSL, Docker) flagged so you do not paste one of those into a phone.

Both addresses come from `applicationUrl` in `Properties/launchSettings.json`, which is **never
published** — so no deployed environment can inherit a `0.0.0.0` binding or a plain-HTTP endpoint
from a config file. Deployments take their addresses from `ASPNETCORE_URLS` or
`ASPNETCORE_HTTP_PORTS`, and HTTPS redirection is on everywhere except Development.

| Profile | Binds | Notes |
| --- | --- | --- |
| `Yalla.Api` (default) | `http://0.0.0.0:5086` and `https://0.0.0.0:7289` | The usual one. Needs the ASP.NET dev certificate for the HTTPS port. |
| `http` | `http://0.0.0.0:5086` | HTTP only, so it runs on a machine with no dev certificate. |
| `Docker` | the container's own `8080`/`8081` | Untouched by the above. |

A run with **no profile at all** — the built executable started directly — falls back to
`http://0.0.0.0:5086`, so it is still reachable from a phone. Anything explicit
(`ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, a `Kestrel:Endpoints` section) always wins.

### What to put in the frontend configs

With the LAN address from the log (say `192.168.1.42`):

| Client | Value |
| --- | --- |
| Web app (Vite) API base URL | `http://192.168.1.42:5086` — or `http://localhost:5086` when the browser is on this machine |
| Expo / mobile app API base URL | `http://192.168.1.42:5086` |
| `pnpm api:generate` | `http://192.168.1.42:5086/swagger/v1/swagger.json` — or `http://localhost:5086/swagger/v1/swagger.json` |
| Swagger UI from the phone | `http://192.168.1.42:5086/swagger` |

Swagger is on in Development with no IP restriction (`Swagger:AllowedIps` is empty), so the
frontend can generate its types from the LAN address as well as from localhost.

### CORS

In Development, browsers at `http://localhost:<any port>` and at any **private-network** address
(`10.x`, `172.16-31.x`, `192.168.x`) are allowed, with credentials - that covers the Vite dev server
on this machine and on the LAN, the Expo dev server (8081, 19006), and a teammate's laptop. A page
served from the public internet is still refused.

Everywhere else CORS is an **explicit allowlist** from `Cors:AllowedOrigins`
(`Cors__AllowedOrigins__0`, `__1`, …). Never a wildcard, never `AllowAnyOrigin` with credentials,
and an empty list means no cross-origin browser access at all.

### If the phone cannot connect

It is almost always **Windows Firewall** blocking inbound connections to the port. Allow it once,
from an elevated prompt:

```
netsh advfirewall firewall add rule name="Yalla API dev HTTP" dir=in action=allow protocol=TCP localport=5086
```

Also check that the phone and the machine are on the same network (a guest wifi or a personal
hotspot often isolates clients from each other), and that you used the HTTP URL - the phone will
reject HTTPS with the dev certificate.

## Continuous integration

`.github/workflows/backend.yml` runs on every push and pull request. It restores, builds with
`-warnaserror`, checks for pending model changes, and runs the whole suite against **a real SQL
Server service container**.

The container is not optional. The tests that are most likely to be quietly broken by a later change
are the concurrency ones — two racing bookings, two racing scans, ten racing orders, two racing cash
payments, two scheduler instances leasing one message — and every one of them proves something only
a real server does: `rowversion` tokens, filtered unique indexes, application locks. The in-memory
provider would pass each assertion while enforcing none of it.

Three details in that workflow are load-bearing:

- **The wait for SQL Server is a health-check loop on the connection, not a sleep.** The container
  reports itself started long before it accepts logins.
- **A skipped test fails the build.** `SqlServerFixture` skips rather than fails when it finds no
  server, which is right on a laptop and a silent disaster in CI — it would report green while
  proving nothing. The workflow greps the `.trx` for `NotExecuted` and fails if there are any.
- **`dotnet ef migrations has-pending-model-changes` fails the build.** An entity edited without a
  migration is the most common way this breaks, and it is invisible locally because your database
  already has the column.

To run the model check yourself:

```
dotnet tool restore
dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/Yalla.Infrastructure
```

It touches no database — `YallaDbContextFactory` builds the context offline, which is why the check
needs neither a connection string nor a running API.

### Branch protection

Set once, in the repository settings, and not something the workflow file can do for itself:
**Settings → Branches → Add rule** on the default branch — which is `master` here, not `main` —
with *Require status checks to pass before merging* and the `build-and-test` check selected.
Without it the workflow is advisory and a red run can still be merged.

The workflow triggers on pushes to both `main` and `master` for the same reason: a workflow naming
only `main` would fire on pull requests alone, leaving the branch everything actually lands on as
the one branch with no check.
