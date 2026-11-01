#!/bin/sh
# Restores a SeenIt backup over the current database. Run on the homelab from ~/seenit:
#   ./scripts/restore.sh seenit-2026-11-02_0300.dump
# The dump must be in the backup folder (NAS: data/backups/seenit). This REPLACES all current data.
set -eu
file=${1:?usage: restore.sh <dump file name>}
printf 'This replaces ALL SeenIt data with %s. Type "restore" to continue: ' "$file"
read -r answer
[ "$answer" = "restore" ] || { echo "Cancelled."; exit 1; }
docker compose stop seenit
docker compose exec -T -e FILE="$file" seenit-backup sh -c 'pg_restore --clean --if-exists --no-owner --dbname="$PGDATABASE" "/backups/$FILE"'
docker compose start seenit
echo "Restored $file."
