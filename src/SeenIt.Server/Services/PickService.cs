using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>"Pick something for me": a random title from the watchlist and/or paused shows, within the filters.</summary>
public class PickService(AppDbContext db, CatalogService catalog, ILogger<PickService> log)
{
    private record Candidate(MediaType Type, int TmdbId, MediaSummary Media, int? Minutes, string Reason);

    public async Task<PickDto?> PickAsync(Guid userId, PickKind kind, PickSource source, int? maxMinutes, bool streamingOnly,
        string region, IReadOnlyCollection<string> exclude, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var candidates = new List<Candidate>();

        if (source is PickSource.Watchlist or PickSource.Both)
        {
            var list = await db.Watchlist.Where(w => w.UserId == userId).ToListAsync(ct);
            if (kind != PickKind.Show)
            {
                var ids = list.Where(w => w.Type == MediaType.Movie).ToDictionary(w => w.TmdbId, w => w.AddedAt);
                var movies = await db.Movies.Where(m => ids.Keys.Contains(m.TmdbId) && m.ReleaseDate != null && m.ReleaseDate <= today).ToListAsync(ct);
                candidates.AddRange(movies.Select(m => new Candidate(MediaType.Movie, m.TmdbId, CatalogService.ToSummary(m), m.Runtime,
                    $"On your watchlist since {ids[m.TmdbId]:MMMM yyyy}")));
            }
            if (kind != PickKind.Movie)
            {
                var ids = list.Where(w => w.Type == MediaType.Show).ToDictionary(w => w.TmdbId, w => w.AddedAt);
                var shows = await db.Shows.Where(s => ids.Keys.Contains(s.TmdbId) && s.FirstAirDate != null && s.FirstAirDate <= today).ToListAsync(ct);
                candidates.AddRange(shows.Select(s => new Candidate(MediaType.Show, s.TmdbId, CatalogService.ToSummary(s), s.EpisodeRuntime,
                    $"On your watchlist since {ids[s.TmdbId]:MMMM yyyy}")));
            }
        }
        if (source is PickSource.Paused or PickSource.Both && kind != PickKind.Movie)
        {
            var paused = await db.ShowStates.Where(x => x.UserId == userId && x.Status == ShowTrackStatus.Paused)
                .Join(db.Shows, x => x.ShowTmdbId, s => s.TmdbId, (x, s) => new { s, x.UpdatedAt }).ToListAsync(ct);
            // Only paused shows with something left to watch.
            var unfinished = (await new ProgressCalculator(db).ForUserAsync(userId, paused.Select(p => p.s.TmdbId).ToList(), ct))
                .Where(p => p.NextEpisode is not null).Select(p => p.ShowTmdbId).ToHashSet();
            candidates.AddRange(paused.Where(p => unfinished.Contains(p.s.TmdbId)).Select(p => new Candidate(MediaType.Show, p.s.TmdbId, CatalogService.ToSummary(p.s), p.s.EpisodeRuntime,
                $"You paused this in {p.UpdatedAt:MMMM yyyy}")));
        }

        // TMDB often leaves a show's episode length empty; use the average of its cached episodes instead.
        var noLength = candidates.Where(c => c.Type == MediaType.Show && c.Minutes is null).Select(c => c.TmdbId).ToList();
        if (noLength.Count > 0)
        {
            var avg = await db.Episodes.Where(e => noLength.Contains(e.ShowTmdbId) && e.SeasonNumber > 0 && e.Runtime > 0)
                .GroupBy(e => e.ShowTmdbId).Select(g => new { g.Key, Avg = g.Average(e => e.Runtime!.Value) })
                .ToDictionaryAsync(x => x.Key, x => (int)Math.Round(x.Avg), ct);
            candidates = candidates.Select(c => c.Minutes is null && avg.TryGetValue(c.TmdbId, out var m) && c.Type == MediaType.Show ? c with { Minutes = m } : c).ToList();
        }

        if (maxMinutes is { } max) candidates = candidates.Where(c => c.Minutes is { } m && m <= max).ToList();
        var total = candidates.Count;
        // "Pick again" skips what was just shown, unless that's all there is.
        var fresh = candidates.Where(c => !exclude.Contains($"{c.Type}:{c.TmdbId}")).ToList();
        if (fresh.Count > 0) candidates = fresh;
        if (candidates.Count == 0) return null;

        var shuffled = candidates.OrderBy(_ => Random.Shared.Next()).ToList();
        foreach (var c in shuffled.Take(streamingOnly ? 30 : 1))
        {
            List<ProviderDto> streaming = [];
            try
            {
                var p = await catalog.GetProvidersAsync(c.Type, c.TmdbId, region, ct);
                streaming = p.Stream.Concat(p.Free).Concat(p.Ads).DistinctBy(x => x.Id).ToList();
            }
            catch (HttpRequestException ex) { log.LogInformation(ex, "No providers for {Type} {Id}", c.Type, c.TmdbId); }
            if (streamingOnly && streaming.Count == 0) continue;

            EpisodeRef? next = null;
            if (c.Type == MediaType.Show)
            {
                next = (await new ProgressCalculator(db).ForUserAsync(userId, [c.TmdbId], ct)).FirstOrDefault()?.NextEpisode;
                next ??= await db.Episodes.Where(e => e.ShowTmdbId == c.TmdbId && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= today)
                    .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                    .Select(e => new EpisodeRef(e.SeasonNumber, e.EpisodeNumber, e.Name, e.AirDate, false)).FirstOrDefaultAsync(ct);
            }
            return new PickDto(c.Media, c.Reason, c.Minutes, next, streaming, total);
        }
        return null;
    }
}
