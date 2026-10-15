#!/usr/bin/env bash
#
# Runs Yalla on this host. Everything is `docker compose` underneath; this script only puts the
# steps in the right order and refuses to skip the ones that hurt when skipped.
#
#   ./deploy.sh check      .env present and complete, Docker reachable, compose file valid
#   ./deploy.sh backup     BACKUP DATABASE to the backups volume, keep the last $KEEP_BACKUPS
#   ./deploy.sh up         pull YALLA_VERSION, run the migration, start everything
#   ./deploy.sh status     container state and health
#   ./deploy.sh logs [svc] follow the console (api by default)
#   ./deploy.sh rollback   what to do when a version has to go back - prints the steps
#
# Deploying a new version is:   ./deploy.sh backup && ./deploy.sh up
#
# Nightly backup, as a root crontab line (crontab -e), 03:15 UTC, with the output kept:
#   15 3 * * * cd /opt/yalla/deploy && ./deploy.sh backup >> /var/log/yalla-backup.log 2>&1
#
# Run it from the deploy/ folder, or from anywhere - it cds to its own location first.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

KEEP_BACKUPS="${KEEP_BACKUPS:-14}"
BACKUP_DIR=/var/opt/mssql/backups
SQLCMD=/opt/mssql-tools18/bin/sqlcmd

say()  { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
die()  { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
compose() { docker compose "$@"; }

# ------------------------------------------------------------------------------- check

require_env() {
  [ -f .env ] || die ".env is missing. Copy .env.example to .env and fill in every CHANGE line."

  # The values the stack cannot run without. Each is checked for presence and for still being
  # the example's placeholder, because the placeholders pass `docker compose config` on purpose
  # - that is how the example is validated in CI - and would pass here too.
  local missing=0 key value
  for key in DOMAIN SQL_SA_PASSWORD ConnectionStrings__Yalla Jwt__SigningKey \
             PlatformAdmin__Email PlatformAdmin__Password \
             Auth__PasswordResetUrlTemplate PublicWeb__ManageBookingUrlTemplate; do
    value="$(grep -E "^${key}=" .env | head -1 | cut -d= -f2- | sed -E 's/[[:space:]]+#.*$//')"
    if [ -z "$value" ]; then
      echo "  $key is not set"; missing=1
    elif [[ "$value" == *change-me* || "$value" == *Change-me* ]]; then
      echo "  $key still holds the example placeholder"; missing=1
    fi
  done
  [ "$missing" -eq 0 ] || die ".env is incomplete (see above)."

  if [ "$(stat -c %a .env 2>/dev/null || echo 600)" != "600" ]; then
    echo "note: .env is readable by others; chmod 600 .env"
  fi
}

cmd_check() {
  say "Checking .env"
  require_env
  echo "ok"

  say "Checking Docker"
  docker info >/dev/null 2>&1 || die "Docker is not reachable. Is the daemon running, and are you in the docker group?"
  docker compose version >/dev/null 2>&1 || die "docker compose (v2) is not available."
  echo "ok: $(docker compose version --short)"

  say "Validating compose.yaml against .env"
  compose config --quiet
  echo "ok"
}

# ------------------------------------------------------------------------------- backup

sa_password() {
  grep -E '^SQL_SA_PASSWORD=' .env | head -1 | cut -d= -f2- | sed -E 's/[[:space:]]+#.*$//'
}

# sqlcmd inside the sql container, as sa. The password goes through the environment of the exec,
# never through this script's argument list, so `ps` on the host does not show it.
sqlcmd_in_container() {
  compose exec -T -e "SA_PW=$(sa_password)" sql \
    bash -c "$SQLCMD -C -S localhost -U sa -P \"\$SA_PW\" -b \"\$@\"" -- "$@"
}

cmd_backup() {
  require_env
  compose ps --status running sql --quiet 2>/dev/null | grep -q . \
    || die "The sql container is not running; nothing to back up. (./deploy.sh up first.)"

  # The backups volume is created root-owned the first time; SQL Server runs as mssql (10001)
  # and cannot write there until it is handed over. Idempotent, so it runs every time.
  compose exec -T --user root sql bash -c "mkdir -p $BACKUP_DIR && chown mssql:root $BACKUP_DIR"

  local stamp file
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  file="$BACKUP_DIR/Yalla-$stamp.bak"

  say "Backing up Yalla to $file"
  sqlcmd_in_container -Q "BACKUP DATABASE [Yalla] TO DISK = N'$file' WITH COMPRESSION, CHECKSUM, INIT, STATS = 25"

  say "Pruning to the newest $KEEP_BACKUPS"
  compose exec -T sql bash -c "cd $BACKUP_DIR && ls -1t Yalla-*.bak 2>/dev/null | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm -v --"

  say "Backups on the volume"
  compose exec -T sql bash -c "ls -lh $BACKUP_DIR"
  echo
  echo "Written: $file  (inside the sql container; volume yalla_backups on the host)"
  echo "Copy it off the host:  docker compose cp sql:$file ./"
}

# ------------------------------------------------------------------------------- up

cmd_up() {
  cmd_check

  local version
  version="$(grep -E '^YALLA_VERSION=' .env | head -1 | cut -d= -f2- | sed -E 's/[[:space:]]+#.*$//')"
  say "Pulling images for YALLA_VERSION=${version:-latest}"
  compose pull --quiet

  say "Starting SQL Server"
  compose up -d --wait sql

  # The migration runs to completion here, in the foreground, with its output on this terminal.
  # `run --rm` returns the bundle's exit code, so a failed migration stops the deploy right here
  # with the API still on the previous version - which is the whole reason it is a separate step.
  # (compose.yaml also lists migrate under api's depends_on, so a plain `docker compose up -d`
  # runs it too; the bundle applies only what is pending, so running it twice is a no-op.)
  say "Applying migrations"
  if ! compose run --rm migrate; then
    cat >&2 <<'MESSAGE'

The migration failed and nothing else was started. The API that was running is still running on
the previous image. Read the output above: a connection failure means SQL Server or the
connection string; a SQL error names the migration. See README.md, "When the migration fails".
MESSAGE
    exit 1
  fi

  say "Starting everything"
  compose up -d --remove-orphans

  say "Waiting for the API to report healthy"
  local attempt
  for attempt in $(seq 1 40); do
    case "$(docker inspect -f '{{.State.Health.Status}}' "$(compose ps -q api)" 2>/dev/null || echo starting)" in
      healthy)   echo "healthy after ${attempt} checks"; cmd_status; return 0 ;;
      unhealthy) break ;;
    esac
    sleep 5
  done

  compose logs --tail 60 api >&2
  die "The API did not become healthy. Its last log lines are above; ./deploy.sh logs api for more."
}

