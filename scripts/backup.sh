#!/bin/sh
# Runs inside the seenit-backup container: dumps the database to /backups every night at
# BACKUP_HOUR (local time, TZ), keeping BACKUP_KEEP_DAYS days of dumps. Restore with scripts/restore.sh.
#
# Off-site: if OFFSITE_TARGET (user@host:dir) is set, once a week the newest dump is added to
# /backups/offsite (the last OFFSITE_KEEP weekly copies) and that folder is mirrored there with rsync
# over ssh, using the key and known_hosts in /offsite (homelab:~/seenit/offsite). A failed copy is
# retried after the next nightly dump.
set -eu
# Results go to /status (shared with the app) as small files whose time is when it happened:
# backup-ok / backup-failed / offsite-ok / offsite-failed. The admin page shows them and the
# app emails the admins when a backup fails or stops happening.
status() {
    [ -d /status ] || return 0
    printf '%s\n' "$2" > "/status/.$1.tmp" && mv "/status/.$1.tmp" "/status/$1" || true
}
: "${BACKUP_HOUR:=3}" "${BACKUP_KEEP_DAYS:=14}" "${OFFSITE_TARGET:=}" "${OFFSITE_KEEP:=8}"

dump() {
    stamp=$(date +%Y-%m-%d_%H%M)
    tmp="/backups/.seenit-$stamp.dump.partial"
    if pg_dump --format=custom --compress=9 --file="$tmp"; then
        mv "$tmp" "/backups/seenit-$stamp.dump"
        echo "$(date -Iseconds) backup ok: seenit-$stamp.dump ($(du -h "/backups/seenit-$stamp.dump" | cut -f1))"
        status backup-ok "seenit-$stamp.dump ($(du -h "/backups/seenit-$stamp.dump" | cut -f1))"
        find /backups -maxdepth 1 -name 'seenit-*.dump' -mtime "+$BACKUP_KEEP_DAYS" -print -delete
    else
        rm -f "$tmp"
        echo "$(date -Iseconds) backup FAILED" >&2
        status backup-failed "pg_dump failed (see: docker logs seenit-backup)"
    fi
}

offsite() {
    [ -n "$OFFSITE_TARGET" ] || return 0
    dir=/backups/offsite
    mkdir -p "$dir"
    # Done within the last week? (-mtime -7 = modified less than 7 days ago)
    [ -n "$(find "$dir" -maxdepth 1 -name .pushed -mtime -7 2>/dev/null)" ] && return 0

    latest=$(ls -1 /backups/seenit-*.dump 2>/dev/null | sort | tail -1)
    [ -n "$latest" ] || return 0
    [ -f "$dir/$(basename "$latest")" ] || cp "$latest" "$dir/"
    # Keep the newest OFFSITE_KEEP weekly copies; rsync --delete removes the rest remotely too.
    ls -1 "$dir"/seenit-*.dump | sort -r | tail -n "+$((OFFSITE_KEEP + 1))" | xargs -r rm -f

    if rsync -a --delete --timeout=120 --exclude=.pushed \
        -e "ssh -i /offsite/id_ed25519 -o UserKnownHostsFile=/offsite/known_hosts -o StrictHostKeyChecking=yes -o BatchMode=yes -o ConnectTimeout=30" \
        "$dir/" "$OFFSITE_TARGET/"; then
        touch "$dir/.pushed"
        echo "$(date -Iseconds) off-site copy ok: $(basename "$latest") -> $OFFSITE_TARGET ($(ls -1 "$dir"/seenit-*.dump | wc -l) weekly copies)"
        status offsite-ok "$(basename "$latest") ($(ls -1 "$dir"/seenit-*.dump | wc -l) weekly copies)"
    else
        echo "$(date -Iseconds) off-site copy FAILED (will retry after the next backup)" >&2
        status offsite-failed "rsync to $OFFSITE_TARGET failed (see: docker logs seenit-backup)"
    fi
}

if [ -n "$OFFSITE_TARGET" ] && ! command -v rsync >/dev/null; then
    apk add --no-cache --quiet rsync openssh-client || echo "couldn't install rsync/ssh; off-site copies disabled" >&2
fi

until pg_isready -q; do sleep 5; done

# Report what's already there, so a restarted container doesn't look like it never backed up.
if [ -d /status ]; then
    if [ -n "$OFFSITE_TARGET" ]; then status offsite-enabled "$OFFSITE_TARGET"; else rm -f /status/offsite-enabled; fi
    newest=$(ls -1 /backups/seenit-*.dump 2>/dev/null | sort | tail -1)
    if [ -n "$newest" ] && [ ! -f /status/backup-ok ]; then
        status backup-ok "$(basename "$newest") ($(du -h "$newest" | cut -f1))"
        touch -r "$newest" /status/backup-ok
    fi
    if [ -f /backups/offsite/.pushed ] && [ ! -f /status/offsite-ok ]; then
        status offsite-ok "last weekly copy"
        touch -r /backups/offsite/.pushed /status/offsite-ok
    fi
fi

# Take one straight away if there's nothing from the last day (first start, or the box was off overnight).
if [ -z "$(find /backups -maxdepth 1 -name 'seenit-*.dump' -mtime -1 2>/dev/null)" ]; then dump; fi
offsite || true

while true; do
    now=$(date +%s)
    next=$(date -d "$(date +%Y-%m-%d) $(printf %02d "$BACKUP_HOUR"):00:00" +%s)
    [ "$next" -le "$now" ] && next=$((next + 86400))
    sleep $((next - now))
    dump
    offsite || true
done
