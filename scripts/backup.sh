#!/bin/sh
# Runs inside the seenit-backup container: dumps the database to /backups every night at
# BACKUP_HOUR (local time, TZ), keeping BACKUP_KEEP_DAYS days of dumps. Restore with scripts/restore.sh.
set -eu
: "${BACKUP_HOUR:=3}" "${BACKUP_KEEP_DAYS:=14}"

dump() {
    stamp=$(date +%Y-%m-%d_%H%M)
    tmp="/backups/.seenit-$stamp.dump.partial"
    if pg_dump --format=custom --compress=9 --file="$tmp"; then
        mv "$tmp" "/backups/seenit-$stamp.dump"
        echo "$(date -Iseconds) backup ok: seenit-$stamp.dump ($(du -h "/backups/seenit-$stamp.dump" | cut -f1))"
        find /backups -maxdepth 1 -name 'seenit-*.dump' -mtime "+$BACKUP_KEEP_DAYS" -print -delete
    else
        rm -f "$tmp"
        echo "$(date -Iseconds) backup FAILED" >&2
    fi
}

until pg_isready -q; do sleep 5; done

# Take one straight away if there's nothing from the last day (first start, or the box was off overnight).
if [ -z "$(find /backups -maxdepth 1 -name 'seenit-*.dump' -mtime -1 2>/dev/null)" ]; then dump; fi

while true; do
    now=$(date +%s)
    next=$(date -d "$(date +%Y-%m-%d) $(printf %02d "$BACKUP_HOUR"):00:00" +%s)
    [ "$next" -le "$now" ] && next=$((next + 86400))
    sleep $((next - now))
    dump
done
