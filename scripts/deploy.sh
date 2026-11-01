#!/usr/bin/env bash
# Deploys the committed code to the homelab and checks it came up.
#
#   ./scripts/deploy.sh             deploy HEAD
#   ./scripts/deploy.sh --dry-run   show what would be copied, change nothing
#
# What goes out is `git archive HEAD`, so the server always runs exactly a commit and
# uncommitted edits can never leak into a deploy. Files deleted from the repo are not removed
# from the server. Data lives in the seenit-db volume and login keys in seenit-keys, so a
# redeploy keeps everyone's history and sessions. Secrets live in ~/seenit/.env on the server
# (see .env.example); the deploy refuses to run without it.
set -euo pipefail

HOST="${DEPLOY_HOST:-homelab}"
DIR="${DEPLOY_DIR:-seenit}"
URL="${DEPLOY_URL:-http://192.168.1.167:8082}"
dry=0
[ "${1:-}" = "--dry-run" ] && dry=1

cd "$(dirname "$0")/.."

if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    echo "Uncommitted changes are NOT deployed (only HEAD is):"
    git status --short --untracked-files=no | sed 's/^/  /'
fi

commit=$(git rev-parse --short HEAD)
git fetch -q origin 2>/dev/null || true
if [ -n "$(git rev-list '@{u}..HEAD' 2>/dev/null)" ]; then
    echo "Warning: HEAD ($commit) has not been pushed."
fi

deployed=$(ssh "$HOST" "cat $DIR/.deployed-commit 2>/dev/null" || true)
echo "Server is on: ${deployed:-unknown}   Deploying: $commit"

if [ "$dry" = 1 ]; then
    echo "Would copy:"
    git archive HEAD | tar -t | grep -v '/$' | sed 's/^/  /'
    echo "(dry run, nothing changed)"
    exit 0
fi

ssh "$HOST" "test -f $DIR/.env" || { echo "Missing $HOST:$DIR/.env (copy .env.example and fill it in)."; exit 1; }

git archive HEAD | ssh "$HOST" "mkdir -p $DIR && tar -x -C $DIR && echo $commit > $DIR/.deployed-commit"
ssh "$HOST" "cd $DIR && docker compose up -d --build" 2>&1 | tail -6

echo "Waiting for the app..."
for _ in $(seq 1 20); do
    curl -fsS --max-time 3 -o /dev/null "$URL/" 2>/dev/null && break
    sleep 1
done

./scripts/verify-deploy.sh "$URL"
