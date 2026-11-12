#!/usr/bin/env bash
# Builds the Android app and publishes it:
#   - to the NAS (/mnt/nas/SeenIt.apk), as before;
#   - to the server's downloads folder, where /download/SeenIt.apk serves it and the app shows
#     "SeenIt x.y is available" to anyone on an older version.
# The version matches the server's numbering: 1.<number of commits>, with the commit count as
# Android's versionCode (it must go up for an update to install over the old app).
#
#   ./scripts/publish-apk.sh              build on this PC (needs JDK 17, Android SDK, maui-android workload)
#   ./scripts/publish-apk.sh --on-server  build on the homelab instead (see docs/android-release.md);
#                                         this PC then only needs git + ssh
set -euo pipefail
cd "$(dirname "$0")/.."
on_server=0
[ "${1:-}" = "--on-server" ] && on_server=1

HOST="${DEPLOY_HOST:-homelab}"
DIR="${DEPLOY_DIR:-seenit}"
NAS="${APK_NAS_PATH:-/mnt/nas/SeenIt.apk}"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    echo "Commit first: the app's version comes from the commit count." >&2
    exit 1
fi
count=$(git rev-list --count HEAD)
version="1.$count"
echo "Building SeenIt $version (versionCode $count)..."

if [ "$on_server" = 1 ]; then
    # Ship exactly HEAD to a scratch folder on the server (git archive, like deploy.sh), build there
    # with the server's own toolchain, and drop the APK straight into the downloads folder.
    git archive HEAD | ssh "$HOST" "rm -rf apk-build && mkdir apk-build && tar -x -C apk-build"
    ssh "$HOST" "cd apk-build && export PATH=\$HOME/.dotnet:\$PATH DOTNET_ROOT=\$HOME/.dotnet && \
        dotnet publish src/SeenIt.Mobile/SeenIt.Mobile.csproj -f net10.0-android -c Release -nologo -v q \
        -p:ApplicationDisplayVersion=$version -p:ApplicationVersion=$count \
        -p:AndroidSdkDirectory=\$HOME/Android/Sdk -p:JavaSdkDirectory=\$HOME/jdk 2>&1 | grep -v warning || true; \
        mkdir -p ../$DIR/downloads && cp src/SeenIt.Mobile/bin/Release/net10.0-android/publish/app.seenit.mobile-Signed.apk ../$DIR/downloads/.SeenIt.apk.partial && \
        cd ../$DIR/downloads && mv .SeenIt.apk.partial SeenIt.apk && echo $version > version.txt"
    echo "Built on $HOST and published $version to $HOST:$DIR/downloads"
    exit 0
fi

dotnet publish src/SeenIt.Mobile/SeenIt.Mobile.csproj -f net10.0-android -c Release -nologo -v q \
    -p:ApplicationDisplayVersion="$version" -p:ApplicationVersion="$count" \
    -p:AndroidSdkDirectory="${ANDROID_SDK:-$HOME/Android/Sdk}" -p:JavaSdkDirectory="${JAVA_SDK:-/usr/lib/jvm/java-17-openjdk}"

apk=src/SeenIt.Mobile/bin/Release/net10.0-android/publish/app.seenit.mobile-Signed.apk
[ -f "$apk" ] || { echo "No APK at $apk" >&2; exit 1; }

if [ -d "$(dirname "$NAS")" ]; then cp "$apk" "$NAS" && echo "Copied to $NAS"; fi

ssh "$HOST" "mkdir -p $DIR/downloads"
scp -q "$apk" "$HOST:$DIR/downloads/.SeenIt.apk.partial"
ssh "$HOST" "cd $DIR/downloads && mv .SeenIt.apk.partial SeenIt.apk && echo $version > version.txt"
echo "Published $version to $HOST:$DIR/downloads"
