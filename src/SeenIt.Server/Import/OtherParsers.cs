using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SeenIt.Contracts;

namespace SeenIt.Server.Import;

/// <summary>Bits the smaller importers share.</summary>
internal static class ImportText
{
    public static string? Get(Dictionary<string, string> row, params string[] keys)
    {
        foreach (var k in keys)
            if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    public static int? Int(string? s) => int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Real ids only: "0", blanks and junk mean "none".</summary>
    public static int? Id(string? s) => Int(s) is > 0 and var n ? n : null;

    public static string? Imdb(string? s) => s is not null && Regex.IsMatch(s.Trim(), @"^tt\d+$") ? s.Trim() : null;

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
    ];

    /// <summary>A timestamp (taken as UTC when it has no offset) or a bare date (noon UTC, so the day is the same everywhere).</summary>
    public static DateTimeOffset? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTimeOffset.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return d.TimeOfDay == TimeSpan.Zero && !s.Contains(':') ? d.AddHours(12) : d;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d) ? d : null;
    }
}

/// <summary>
/// IMDb's CSV exports (Your Ratings, Watchlist, or any list: ⋯ → Export). Rows carry IMDb ids,
/// so they're matched exactly through TMDB. Rated movies count as watched on the day you rated them.
/// </summary>
public static class ImdbParser
{
    public static ParsedImport Parse(IReadOnlyDictionary<string, string> files)
    {
        var result = new ParsedImport();
        foreach (var (name, text) in files.Where(f => f.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            var rows = LetterboxdParser.Rows(text).ToList();
            if (rows.Count == 0 || !rows[0].ContainsKey("Const")) continue;
            var file = Path.GetFileNameWithoutExtension(name);

            if (rows[0].ContainsKey("Your Rating"))
            {
                foreach (var row in rows)
                {
                    if (Item(row) is not { } item || ImportText.Int(ImportText.Get(row, "Your Rating")) is not (>= 1 and <= 10 and var value)) continue;
                    var at = ImportText.Date(ImportText.Get(row, "Date Rated")) ?? DateTimeOffset.UtcNow;
                    result.Ratings.Add(new ImportRating(item, null, null, value, at));
                    // You rate what you've seen. The date is when you rated it, so it's only a stand-in.
                    if (item.Type == MediaType.Movie) result.History.Add(new ImportWatch(item, null, null, at, Approximate: true));
                }
                continue;
            }

            var items = rows.Select(r => Item(r) is { } i ? new ImportListItem(i, ImportText.Date(ImportText.Get(r, "Created")) ?? DateTimeOffset.UtcNow) : null)
                .OfType<ImportListItem>().ToList();
            if (file.Contains("watchlist", StringComparison.OrdinalIgnoreCase)) result.Watchlist.AddRange(items);
            else if (items.Count > 0) result.Lists.Add(new ImportList(ListName(file), "Imported from IMDb.", items));
        }
        return result;
    }

    private static string ListName(string file) =>
        Regex.IsMatch(file, @"^ls\d+$") ? $"IMDb list {file}" : Regex.Replace(file, @"[_\-]+", " ").Trim();

    private static ImportRef? Item(Dictionary<string, string> row)
    {
        if (ImportText.Imdb(ImportText.Get(row, "Const")) is not { } imdb) return null;
        // "Movie", "TV Series", "TV Mini Series", "TV Movie", "Short"... or the older "movie", "tvSeries".
        var kind = (ImportText.Get(row, "Title Type") ?? "movie").Replace(" ", "").ToLowerInvariant();
        MediaType? type = kind switch
        {
            "movie" or "tvmovie" or "short" or "video" or "tvspecial" or "tvshort" => MediaType.Movie,
            "tvseries" or "tvminiseries" => MediaType.Show,
            _ => null,   // episodes, games, podcasts
        };
        return type is null ? null
            : new ImportRef(type.Value, null, imdb, ImportText.Get(row, "Title"), ImportText.Int(ImportText.Get(row, "Year")));
    }
}

/// <summary>
/// TV Time's data export (the GDPR zip). seen_episode*.csv lists every episode marked watched;
/// tracking-prod-records*.csv supplies TheTVDB ids for the shows and the "for later" ones.
/// TV Time's export doesn't list movies in a usable way, so only shows come in.
/// </summary>
public static class TvTimeParser
{
    public static ParsedImport Parse(IReadOnlyDictionary<string, string> files)
    {
        var result = new ParsedImport();
        var tvdb = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var later = new List<(string Name, DateTimeOffset At)>();

        foreach (var (_, text) in files.Where(f => Path.GetFileName(f.Key).Contains("prod-records", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var row in LetterboxdParser.Rows(text))
            {
                if (ImportText.Get(row, "series_name") is not { } name) continue;
                if (ImportText.Id(ImportText.Get(row, "s_id", "series_id")) is { } id) tvdb.TryAdd(name, id);
                if (ImportText.Get(row, "is_for_later") == "true") later.Add((name, ImportText.Date(ImportText.Get(row, "created_at")) ?? DateTimeOffset.UtcNow));
            }
        }

        ImportRef Show(string name) => new(MediaType.Show, null, null, name, null, TvdbId: tvdb.TryGetValue(name, out var id) ? id : null);

        var seen = new HashSet<(string, int, int, DateTimeOffset)>();
        foreach (var (_, text) in files.Where(f => Path.GetFileName(f.Key).StartsWith("seen_episode", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var row in LetterboxdParser.Rows(text))
            {
                if (ImportText.Get(row, "tv_show_name", "series_name") is not { } name
                    || ImportText.Int(ImportText.Get(row, "episode_season_number", "season_number")) is not { } s
                    || ImportText.Int(ImportText.Get(row, "episode_number")) is not { } e) continue;
                var at = ImportText.Date(ImportText.Get(row, "created_at", "updated_at")) ?? WatchDates.Unknown;
                if (!seen.Add((name.ToLowerInvariant(), s, e, at))) continue;   // the same mark in two files
                result.History.Add(new ImportWatch(Show(name), s, e, at));
            }
        }

        var watched = result.History.Select(h => h.Item.Title!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, at) in later.Where(l => !watched.Contains(l.Name)).DistinctBy(l => l.Name.ToLowerInvariant()))
            result.Watchlist.Add(new ImportListItem(Show(name), at));
        return result;
    }
}

/// <summary>
/// Simkl's backups: the CSV (SIMKL_ID, Title, Type, Watchlist, LastEpWatched, WatchedDate, Rating,
/// TMDB, IMDB…) or the JSON one, which has every episode's watch time.
/// </summary>
public static class SimklParser
{
    public static ParsedImport Parse(IReadOnlyDictionary<string, string> files)
    {
        var result = new ParsedImport();
        foreach (var (name, text) in files)
        {
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ParseJson(text, result);
            else if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ParseCsv(text, result);
        }
        return result;
    }

