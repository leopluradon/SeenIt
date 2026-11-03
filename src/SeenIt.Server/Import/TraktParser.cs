using System.Globalization;
using System.Text.Json;
using SeenIt.Contracts;

namespace SeenIt.Server.Import;

/// <summary>
/// Reads a Trakt data export. Files are recognized by their contents rather than their names,
/// so both trakt.tv's own export and API-shaped dumps (sync/history, sync/watched, …) work.
/// </summary>
public static class TraktParser
{
    public static ParsedImport Parse(IReadOnlyDictionary<string, string> files)
    {
        var result = new ParsedImport();
        var watchedSummaries = new List<JsonElement>();
        var listMeta = new Dictionary<string, (string Name, string? Description)>(StringComparer.OrdinalIgnoreCase);
        var listFiles = new List<(string FileName, JsonElement Items)>();

        foreach (var (name, text) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            JsonElement root;
            try { root = JsonDocument.Parse(text).RootElement; }
            catch (JsonException) { continue; }
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) continue;
            var first = root[0];
            if (first.ValueKind != JsonValueKind.Object) continue;

            if (first.TryGetProperty("watched_at", out _))
                foreach (var e in root.EnumerateArray()) AddHistory(result, e);
            else if (first.TryGetProperty("rated_at", out _) && first.TryGetProperty("rating", out _))
                foreach (var e in root.EnumerateArray()) AddRating(result, e);
            else if (first.TryGetProperty("listed_at", out _))
                listFiles.Add((name, root));
            else if (first.TryGetProperty("last_watched_at", out _) && first.TryGetProperty("plays", out _))
                watchedSummaries.AddRange(root.EnumerateArray());
            else if (first.TryGetProperty("name", out _) && first.TryGetProperty("ids", out _))
                foreach (var l in root.EnumerateArray())
                {
                    var meta = (Str(l, "name") ?? Str(l, "ids", "slug") ?? "List", Str(l, "description"));
                    if (Str(l, "ids", "slug") is { } slug) listMeta[slug] = meta;
                    // Export files are named lists-list-<trakt id>-<slug>.json, so the id is the reliable key.
                    if (Int(l, "ids", "trakt") is { } traktId) listMeta[$"#{traktId}"] = meta;
                }
        }

        // Full history is better; summaries are only a fallback (using both would double-count).
        if (result.History.Count == 0)
            foreach (var w in watchedSummaries) AddWatchedSummary(result, w);

        foreach (var (fileName, items) in listFiles)
        {
            var parsed = items.EnumerateArray()
                .Select(i => Ref(i) is { } r ? new ImportListItem(r, Date(i, "listed_at")) : null)
                .OfType<ImportListItem>().ToList();
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (baseName.Contains("watchlist", StringComparison.OrdinalIgnoreCase))
            {
                result.Watchlist.AddRange(parsed);
                continue;
            }
            if (baseName.Equals("lists-favorites", StringComparison.OrdinalIgnoreCase) || baseName.Equals("favorites", StringComparison.OrdinalIgnoreCase))
            {
                // Trakt favorites are ranked; keep that order.
                result.Favorites.AddRange(items.EnumerateArray()
                    .Select(i => (Item: Ref(i) is { } r ? new ImportListItem(r, Date(i, "listed_at")) : null, Rank: Int(i, "rank") ?? int.MaxValue))
                    .Where(x => x.Item is not null).OrderBy(x => x.Rank).Select(x => x.Item!));
                continue;
            }
            var slug = baseName.StartsWith("lists-", StringComparison.OrdinalIgnoreCase) ? baseName[6..] : baseName;
            var byId = System.Text.RegularExpressions.Regex.Match(slug, @"^list-(\d+)(-|$)");
            var withoutId = byId.Success && slug.Length > byId.Length ? slug[byId.Length..] : slug;
            var (listName, desc) =
                byId.Success && listMeta.TryGetValue($"#{byId.Groups[1].Value}", out var found) ? found
                : listMeta.TryGetValue(slug, out var bySlug) ? bySlug
                : (Humanize(withoutId), null);
            // Older SeenIt imports named these "List 11732618 6 12 Weekend"; a re-import renames them.
            if (parsed.Count > 0) result.Lists.Add(new ImportList(listName, desc, parsed, Humanize(slug)));
        }

