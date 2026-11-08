#!/usr/bin/env bash
#
# Runs Yalla on this host. Everything is `docker compose` underneath; this script only puts the
# steps in the right order and refuses to skip the ones that hurt when skipped.
#
#   ./deploy.sh check      .env present and complete, Docker reachable, compose file valid
#   ./deploy.sh backup     the database and the photos to the backups volume (and off the host)
#   ./deploy.sh up         pull YALLA_VERSION, run the migration, start everything
#   ./deploy.sh status     container state and health
#   ./deploy.sh logs [svc] follow the console (api by default)
#   ./deploy.sh rollback   what to do when a version has to go back - prints the steps
#   ./deploy.sh watch      /health, the containers, the disk, the backups' age; alerts by Telegram
#
# Deploying a new version is:   ./deploy.sh backup && ./deploy.sh up
#
# Root crontab lines (crontab -e): the nightly backup at 03:15 UTC and the watch every five
# minutes, both with their output kept:
#   15 3 * * *  cd /opt/yalla/deploy && ./deploy.sh backup >> /var/log/yalla-backup.log 2>&1
#   */5 * * * * cd /opt/yalla/deploy && ./deploy.sh watch  >> /var/log/yalla-watch.log  2>&1
#
# Run it from the deploy/ folder, or from anywhere - it cds to its own location first.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

KEEP_BACKUPS="${KEEP_BACKUPS:-14}"
BACKUP_DIR=/var/opt/mssql/backups
# The watch's memory between runs: which problems it has already reported, and when.
WATCH_STATE="${WATCH_STATE:-/var/lib/yalla-watch.state}"
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

# One value from .env, with a trailing `# comment` stripped. Empty when the key is absent.
env_value() {
  grep -E "^$1=" .env 2>/dev/null | head -1 | cut -d= -f2- | sed -E 's/[[:space:]]+#.*$//'
}

