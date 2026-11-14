using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>The fields SeenIt reads from a Jellyfin Webhook plugin notification.</summary>
public record JellyfinPayload(
    string? NotificationType,
    string? ItemType,
    string? Name,
    int? Year,
    string? SeriesName,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? Tmdb,
    string? Imdb,
    string? Tvdb,
    long? PositionTicks,
    long? RunTimeTicks,
    bool? PlayedToCompletion,
    string? SaveReason,
    bool? Played,
    DateTimeOffset? Timestamp,
    bool? IsPaused = null,
    // The show's own ids, when the sender knows them (Kodi, Plex's older agents).
    string? SeriesTmdb = null,
    string? SeriesTvdb = null,
    string? SeriesImdb = null)
{
    /// <summary>
    /// Accepts both the recommended template and "Send All Properties": keys match case-insensitively
    /// and values may be strings or numbers (templates often quote everything).
    /// </summary>
    public static JellyfinPayload Parse(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var p in root.EnumerateObject()) map[p.Name] = p.Value;

        string? Str(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!map.TryGetValue(k, out var v)) continue;
                var s = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
                    _ => null,
                };
                // Unfilled template variables come through blank.
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            return null;
        }
        long? Long(params string[] keys) => long.TryParse(Str(keys), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        int? Int(params string[] keys) => Long(keys) is { } n and >= int.MinValue and <= int.MaxValue ? (int)n : null;
        bool? Bool(params string[] keys) => bool.TryParse(Str(keys), out var b) ? b : null;

        return new JellyfinPayload(
            Str("NotificationType", "Event"),
            Str("ItemType", "Type"),
            Str("Name"),
            Int("Year", "ProductionYear"),
            Str("SeriesName"),
            Int("SeasonNumber", "ParentIndexNumber"),
            Int("EpisodeNumber", "IndexNumber"),
            Str("Provider_tmdb", "Tmdb"),
            Str("Provider_imdb", "Imdb"),
            Str("Provider_tvdb", "Tvdb"),
            Long("PlaybackPositionTicks"),
            Long("RunTimeTicks"),
            Bool("PlayedToCompletion"),
            Str("SaveReason"),
            Bool("Played"),
            DateTimeOffset.TryParse(Str("UtcTimestamp", "Timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? ts : null,
            Bool("IsPaused"));
    }

    public string DisplayName => ItemType == "Episode"
        ? $"{SeriesName ?? "?"} S{SeasonNumber:00}E{EpisodeNumber:00}"
        : Year is { } y ? $"{Name} ({y})" : Name ?? "(unknown)";

    /// <summary>Whether this notification means "watched", and if not, why.</summary>
    public (bool Scrobble, string Reason) Decide(int thresholdPercent)
    {
        if (ItemType is not ("Movie" or "Episode"))
            return (false, $"Ignored {ItemType ?? "unknown"} item");

        switch (NotificationType)
        {
            case "PlaybackStop":
                if (PlayedToCompletion == true) return (true, "Played to completion");
                if (PositionTicks is { } pos && RunTimeTicks is { } run and > 0)
                {
                    var pct = 100.0 * pos / run;
                    return pct >= thresholdPercent
                        ? (true, $"Stopped at {pct:0}%")
                        : (false, $"Stopped at {pct:0}% (below {thresholdPercent}%)");
                }
                return (false, "Stopped, but no position was sent");

            case "UserDataSaved" when SaveReason == "TogglePlayed":
                return Played == true ? (true, "Marked played") : (false, "Marked unplayed");

            default:
                return (false, $"Ignored {NotificationType ?? "unknown"} event");
        }
    }
}

public class JellyfinService(AppDbContext db, TmdbClient tmdb, TrackingService tracking, CheckInService checkIns, ActivityService activity, LibraryService library, ILogger<JellyfinService> log, LiveHub live)
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(3);

    public async Task<bool> HandleAsync(string token, JsonElement body, CancellationToken ct) =>
        await HandleAsync(token, WatchSource.Jellyfin, _ => Task.FromResult<JellyfinPayload?>(JellyfinPayload.Parse(body)), ct);

    /// <summary>A Plex webhook (Plex Pass): the JSON is in the multipart form's "payload" field.</summary>
    public async Task<bool> HandlePlexAsync(string token, string json, CancellationToken ct) =>
        await HandleAsync(token, WatchSource.Plex, user =>
        {
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult(MediaServerPayloads.FromPlex(doc.RootElement, user.PlexUsername));
        }, ct);

    /// <summary>The SeenIt Kodi add-on's notifications.</summary>
    public async Task<bool> HandleKodiAsync(string token, JsonElement body, CancellationToken ct) =>
        await HandleAsync(token, WatchSource.Kodi, _ => Task.FromResult(MediaServerPayloads.FromKodi(body)), ct);

    /// <summary>The parser returns null for events that aren't for this user (another Plex account).</summary>
    private async Task<bool> HandleAsync(string token, WatchSource source, Func<AppUser, Task<JellyfinPayload?>> parse, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.JellyfinToken == token, ct);
        if (user is null) return false;

        var payload = await parse(user);
        if (payload is null) return true;
        string outcome;
        try
        {
            outcome = await ProcessAsync(user, payload, source, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "{Source} scrobble failed for {Item}", source, payload.DisplayName);
            outcome = "Error: " + ex.Message;
        }

        // Ignore the noisy progress pings in the visible log.
        if (payload.NotificationType is not ("PlaybackProgress" or "PlaybackStart"))
        {
            var name = source == WatchSource.Jellyfin ? payload.DisplayName : $"{WatchSources.Name(source)} · {payload.DisplayName}";
            db.JellyfinEvents.Add(new JellyfinEvent { UserId = user.Id, ItemName = name.Length > 300 ? name[..300] : name, Outcome = outcome });
            await db.SaveChangesAsync(ct);
            var stale = await db.JellyfinEvents.Where(e => e.UserId == user.Id)
                .OrderByDescending(e => e.ReceivedAt).Skip(50).Select(e => e.Id).ToListAsync(ct);
            if (stale.Count > 0)
            {
                db.JellyfinEvents.RemoveRange(stale.Select(id => new JellyfinEvent { Id = id }));
                await db.SaveChangesAsync(ct);
            }
        }
        return true;
    }

    private async Task<string> ProcessAsync(AppUser user, JellyfinPayload p, WatchSource source, CancellationToken ct)
    {
        // Jellyfin's "Item Added" / Plex's library.new: it's on your server now, so it's in your library.
        if (p.NotificationType == "ItemAdded")
        {
            if (await ResolveOwnedAsync(p, ct) is not { } owned) return "Added to the server, but couldn't match it to TMDB";
            return await library.AddAsync(user.Id, owned.Type, owned.Id, OwnedFormat.MediaServer, source, ct)
                ? "Added to your library" : "Already in your library";
        }
        if (p.NotificationType is "PlaybackStart" or "PlaybackProgress" && p.ItemType is "Movie" or "Episode")
            return await NowPlayingAsync(user, p, source, ct);
        if (p.NotificationType == "PlaybackStop")
            await checkIns.CancelAsync(user.Id, ct, source);

        var (scrobble, reason) = p.Decide(user.JellyfinThresholdPercent);
        if (!scrobble) return reason;

        var req = await ResolveAsync(p, ct);
        if (req is null) return $"{reason}, but couldn't match it on TMDB";

        var since = DateTimeOffset.UtcNow - DuplicateWindow;
        var dup = await db.WatchEvents.AnyAsync(w => w.UserId == user.Id && w.Type == req.Type && w.TmdbId == req.TmdbId
                                                     && w.SeasonNumber == req.SeasonNumber && w.EpisodeNumber == req.EpisodeNumber
                                                     && w.WatchedAt >= since, ct);
        if (dup) return $"{reason}; already recorded recently";

        await tracking.WatchAsync(user.Id, req with { WatchedAt = DateTimeOffset.UtcNow }, source, ct);
        await activity.LogAsync(user.Id, ActivityKind.Watch, req.Type, req.TmdbId, req.SeasonNumber, req.EpisodeNumber,
            reason, source, user.UserName);
        await live.NotifyWithFollowersAsync(db, user.Id, ct);
        return $"{reason} → scrobbled";
    }

    /// <summary>Playback start/progress pings keep the user's "watching now" status current.</summary>
    private async Task<string> NowPlayingAsync(AppUser user, JellyfinPayload p, WatchSource source, CancellationToken ct)
    {
        var current = await db.CheckIns.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == user.Id, ct);
        DateTimeOffset? endsAt = p.RunTimeTicks is { } run and > 0
            ? DateTimeOffset.UtcNow + TimeSpan.FromTicks(Math.Max(0, run - (p.PositionTicks ?? 0)))
            : null;
        // Where playback really began, as if watched straight through: now minus the position.
        DateTimeOffset? startedAt = p.RunTimeTicks is > 0 && p.PositionTicks is { } pos
            ? DateTimeOffset.UtcNow - TimeSpan.FromTicks(Math.Max(0, pos))
            : null;

        // A paused player keeps reporting the same spot: note when the pause began (it stops counting as watching after a while).
        if (p.NotificationType == "PlaybackProgress" && p.IsPaused == true && current is not null && current.Source == source && CheckInService.IsLive(current))
        {
            if (current.PausedAt is null || DateTimeOffset.UtcNow - current.LastPingAt > TimeSpan.FromMinutes(1))
            {
                var now = DateTimeOffset.UtcNow;
                await db.CheckIns.Where(c => c.UserId == user.Id)
                    .ExecuteUpdateAsync(c => c.SetProperty(x => x.LastPingAt, now).SetProperty(x => x.PausedAt, x => x.PausedAt ?? now), ct);
                if (current.PausedAt is null) await live.NotifyWithFollowersAsync(db, user.Id, ct);
            }
            return "Paused";
        }
        // Paused with nothing live (the pause ran out, or it was never started here): don't bring it back.
        if (p.NotificationType == "PlaybackProgress" && p.IsPaused == true) return "Paused";

        // Progress pings arrive every few seconds; only touch the database when something changed.
        if (p.NotificationType == "PlaybackProgress" && current is not null && current.Source == source && CheckInService.IsLive(current)
            && (endsAt is null || Math.Abs((current.EndsAt - endsAt.Value).TotalMinutes) < 2)
            && (startedAt is null || Math.Abs((current.StartedAt - startedAt.Value).TotalMinutes) < 2))
        {
            // Nothing to change, but note the player is still reporting so the check-in isn't dropped as quiet.
            if (current.PausedAt is not null || DateTimeOffset.UtcNow - current.LastPingAt > TimeSpan.FromMinutes(1))
                await db.CheckIns.Where(c => c.UserId == user.Id)
                    .ExecuteUpdateAsync(c => c.SetProperty(x => x.LastPingAt, DateTimeOffset.UtcNow).SetProperty(x => x.PausedAt, (DateTimeOffset?)null), ct);
            return "Still watching";
        }

        var req = await ResolveAsync(p, ct);
        if (req is null) return "Now playing, but couldn't match it on TMDB";
        try
        {
            await checkIns.StartAsync(user.Id, new CheckInRequest(req.Type, req.TmdbId, req.SeasonNumber, req.EpisodeNumber), source, endsAt, ct, startedAt);
        }
        catch (TrackingException ex) { return ex.Message; }
        return "Now watching";
    }

    /// <summary>Maps a Jellyfin item to TMDB using provider ids first, then a title search.</summary>
    /// <summary>The movie or show something added to a server belongs to (an episode or season counts as its show).</summary>
    public async Task<(MediaType Type, int Id)?> ResolveOwnedAsync(JellyfinPayload p, CancellationToken ct)
    {
        switch (p.ItemType)
        {
            case "Movie":
                return await ResolveAsync(p, ct) is { } m ? (MediaType.Movie, m.TmdbId) : null;
            case "Episode":
                return await ResolveAsync(p, ct) is { } e ? (MediaType.Show, e.TmdbId) : null;
            case "Series" or "Season":
                var ids = p.ItemType == "Series"
                    ? new[] { (p.Tmdb, "tmdb"), (p.Tvdb, "tvdb_id"), (p.Imdb, "imdb_id") }
                    : [(p.SeriesTmdb, "tmdb"), (p.SeriesTvdb, "tvdb_id"), (p.SeriesImdb, "imdb_id")];
                foreach (var (value, source) in ids)
                {
                    if (string.IsNullOrEmpty(value)) continue;
                    if (source == "tmdb" && int.TryParse(value, out var id)) return (MediaType.Show, id);
                    if (source != "tmdb" && (await tmdb.FindAsync(value, source, ct))?.TvResults?.FirstOrDefault() is { } show) return (MediaType.Show, show.Id);
                }
                var name = p.ItemType == "Series" ? p.Name : p.SeriesName;
                if (name is not null && (await tmdb.SearchShowAsync(name, p.ItemType == "Series" ? p.Year : null, ct))?.Results.FirstOrDefault() is { } hit)
                    return (MediaType.Show, hit.Id);
                return null;
            default:
                return null;
        }
    }

    public async Task<WatchRequest?> ResolveAsync(JellyfinPayload p, CancellationToken ct)
    {
        if (p.ItemType == "Movie")
        {
            if (int.TryParse(p.Tmdb, out var id)) return new WatchRequest(MediaType.Movie, id);
            if (p.Imdb is not null && (await tmdb.FindAsync(p.Imdb, "imdb_id", ct))?.MovieResults?.FirstOrDefault() is { } m)
                return new WatchRequest(MediaType.Movie, m.Id);
            if (p.Name is not null)
            {
                var hit = (await tmdb.SearchMovieAsync(p.Name, p.Year, ct))?.Results.FirstOrDefault()
                          ?? (await tmdb.SearchMovieAsync(p.Name, null, ct))?.Results.FirstOrDefault();
                if (hit is not null) return new WatchRequest(MediaType.Movie, hit.Id);
            }
            return null;
        }

        // Episodes: the show's own ids plus the numbering, when the sender has them.
        if (p.SeasonNumber is { } season && p.EpisodeNumber is { } number)
        {
            if (int.TryParse(p.SeriesTmdb, out var showId)) return new WatchRequest(MediaType.Episode, showId, season, number);
            foreach (var (externalId, source) in new[] { (p.SeriesTvdb, "tvdb_id"), (p.SeriesImdb, "imdb_id") })
            {
                if (externalId is null) continue;
                if ((await tmdb.FindAsync(externalId, source, ct))?.TvResults?.FirstOrDefault() is { } show)
                    return new WatchRequest(MediaType.Episode, show.Id, season, number);
            }
        }

        // Otherwise an episode's own provider ids, via TMDB's /find, give the show and numbering directly.
        foreach (var (externalId, source) in new[] { (p.Tvdb, "tvdb_id"), (p.Imdb, "imdb_id") })
        {
            if (externalId is null) continue;
            if ((await tmdb.FindAsync(externalId, source, ct))?.TvEpisodeResults?.FirstOrDefault() is { } e)
                return new WatchRequest(MediaType.Episode, e.ShowId, e.SeasonNumber, e.EpisodeNumber);
        }
        if (p.SeriesName is not null && p.SeasonNumber is { } s && p.EpisodeNumber is { } n)
        {
            var show = (await tmdb.SearchShowAsync(p.SeriesName, null, ct))?.Results.FirstOrDefault();
            if (show is not null) return new WatchRequest(MediaType.Episode, show.Id, s, n);
        }
        return null;
    }
}
