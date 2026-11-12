#!/usr/bin/env bash
# Copies the published APK (~/seenit/downloads/SeenIt.apk) to the NAS, where the PC mounts it as
# /mnt/nas/SeenIt.apk. Runs ON the homelab: it has no NAS mount of its own, so a throwaway Docker
# volume mounts the NAS's NFS share just for the copy (the same way the backup volumes reach it).
#
#   ./scripts/apk-to-nas.sh             (publish-apk.sh --on-server runs it after each build)
set -euo pipefail

NAS_ADDR="${NAS_ADDR:-192.168.1.61}"
NAS_EXPORT="${APK_NAS_EXPORT:-/volume1/data}"    # the share the PC mounts at /mnt/nas
SRC="${APK_DIR:-$HOME/seenit/downloads}"
VOL="seenit-apk-nas-$$"

[ -f "$SRC/SeenIt.apk" ] || { echo "No APK at $SRC/SeenIt.apk" >&2; exit 1; }
docker volume create --driver local --opt type=nfs --opt "o=addr=$NAS_ADDR,nfsvers=4.1,rw,soft,timeo=50" \
    --opt "device=:$NAS_EXPORT" "$VOL" >/dev/null
trap 'docker volume rm -f "$VOL" >/dev/null 2>&1 || true' EXIT
# Written under a temporary name and then renamed, so the NAS never holds half an APK.
docker run --rm -v "$VOL:/nas" -v "$SRC:/src:ro" alpine sh -c \
    'cp /src/SeenIt.apk /nas/.SeenIt.apk.partial && mv /nas/.SeenIt.apk.partial /nas/SeenIt.apk'
echo "Copied $(cat "$SRC/version.txt" 2>/dev/null || echo the APK) to $NAS_ADDR:$NAS_EXPORT/SeenIt.apk"
