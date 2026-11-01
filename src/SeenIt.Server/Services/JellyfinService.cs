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
    bool? IsPaused = null)
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

public class JellyfinService(AppDbContext db, TmdbClient tmdb, TrackingService tracking, CheckInService checkIns, ILogger<JellyfinService> log)
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(3);

    public async Task<bool> HandleAsync(string token, JsonElement body, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.JellyfinToken == token, ct);
        if (user is null) return false;

        var payload = JellyfinPayload.Parse(body);
        string outcome;
        try
        {
            outcome = await ProcessAsync(user, payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Jellyfin scrobble failed for {Item}", payload.DisplayName);
            outcome = "Error: " + ex.Message;
        }

        // Ignore the noisy progress pings in the visible log.
        if (payload.NotificationType is not ("PlaybackProgress" or "PlaybackStart"))
        {
            db.JellyfinEvents.Add(new JellyfinEvent { UserId = user.Id, ItemName = payload.DisplayName, Outcome = outcome });
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

    private async Task<string> ProcessAsync(AppUser user, JellyfinPayload p, CancellationToken ct)
    {
        if (p.NotificationType is "PlaybackStart" or "PlaybackProgress" && p.ItemType is "Movie" or "Episode")
            return await NowPlayingAsync(user, p, ct);
        if (p.NotificationType == "PlaybackStop")
            await checkIns.CancelAsync(user.Id, ct, WatchSource.Jellyfin);

        var (scrobble, reason) = p.Decide(user.JellyfinThresholdPercent);
        if (!scrobble) return reason;

        var req = await ResolveAsync(p, ct);
        if (req is null) return $"{reason}, but couldn't match it on TMDB";

        var since = DateTimeOffset.UtcNow - DuplicateWindow;
        var dup = await db.WatchEvents.AnyAsync(w => w.UserId == user.Id && w.Type == req.Type && w.TmdbId == req.TmdbId
                                                     && w.SeasonNumber == req.SeasonNumber && w.EpisodeNumber == req.EpisodeNumber
                                                     && w.WatchedAt >= since, ct);
        if (dup) return $"{reason}; already recorded recently";

        await tracking.WatchAsync(user.Id, req with { WatchedAt = DateTimeOffset.UtcNow }, WatchSource.Jellyfin, ct);
        return $"{reason} → scrobbled";
    }

    /// <summary>Playback start/progress pings keep the user's "watching now" status current.</summary>
    private async Task<string> NowPlayingAsync(AppUser user, JellyfinPayload p, CancellationToken ct)
    {
        var current = await db.CheckIns.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == user.Id, ct);
        DateTimeOffset? endsAt = p.RunTimeTicks is { } run and > 0
            ? DateTimeOffset.UtcNow + TimeSpan.FromTicks(Math.Max(0, run - (p.PositionTicks ?? 0)))
            : null;

        // Progress pings arrive every few seconds; only touch the database when something changed.
        if (p.NotificationType == "PlaybackProgress" && current is { Source: WatchSource.Jellyfin } && CheckInService.IsLive(current)
            && (endsAt is null || p.IsPaused == true || Math.Abs((current.EndsAt - endsAt.Value).TotalMinutes) < 2))
            return "Still watching";

        var req = await ResolveAsync(p, ct);
        if (req is null) return "Now playing, but couldn't match it on TMDB";
        try
        {
            await checkIns.StartAsync(user.Id, new CheckInRequest(req.Type, req.TmdbId, req.SeasonNumber, req.EpisodeNumber), WatchSource.Jellyfin, endsAt, ct);
        }
        catch (TrackingException ex) { return ex.Message; }
        return "Now watching";
    }

    /// <summary>Maps a Jellyfin item to TMDB using provider ids first, then a title search.</summary>
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

        // Episodes: an episode's own provider ids, via TMDB's /find, give the show and numbering directly.
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