        return result;
    }

    private static void AddHistory(ParsedImport result, JsonElement e)
    {
        var at = Date(e, "watched_at");
        var type = Str(e, "type");
        if (type == "movie" && Ref(e) is { } movie)
            result.History.Add(new ImportWatch(movie, null, null, at));
        else if (type == "episode" && e.TryGetProperty("show", out var show) && e.TryGetProperty("episode", out var ep)
                 && ShowRef(show) is { } s && Int(ep, "season") is { } sn && Int(ep, "number") is { } en)
            result.History.Add(new ImportWatch(s, sn, en, at));
    }

    private static void AddRating(ParsedImport result, JsonElement e)
    {
        if (Int(e, "rating") is not { } rating || rating is < 1 or > 10) return;
        var at = Date(e, "rated_at");
        switch (Str(e, "type"))
        {
            case "movie" or "show" when Ref(e) is { } r:
                result.Ratings.Add(new ImportRating(r, null, null, rating, at));
                break;
            case "episode" when e.TryGetProperty("show", out var show) && e.TryGetProperty("episode", out var ep)
                                && ShowRef(show) is { } s && Int(ep, "season") is { } sn && Int(ep, "number") is { } en:
                result.Ratings.Add(new ImportRating(s, sn, en, rating, at));
                break;
        }
    }

    private static void AddWatchedSummary(ParsedImport result, JsonElement w)
    {
        var at = Date(w, "last_watched_at");
        if (w.TryGetProperty("movie", out var movie) && MovieRef(movie) is { } m)
        {
            var plays = Math.Clamp(Int(w, "plays") ?? 1, 1, 50);
            for (var i = 0; i < plays; i++) result.History.Add(new ImportWatch(m, null, null, at, Approximate: true));
        }
        else if (w.TryGetProperty("show", out var show) && ShowRef(show) is { } s && w.TryGetProperty("seasons", out var seasons))
        {
            foreach (var season in seasons.EnumerateArray())
            {
                if (Int(season, "number") is not { } sn || !season.TryGetProperty("episodes", out var eps)) continue;
                foreach (var ep in eps.EnumerateArray())
                    if (Int(ep, "number") is { } en)
                        result.History.Add(new ImportWatch(s, sn, en, ep.TryGetProperty("last_watched_at", out _) ? Date(ep, "last_watched_at") : at, Approximate: true));
            }
        }
    }

    /// <summary>Movie or show from an item with a "type" and a matching nested object.</summary>
    private static ImportRef? Ref(JsonElement e) => Str(e, "type") switch
    {
        "movie" when e.TryGetProperty("movie", out var m) => MovieRef(m),
        "show" when e.TryGetProperty("show", out var s) => ShowRef(s),
        _ => null,
    };

    private static ImportRef? MovieRef(JsonElement m) => MediaRef(MediaType.Movie, m);
    private static ImportRef? ShowRef(JsonElement s) => MediaRef(MediaType.Show, s);

    private static ImportRef? MediaRef(MediaType type, JsonElement o)
    {
        var tmdb = Int(o, "ids", "tmdb");
        var imdb = Str(o, "ids", "imdb");
        var title = Str(o, "title");
        if (tmdb is null && imdb is null && title is null) return null;
        return new ImportRef(type, tmdb, imdb, title, Int(o, "year"));
    }

    private static JsonElement? Get(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.Null ? null : e;
    }

    private static string? Str(JsonElement e, params string[] path) =>
        Get(e, path) is { } v ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null : null;

    private static int? Int(JsonElement e, params string[] path) =>
        Get(e, path) is { } v && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var n) : int.TryParse(v.GetString(), out n)) ? n : null;

    private static DateTimeOffset Date(JsonElement e, string prop) =>
        DateTimeOffset.TryParse(Str(e, prop), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : DateTimeOffset.UtcNow;

    private static string Humanize(string slug) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.Replace('-', ' ').Replace('_', ' '));
}
