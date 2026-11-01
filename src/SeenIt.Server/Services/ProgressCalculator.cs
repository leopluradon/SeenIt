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
        var shows = await db.Shows.Where(s => ids.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var episodes = await db.Episodes
            .Where(e => ids.Contains(e.ShowTmdbId) && e.SeasonNumber > 0)
            .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Name, e.AirDate })
            .ToListAsync(ct);
        var episodesByShow = episodes.ToLookup(e => e.ShowTmdbId);

        var result = new List<ShowProgressDto>();
        foreach (var group in watches.GroupBy(w => w.TmdbId))
        {
            if (!shows.TryGetValue(group.Key, out var show)) continue;

            var watched = group.Where(w => w.SeasonNumber > 0)
                .Select(w => (S: w.SeasonNumber!.Value, E: w.EpisodeNumber!.Value)).ToHashSet();
            var aired = episodesByShow[group.Key]
                .Where(e => e.AirDate is { } d && d <= today)
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
                next is null ? null : new EpisodeRef(next.SeasonNumber, next.EpisodeNumber, next.Name, next.AirDate),
                aired.Count > 0 && watchedAired >= aired.Count));
        }

        return result.OrderByDescending(p => p.LastWatchedAt).ToList();
    }
}
