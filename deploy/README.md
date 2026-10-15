# Deploying Yalla

One Linux host, one public hostname, five containers. Caddy terminates TLS and routes `/api/*`,
`/swagger*` and `/health` to the API and everything else to the console's static build, so web
and API share an origin and nothing needs CORS. SQL Server Express holds the data, photos sit on
a local volume, and a one-shot `migrate` container brings the schema up to date before the API
starts. Everything the stack needs is in this folder; everything it is configured with is in
`.env`.

## First bring-up

Before the first command: an **A record** for the domain pointing at the host, and **ports 80
and 443 open** inbound (Caddy needs both to obtain its certificate - 80 for the challenge and the
HTTP→HTTPS redirect, 443 for everything else). Docker Engine with Compose **2.24 or later**
(`docker compose version`; the `.env` interpolation this package relies on is newer than some
distribution packages). Nothing else on the host may listen on 80/443.

```sh
git clone https://github.com/grigoravagyann/Yalla.git /opt/yalla && cd /opt/yalla/deploy   # or copy deploy/ alone
cp .env.example .env && chmod 600 .env && $EDITOR .env                                     # every line marked CHANGE
./deploy.sh up
```

`up` validates `.env`, pulls the images named by `YALLA_VERSION`, starts SQL Server and waits for
it, runs the migration bundle to completion, starts the rest, and waits for the API to report
healthy. The first run also obtains the certificate and creates the platform admin from
`PlatformAdmin__Email` / `__Password`; sign in at `https://<DOMAIN>/` with those. Afterwards:

```sh
./deploy.sh status          # container state and health
./deploy.sh logs            # follow the API console (./deploy.sh logs caddy, sql, web)
curl -s https://<DOMAIN>/health
```

Sizing: 4 vCPU / 8 GB and 40 GB of disk is comfortable. SQL Server is capped at 2 GB of memory
in `compose.yaml` and the API uses well under 1 GB; the rest is headroom for backups and photos.
2 vCPU / 4 GB works for a pilot.

## What lives where

| Thing | Where | Notes |
| --- | --- | --- |
| Configuration and secrets | `deploy/.env` | Mode 600. The only file with values in it. |
| Database | volume `yalla_mssql_data` | `/var/opt/mssql` in the `sql` container. |
| Backups | volume `yalla_backups` | `/var/opt/mssql/backups/Yalla-<utc>.bak`; `deploy.sh backup` writes them. |
| Photos | volume `yalla_photos` | `/app/.photos` in the API: every upload, content-addressed (`README.md`, "Photos"). |
| API log files | volume `yalla_api_logs` | `/app/Logs/yalla-<day>.log`. The same lines go to the console, which `deploy.sh logs` follows. |
| Certificates | volume `yalla_caddy_data` | Re-issued if lost; keep it to stay inside Let's Encrypt's rate limits. |

`docker volume inspect yalla_photos` shows the host path if you need the files directly.

## Updating

```sh
./deploy.sh backup && ./deploy.sh up
```

Set `YALLA_VERSION` in `.env` first if you pin versions (recommended: a git SHA or a `v*` tag as
pushed by the `publish` workflows of the two repositories; `latest` follows `master`). `up`
re-runs the migration - a no-op when nothing is pending - and restarts only the containers whose
image or configuration changed. Expect the API to be away for the ten to thirty seconds between
the old container stopping and the new one answering `/health`.

Nightly backups: the crontab line is at the top of `deploy.sh`. `KEEP_BACKUPS` (default 14)
prunes old files on the volume. **The volume is on the same disk as the database.** Copy the
newest file off the host from the cron job or by hand (`docker compose cp sql:/var/opt/mssql/backups/<file> .`);
this package does not do that for you.

## Rollback

`./deploy.sh rollback` prints the procedure. In short: put the previous tag in `YALLA_VERSION`
and `./deploy.sh up` to roll the code back; restore the pre-deploy backup only if the new version
wrote data the old one cannot read. Migrations are only ever applied, never reverted, by this
package; they are written to add rather than change, so an older API against a newer schema runs.

## How the migration step works, and why it runs first

Outside Development the API never applies migrations on start (`src/Yalla.Api/Program.cs`, "The
way in to a fresh database"): a crash-looping API over a half-applied schema was judged worse
than a deploy step. The deploy step is the `migrate` service. The image carries `/app/efbundle`,
a self-contained executable built by `dotnet ef migrations bundle` in the Dockerfile from the
same commit as the API next to it, so the schema it applies is exactly the one that API expects.
It reads `YALLA_MIGRATIONS_CONNECTION` (`YallaDbContextFactory.cs`), which `compose.yaml` sets
to the API's connection string, applies whatever is pending, and exits 0. The `api` service
declares `depends_on: migrate: service_completed_successfully`, so Compose will not start the
API until that exit code is 0 - and `deploy.sh up` runs the bundle in the foreground first so a
failure is on your terminal with the previous API still running.

### When the migration fails

- **Cannot connect**: SQL Server was not ready or `ConnectionStrings__Yalla` is wrong. `./deploy.sh
  logs sql`, fix, `./deploy.sh up` again. Nothing was changed.
- **A SQL error naming a migration**: each migration runs in its own transaction and the failed one
  is rolled back; earlier ones in the same run are already applied and stay applied, which is
  fine - the bundle resumes from the failed one next time. Read the error, fix the data it
  complains about (or restore the backup you just took), run `./deploy.sh up` again.
- **To run it by hand**: `docker compose run --rm migrate` (already `--verbose`; `docker compose
  run --rm migrate --help` lists the bundle's options, including `--connection` to point it at
  another database).

## Telegram

The bot runs in **webhook mode** - the only mode allowed outside Development. Every API start
calls `setWebhook` with `Telegram__WebhookPublicUrl` (`https://<DOMAIN>/api/telegram/webhook`)
and `Telegram__WebhookSecret`, so Telegram pushes updates to this host and the API checks the
secret header on each one. A failed `setWebhook` is logged as an error and the API still starts;
the bot is simply deaf until the next successful start. Verify with
`curl https://api.telegram.org/bot<token>/getWebhookInfo` from anywhere. Set
`Telegram__Enabled=false` to run without the bot entirely.

## Proxy trust and rate limits

Caddy adds `X-Forwarded-For` / `X-Forwarded-Proto`; the API believes them only from
`ForwardedHeaders__KnownNetworks__0`, which `compose.yaml` sets to the compose network's subnet
(`YALLA_SUBNET`, pinned with an explicit `ipam` block so the value is true). Container addresses
are not stable, the subnet is. That trust is what makes the per-address rate limits per
address rather than one budget for the whole internet, and what makes the Production host's HTTPS
redirection see Caddy's requests as HTTPS. See the repository `README.md`, "Behind a reverse
proxy".

## Deliberately not covered

- **Monitoring and alerting.** `/health` (with a database round-trip) is there to be polled by
  whatever you use; nothing polls it except Docker's own health check.
- **Off-host backup copies.** `deploy.sh backup` writes to a volume on the same disk. Copy the
  files somewhere else; the cron job is the obvious place.
- **More than one API instance.** Photos are on a local volume and the rate limiter is in-process.
  Moving to object storage for photos is the first step (`docs/notifications.md`, section 10).
- **An external database.** The compose file assumes the `sql` container. A managed SQL Server
  only needs `ConnectionStrings__Yalla` changed and the `sql` service and its `depends_on`
  removed; the migration step is unchanged.
- **Log shipping, secrets management, firewalling, OS hardening.** The host's job.
