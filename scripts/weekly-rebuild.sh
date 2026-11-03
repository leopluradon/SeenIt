#!/usr/bin/env bash
# Rebuilds the deployed code onto the latest patched .NET images and pulls the latest Postgres 17
# patch release, so security fixes land without waiting for a code change. Meant to run weekly on
# the homelab from cron (see README → Updates). NuGet package versions don't change here (that
# needs a normal deploy); the admin page lists which ones are out of date.
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f .deployed-version ] || { echo "$(date -Iseconds) no .deployed-version yet; deploy once first"; exit 0; }
# shellcheck disable=SC1091
. ./.deployed-version
export SEENIT_VERSION SEENIT_COMMIT
export SEENIT_BUILT_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
export SEENIT_SUBJECT_B64="$(printf 'Weekly rebuild with security updates' | base64 -w0)"

echo "$(date -Iseconds) weekly rebuild of $SEENIT_VERSION ($SEENIT_COMMIT)"
docker compose pull --quiet seenit-db seenit-backup
docker compose build --pull --quiet seenit
docker compose up -d
docker image prune -f >/dev/null
echo "$(date -Iseconds) done"
