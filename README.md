# SeenIt

Track the movies and TV you watch, Trakt-style. Metadata comes from [TMDB](https://www.themoviedb.org).
The same UI runs as a website (Blazor WebAssembly, installable as a PWA) and as an Android/iOS
app (.NET MAUI Blazor Hybrid).

**Features:** watch history with rewatches · watchlist · 1–10 ratings (movies, shows, episodes) ·
show progress and "Up next" · calendar of upcoming episodes and watchlist releases · custom lists
(public or private) · stats · search and discover · Jellyfin scrobbling via webhook · Trakt and
Letterboxd import · public sign-up with per-user accounts.

## Layout

```
src/
  SeenIt.Contracts   DTOs shared by server and clients
  SeenIt.Server      ASP.NET Core API + Postgres (EF Core); also serves the website
  SeenIt.UI          Razor class library: every page, component and the stylesheet
  SeenIt.Web         Blazor WebAssembly host for the website
  SeenIt.Mobile      .NET MAUI Blazor Hybrid app (Android + iOS) hosting SeenIt.UI
tests/SeenIt.Tests   Import parsers, Jellyfin payload handling, progress maths
```

The clients only ever talk to the API over HTTP with bearer tokens, so the website and the apps
behave identically. The server caches TMDB metadata (titles, runtimes, genres, full episode lists)
in Postgres, so history, progress, calendar and stats never wait on TMDB. A background worker keeps
episode lists fresh for shows anyone follows; another runs imports.

## Running

Production runs on the homelab with Docker Compose (app on port **8082**, plus a Postgres container):

```sh
./scripts/deploy.sh           # ships git HEAD to homelab:~/seenit, rebuilds, runs verify-deploy.sh
./scripts/verify-deploy.sh    # read-only health checks
```

Secrets live in `~/seenit/.env` on the server (see `.env.example`), never in git:
`POSTGRES_PASSWORD` and `TMDB_READ_ACCESS_TOKEN` (TMDB's "API Read Access Token").

Locally: `dotnet run --project src/SeenIt.Server` with `ConnectionStrings__Default` pointing at a
Postgres and `Tmdb__ReadAccessToken` set. Migrations apply on startup.
`dotnet test` runs the tests (no database needed).

## Backups

The `seenit-backup` container dumps the database nightly at 03:00 (New York time) to the NAS
(`192.168.1.61:/volume1/data/backups/seenit`), keeping 14 days. Restore with `scripts/restore.sh <file>`.

Off-site: with `OFFSITE_TARGET=user@host:dir` in `.env`, the newest dump is copied there once a week
with rsync over ssh, keeping the last `OFFSITE_KEEP` (8) weekly copies. The key and the pinned host
key live in `homelab:~/seenit/offsite/` (`id_ed25519`, `known_hosts`); on the remote, restrict the key
to that folder with `command="rrsync -wo ~/seenit-backups",restrict` in `authorized_keys`.

## Jellyfin

Each user gets a private webhook URL under **Settings → Jellyfin**, with step-by-step setup for the
Jellyfin Webhook plugin. SeenIt scrobbles on *Playback Stop* once you've watched past your
threshold (90% by default) or Jellyfin says "played to completion", and on *mark played*. Items are
matched by TMDB id, then IMDb/TVDB ids via TMDB's `/find`, then by title. The page also shows the
last 20 webhook events and what SeenIt did with each one, which makes setup easy to debug.

## Imports

**Settings → Import** takes the `.zip` from Trakt (history, ratings, watchlist, lists; files are
recognised by content, so API-shaped dumps work too) or Letterboxd (diary, watched, ratings,
watchlist, lists; matched to TMDB by title and year). Imports run in the background, are safe to
re-run (duplicates are skipped) and list anything that couldn't be matched.

## Mobile app

The app is a thin MAUI shell around `SeenIt.UI`. Tokens are kept in SecureStorage, and the server
address can be changed on the sign-in screen. The default is in
`src/SeenIt.Mobile/MobileServices.cs` (`PreferencesServerConfig.DefaultUrl`).

It is deliberately **not** in `SeenIt.slnx`, so the server builds anywhere without the MAUI workload.

```sh
# Android (Linux/Windows/Mac). Needs a JDK 17+ and the Android SDK.
dotnet workload install maui-android
dotnet build src/SeenIt.Mobile -f net10.0-android -t:InstallAndroidDependencies \
    -p:AndroidSdkDirectory=$HOME/Android/Sdk -p:AcceptAndroidSDKLicenses=true
dotnet publish src/SeenIt.Mobile -f net10.0-android -c Release   # → .apk/.aab under bin/

# iOS: on a Mac with Xcode, plus an Apple Developer account to install on devices
dotnet workload install maui-ios
dotnet build src/SeenIt.Mobile -f net10.0-ios
```

The app talks to `https://seenit.vohiyo.com/` by default (the Synology reverse proxy terminates
HTTPS and forwards to homelab:8082). Plain-http servers are blocked, so a self-hosted server
entered on the sign-in screen needs HTTPS too.

## Not done yet

- Email confirmation and password reset (needs SMTP), so sign-ups aren't email-verified.
- Social features (following people, comments), recommendations and public profiles.
- Offline mode in the app.
