using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>What the user watched on today's date in earlier years, and shows they finished on this date.</summary>
public class OnThisDayService(AppDbContext db)
{
    public async Task<List<OnThisDayItem>> GetAsync(Guid userId, string? tzId, CancellationToken ct)
    {
        var tz = ReviewService.Zone(tzId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
        // Pre-filter in SQL on the UTC date a day either side, then match the local date exactly.
        var days = new[] { today.AddDays(-1), today, today.AddDays(1) };
        int m0 = days[0].Month, d0 = days[0].Day, m1 = days[1].Month, d1 = days[1].Day, m2 = days[2].Month, d2 = days[2].Day;
        var unknown = WatchDates.Unknown;

        var plays = (await db.WatchEvents
                .Where(w => w.UserId == userId && w.WatchedAt != unknown && w.WatchedAt.Year < today.Year + 1)
                .Where(w => (w.WatchedAt.Month == m0 && w.WatchedAt.Day == d0) || (w.WatchedAt.Month == m1 && w.WatchedAt.Day == d1)
                         || (w.WatchedAt.Month == m2 && w.WatchedAt.Day == d2))
                .Select(w => new { w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt })
                .ToListAsync(ct))
            .Select(w => (w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, Date: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(w.WatchedAt, tz).DateTime)))
            .Where(w => w.Date.Month == today.Month && w.Date.Day == today.Day && w.Date.Year < today.Year)
            .ToList();

        var items = new List<OnThisDayItem>();
        var movieIds = plays.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct().ToList();
        var showIds = plays.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();
        var movies = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, ct);
        var shows = await db.Shows.Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);

        foreach (var p in plays.Where(p => p.Type == MediaType.Movie).DistinctBy(p => (p.TmdbId, p.Date.Year)))
            if (movies.TryGetValue(p.TmdbId, out var m))
                items.Add(new OnThisDayItem(today.Year - p.Date.Year, p.Date, MediaType.Movie, m.TmdbId, m.Title, m.PosterPath, "Watched"));

        foreach (var g in plays.Where(p => p.Type == MediaType.Episode).GroupBy(p => (p.TmdbId, p.Date.Year)))
        {
            if (!shows.TryGetValue(g.Key.TmdbId, out var s)) continue;
            var eps = g.Select(p => (S: p.SeasonNumber ?? 0, E: p.EpisodeNumber ?? 0)).Distinct().Order().ToList();
            var text = eps.Count == 1 ? $"Watched {Ep(eps[0])}"
                : eps.Select(e => e.S).Distinct().Count() == 1 ? $"Watched {eps.Count} episodes ({Ep(eps[0])}–E{eps[^1].E:00})"
                : $"Watched {eps.Count} episodes";
            items.Add(new OnThisDayItem(today.Year - g.Key.Year, g.First().Date, MediaType.Show, s.TmdbId, s.Name, s.PosterPath, text));
        }

        items.AddRange(await FinishedOnThisDayAsync(userId, tz, today, ct));

        // Finishing a show beats watching some of it on the same day.
        return items.OrderBy(i => i.YearsAgo).ThenBy(i => i.Text.StartsWith("Finished") ? 0 : 1)
            .DistinctBy(i => (i.Type, i.TmdbId, i.YearsAgo)).Take(12).ToList();
    }

    /// <summary>Ended shows whose final episode the user first watched on this date in an earlier year.</summary>
    private async Task<List<OnThisDayItem>> FinishedOnThisDayAsync(Guid userId, TimeZoneInfo tz, DateOnly today, CancellationToken ct)
    {
        var watchedShows = db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct();
        var ended = await db.Shows.Where(s => watchedShows.Contains(s.TmdbId) && (s.Status == "Ended" || s.Status == "Canceled"))
            .Select(s => new { s.TmdbId, s.Name, s.PosterPath }).ToListAsync(ct);
        if (ended.Count == 0) return [];
        var endedIds = ended.Select(s => s.TmdbId).ToList();

        var finales = (await db.Episodes.Where(e => endedIds.Contains(e.ShowTmdbId) && e.SeasonNumber > 0)
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber }).ToListAsync(ct))
            .GroupBy(e => e.ShowTmdbId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.SeasonNumber).ThenByDescending(e => e.EpisodeNumber).First());
        var unknown = WatchDates.Unknown;
        var finaleWatches = await db.WatchEvents
            .Where(w => w.UserId == userId && w.Type == MediaType.Episode && endedIds.Contains(w.TmdbId) && w.WatchedAt != unknown)
            .Select(w => new { w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt }).ToListAsync(ct);

        var result = new List<OnThisDayItem>();
        foreach (var s in ended)
        {
            if (!finales.TryGetValue(s.TmdbId, out var f)) continue;
            var first = finaleWatches.Where(w => w.TmdbId == s.TmdbId && w.SeasonNumber == f.SeasonNumber && w.EpisodeNumber == f.EpisodeNumber)
                .Select(w => (DateTimeOffset?)w.WatchedAt).Min();
            if (first is null) continue;
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(first.Value, tz).DateTime);
            if (date.Month == today.Month && date.Day == today.Day && date.Year < today.Year)
                result.Add(new OnThisDayItem(today.Year - date.Year, date, MediaType.Show, s.TmdbId, s.Name, s.PosterPath, "Finished the show"));
        }
        return result;
    }

    private static string Ep((int S, int E) e) => $"S{e.S:00}E{e.E:00}";
}
