using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>Works out per-show progress from watch history and the cached episode lists. Specials (season 0) don't count.</summary>
public class ProgressCalculator(AppDbContext db)
{
    public async Task<List<ShowProgressDto>> ForUserAsync(Guid userId, IReadOnlyCollection<int>? showIds, CancellationToken ct)
    {
        var watchQuery = db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Episode);
        if (showIds is not null) watchQuery = watchQuery.Where(w => showIds.Contains(w.TmdbId));
        var watches = await watchQuery
            .Select(w => new { w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt })
            .ToListAsync(ct);
        if (watches.Count == 0) return [];

        var ids = watches.Select(w => w.TmdbId).Distinct().ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.TmdbId))
            .Select(s => new { s.TmdbId, s.Name, s.PosterPath, s.BackdropPath }).ToDictionaryAsync(s => s.TmdbId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Shows watched in another order (chronological...) can include specials.
        var orders = await WatchOrderService.SequencesAsync(db, userId, ids, ct);
        var ordered = orders.Keys.ToList();
        // Numbers and air dates only; names are fetched afterwards for just the "next up" episodes.
        var episodes = await db.Episodes.AsNoTracking()
            .Where(e => ids.Contains(e.ShowTmdbId) && (e.SeasonNumber > 0 || ordered.Contains(e.ShowTmdbId)) && e.AirDate != null && e.AirDate <= today)
            .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.AirDate })
            .ToListAsync(ct);
        var episodesByShow = episodes.ToLookup(e => e.ShowTmdbId);
        var states = await db.ShowStates.Where(x => x.UserId == userId && ids.Contains(x.ShowTmdbId))
            .ToDictionaryAsync(x => x.ShowTmdbId, x => x.Status, ct);
        var rewatches = await db.Rewatches.Where(x => x.UserId == userId && ids.Contains(x.ShowTmdbId)).ToListAsync(ct);
        var active = rewatches.Where(x => x.CompletedAt == null).GroupBy(x => x.ShowTmdbId).ToDictionary(g => g.Key, g => g.First());
        var completedRewatches = rewatches.Where(x => x.CompletedAt != null).GroupBy(x => x.ShowTmdbId).ToDictionary(g => g.Key, g => g.Count());

        var result = new List<ShowProgressDto>();
        foreach (var group in watches.GroupBy(w => w.TmdbId))
        {
            if (!shows.TryGetValue(group.Key, out var show)) continue;

            // During a rewatch only plays since it started count.
            var rewatch = active.GetValueOrDefault(group.Key);
            var order = orders.TryGetValue(group.Key, out var o) ? o : default;

            if (order.Sequence is { } sequence)
            {
                var airedSet = episodesByShow[group.Key].Select(e => (e.SeasonNumber, e.EpisodeNumber)).ToHashSet();
                var seen = group.Where(w => w.SeasonNumber is not null && (rewatch is null || w.WatchedAt >= rewatch.StartedAt))
                    .Select(w => (w.SeasonNumber!.Value, w.EpisodeNumber!.Value)).ToHashSet();
                var airedInOrder = sequence.Where(airedSet.Contains).ToList();
                var done = airedInOrder.Count(seen.Contains);
                var up = WatchOrderService.NextUp(airedInOrder, seen);
                result.Add(new ShowProgressDto(
                    show.TmdbId, show.Name, show.PosterPath, show.BackdropPath,
                    airedInOrder.Count, done, group.Max(w => w.WatchedAt),
                    up is { } u ? new EpisodeRef(u.Item1, u.Item2, null, null) : null,
                    airedInOrder.Count > 0 && done >= airedInOrder.Count,
                    states.GetValueOrDefault(group.Key, ShowTrackStatus.Watching),
                    rewatch?.Number, completedRewatches.GetValueOrDefault(group.Key), order.Name));
                continue;
            }

            var watched = group.Where(w => w.SeasonNumber > 0 && (rewatch is null || w.WatchedAt >= rewatch.StartedAt))
                .Select(w => (S: w.SeasonNumber!.Value, E: w.EpisodeNumber!.Value)).ToHashSet();
            var aired = episodesByShow[group.Key]
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .ToList();

            var watchedAired = aired.Count(e => watched.Contains((e.SeasonNumber, e.EpisodeNumber)));

            // Next up is the first unwatched aired episode after the furthest one watched;
            // if everything after that is watched, fall back to the earliest gap.
            var furthest = watched.Count == 0 ? (S: 0, E: 0) : watched.Max();
            var next = aired.FirstOrDefault(e => (e.SeasonNumber, e.EpisodeNumber).CompareTo(furthest) > 0 && !watched.Contains((e.SeasonNumber, e.EpisodeNumber)))
                       ?? aired.FirstOrDefault(e => !watched.Contains((e.SeasonNumber, e.EpisodeNumber)));

            result.Add(new ShowProgressDto(
                show.TmdbId, show.Name, show.PosterPath, show.BackdropPath,
                aired.Count, watchedAired,
                group.Max(w => w.WatchedAt),
                next is null ? null : new EpisodeRef(next.SeasonNumber, next.EpisodeNumber, null, next.AirDate),
                aired.Count > 0 && watchedAired >= aired.Count,
                states.GetValueOrDefault(group.Key, ShowTrackStatus.Watching),
                rewatch?.Number,
                completedRewatches.GetValueOrDefault(group.Key)));
        }

        // Names for the next episodes, in one query (seasons narrow it; the exact pairs are matched here).
        var nexts = result.Where(r => r.NextEpisode is not null).ToList();
        if (nexts.Count > 0)
        {
            var nextShows = nexts.Select(n => n.ShowTmdbId).ToList();
            var nextSeasons = nexts.Select(n => n.NextEpisode!.SeasonNumber).Distinct().ToList();
            var wanted = nexts.Select(n => (n.ShowTmdbId, n.NextEpisode!.SeasonNumber, n.NextEpisode.EpisodeNumber)).ToHashSet();
            var names = (await db.Episodes.AsNoTracking()
                    .Where(e => nextShows.Contains(e.ShowTmdbId) && nextSeasons.Contains(e.SeasonNumber))
                    .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Name, e.AirDate }).ToListAsync(ct))
                .Where(e => wanted.Contains((e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber)))
                .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber));
            for (var i = 0; i < result.Count; i++)
                if (result[i].NextEpisode is { } n && names.TryGetValue((result[i].ShowTmdbId, n.SeasonNumber, n.EpisodeNumber), out var ep))
                    result[i] = result[i] with { NextEpisode = n with { Name = ep.Name, AirDate = n.AirDate ?? ep.AirDate } };
        }

        // Newest aired episode per show, for "newest episode" sorting.
        if (result.Count > 0)
        {
            var asOf = DateOnly.FromDateTime(DateTime.UtcNow);
            var shown = result.Select(r => r.ShowTmdbId).ToList();
            var latest = await db.Episodes.AsNoTracking().Where(e => shown.Contains(e.ShowTmdbId) && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= asOf)
                .GroupBy(e => e.ShowTmdbId).Select(g => new { g.Key, Latest = g.Max(e => e.AirDate) }).ToDictionaryAsync(x => x.Key, x => x.Latest, ct);
            for (var i = 0; i < result.Count; i++)
                if (latest.TryGetValue(result[i].ShowTmdbId, out var d)) result[i] = result[i] with { LatestAired = d };
        }
        return result.OrderByDescending(p => p.LastWatchedAt).ToList();
    }
}
