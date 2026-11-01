using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

public class TrackingException(string message) : Exception(message);

public class TrackingService(AppDbContext db, CatalogService catalog)
{
    // ---------- Watching ----------

    /// <summary>Records one play. Shows/seasons expand to every aired, unwatched episode.</summary>
    public async Task<int> WatchAsync(Guid userId, WatchRequest req, WatchSource source, CancellationToken ct)
    {
        var at = Clamp(req.WatchedAt);
        switch (req.Type)
        {
            case MediaType.Movie:
                if (await catalog.EnsureMovieAsync(req.TmdbId, ct) is null) throw new TrackingException("Movie not found.");
                db.WatchEvents.Add(new WatchEvent { UserId = userId, Type = MediaType.Movie, TmdbId = req.TmdbId, WatchedAt = at, Source = source });
                await RemoveFromWatchlistAsync(userId, MediaType.Movie, req.TmdbId, ct);
                await db.SaveChangesAsync(ct);
                return 1;

            case MediaType.Episode when req.SeasonNumber is { } s && req.EpisodeNumber is { } e:
                if (await catalog.EnsureShowAsync(req.TmdbId, ct) is null) throw new TrackingException("Show not found.");
                if (!await db.Episodes.AnyAsync(x => x.ShowTmdbId == req.TmdbId && x.SeasonNumber == s && x.EpisodeNumber == e, ct))
                    throw new TrackingException($"S{s:00}E{e:00} isn't an episode of this show.");
                db.WatchEvents.Add(new WatchEvent { UserId = userId, Type = MediaType.Episode, TmdbId = req.TmdbId, SeasonNumber = s, EpisodeNumber = e, WatchedAt = at, Source = source });
                await RemoveFromWatchlistAsync(userId, MediaType.Show, req.TmdbId, ct);
                await db.SaveChangesAsync(ct);
                return 1;

            case MediaType.Show:
                return await BulkWatchAsync(userId, new BulkWatchRequest(req.TmdbId, req.SeasonNumber, req.WatchedAt), ct);

            default:
                throw new TrackingException("Episodes need a season and episode number.");
        }
    }

    public async Task<int> BulkWatchAsync(Guid userId, BulkWatchRequest req, CancellationToken ct)
    {
        if (await catalog.EnsureShowAsync(req.ShowTmdbId, ct) is null) throw new TrackingException("Show not found.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var at = Clamp(req.WatchedAt);

        var episodes = await db.Episodes
            .Where(e => e.ShowTmdbId == req.ShowTmdbId && e.AirDate != null && e.AirDate <= today)
            .Where(e => req.SeasonNumber == null ? e.SeasonNumber > 0 : e.SeasonNumber == req.SeasonNumber)
            .Select(e => new { e.SeasonNumber, e.EpisodeNumber })
            .ToListAsync(ct);

        var watched = req.OnlyUnwatched
            ? (await db.WatchEvents
                .Where(w => w.UserId == userId && w.Type == MediaType.Episode && w.TmdbId == req.ShowTmdbId)
                .Select(w => new { S = w.SeasonNumber!.Value, E = w.EpisodeNumber!.Value }).ToListAsync(ct))
                .Select(w => (w.S, w.E)).ToHashSet()
            : [];

        var added = 0;
        foreach (var e in episodes.Where(e => !watched.Contains((e.SeasonNumber, e.EpisodeNumber))))
        {
            db.WatchEvents.Add(new WatchEvent
            {
                UserId = userId, Type = MediaType.Episode, TmdbId = req.ShowTmdbId,
                SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber, WatchedAt = at, Source = WatchSource.Manual,
            });
            added++;
        }
        if (added > 0) await RemoveFromWatchlistAsync(userId, MediaType.Show, req.ShowTmdbId, ct);
        await db.SaveChangesAsync(ct);
        return added;
    }

    public async Task<bool> DeleteHistoryAsync(Guid userId, Guid id, CancellationToken ct) =>
        await db.WatchEvents.Where(w => w.UserId == userId && w.Id == id).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Removes every play of a movie, a whole show, a season or a single episode.</summary>
    public async Task<int> UnwatchAsync(Guid userId, MediaType type, int tmdbId, int? season, int? episode, CancellationToken ct)
    {
        var q = db.WatchEvents.Where(w => w.UserId == userId && w.TmdbId == tmdbId);
        q = type == MediaType.Movie ? q.Where(w => w.Type == MediaType.Movie) : q.Where(w => w.Type == MediaType.Episode);
        if (type != MediaType.Movie && season is not null) q = q.Where(w => w.SeasonNumber == season);
        if (type != MediaType.Movie && episode is not null) q = q.Where(w => w.EpisodeNumber == episode);
        return await q.ExecuteDeleteAsync(ct);
    }

    private static DateTimeOffset Clamp(DateTimeOffset? at)
    {
        var now = DateTimeOffset.UtcNow;
        if (at is null || at > now.AddMinutes(5)) return now;
        return at.Value < new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero) ? now : at.Value.ToUniversalTime();
    }