    private static MediaType? Kind(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => MediaType.Movie,
        "tv" or "show" or "shows" or "anime" => MediaType.Show,
        _ => null,
    };

    private enum Status { Unknown, Completed, Watching, PlanToWatch, Other }

    private static Status ParseStatus(string? s) => Regex.Replace(s ?? "", @"[\s_\-]", "").ToLowerInvariant() switch
    {
        "completed" => Status.Completed,
        "watching" => Status.Watching,
        "plantowatch" or "plantowatchlist" or "watchlist" => Status.PlanToWatch,
        "" => Status.Unknown,
        _ => Status.Other,   // on hold, dropped
    };

    private static void ParseCsv(string text, ParsedImport result)
    {
        foreach (var row in LetterboxdParser.Rows(text))
        {
            if (Kind(ImportText.Get(row, "Type")) is not { } type) continue;
            var item = new ImportRef(type, ImportText.Id(ImportText.Get(row, "TMDB")), ImportText.Imdb(ImportText.Get(row, "IMDB")),
                ImportText.Get(row, "Title"), ImportText.Int(ImportText.Get(row, "Year")), TvdbId: ImportText.Id(ImportText.Get(row, "TVDB")));
            if (item.TmdbId is null && item.ImdbId is null && item.Title is null) continue;
            var status = ParseStatus(ImportText.Get(row, "Watchlist", "Status"));
            var at = ImportText.Date(ImportText.Get(row, "WatchedDate", "Watched Date", "LastWatchedAt"));
            Add(result, item, status, at, ImportText.Int(ImportText.Get(row, "Rating", "User Rating")), LastEpisode(ImportText.Get(row, "LastEpWatched")));
        }
    }

