using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SeenIt.Server.Services;

/// <summary>
/// Turns Plex and Kodi notifications into the Jellyfin shape, so all three share one set of rules
/// for "watched", "watching now" and matching to TMDB.
/// </summary>
public static class MediaServerPayloads
{
    private const long TicksPerMs = TimeSpan.TicksPerMillisecond;

    /// <summary>
    /// Plex webhook JSON. Null when it isn't playback (library.new and the like) or it's another
    /// Plex account on the same server (when the user said which account is theirs).
    /// </summary>
    public static JellyfinPayload? FromPlex(JsonElement root, string? onlyAccount)
    {
        var ev = Str(root, "event");
        if (ev is null || !ev.StartsWith("media.", StringComparison.Ordinal)) return null;
        if (!string.IsNullOrWhiteSpace(onlyAccount) && root.TryGetProperty("Account", out var account)
            && !string.Equals(Str(account, "title"), onlyAccount.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;
        if (!root.TryGetProperty("Metadata", out var m) || m.ValueKind != JsonValueKind.Object) return null;

        var itemType = Str(m, "type") switch { "movie" => "Movie", "episode" => "Episode", var t => t };
        var notification = ev switch
        {
            "media.play" or "media.resume" => "PlaybackStart",
            "media.pause" => "PlaybackProgress",
            "media.stop" or "media.scrobble" => "PlaybackStop",
            _ => ev,
        };

        string? tmdb = null, imdb = null, tvdb = null, seriesTmdb = null, seriesTvdb = null;
        // Current agents: "Guid": [{ "id": "imdb://tt…" }, { "id": "tmdb://…" }, { "id": "tvdb://…" }] (the episode's own ids for episodes).
        if (m.TryGetProperty("Guid", out var guids) && guids.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in guids.EnumerateArray())
            {
                var id = Str(g, "id") ?? "";
                if (id.StartsWith("imdb://")) imdb = id[7..];
                else if (id.StartsWith("tmdb://")) tmdb = id[7..];
                else if (id.StartsWith("tvdb://")) tvdb = id[7..];
            }
        }
        // Legacy agents: "guid": "com.plexapp.agents.thetvdb://81189/1/2?lang=en" (the show's id, then the numbering).
        var legacy = Regex.Match(Str(m, "guid") ?? "", @"agents\.(imdb|themoviedb|thetvdb)://([^/?]+)(/\d+/\d+)?");
        if (legacy.Success)
        {
            var (agent, value, isEpisode) = (legacy.Groups[1].Value, legacy.Groups[2].Value, legacy.Groups[3].Success);
            if (agent == "imdb") imdb ??= value;
            else if (agent == "themoviedb") { if (isEpisode) seriesTmdb = value; else tmdb ??= value; }
            else if (isEpisode) seriesTvdb = value;
        }

        long? offset = Long(m, "viewOffset"), duration = Long(m, "duration");
        return new JellyfinPayload(
            notification, itemType, Str(m, "title"), (int?)Long(m, "year"),
            Str(m, "grandparentTitle"), (int?)Long(m, "parentIndex"), (int?)Long(m, "index"),
            itemType == "Movie" ? tmdb : null, imdb, tvdb,
            offset * TicksPerMs, duration * TicksPerMs,
            ev == "media.scrobble" ? true : null,   // Plex sends this once 90% is played
            null, null, DateTimeOffset.UtcNow,
            ev == "media.pause" ? true : null,
            seriesTmdb, seriesTvdb);
    }

    /// <summary>
    /// The SeenIt Kodi add-on: { event: start|pause|stop|end, type: movie|episode, title, year, showtitle,
    /// season, episode, ids: {tmdb, imdb, tvdb}, show_ids: {…}, position, duration } (seconds).
    /// </summary>
    public static JellyfinPayload FromKodi(JsonElement root)
    {
        var ev = Str(root, "event");
        var itemType = Str(root, "type") switch { "movie" => "Movie", "episode" => "Episode", var t => t };
        string? Id(string group, string key) =>
            root.TryGetProperty(group, out var ids) && ids.ValueKind == JsonValueKind.Object ? Str(ids, key) is { Length: > 0 } v && v != "0" ? v : null : null;
        long? Seconds(string key) => Double(root, key) is { } s ? (long)(s * TimeSpan.TicksPerSecond) : null;

        return new JellyfinPayload(
            ev switch { "start" or "resume" => "PlaybackStart", "pause" => "PlaybackProgress", "stop" or "end" => "PlaybackStop", _ => ev },
            itemType, Str(root, "title"), (int?)Long(root, "year"),
            Str(root, "showtitle"), (int?)Long(root, "season"), (int?)Long(root, "episode"),
            itemType == "Movie" ? Id("ids", "tmdb") : null, Id("ids", "imdb"), Id("ids", "tvdb"),
            Seconds("position"), Seconds("duration"),
            ev == "end" ? true : null,
            null, null, DateTimeOffset.UtcNow,
            ev == "pause" ? true : null,
            Id("show_ids", "tmdb"), Id("show_ids", "tvdb"), Id("show_ids", "imdb"));
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static double? Double(JsonElement o, string name) =>
        double.TryParse(Str(o, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? Long(JsonElement o, string name) => Double(o, name) is { } d ? (long)d : null;
}
