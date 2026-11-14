using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// "Pick something for me": a random title from the watchlist and/or paused shows, within the filters. With
/// friends along ("who's watching"), it picks from everyone's watchlists instead, leaving out anything any of
/// you has watched (or started, for shows), and favoring titles that are on more than one of your lists.
/// </summary>
public class PickService(AppDbContext db, CatalogService catalog, ILogger<PickService> log)
{
    internal record Candidate(MediaType Type, int TmdbId, MediaSummary Media, int? Minutes, string Reason, int Weight = 1);

    public async Task<PickDto?> PickAsync(Guid userId, PickKind kind, PickSource source, int? maxMinutes, bool streamingOnly,
        string region, IReadOnlyCollection<string> exclude, CancellationToken ct, IReadOnlyList<(Guid Id, string Name)>? group = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var candidates = new List<Candidate>();

        if (group is { Count: > 0 })
            candidates = await GroupCandidatesAsync(userId, group, kind, today, ct);
        else if (source is PickSource.Watchlist or PickSource.Both)
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

        // Weighted shuffle (each key is u^(1/weight)): a title on three watchlists comes up three times as often as one on one.
        var shuffled = candidates.OrderByDescending(c => Math.Pow(Random.Shared.NextDouble(), 1.0 / c.Weight)).ToList();
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
            var pick = new PickDto(c.Media, c.Reason, c.Minutes, next, streaming, total, FitMinutes: c.Type == MediaType.Movie ? c.Minutes : null);
            if (c.Type == MediaType.Show && next is not null && maxMinutes is { } budget)
                pick = await FitAsync(userId, pick, budget, c.Minutes, ct);
            return pick;
        }
        return null;
    }

    /// <summary>
    /// Watching together: everything on any of your watchlists that none of you has seen (movies) or started (shows),
    /// weighted by how many of you want to watch it.
    /// </summary>
    internal async Task<List<Candidate>> GroupCandidatesAsync(Guid userId, IReadOnlyList<(Guid Id, string Name)> group, PickKind kind, DateOnly today, CancellationToken ct)
    {
        var names = group.Where(g => g.Id != userId).DistinctBy(g => g.Id).ToDictionary(g => g.Id, g => g.Name);
        var ids = names.Keys.Append(userId).ToList();
        var lists = await db.Watchlist.Where(w => ids.Contains(w.UserId) && w.Type != MediaType.Episode).ToListAsync(ct);
        var seenMovies = (await db.WatchEvents.Where(w => ids.Contains(w.UserId) && w.Type == MediaType.Movie).Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet();
        var startedShows = (await db.WatchEvents.Where(w => ids.Contains(w.UserId) && w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet();
        var wanted = lists
            .Where(w => w.Type == MediaType.Movie ? kind != PickKind.Show && !seenMovies.Contains(w.TmdbId) : kind != PickKind.Movie && !startedShows.Contains(w.TmdbId))
            .GroupBy(w => (w.Type, w.TmdbId))
            .ToDictionary(g => g.Key, g => g.Select(w => w.UserId).Distinct().ToList());

        string Reason(List<Guid> owners)
        {
            var who = owners.Where(o => o != userId).Select(o => names[o] + "'s").Order(StringComparer.OrdinalIgnoreCase).ToList();
            var mine = owners.Contains(userId);
            if (mine) who.Add("your");
            var joined = who.Count switch { 1 => who[0], 2 => $"{who[0]} and {who[1]}", _ => string.Join(", ", who[..^1]) + " and " + who[^1] };
            return owners.Count == ids.Count && ids.Count > 1 ? "On all your watchlists"
                : $"On {joined} watchlist{(who.Count > 1 ? "s" : "")}";
        }

        var candidates = new List<Candidate>();
        var movieIds = wanted.Keys.Where(k => k.Type == MediaType.Movie).Select(k => k.TmdbId).ToList();
        var showIds = wanted.Keys.Where(k => k.Type == MediaType.Show).Select(k => k.TmdbId).ToList();
        foreach (var m in await db.Movies.Where(m => movieIds.Contains(m.TmdbId) && m.ReleaseDate != null && m.ReleaseDate <= today).ToListAsync(ct))
        {
            var owners = wanted[(MediaType.Movie, m.TmdbId)];
            candidates.Add(new Candidate(MediaType.Movie, m.TmdbId, CatalogService.ToSummary(m), m.Runtime, Reason(owners), owners.Count));
        }
        foreach (var s in await db.Shows.Where(s => showIds.Contains(s.TmdbId) && s.FirstAirDate != null && s.FirstAirDate <= today).ToListAsync(ct))
        {
            var owners = wanted[(MediaType.Show, s.TmdbId)];
            candidates.Add(new Candidate(MediaType.Show, s.TmdbId, CatalogService.ToSummary(s), s.EpisodeRuntime, Reason(owners), owners.Count));
        }
        return candidates;
    }

    /// <summary>With a time budget: how many unwatched episodes, from the next one on, fit in it.</summary>
    private async Task<PickDto> FitAsync(Guid userId, PickDto pick, int budget, int? typical, CancellationToken ct)
    {
        var showId = pick.Media.TmdbId;
        var next = pick.NextEpisode!;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var watched = (await db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Episode && w.TmdbId == showId)
                .Select(w => new { w.SeasonNumber, w.EpisodeNumber }).ToListAsync(ct))
            .Select(w => (w.SeasonNumber ?? -1, w.EpisodeNumber ?? -1)).ToHashSet();
        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => e.ShowTmdbId == showId && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= today
                        && (e.SeasonNumber > next.SeasonNumber || (e.SeasonNumber == next.SeasonNumber && e.EpisodeNumber >= next.EpisodeNumber)))
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.Name, e.AirDate, e.Runtime }).Take(200).ToListAsync(ct);
        var (count, minutes) = Fit(episodes.Where(e => !watched.Contains((e.SeasonNumber, e.EpisodeNumber)))
            .Select(e => e.Runtime is > 0 ? e.Runtime.Value : typical ?? 0).ToList(), budget);
        if (count == 0) return pick;
        var lastEp = episodes.Where(e => !watched.Contains((e.SeasonNumber, e.EpisodeNumber))).ElementAt(count - 1);
        return pick with
        {
            FitEpisodes = count, FitMinutes = minutes,
            FitThrough = new EpisodeRef(lastEp.SeasonNumber, lastEp.EpisodeNumber, lastEp.Name, lastEp.AirDate),
        };
    }

    /// <summary>Episodes are watched in order, so it's how many from the start fit, not the best combination.</summary>
    public static (int Count, int Minutes) Fit(IReadOnlyList<int> runtimes, int budget)
    {
        int count = 0, total = 0;
        foreach (var r in runtimes)
        {
            if (r <= 0 || total + r > budget) break;
            total += r;
            count++;
        }
        return (count, total);
    }
}