# ------------------------------------------------------------------------------- the rest

cmd_status() {
  say "Containers"
  compose ps
}

cmd_logs() {
  compose logs -f --tail 200 "${1:-api}"
}

cmd_rollback() {
  cat <<'MESSAGE'
Rolling back is two independent decisions:

1. The code. Set YALLA_VERSION in .env to the previous image tag (a git SHA or v* tag that was
   running before - `docker image ls ghcr.io/grigoravagyann/yalla-api` shows what is on the host)
   and run:
       ./deploy.sh up
   The migrate step runs again and finds nothing pending, because EF migrations are never
   un-applied automatically. An older API against a newer schema is fine when the migration only
   added things, which is how migrations here are written.

2. The data, only if the new version wrote something the old one cannot live with. Restore the
   backup taken before the deploy (./deploy.sh backup lists them):
       docker compose stop api
       docker compose exec sql bash -c "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"\$MSSQL_SA_PASSWORD\" -b -Q \"
         ALTER DATABASE [Yalla] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
         RESTORE DATABASE [Yalla] FROM DISK = N'/var/opt/mssql/backups/<file>.bak' WITH REPLACE;
         ALTER DATABASE [Yalla] SET MULTI_USER;\""
       ./deploy.sh up
   Everything written after that backup is gone, bookings included. Do this knowingly.
MESSAGE
}

usage() {
  sed -n '3,19p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
}

case "${1:-}" in
  check)    cmd_check ;;
  backup)   cmd_backup ;;
  up)       cmd_up ;;
  status)   cmd_status ;;
  logs)     shift; cmd_logs "$@" ;;
  rollback) cmd_rollback ;;
  *)        usage ;;
esac
