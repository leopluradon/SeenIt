# Releasing the Android app

The app's version is `1.<commit count>`, and the commit count is also Android's versionCode, so
every new commit makes a newer build. The APK ends up at `homelab:~/seenit/downloads/SeenIt.apk`
(served at `/download/SeenIt.apk`; the app's update banner reads `version.txt` next to it).

**Always `git pull` first.** The version comes from the local commit count, so a stale checkout
publishes an older APK over a newer one.

```
git pull --ff-only
./scripts/publish-apk.sh              # build on this PC, also copies to /mnt/nas/SeenIt.apk
./scripts/publish-apk.sh --on-server  # build on the homelab (needs only git + ssh locally)
```

Both need a clean commit (`git status` empty) and the `homelab` ssh alias.

## What the build needs (this PC and the homelab)

All user-level, no root:

| Piece | Where | Notes |
|---|---|---|
| .NET SDK 10.0.400 | `~/.dotnet` | not on PATH; the script exports `PATH`/`DOTNET_ROOT` |
| `maui-android` workload | inside `~/.dotnet` (packs, ~3.4 GB) | `~/.dotnet/dotnet workload install maui-android` |
| JDK 17 | PC: `/usr/lib/jvm/java-17-openjdk`; homelab: `~/jdk` (Temurin 17) | |
| Android SDK | `~/Android/Sdk` with `platforms/android-36`, `build-tools/36.0.0`, `platform-tools`, `cmdline-tools` | emulator/system-images are not needed for builds |
| **Signing keystore** | `~/.local/share/Xamarin/Mono for Android/debug.keystore` | see below |

### The keystore is the important part
The APK is signed with the auto-generated debug keystore. Android only installs an update over an
installed app if the signing certificate is identical, so **every machine that builds must have a
copy of the same `debug.keystore`**, and it should be backed up. A different keystore means users
must uninstall and reinstall (losing local app data). Check with:

```
~/Android/Sdk/build-tools/36.0.0/apksigner verify --print-certs SeenIt.apk | grep SHA-256
# expected: 0fd65d99c5f4f0bf077696779f6c09281c1bbe9a6e691ee6a81d417d092a6289
```

## Setting up the homelab as a build machine (done 2026-11-12)

The homelab is x86_64 Linux like the PC, and has no rsync or unzip, so the toolchain was copied
with tar over ssh (run from the PC):

```
ssh homelab 'mkdir -p ~/.dotnet ~/Android/Sdk ~/jdk ~/.local/share/Xamarin/"Mono for Android"'
tar -c -C ~/.dotnet . | ssh homelab 'tar -x -C ~/.dotnet'
tar -c --exclude=./emulator --exclude=./system-images -C ~/Android/Sdk . | ssh homelab 'tar -x -C ~/Android/Sdk'
scp ~/.local/share/Xamarin/"Mono for Android"/debug.keystore homelab:".local/share/Xamarin/Mono for Android/"
ssh homelab 'curl -fsSL "https://api.adoptium.net/v3/binary/latest/17/ga/linux/x64/jdk/hotspot/normal/eclipse" | tar -xz -C ~/jdk --strip-components=1'
```

Then `./scripts/publish-apk.sh --on-server` ships `git archive HEAD` to `~/apk-build`, builds there
(~2.5 min), moves the APK into `~/seenit/downloads`, and copies it to the NAS with
`scripts/apk-to-nas.sh` (the homelab has no NAS mount, so it mounts `192.168.1.61:/volume1/data`,
the share the PC sees as `/mnt/nas`, in a throwaway Docker volume). Takes ~4.5 GB of disk; `~/apk-build` is
recreated on every run.

To upgrade the SDK or workload later, update the PC first, then repeat the tar copies (the
workload manifests live inside `~/.dotnet`).