    // ---------- History ----------

    public async Task<PagedResult<HistoryEntry>> GetHistoryAsync(Guid userId, MediaType? type, int page, int pageSize, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);
        var q = db.WatchEvents.Where(w => w.UserId == userId);
        if (type == MediaType.Movie) q = q.Where(w => w.Type == MediaType.Movie);
        else if (type is MediaType.Show or MediaType.Episode) q = q.Where(w => w.Type == MediaType.Episode);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(w => w.WatchedAt).ThenByDescending(w => w.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(await ToHistoryAsync(rows, ct), page, (total + pageSize - 1) / pageSize, total);
    }

    public async Task<List<HistoryEntry>> ToHistoryAsync(List<WatchEvent> rows, CancellationToken ct)
    {
        var movieIds = rows.Where(r => r.Type == MediaType.Movie).Select(r => r.TmdbId).Distinct().ToList();
        var showIds = rows.Where(r => r.Type == MediaType.Episode).Select(r => r.TmdbId).Distinct().ToList();
        var movies = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, ct);
        var shows = await db.Shows.Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var eps = (await db.Episodes.Where(e => showIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Name }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber), e => e.Name);

        return rows.Select(r => r.Type == MediaType.Movie
            ? new HistoryEntry(r.Id, MediaType.Movie, r.TmdbId, movies.GetValueOrDefault(r.TmdbId)?.Title ?? $"Movie {r.TmdbId}",
                null, null, null, movies.GetValueOrDefault(r.TmdbId)?.PosterPath, r.WatchedAt, r.Source)
            : new HistoryEntry(r.Id, MediaType.Episode, r.TmdbId, shows.GetValueOrDefault(r.TmdbId)?.Name ?? $"Show {r.TmdbId}",
                r.SeasonNumber, r.EpisodeNumber,
                eps.GetValueOrDefault((r.TmdbId, r.SeasonNumber ?? 0, r.EpisodeNumber ?? 0)),
                shows.GetValueOrDefault(r.TmdbId)?.PosterPath, r.WatchedAt, r.Source)).ToList();
    }

    // ---------- Watchlist ----------

    public async Task AddToWatchlistAsync(Guid userId, MediaType type, int tmdbId, CancellationToken ct, DateTimeOffset? addedAt = null)
    {
        if (type is not (MediaType.Movie or MediaType.Show)) throw new TrackingException("Only movies and shows can be on the watchlist.");
        await EnsureMetaAsync(type, tmdbId, ct);
        if (await db.Watchlist.AnyAsync(w => w.UserId == userId && w.Type == type && w.TmdbId == tmdbId, ct)) return;
        db.Watchlist.Add(new WatchlistItem { UserId = userId, Type = type, TmdbId = tmdbId, AddedAt = addedAt ?? DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveFromWatchlistAsync(Guid userId, MediaType type, int tmdbId, CancellationToken ct)
    {
        var row = await db.Watchlist.FirstOrDefaultAsync(w => w.UserId == userId && w.Type == type && w.TmdbId == tmdbId, ct);
        if (row is not null) db.Watchlist.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<WatchlistEntry>> GetWatchlistAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.Watchlist.Where(w => w.UserId == userId).OrderByDescending(w => w.AddedAt).ToListAsync(ct);
        var (movies, shows) = await LoadMetaAsync(rows.Select(r => (r.Type, r.TmdbId)), ct);
        return rows.Select(r => r.Type == MediaType.Movie
                ? movies.TryGetValue(r.TmdbId, out var m) ? new WatchlistEntry(CatalogService.ToSummary(m), r.AddedAt, m.ReleaseDate) : null
                : shows.TryGetValue(r.TmdbId, out var s) ? new WatchlistEntry(CatalogService.ToSummary(s), r.AddedAt, s.FirstAirDate) : null)
            .OfType<WatchlistEntry>().ToList();
    }

    // ---------- Ratings ----------

    public async Task RateAsync(Guid userId, RatingRequest req, CancellationToken ct, DateTimeOffset? ratedAt = null)
    {
        if (req.Rating is < 1 or > 10) throw new TrackingException("Ratings are 1 to 10.");
        int? season = null, episode = null;
        if (req.Type == MediaType.Episode)
        {
            if (req.SeasonNumber is null || req.EpisodeNumber is null) throw new TrackingException("Episode ratings need a season and episode.");
            (season, episode) = (req.SeasonNumber, req.EpisodeNumber);
            await EnsureMetaAsync(MediaType.Show, req.TmdbId, ct);
        }
        else await EnsureMetaAsync(req.Type, req.TmdbId, ct);

        var row = await db.Ratings.FirstOrDefaultAsync(r => r.UserId == userId && r.Type == req.Type && r.TmdbId == req.TmdbId
                                                             && r.SeasonNumber == season && r.EpisodeNumber == episode, ct);
        if (row is null)
        {
            row = new Rating { UserId = userId, Type = req.Type, TmdbId = req.TmdbId, SeasonNumber = season, EpisodeNumber = episode };
            db.Ratings.Add(row);
        }
        row.Value = req.Rating;
        row.RatedAt = ratedAt ?? DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnrateAsync(Guid userId, MediaType type, int tmdbId, int? season, int? episode, CancellationToken ct) =>
        await db.Ratings.Where(r => r.UserId == userId && r.Type == type && r.TmdbId == tmdbId
                                    && r.SeasonNumber == season && r.EpisodeNumber == episode).ExecuteDeleteAsync(ct);

    public async Task<List<RatingEntry>> GetRatingsAsync(Guid userId, MediaType? type, CancellationToken ct)
    {
        var q = db.Ratings.Where(r => r.UserId == userId);
        if (type is not null) q = q.Where(r => r.Type == type);
        var rows = await q.OrderByDescending(r => r.RatedAt).ToListAsync(ct);
        var (movies, shows) = await LoadMetaAsync(rows.Select(r => (r.Type == MediaType.Episode ? MediaType.Show : r.Type, r.TmdbId)), ct);
        return rows.Select(r =>
        {
            var (title, poster) = r.Type == MediaType.Movie
                ? (movies.GetValueOrDefault(r.TmdbId)?.Title, movies.GetValueOrDefault(r.TmdbId)?.PosterPath)
                : (shows.GetValueOrDefault(r.TmdbId)?.Name, shows.GetValueOrDefault(r.TmdbId)?.PosterPath);
            return new RatingEntry(r.Type, r.TmdbId, title ?? $"#{r.TmdbId}", r.SeasonNumber, r.EpisodeNumber, poster, r.Value, r.RatedAt);
        }).ToList();
    }

    // ---------- Progress, calendar, dashboard ----------

    public Task<List<ShowProgressDto>> GetProgressAsync(Guid userId, CancellationToken ct) =>
        new ProgressCalculator(db).ForUserAsync(userId, null, ct);

    public async Task<List<CalendarEntry>> GetCalendarAsync(Guid userId, DateOnly from, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 92);
        var to = from.AddDays(days);

        // Shows you've watched any of, or have on your watchlist.
        var watchedShows = db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Episode).Select(w => w.TmdbId);
        var watchlistShows = db.Watchlist.Where(w => w.UserId == userId && w.Type == MediaType.Show).Select(w => w.TmdbId);
        var showIds = await watchedShows.Union(watchlistShows).ToListAsync(ct);

        var episodes = await db.Episodes
            .Where(e => showIds.Contains(e.ShowTmdbId) && e.AirDate >= from && e.AirDate < to)
            .ToListAsync(ct);
        var shows = await db.Shows.Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var watched = (await db.WatchEvents
                .Where(w => w.UserId == userId && w.Type == MediaType.Episode && showIds.Contains(w.TmdbId))
                .Select(w => new { w.TmdbId, w.SeasonNumber, w.EpisodeNumber }).ToListAsync(ct))
            .Select(w => (w.TmdbId, w.SeasonNumber, w.EpisodeNumber)).ToHashSet();

        var entries = episodes.Where(e => shows.ContainsKey(e.ShowTmdbId)).Select(e => new CalendarEntry(
            e.AirDate!.Value, MediaType.Episode, e.ShowTmdbId, shows[e.ShowTmdbId].Name, e.SeasonNumber, e.EpisodeNumber, e.Name,
            shows[e.ShowTmdbId].PosterPath, watched.Contains((e.ShowTmdbId, (int?)e.SeasonNumber, (int?)e.EpisodeNumber)))).ToList();

        var watchlistMovies = await db.Watchlist
            .Where(w => w.UserId == userId && w.Type == MediaType.Movie)
            .Join(db.Movies, w => w.TmdbId, m => m.TmdbId, (w, m) => m)
            .Where(m => m.ReleaseDate >= from && m.ReleaseDate < to)
            .ToListAsync(ct);
        entries.AddRange(watchlistMovies.Select(m => new CalendarEntry(m.ReleaseDate!.Value, MediaType.Movie, m.TmdbId, m.Title, null, null, null, m.PosterPath, false)));

        return entries.OrderBy(e => e.Date).ThenBy(e => e.Title).ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
    }

    public async Task<DashboardDto> GetDashboardAsync(Guid userId, CancellationToken ct)
    {
        var progress = await GetProgressAsync(userId, ct);
        var upNext = progress.Where(p => p.NextEpisode is not null).Take(12).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var upcoming = (await GetCalendarAsync(userId, today, 14, ct)).Take(12).ToList();
        var recent = await db.WatchEvents.Where(w => w.UserId == userId)
            .OrderByDescending(w => w.WatchedAt).Take(12).ToListAsync(ct);
        return new DashboardDto(upNext, upcoming, await ToHistoryAsync(recent, ct));
    }

    // ---------- Stats ----------

    public async Task<StatsDto> GetStatsAsync(Guid userId, CancellationToken ct)
    {
        var watches = await db.WatchEvents.Where(w => w.UserId == userId)
            .Select(w => new { w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt }).ToListAsync(ct);

        var movieIds = watches.Where(w => w.Type == MediaType.Movie).Select(w => w.TmdbId).Distinct().ToList();
        var showIds = watches.Where(w => w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct().ToList();
        var movies = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, ct);
        var shows = await db.Shows.Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var runtimes = (await db.Episodes.Where(e => showIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Runtime }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber), e => e.Runtime);

        long movieMinutes = 0, episodeMinutes = 0;
        var genres = new Dictionary<string, int>();
        foreach (var w in watches)
        {
            if (w.Type == MediaType.Movie)
            {
                var m = movies.GetValueOrDefault(w.TmdbId);
                movieMinutes += m?.Runtime ?? 0;
                foreach (var g in m?.Genres ?? []) genres[g] = genres.GetValueOrDefault(g) + 1;
            }
            else
            {
                var s = shows.GetValueOrDefault(w.TmdbId);
                episodeMinutes += runtimes.GetValueOrDefault((w.TmdbId, w.SeasonNumber ?? 0, w.EpisodeNumber ?? 0)) ?? s?.EpisodeRuntime ?? 0;
                foreach (var g in s?.Genres ?? []) genres[g] = genres.GetValueOrDefault(g) + 1;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var months = Enumerable.Range(0, 12).Select(i => now.AddMonths(-11 + i)).Select(d => (d.Year, d.Month)).ToList();
        var byMonth = watches.GroupBy(w => (w.WatchedAt.Year, w.WatchedAt.Month)).ToDictionary(g => g.Key, g => g.Count());

        var ratings = await db.Ratings.Where(r => r.UserId == userId).Select(r => r.Value).ToListAsync(ct);
        var weekdays = watches.GroupBy(w => w.WatchedAt.DayOfWeek).ToDictionary(g => g.Key, g => g.Count());

        return new StatsDto(
            MoviesWatched: movieIds.Count,
            MoviePlays: watches.Count(w => w.Type == MediaType.Movie),
            ShowsWatched: showIds.Count,
            EpisodesWatched: watches.Where(w => w.Type == MediaType.Episode).Select(w => (w.TmdbId, w.SeasonNumber, w.EpisodeNumber)).Distinct().Count(),
            EpisodePlays: watches.Count(w => w.Type == MediaType.Episode),
            MinutesMovies: movieMinutes,
            MinutesEpisodes: episodeMinutes,
            Ratings: ratings.Count,
            AverageRating: ratings.Count > 0 ? Math.Round(ratings.Average(), 2) : null,
            Genres: genres.OrderByDescending(g => g.Value).Take(12).Select(g => new CountBucket(g.Key, g.Value)).ToList(),
            PlaysByMonth: months.Select(m => new CountBucket(
                new DateTime(m.Year, m.Month, 1).ToString("MMM yy", CultureInfo.InvariantCulture), byMonth.GetValueOrDefault(m))).ToList(),
            RatingDistribution: Enumerable.Range(1, 10).Select(i => new CountBucket(i.ToString(), ratings.Count(r => r == i))).ToList(),
            PlaysByWeekday: Enum.GetValues<DayOfWeek>().Select(d => new CountBucket(d.ToString()[..3], weekdays.GetValueOrDefault(d))).ToList());
    }

    // ---------- Helpers ----------

    public async Task EnsureMetaAsync(MediaType type, int tmdbId, CancellationToken ct)
    {
        var ok = type switch
        {
            MediaType.Movie => await catalog.EnsureMovieAsync(tmdbId, ct) is not null,
            MediaType.Show => await catalog.EnsureShowAsync(tmdbId, ct) is not null,
            _ => false,
        };
        if (!ok) throw new TrackingException($"{type} {tmdbId} not found on TMDB.");
    }

    public async Task<(Dictionary<int, MovieMeta> Movies, Dictionary<int, ShowMeta> Shows)> LoadMetaAsync(
        IEnumerable<(MediaType Type, int TmdbId)> items, CancellationToken ct)
    {
        var list = items.ToList();
        var movieIds = list.Where(i => i.Type == MediaType.Movie).Select(i => i.TmdbId).Distinct().ToList();
        var showIds = list.Where(i => i.Type == MediaType.Show).Select(i => i.TmdbId).Distinct().ToList();
        return (await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, ct),
                await db.Shows.Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct));
    }
}
