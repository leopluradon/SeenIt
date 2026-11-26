using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Yearly challenges (Challenges.All), filled in from what you watched in the year, counted like goals (your time
/// zone, from your tracking start). Every challenge is worked out, so Goals can show progress on ones not taken on yet.
/// </summary>
public class ChallengeService(AppDbContext db, TrackingService tracking, ReviewService reviews)
{
    public async Task<List<ChallengeDto>> GetAsync(Guid userId, int year, string? tzId, CancellationToken ct)
    {
        var tz = ReviewService.Zone(await tracking.ZoneIdAsync(userId, tzId, ct));
        var since = await tracking.TrackingSinceAsync(userId, ct);
        var start = new DateTime(year, 1, 1);
        var end = start.AddYears(1);
        var plays = (await reviews.LoadPlaysAsync(userId, tz, null, ct))
            .Where(p => p.Local >= start && p.Local < end && (since is null || DateOnly.FromDateTime(p.Local) >= since))
            .OrderBy(p => p.Local).ToList();

        // Movies in the order first watched this year; shows likewise.
        var movieIds = plays.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct().ToList();
        var showIds = plays.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();
        var metas = await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, ct);
        var showMetas = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var movies = movieIds.Where(metas.ContainsKey).Select(id => metas[id]).ToList();
        var shows = showIds.Where(showMetas.ContainsKey).Select(id => showMetas[id]).ToList();
        var seasons = await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId) && e.SeasonNumber > 0)
            .GroupBy(e => e.ShowTmdbId).Select(g => new { g.Key, Max = g.Max(e => e.SeasonNumber) }).ToDictionaryAsync(x => x.Key, x => x.Max, ct);

        static MediaSummary M(MovieMeta m) => new(MediaType.Movie, m.TmdbId, m.Title, m.ReleaseDate?.Year, m.PosterPath, null, m.VoteAverage, null);
        static MediaSummary S(ShowMeta s) => new(MediaType.Show, s.TmdbId, s.Name, s.FirstAirDate?.Year, s.PosterPath, null, s.VoteAverage, null);
        // One slot per distinct key, filled by the first title (in watch order) that has it.
        static List<ChallengeSlot> Distinct<T>(IEnumerable<T> items, Func<T, IEnumerable<string>> keys, Func<string, string> label, Func<T, MediaSummary> media)
        {
            var slots = new Dictionary<string, ChallengeSlot>();
            foreach (var i in items)
                foreach (var k in keys(i))
                    slots.TryAdd(k, new ChallengeSlot(label(k), media(i)));
            return [.. slots.Values];
        }
        static List<ChallengeSlot> Each<T>(IEnumerable<T> items, Func<T, MediaSummary> media) =>
            items.Select(i => media(i)).Select(m => new ChallengeSlot(m.Title, m)).ToList();

        var result = new List<ChallengeDto>();
        foreach (var c in Challenges.All)
        {
            var slots = c.Key switch
            {
                // Every decade gets a slot, filled or not, so you can see which are missing.
                "decades" => Enumerable.Range(0, 11).Select(i => 1920 + i * 10).Select(d =>
                    new ChallengeSlot($"{d}s", movies.FirstOrDefault(m => m.ReleaseDate?.Year / 10 * 10 == d) is { } m ? M(m) : null)).ToList(),
                "countries" => Distinct(movies, m => m.Countries, Country, M),
                "languages" => Distinct(movies, m => m.OriginalLanguage is { Length: > 0 } l && l != "en" ? [l] : [], Language, M),
                "silent" => Each(movies.Where(m => m.ReleaseDate is { Year: < 1930 }), M),
                "classics" => Each(movies.Where(m => m.ReleaseDate is { Year: < 1970 }), M),
                "epics" => Each(movies.Where(m => m.Runtime >= 180), M),
                "genres" => Distinct(movies, m => m.Genres, g => g, M),
                "docs" => Each(movies.Where(m => m.Genres.Contains("Documentary")), M),
                "animation" => Each(movies.Where(m => m.Genres.Contains("Animation")), M),
                "fresh" => Each(movies.Where(m => m.ReleaseDate?.Year == year), M),
                "longrunner" => Each(shows.Where(s => seasons.GetValueOrDefault(s.TmdbId) >= 10), S),
                "worldtv" => Distinct(shows, s => s.Countries, Country, S),
                _ => [],
            };
            var count = slots.Count(s => s.Item is not null);
            // A plain count only lists as many as it needs (and a few over), not every documentary of the year.
            if (c.Key is not "decades") slots = slots.Take(Math.Max(c.Target, 1) + 3).ToList();
            result.Add(new ChallengeDto(c.Key, c.Title, c.Description, c.Target, count, slots));
        }
        return result;
    }

    private static string Country(string code)
    {
        try { return new RegionInfo(code).EnglishName; } catch (ArgumentException) { return code; }
    }

    private static string Language(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).EnglishName; } catch (CultureNotFoundException) { return code; }
    }
}
