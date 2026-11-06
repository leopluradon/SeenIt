using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>Works out a smart list's entries from its rules, for the list's owner.</summary>
public class SmartListService(AppDbContext db, TmdbClient tmdb, TrackingService tracking)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public const int MaxLimit = 200;

    /// <summary>TMDB names some show genres differently; picking either side matches both.</summary>
    private static readonly Dictionary<string, string[]> Equivalent = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Science Fiction"] = ["Sci-Fi & Fantasy"], ["Fantasy"] = ["Sci-Fi & Fantasy"], ["Sci-Fi & Fantasy"] = ["Science Fiction", "Fantasy"],
        ["Action"] = ["Action & Adventure"], ["Adventure"] = ["Action & Adventure"], ["Action & Adventure"] = ["Action", "Adventure"],
        ["War"] = ["War & Politics"], ["War & Politics"] = ["War"],
    };

    public static string Serialize(SmartRules r) => JsonSerializer.Serialize(Clean(r), Json);
    public static SmartRules? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SmartRules>(json, Json); } catch (JsonException) { return null; }
    }

    public static SmartRules Clean(SmartRules r) => r with
    {
        Genres = r.Genres?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
        Language = string.IsNullOrWhiteSpace(r.Language) ? null : r.Language.Trim().ToLowerInvariant()[..Math.Min(3, r.Language.Trim().Length)],
        Limit = Math.Clamp(r.Limit, 1, MaxLimit),
        MinScore = r.MinScore is { } s ? Math.Clamp(s, 0, 10) : null,
        MinMyRating = r.MinMyRating is { } m ? Math.Clamp(m, 1, 10) : null,
    };

    /// <summary>Genre names to offer in the editor: TMDB's movie and show genres together.</summary>
    public async Task<List<string>> GenresAsync(CancellationToken ct)
    {
        var movie = (await tmdb.GetGenresAsync("movie", ct))?.Genres ?? [];
        var tv = (await tmdb.GetGenresAsync("tv", ct))?.Genres ?? [];
        return movie.Concat(tv).Select(g => g.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<MediaSummary>> EvaluateAsync(Guid ownerId, SmartRules rules, CancellationToken ct)
    {
        rules = Clean(rules);
        var genres = rules.Genres is { Count: > 0 } gs
            ? gs.SelectMany(g => Equivalent.TryGetValue(g, out var eq) ? eq.Append(g) : [g]).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var myRatings = await db.Ratings.AsNoTracking().Where(r => r.UserId == ownerId && r.SeasonNumber == null && r.Type != MediaType.Episode)
            .Select(r => new { r.Type, r.TmdbId, r.Value }).ToListAsync(ct);
        var rated = myRatings.ToDictionary(r => (r.Type, r.TmdbId), r => r.Value);

        return rules.Source == SmartSource.Tmdb
            ? await FromTmdbAsync(ownerId, rules, genres, rated, ct)
            : await FromLibraryAsync(ownerId, rules, genres, rated, ct);
    }

    private sealed record Row(MediaSummary Media, IReadOnlyCollection<string> Genres, string? Language, DateTimeOffset? LastWatched, int Plays, DateTimeOffset? Added);

    private async Task<List<MediaSummary>> FromLibraryAsync(Guid ownerId, SmartRules rules, HashSet<string>? genres,
        Dictionary<(MediaType, int), double> rated, CancellationToken ct)
    {
        var plays = await db.WatchEvents.AsNoTracking().Where(w => w.UserId == ownerId)
            .GroupBy(w => new { Type = w.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, w.TmdbId })
            .Select(g => new { g.Key.Type, g.Key.TmdbId, Plays = g.Count(), Last = g.Max(w => w.WatchedAt) }).ToListAsync(ct);
        var played = plays.ToDictionary(p => (p.Type, p.TmdbId));

        List<(MediaType Type, int Id, DateTimeOffset? Added)> keys;
        if (rules.Source == SmartSource.History)
            keys = plays.Select(p => (p.Type, p.TmdbId, (DateTimeOffset?)null)).ToList();
        else
            keys = (await db.Watchlist.AsNoTracking().Where(w => w.UserId == ownerId).Select(w => new { w.Type, w.TmdbId, w.AddedAt }).ToListAsync(ct))
                .Select(w => (w.Type, w.TmdbId, (DateTimeOffset?)w.AddedAt)).ToList();
        if (rules.Type is { } only) keys = keys.Where(k => k.Type == only).ToList();

        var (movies, shows) = await tracking.LoadMetaAsync(keys.Select(k => (k.Type, k.Id)), ct);
        var rows = new List<Row>();
        foreach (var (type, id, added) in keys)
        {
            var p = played.GetValueOrDefault((type, id));
            if (type == MediaType.Movie && movies.TryGetValue(id, out var m))
                rows.Add(new Row(CatalogService.ToSummary(m), m.Genres, m.OriginalLanguage, p?.Last, p?.Plays ?? 0, added));
            else if (type == MediaType.Show && shows.TryGetValue(id, out var s))
                rows.Add(new Row(CatalogService.ToSummary(s), s.Genres, s.OriginalLanguage, p?.Last, p?.Plays ?? 0, added));
        }

        IEnumerable<Row> q = rows.Where(r => Matches(r.Media, r.Genres, r.Language, rules, genres, rated));
        if (rules.Watched is { } w) q = q.Where(r => (r.Plays > 0) == w);
        q = rules.Sort switch
        {
            SmartSort.Score => q.OrderByDescending(r => r.Media.VoteAverage ?? 0),
            SmartSort.Newest => q.OrderByDescending(r => r.Media.Year ?? 0).ThenBy(r => r.Media.Title),
            SmartSort.Oldest => q.OrderBy(r => r.Media.Year ?? int.MaxValue).ThenBy(r => r.Media.Title),
            SmartSort.RecentlyWatched => q.OrderByDescending(r => r.LastWatched ?? DateTimeOffset.MinValue),
            SmartSort.Title => q.OrderBy(r => r.Media.Title, StringComparer.OrdinalIgnoreCase),
            // "Popular" in your own library: most played, then most recently added to the watchlist.
            _ => q.OrderByDescending(r => r.Plays).ThenByDescending(r => r.Added ?? r.LastWatched ?? DateTimeOffset.MinValue),
        };
        return q.Take(rules.Limit).Select(r => r.Media).ToList();
    }

    private static bool Matches(MediaSummary m, IReadOnlyCollection<string> titleGenres, string? language, SmartRules rules,
        HashSet<string>? genres, Dictionary<(MediaType, int), double> rated)
    {
        if (genres is not null && !titleGenres.Any(genres.Contains)) return false;
        if (rules.YearFrom is { } from && (m.Year is not { } y1 || y1 < from)) return false;
        if (rules.YearTo is { } to && (m.Year is not { } y2 || y2 > to)) return false;
        if (rules.MinScore is { } min && (m.VoteAverage ?? 0) < min) return false;
        if (rules.MinMyRating is { } mine && (!rated.TryGetValue((m.Type, m.TmdbId), out var r) || r < mine)) return false;
        if (rules.Language is { } lang && !string.Equals(language, lang, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private async Task<List<MediaSummary>> FromTmdbAsync(Guid ownerId, SmartRules rules, HashSet<string>? genres,
        Dictionary<(MediaType, int), double> rated, CancellationToken ct)
    {
        var seen = (await db.WatchEvents.AsNoTracking().Where(w => w.UserId == ownerId)
                .Select(w => new { Type = w.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, w.TmdbId }).Distinct().ToListAsync(ct))
            .Select(x => (x.Type, x.TmdbId)).ToHashSet();

        var kinds = rules.Type switch { MediaType.Movie => new[] { MediaType.Movie }, MediaType.Show => [MediaType.Show], _ => [MediaType.Movie, MediaType.Show] };
        var results = new List<MediaSummary>();
        foreach (var type in kinds)
        {
            var kind = type == MediaType.Movie ? "movie" : "tv";
            var query = new List<string>();
            if (genres is not null)
            {
                var all = (await tmdb.GetGenresAsync(kind, ct))?.Genres ?? [];
                var ids = all.Where(g => genres.Contains(g.Name)).Select(g => g.Id).ToList();
                if (ids.Count == 0) continue;   // none of these genres exist for this kind
                query.Add("with_genres=" + string.Join("|", ids));
            }
            var dateField = type == MediaType.Movie ? "primary_release_date" : "first_air_date";
            if (rules.YearFrom is { } from) query.Add($"{dateField}.gte={from}-01-01");
            if (rules.YearTo is { } to) query.Add($"{dateField}.lte={to}-12-31");
            if (rules.MinScore is { } min) query.Add($"vote_average.gte={min.ToString(CultureInfo.InvariantCulture)}");
            query.Add($"vote_count.gte={(rules.Sort == SmartSort.Score ? 300 : 50)}");   // scores from a handful of votes aren't worth much
            if (rules.Language is { } lang) query.Add($"with_original_language={Uri.EscapeDataString(lang)}");
            query.Add("sort_by=" + rules.Sort switch
            {
                SmartSort.Score => "vote_average.desc",
                SmartSort.Newest => $"{dateField}.desc",
                SmartSort.Oldest => $"{dateField}.asc",
                SmartSort.Title => type == MediaType.Movie ? "title.asc" : "name.asc",
                _ => "popularity.desc",
            });
            if (rules.Sort == SmartSort.Newest) query.Add($"{dateField}.lte={DateTime.UtcNow:yyyy-MM-dd}");

            for (var page = 1; page <= 10 && results.Count(r => r.Type == type) < rules.Limit; page++)
            {
                var res = await tmdb.DiscoverAsync(kind, string.Join("&", query), page, ct);
                if (res is null || res.Results.Count == 0) break;
                foreach (var item in res.Results.Select(r => CatalogService.ToSummary(r, type)))
                {
                    var isSeen = seen.Contains((item.Type, item.TmdbId));
                    if (rules.Watched == false && isSeen) continue;
                    if (rules.Watched == true && !isSeen) continue;
                    if (rules.MinMyRating is { } mine && (!rated.TryGetValue((item.Type, item.TmdbId), out var r) || r < mine)) continue;
                    results.Add(item);
                }
                if (page >= res.TotalPages) break;
            }
        }
        // Movies and shows together: interleave by the same order TMDB used for each.
        IEnumerable<MediaSummary> merged = kinds.Length == 1 ? results
            : results.Where(r => r.Type == MediaType.Movie).Select((m, i) => (m, i))
                .Concat(results.Where(r => r.Type == MediaType.Show).Select((m, i) => (m, i)))
                .OrderBy(x => x.i).ThenBy(x => x.m.Type).Select(x => x.m);
        if (kinds.Length > 1 && rules.Sort is SmartSort.Score) merged = merged.OrderByDescending(m => m.VoteAverage ?? 0);
        if (kinds.Length > 1 && rules.Sort is SmartSort.Title) merged = merged.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase);
        return merged.DistinctBy(m => (m.Type, m.TmdbId)).Take(rules.Limit).ToList();
    }
}
