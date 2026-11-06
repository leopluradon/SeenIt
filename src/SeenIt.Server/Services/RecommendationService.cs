using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>
/// "For you": TMDB's recommendations for what you rated highly and what you've watched lately, scored so
/// titles several of your favorites point to come first, leaving out anything you've seen, rated,
/// watchlisted or marked not interested.
/// </summary>
public class RecommendationService(AppDbContext db, TmdbClient tmdb, TrackingService tracking, UserDataCache cache)
{
    private sealed record Seed(MediaType Type, int TmdbId, double Weight);

    public async Task<ForYouDto> ForYouAsync(Guid userId, CancellationToken ct)
    {
        // The cache is keyed on history and ratings; watchlist and "not interested" changes are folded into the key too.
        var wl = await db.Watchlist.CountAsync(w => w.UserId == userId, ct);
        var ni = await db.NotInterested.CountAsync(n => n.UserId == userId, ct);
        return await cache.GetAsync(userId, $"foryou:{wl}:{ni}", () => BuildAsync(userId, ct), ct);
    }

    private async Task<ForYouDto> BuildAsync(Guid userId, CancellationToken ct)
    {
        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => r.UserId == userId && r.SeasonNumber == null && (r.Type == MediaType.Movie || r.Type == MediaType.Show))
            .Select(r => new { r.Type, r.TmdbId, r.Value, r.RatedAt }).ToListAsync(ct);
        var rated = ratings.ToDictionary(r => (r.Type, r.TmdbId), r => r.Value);

        // Seeds: what you loved (8+), then what you've watched in the last two months (unless you rated it low).
        var seeds = ratings.Where(r => r.Value >= 8).OrderByDescending(r => r.Value).ThenByDescending(r => r.RatedAt).Take(16)
            .Select(r => new Seed(r.Type, r.TmdbId, r.Value - 6)).ToList();
        var since = DateTimeOffset.UtcNow.AddDays(-60);
        var recent = await db.WatchEvents.AsNoTracking().Where(w => w.UserId == userId && w.WatchedAt > since)
            .GroupBy(w => new { Type = w.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, w.TmdbId })
            .Select(g => new { g.Key.Type, g.Key.TmdbId, Plays = g.Count(), Last = g.Max(w => w.WatchedAt) })
            .OrderByDescending(g => g.Last).Take(30).ToListAsync(ct);
        foreach (var r in recent.Where(r => r.Type == MediaType.Movie || r.Plays >= 3).Take(10))
        {
            if (seeds.Any(s => s.Type == r.Type && s.TmdbId == r.TmdbId)) continue;
            if (rated.TryGetValue((r.Type, r.TmdbId), out var v) && v < 6) continue;
            seeds.Add(new Seed(r.Type, r.TmdbId, 1.5));
        }
        if (seeds.Count == 0) return new ForYouDto([], []);

        // Everything you already know about is left out.
        var seenMovies = await db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Movie).Select(w => w.TmdbId).Distinct().ToListAsync(ct);
        var seenShows = await db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct().ToListAsync(ct);
        var watchlist = await db.Watchlist.Where(w => w.UserId == userId).Select(w => new { w.Type, w.TmdbId }).ToListAsync(ct);
        var hidden = await db.NotInterested.Where(n => n.UserId == userId).Select(n => new { n.Type, n.TmdbId }).ToListAsync(ct);
        var known = seenMovies.Select(id => (MediaType.Movie, id)).Concat(seenShows.Select(id => (MediaType.Show, id)))
            .Concat(watchlist.Select(w => (w.Type, w.TmdbId))).Concat(hidden.Select(h => (h.Type, h.TmdbId)))
            .Concat(rated.Keys).ToHashSet();

        // TMDB's lists for each seed, a few at a time.
        using var gate = new SemaphoreSlim(6);
        var lists = await Task.WhenAll(seeds.Select(async s =>
        {
            await gate.WaitAsync(ct);
            try { return (Seed: s, Page: await tmdb.GetRecommendationsAsync(s.Type == MediaType.Movie ? "movie" : "tv", s.TmdbId, ct)); }
            catch (HttpRequestException) { return (Seed: s, Page: (TmdbPage<TmdbListItem>?)null); }
            finally { gate.Release(); }
        }));

        var scores = new Dictionary<(MediaType, int), (double Score, MediaSummary Media, Seed Best, double BestPart)>();
        var perSeed = new Dictionary<Seed, List<MediaSummary>>();
        foreach (var (seed, page) in lists)
        {
            var items = (page?.Results ?? []).Select(r => CatalogService.ToSummary(r, seed.Type)).ToList();
            perSeed[seed] = items.Where(m => !known.Contains((m.Type, m.TmdbId))).ToList();
            for (var i = 0; i < items.Count; i++)
            {
                var m = items[i];
                if (known.Contains((m.Type, m.TmdbId)) || m.PosterPath is null) continue;
                var part = seed.Weight * (1 - i / 25.0) * (0.6 + Math.Min(m.VoteAverage ?? 6, 9) / 22.5);
                scores[(m.Type, m.TmdbId)] = scores.TryGetValue((m.Type, m.TmdbId), out var cur)
                    ? (cur.Score + part, cur.Media, part > cur.BestPart ? seed : cur.Best, Math.Max(part, cur.BestPart))
                    : (part, m, seed, part);
            }
        }

        var seedMeta = await tracking.LoadMetaAsync(seeds.Select(s => (s.Type, s.TmdbId)), ct);
        MediaSummary? SeedSummary(Seed s) => s.Type == MediaType.Movie
            ? seedMeta.Movies.TryGetValue(s.TmdbId, out var m) ? CatalogService.ToSummary(m) : null
            : seedMeta.Shows.TryGetValue(s.TmdbId, out var sh) ? CatalogService.ToSummary(sh) : null;

        var top = scores.Values.OrderByDescending(x => x.Score).Take(40)
            .Select(x => new ForYouItem(x.Media, SeedSummary(x.Best)?.Title ?? "your favorites", x.Best.Type, x.Best.TmdbId)).ToList();
        var topIds = top.Take(12).Select(t => (t.Media.Type, t.Media.TmdbId)).ToHashSet();
        var groups = seeds.OrderByDescending(s => s.Weight).Where(s => perSeed.GetValueOrDefault(s)?.Count >= 4)
            .Select(s => (Seed: SeedSummary(s), Items: perSeed[s].Where(m => m.PosterPath is not null && !topIds.Contains((m.Type, m.TmdbId))).Take(12).ToList()))
            .Where(g => g.Seed is not null && g.Items.Count >= 4).Take(5)
            .Select(g => new ForYouGroup(g.Seed!, g.Items)).ToList();
        return new ForYouDto(top, groups);
    }
}
