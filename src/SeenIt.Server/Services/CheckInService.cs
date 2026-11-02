using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>"Watching now": manual check-ins (recorded as a play when they finish) and live Jellyfin playback.</summary>
public class CheckInService(AppDbContext db, CatalogService catalog, TrackingService tracking, ActivityService activity)
{
    /// <summary>Jellyfin sessions that never sent a stop are dropped this long after they should have ended.</summary>
    private static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(30);

    public async Task<NowWatchingDto?> GetAsync(Guid userId, CancellationToken ct)
    {
        var c = await db.CheckIns.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return c is null || !IsLive(c) ? null : (await ToDtosAsync([c], ct)).FirstOrDefault();
    }

    public static bool IsLive(CheckIn c) =>
        c.Source == WatchSource.Manual ? c.EndsAt > DateTimeOffset.UtcNow : c.EndsAt + StaleGrace > DateTimeOffset.UtcNow;

    /// <summary>Starts (or replaces) the user's check-in. endsAt defaults to start + runtime.</summary>
    public async Task<NowWatchingDto> StartAsync(Guid userId, CheckInRequest req, WatchSource source, DateTimeOffset? endsAt, CancellationToken ct)
    {
        int runtime;
        switch (req.Type)
        {
            case MediaType.Movie:
                var movie = await catalog.EnsureMovieAsync(req.TmdbId, ct) ?? throw new TrackingException("Movie not found.");
                runtime = movie.Runtime is > 0 ? movie.Runtime.Value : 120;
                break;
            case MediaType.Episode when req.SeasonNumber is { } s && req.EpisodeNumber is { } e:
                var show = await catalog.EnsureShowAsync(req.TmdbId, ct) ?? throw new TrackingException("Show not found.");
                var ep = await db.Episodes.AsNoTracking().FirstOrDefaultAsync(x => x.ShowTmdbId == req.TmdbId && x.SeasonNumber == s && x.EpisodeNumber == e, ct)
                         ?? throw new TrackingException($"S{s:00}E{e:00} isn't an episode of this show.");
                runtime = ep.Runtime is > 0 ? ep.Runtime.Value : show.EpisodeRuntime is > 0 ? show.EpisodeRuntime.Value : 45;
                break;
            default:
                throw new TrackingException("Check in to a movie or an episode.");
        }

        var now = DateTimeOffset.UtcNow;
        var row = await db.CheckIns.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (row is null) db.CheckIns.Add(row = new CheckIn { UserId = userId });
        var same = row.Type == req.Type && row.TmdbId == req.TmdbId && row.SeasonNumber == req.SeasonNumber && row.EpisodeNumber == req.EpisodeNumber && IsLive(row);
        row.Type = req.Type;
        row.TmdbId = req.TmdbId;
        row.SeasonNumber = req.Type == MediaType.Episode ? req.SeasonNumber : null;
        row.EpisodeNumber = req.Type == MediaType.Episode ? req.EpisodeNumber : null;
        if (!same) row.StartedAt = now;
        row.EndsAt = endsAt ?? now.AddMinutes(runtime);
        row.Source = source;
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([row], ct))[0];
    }

    public async Task<bool> CancelAsync(Guid userId, CancellationToken ct, WatchSource? onlySource = null)
    {
        var q = db.CheckIns.Where(x => x.UserId == userId);
        if (onlySource is { } src) q = q.Where(x => x.Source == src);
        return await q.ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Records finished manual check-ins as plays and clears abandoned Jellyfin ones.</summary>
    public async Task<int> CompleteDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await db.CheckIns.Where(x => x.Source == WatchSource.Manual && x.EndsAt <= now).ToListAsync(ct);
        foreach (var c in due)
        {
            db.CheckIns.Remove(c);
            await db.SaveChangesAsync(ct);
            try
            {
                await tracking.WatchAsync(c.UserId, new WatchRequest(c.Type, c.TmdbId, c.SeasonNumber, c.EpisodeNumber, c.EndsAt, WatchTime.Custom), WatchSource.Manual, ct);
                await activity.LogAsync(c.UserId, ActivityKind.Watch, c.Type, c.TmdbId, c.SeasonNumber, c.EpisodeNumber, "check-in finished");
            }
            catch (TrackingException) { /* the title vanished from TMDB; nothing to record */ }
        }
        var staleBefore = now - StaleGrace;
        await db.CheckIns.Where(x => x.Source != WatchSource.Manual && x.EndsAt < staleBefore).ExecuteDeleteAsync(ct);
        return due.Count;
    }

    public async Task<List<NowWatchingDto>> ToDtosAsync(IReadOnlyCollection<CheckIn> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var movieIds = rows.Where(r => r.Type == MediaType.Movie).Select(r => r.TmdbId).Distinct().ToList();
        var showIds = rows.Where(r => r.Type == MediaType.Episode).Select(r => r.TmdbId).Distinct().ToList();
        var movies = await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, ct);
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var eps = (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Name, e.StillPath }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, (int?)e.SeasonNumber, (int?)e.EpisodeNumber));

        return rows.Select(r =>
        {
            if (r.Type == MediaType.Movie)
            {
                var m = movies.GetValueOrDefault(r.TmdbId);
                return new NowWatchingDto(r.Type, r.TmdbId, m?.Title ?? $"#{r.TmdbId}", null, null, null, m?.PosterPath, m?.BackdropPath, r.StartedAt, r.EndsAt, r.Source);
            }
            var s = shows.GetValueOrDefault(r.TmdbId);
            var ep = eps.GetValueOrDefault((r.TmdbId, r.SeasonNumber, r.EpisodeNumber));
            return new NowWatchingDto(r.Type, r.TmdbId, s?.Name ?? $"#{r.TmdbId}", r.SeasonNumber, r.EpisodeNumber, ep?.Name,
                s?.PosterPath, s?.BackdropPath, r.StartedAt, r.EndsAt, r.Source);
        }).ToList();
    }
}