    /// <summary>"s02e05", "S2E5" or "2x05".</summary>
    private static (int S, int E)? LastEpisode(string? s)
    {
        var m = Regex.Match(s ?? "", @"s?(\d+)\s*[ex]\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    private static void Add(ParsedImport result, ImportRef item, Status status, DateTimeOffset? at, int? rating, (int S, int E)? lastEpisode)
    {
        if (rating is >= 1 and <= 10) result.Ratings.Add(new ImportRating(item, null, null, rating.Value, at ?? DateTimeOffset.UtcNow));
        if (status == Status.PlanToWatch) { result.Watchlist.Add(new ImportListItem(item, DateTimeOffset.UtcNow)); return; }
        if (item.Type == MediaType.Movie)
        {
            if (status == Status.Completed || at is not null)
                result.History.Add(new ImportWatch(item, null, null, at ?? WatchDates.Unknown, Approximate: at is null));
        }
        else if (lastEpisode is { } last)
            result.Progress.Add(new ImportProgress(item, last.S, last.E, at));
        else if (status == Status.Completed)
            result.Progress.Add(new ImportProgress(item, int.MaxValue, int.MaxValue, at));
    }

    private static void ParseJson(string text, ParsedImport result)
    {
        using var doc = JsonDocument.Parse(text);
        // The backup groups titles by kind ("movies", "shows", "anime"), sometimes under a status.
        Walk(doc.RootElement, null);

        void Walk(JsonElement e, string? key)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (TryEntry(e, key)) return;
                foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name);
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var x in e.EnumerateArray()) Walk(x, key);
        }

        bool TryEntry(JsonElement e, string? key)
        {
            JsonElement media = default;
            MediaType? type = null;
            foreach (var (prop, t) in new[] { ("movie", MediaType.Movie), ("show", MediaType.Show), ("anime", MediaType.Show) })
                if (e.TryGetProperty(prop, out var m) && m.ValueKind == JsonValueKind.Object) { media = m; type = t; break; }
            if (type is null) return false;

            var ids = media.TryGetProperty("ids", out var i) ? i : default;
            string? Id(string name) => ids.ValueKind == JsonValueKind.Object && ids.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ValueKind == JsonValueKind.String ? v.GetString() : null
                : null;
            string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
                : null;

            var item = new ImportRef(type.Value, ImportText.Id(Id("tmdb")), ImportText.Imdb(Id("imdb")), Str(media, "title"),
                ImportText.Int(Str(media, "year")), TvdbId: ImportText.Id(Id("tvdb")));
            var status = ParseStatus(Str(e, "status") ?? key);
            var at = ImportText.Date(Str(e, "last_watched_at"));
            var rating = ImportText.Int(Str(e, "user_rating"));

            // Full backups list each episode with its own watch time.
            var episodes = 0;
            if (type == MediaType.Show && e.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
            {
                foreach (var season in seasons.EnumerateArray())
                {
                    if (ImportText.Int(Str(season, "number")) is not { } s || !season.TryGetProperty("episodes", out var eps) || eps.ValueKind != JsonValueKind.Array) continue;
                    foreach (var ep in eps.EnumerateArray())
                    {
                        if (ImportText.Int(Str(ep, "number")) is not { } n) continue;
                        var when = ImportText.Date(Str(ep, "watched_at"));
                        result.History.Add(new ImportWatch(item, s, n, when ?? WatchDates.Unknown, Approximate: when is null));
                        episodes++;
                    }
                }
            }
            if (episodes > 0)
            {
                if (rating is >= 1 and <= 10) result.Ratings.Add(new ImportRating(item, null, null, rating.Value, at ?? DateTimeOffset.UtcNow));
            }
            else Add(result, item, status, at, rating, LastEpisode(Str(e, "last_watched")));
            return true;
        }
    }
}