sa_password() { env_value SQL_SA_PASSWORD; }

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

  # The photos too: content-addressed files on their own volume, which no database backup
  # carries. A tarball beside the .bak, pruned the same way. `alpine` is pulled once.
  local photos="$BACKUP_DIR/photos-$stamp.tgz"
  say "Backing up the photos to $photos"
  docker run --rm -v yalla_photos:/photos:ro -v yalla_backups:/backups alpine \
    sh -c "tar czf /backups/photos-$stamp.tgz -C /photos . && cd /backups && ls -1t photos-*.tgz | tail -n +$((KEEP_BACKUPS + 1)) | xargs -r rm -v --"

  say "Backups on the volume"
  compose exec -T sql bash -c "ls -lh $BACKUP_DIR"
  echo
  echo "Written: $file and $photos  (inside the sql container; volume yalla_backups on the host)"

  # Off the host, when .env says where: BACKUP_COPY_TO is an scp destination (user@host:/path)
  # reachable with this user's ssh key. The disk the backups volume sits on is the disk the
  # database sits on, so without this a dead disk takes both.
  local copy_to
  copy_to="$(env_value BACKUP_COPY_TO)"
  if [ -n "$copy_to" ]; then
    say "Copying both to $copy_to"
    local staging
    staging="$(mktemp -d)"
    compose cp "sql:$file" "$staging/" >/dev/null
    compose cp "sql:$photos" "$staging/" >/dev/null
    if scp -q "$staging"/* "$copy_to/"; then
      echo "copied"
      rm -rf "$staging"
    else
      rm -rf "$staging"
      die "The copy to $copy_to failed; the backup is still on the volume."
    fi
  else
    echo "Not copied off the host (set BACKUP_COPY_TO in .env). By hand:  docker compose cp sql:$file ./"
  fi
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

# ------------------------------------------------------------------------------- watch

# Posts one message to the operator's Telegram chat through the verification bot, which is
# already configured. Prints it as well, so the cron log is the history. No chat id: print only.
alert() {
  local text="$1" token chat
  echo "$(date -u +%FT%TZ) $text"
  token="$(env_value Telegram__BotToken)"
  chat="$(env_value ALERT_TELEGRAM_CHAT_ID)"
  [ -n "$token" ] && [ -n "$chat" ] || return 0
  curl -fsS --max-time 10 -o /dev/null "https://api.telegram.org/bot$token/sendMessage" \
    --data-urlencode "chat_id=$chat" --data-urlencode "text=$text" \
    || echo "  (could not reach Telegram to send that)"
}

# Every check appends one line to $problems; an empty list means all is well. Each check is cheap
# and local except /health, which goes out through Caddy like a guest would - the certificate,
# the proxy and the database are all on the path.
cmd_watch() {
  require_env
  local domain problems=""
  domain="$(env_value DOMAIN)"

  local health
  health="$(curl -fsS --max-time 15 "https://$domain/health" 2>&1)" \
    && echo "$health" | grep -q '"status":"Healthy"' \
    || problems+="https://$domain/health is not Healthy: ${health:0:160}"$'\n'

  local svc state
  for svc in caddy web api sql; do
    state="$(docker inspect -f '{{.State.Status}}' "$(compose ps -q "$svc" 2>/dev/null)" 2>/dev/null || echo missing)"
    [ "$state" = running ] || problems+="container $svc is $state"$'\n'
  done

  local limit used
  limit="$(env_value DISK_ALERT_PERCENT)"; limit="${limit:-85}"
  used="$(df -P / | awk 'NR==2 {gsub("%","",$5); print $5}')"
  [ "$used" -lt "$limit" ] || problems+="disk is ${used}% full (limit ${limit}%)"$'\n'
  used="$(df -P "$(docker info -f '{{.DockerRootDir}}' 2>/dev/null || echo /var/lib/docker)" | awk 'NR==2 {gsub("%","",$5); print $5}')"
  [ "$used" -lt "$limit" ] || problems+="docker's disk is ${used}% full (limit ${limit}%)"$'\n'

  # Newest .bak younger than 36 hours: a nightly that failed twice is a problem, one that failed
  # once is this morning's log.
  local newest age
  newest="$(compose exec -T sql bash -c "ls -1t $BACKUP_DIR/Yalla-*.bak 2>/dev/null | head -1 | xargs -r stat -c %Y" 2>/dev/null || true)"
  if [ -z "$newest" ]; then
    problems+="no database backup on the volume"$'\n'
  else
    age=$(( ($(date +%s) - newest) / 3600 ))
    [ "$age" -lt 36 ] || problems+="newest database backup is ${age} h old"$'\n'
  fi

  # Report on change, repeat an open problem every six hours, and say when it is over. The
  # state file holds when the last report was sent and what it said.
  local previous="" sent_at=0 now
  now="$(date +%s)"
  if [ -s "$WATCH_STATE" ]; then
    sent_at="$(head -1 "$WATCH_STATE")"
    previous="$(tail -n +2 "$WATCH_STATE")"
  fi
  local current="${problems%$'\n'}"
  if [ -z "$current" ]; then
    if [ -n "$previous" ]; then
      alert "Yalla on $domain: all clear again."
      : > "$WATCH_STATE"
    fi
    echo "$(date -u +%FT%TZ) ok"
    return 0
  fi
  if [ "$current" != "$previous" ] || [ $((now - sent_at)) -ge $((6 * 3600)) ]; then
    alert "Yalla on $domain needs a look:"$'\n'"$current"
    { echo "$now"; echo "$current"; } > "$WATCH_STATE"
  else
    echo "$(date -u +%FT%TZ) still open: $(echo "$current" | tr '\n' ';')"
  fi
  return 1
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
  sed -n '3,22p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
}

case "${1:-}" in
  check)    cmd_check ;;
  backup)   cmd_backup ;;
  up)       cmd_up ;;
  status)   cmd_status ;;
  logs)     shift; cmd_logs "$@" ;;
  rollback) cmd_rollback ;;
  watch)    cmd_watch ;;
  *)        usage ;;
esac
