#!/usr/bin/env bash
#
# Runs exactly what .github/workflows/backend.yml runs, so a green run here means a green check
# there.
#
# It exists because "0 warnings, 0 errors" was reported on three pull requests and was true of a
# different command each time. Directory.Build.props sets TreatWarningsAsErrors=false so a
# developer can build mid-thought; CI overrides it to true on the command line. A doc comment with
# an unresolvable cref is a warning in one and a failed merge in the other, and nothing local said
# so. A note telling people to remember the flags is not a fix - this is.
#
# Usage:
#   ./verify.sh              build, check migrations, test, fail on any skip
#   ./verify.sh --no-test    the fast half: build and migrations only
#
# On Windows, run it from Git Bash. It needs a reachable SQL Server for the integration tests;
# without one every one of them skips, and this fails for that reason rather than reporting green -
# which is the same rule CI applies and the reason that rule exists.
set -euo pipefail

cd "$(dirname "$0")"

RESULTS_DIR="artifacts/test-results"
RUN_TESTS=1

for arg in "$@"; do
  case "$arg" in
    --no-test) RUN_TESTS=0 ;;
    -h|--help) sed -n '3,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "verify.sh: unknown argument '$arg'" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }

# --locked-mode, like CI: a PackageReference changed without committing the regenerated
# packages.lock.json fails here rather than being silently regenerated.
step "Restore (--locked-mode)"
dotnet restore Yalla.sln --locked-mode

# The strict gate. This one command is the difference between a local build and the merge gate.
step "Build (Release, warnings as errors)"
dotnet build Yalla.sln -c Release --no-restore -p:TreatWarningsAsErrors=true

# The failure that is invisible locally: an entity edited without a migration. The developer's own
# database already has the column, so nothing complains until a fresh one is created - which is a
# deploy. No --startup-project, because YallaDbContextFactory exists so the tools never boot the API.
step "Check for un-migrated model changes"
dotnet tool restore
dotnet tool run dotnet-ef migrations has-pending-model-changes \
  --project src/Yalla.Infrastructure \
  --no-build --configuration Release

if [ "$RUN_TESTS" -eq 0 ]; then
  printf '\n\033[1mBuild and migrations verified. Tests skipped (--no-test).\033[0m\n'
  exit 0
fi

step "Test (Release, whole solution)"
rm -rf "$RESULTS_DIR"
dotnet test Yalla.sln -c Release --no-build \
  --logger "trx;LogFileName=results.trx" \
  --results-directory "$RESULTS_DIR"

# The step the workflow exists for, and the one most worth having locally.
#
# Every integration test is a SkippableFact gated on reaching SQL Server. A run with no database
# reports success while proving nothing - correct on a laptop with no server, and a lie when you are
# about to push. CI fails on any skip; so does this, for the same reason and by reading the same
# counts out of the same trx.
step "Fail if any test skipped"
shopt -s nullglob
files=("$RESULTS_DIR"/*.trx)

if [ ${#files[@]} -eq 0 ]; then
  echo "No test results were produced." >&2
  exit 1
fi

total_skipped=0
for file in "${files[@]}"; do
  skipped=$(grep -o 'notExecuted="[0-9]*"' "$file" | head -1 | grep -o '[0-9]*' || echo 0)
  executed=$(grep -o 'executed="[0-9]*"' "$file" | head -1 | grep -o '[0-9]*' || echo 0)
  echo "$(basename "$file"): executed=$executed skipped=$skipped"
  total_skipped=$((total_skipped + skipped))
done

if [ "$total_skipped" -gt 0 ]; then
  cat >&2 <<'MESSAGE'

Some tests were skipped, so this run proved less than it appears to.

That means no SQL Server was reachable. The tests most likely to be quietly broken are the
concurrency ones - two racing bookings, two racing scans, two racing cash payments - and every one
of them is a statement about rowversion tokens, filtered unique indexes and application locks.
None of that is exercised by a run that skipped.

Start SQL Server, or point the fixture at one with YALLA_TEST_SQL_SERVER / _USER / _PASSWORD, and
run this again. CI fails on the same condition.
MESSAGE
  exit 1
fi

printf '\n\033[1;32mVerified. This is what CI runs.\033[0m\n'
