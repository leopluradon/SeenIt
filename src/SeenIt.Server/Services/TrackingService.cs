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
        var when = Resolve(req.When, req.WatchedAt);
        switch (req.Type)
        {
            case MediaType.Movie:
                if (await catalog.EnsureMovieAsync(req.TmdbId, ct) is not { } movie) throw new TrackingException("Movie not found.");
                var at = PlayTime(when, req.WatchedAt, movie.ReleaseDate);
                db.WatchEvents.Add(new WatchEvent { UserId = userId, Type = MediaType.Movie, TmdbId = req.TmdbId, WatchedAt = at, Source = source });
                await RemoveFromWatchlistAsync(userId, MediaType.Movie, req.TmdbId, ct);
                await db.SaveChangesAsync(ct);
                return 1;

            case MediaType.Episode when req.SeasonNumber is { } s && req.EpisodeNumber is { } e:
                if (await catalog.EnsureShowAsync(req.TmdbId, ct) is null) throw new TrackingException("Show not found.");
                var ep = await db.Episodes.FirstOrDefaultAsync(x => x.ShowTmdbId == req.TmdbId && x.SeasonNumber == s && x.EpisodeNumber == e, ct)
                         ?? throw new TrackingException($"S{s:00}E{e:00} isn't an episode of this show.");
                db.WatchEvents.Add(new WatchEvent
                {
                    UserId = userId, Type = MediaType.Episode, TmdbId = req.TmdbId, SeasonNumber = s, EpisodeNumber = e,
                    WatchedAt = PlayTime(when, req.WatchedAt, ep.AirDate), Source = source,
                });
                await RemoveFromWatchlistAsync(userId, MediaType.Show, req.TmdbId, ct);
                // Watching a new episode of a paused show picks it back up.
                if (source is WatchSource.Manual or WatchSource.Jellyfin
                    && await db.ShowStates.FirstOrDefaultAsync(x => x.UserId == userId && x.ShowTmdbId == req.TmdbId && x.Status == ShowTrackStatus.Paused, ct) is { } paused)
                    db.ShowStates.Remove(paused);
                await db.SaveChangesAsync(ct);
                return 1;

            case MediaType.Show:
                return await BulkWatchAsync(userId, new BulkWatchRequest(req.TmdbId, req.SeasonNumber, req.WatchedAt, When: req.When), ct);

            default:
                throw new TrackingException("Episodes need a season and episode number.");
        }
    }

    public async Task<int> BulkWatchAsync(Guid userId, BulkWatchRequest req, CancellationToken ct)
    {
        if (await catalog.EnsureShowAsync(req.ShowTmdbId, ct) is null) throw new TrackingException("Show not found.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var when = Resolve(req.When, req.WatchedAt);

        var episodes = await db.Episodes
            .Where(e => e.ShowTmdbId == req.ShowTmdbId && e.AirDate != null && e.AirDate <= today)
            .Where(e => req.SeasonNumber == null ? e.SeasonNumber > 0 : e.SeasonNumber == req.SeasonNumber)
            .Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.AirDate })
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
                SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber,
                // "Release date" gives every episode its own air date.
                WatchedAt = PlayTime(when, req.WatchedAt, e.AirDate), Source = WatchSource.Manual,
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

    private static WatchTime Resolve(WatchTime? when, DateTimeOffset? at) =>
        when ?? (at is null ? WatchTime.Now : WatchTime.Custom);

    /// <summary>Turns a watch-time choice into the timestamp stored for one play.</summary>
    public static DateTimeOffset PlayTime(WatchTime when, DateTimeOffset? at, DateOnly? released) => when switch
    {
        WatchTime.Unknown => WatchDates.Unknown,
        // Noon UTC keeps the calendar day the same in every time zone. Unreleased → now.
        WatchTime.ReleaseDate when released is { } d && d <= DateOnly.FromDateTime(DateTime.UtcNow)
            => new DateTimeOffset(d.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero),
        WatchTime.Custom => Clamp(at),
        _ => DateTimeOffset.UtcNow,
    };

    private static DateTimeOffset Clamp(DateTimeOffset? at)
    {
        var now = DateTimeOffset.UtcNow;
        if (at is null || at > now.AddMinutes(5)) return now;
        return at.Value < new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero) ? now : at.Value.ToUniversalTime();
    }

    // ---------- History ----------

    public async Task<HistoryPage> GetHistoryAsync(Guid userId, HistoryQuery f, CancellationToken ct)
    {
        var pageSize = Math.Clamp(f.PageSize, 1, 100);
        var page = Math.Max(f.Page, 1);
        var q = FilterHistory(db.WatchEvents.Where(w => w.UserId == userId), f);

        var total = await q.CountAsync(ct);
        var ordered = f.Oldest
            ? q.OrderBy(w => w.WatchedAt).ThenBy(w => w.CreatedAt)
            : q.OrderByDescending(w => w.WatchedAt).ThenByDescending(w => w.CreatedAt);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        // Total running time of everything matched, not just this page.
        var minutes = 0L;
        if (total > 0)
        {
            var movieMinutes = await q.Where(w => w.Type == MediaType.Movie)
                .Join(db.Movies, w => w.TmdbId, m => m.TmdbId, (w, m) => (long?)m.Runtime).SumAsync(ct) ?? 0;
            var episodeMinutes = await q.Where(w => w.Type == MediaType.Episode)
                .Join(db.Shows, w => w.TmdbId, s => s.TmdbId, (w, s) => new { w.TmdbId, w.SeasonNumber, w.EpisodeNumber, s.EpisodeRuntime })
                .Select(x => (long?)(db.Episodes
                    .Where(e => e.ShowTmdbId == x.TmdbId && e.SeasonNumber == x.SeasonNumber && e.EpisodeNumber == x.EpisodeNumber)
                    .Select(e => e.Runtime).FirstOrDefault() ?? x.EpisodeRuntime))
                .SumAsync(ct) ?? 0;
            minutes = movieMinutes + episodeMinutes;
        }

        var entries = await ToHistoryAsync(rows, ct);
        return new(entries, page, (total + pageSize - 1) / pageSize, total, minutes, await DayTotalsAsync(q, entries, f.TimeZone, ct));
    }

    /// <summary>
    /// Totals for each day shown on this page, counting every matching play that day, so a day
    /// split across two pages still shows its full total.
    /// </summary>
    private async Task<List<HistoryDay>> DayTotalsAsync(IQueryable<WatchEvent> q, List<HistoryEntry> page, string? tzId, CancellationToken ct)
    {
        if (page.Count == 0) return [];
        var tz = ReviewService.Zone(tzId);
        DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime);
        DateTimeOffset Start(DateOnly d) { var local = d.ToDateTime(TimeOnly.MinValue); return new DateTimeOffset(local, tz.GetUtcOffset(local)); }

        var days = page.Where(e => !WatchDates.IsUnknown(e.WatchedAt)).Select(e => LocalDay(e.WatchedAt)).Distinct().ToList();
        var result = new List<HistoryDay>();
        if (days.Count > 0)
        {
            var from = Start(days.Min()).ToUniversalTime();
            var to = Start(days.Max().AddDays(1)).ToUniversalTime();
            var plays = await q.Where(w => w.WatchedAt >= from && w.WatchedAt < to).ToListAsync(ct);
            var withRuntime = await ToHistoryAsync(plays, ct);
            var wanted = days.ToHashSet();
            result.AddRange(withRuntime.GroupBy(e => LocalDay(e.WatchedAt)).Where(g => wanted.Contains(g.Key))
                .Select(g => new HistoryDay(g.Key, g.Count(), g.Sum(e => (long)(e.Runtime ?? 0))))
                .OrderBy(d => days.IndexOf(d.Date!.Value)));   // same order as the page
        }
        if (page.Any(e => WatchDates.IsUnknown(e.WatchedAt)))
        {
            var unknown = await ToHistoryAsync(await q.Where(w => w.WatchedAt == WatchDates.Unknown).ToListAsync(ct), ct);
            result.Add(new HistoryDay(null, unknown.Count, unknown.Sum(e => (long)(e.Runtime ?? 0))));
        }
        return result;
    }

    private IQueryable<WatchEvent> FilterHistory(IQueryable<WatchEvent> q, HistoryQuery f)
    {
        if (f.Type == MediaType.Movie) q = q.Where(w => w.Type == MediaType.Movie);
        else if (f.Type is MediaType.Show or MediaType.Episode) q = q.Where(w => w.Type == MediaType.Episode);

        if (f.Source is { } source) q = q.Where(w => w.Source == source);

        // Any date bound leaves out plays whose date is unknown.
        if (f.From is not null || f.To is not null) q = q.Where(w => w.WatchedAt != WatchDates.Unknown);
        if (f.From is { } from) q = q.Where(w => w.WatchedAt >= from.ToUniversalTime());
        if (f.To is { } to) q = q.Where(w => w.WatchedAt < to.ToUniversalTime());

        if (!string.IsNullOrWhiteSpace(f.Genre))
        {
            var genre = f.Genre.Trim();
            var movies = db.Movies.Where(m => m.Genres.Contains(genre)).Select(m => m.TmdbId);
            var shows = db.Shows.Where(s => s.Genres.Contains(genre)).Select(s => s.TmdbId);
            q = q.Where(w => w.Type == MediaType.Movie ? movies.Contains(w.TmdbId) : shows.Contains(w.TmdbId));
        }

        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var term = f.Q.Trim().ToLower();
            var movies = db.Movies.Where(m => m.Title.ToLower().Contains(term)).Select(m => m.TmdbId);
            var shows = db.Shows.Where(s => s.Name.ToLower().Contains(term)).Select(s => s.TmdbId);
            var episodes = db.Episodes.Where(e => e.Name != null && e.Name.ToLower().Contains(term));
            q = q.Where(w => w.Type == MediaType.Movie
                ? movies.Contains(w.TmdbId)
                : shows.Contains(w.TmdbId) || episodes.Any(e => e.ShowTmdbId == w.TmdbId && e.SeasonNumber == w.SeasonNumber && e.EpisodeNumber == w.EpisodeNumber));
        }
        return q;
    }

    /// <summary>Genres across everything the user has watched, most-watched first, for the history filter.</summary>
    public async Task<List<string>> GetHistoryGenresAsync(Guid userId, CancellationToken ct)
    {
        var watched = await db.WatchEvents.Where(w => w.UserId == userId)
            .GroupBy(w => new { w.Type, w.TmdbId }).Select(g => new { g.Key.Type, g.Key.TmdbId, Plays = g.Count() }).ToListAsync(ct);
        var (movies, shows) = await LoadMetaAsync(watched.Select(w => (w.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, w.TmdbId)), ct);
        var counts = new Dictionary<string, int>();
        foreach (var w in watched)
        {
            var genres = w.Type == MediaType.Movie ? movies.GetValueOrDefault(w.TmdbId)?.Genres : shows.GetValueOrDefault(w.TmdbId)?.Genres;
            foreach (var g in genres ?? []) counts[g] = counts.GetValueOrDefault(g) + w.Plays;
        }
        return counts.OrderByDescending(c => c.Value).Select(c => c.Key).ToList();
    }

    public async Task<List<HistoryEntry>> ToHistoryAsync(List<WatchEvent> rows, CancellationToken ct)
    {
        var movieIds = rows.Where(r => r.Type == MediaType.Movie).Select(r => r.TmdbId).Distinct().ToList();
        var showIds = rows.Where(r => r.Type == MediaType.Episode).Select(r => r.TmdbId).Distinct().ToList();
        var movies = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, ct);
        var shows = await db.Shows.Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var eps = (await db.Episodes.Where(e => showIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Name, e.Runtime }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber));

        return rows.Select(r =>
        {
            if (r.Type == MediaType.Movie)
            {
                var m = movies.GetValueOrDefault(r.TmdbId);
                return new HistoryEntry(r.Id, MediaType.Movie, r.TmdbId, m?.Title ?? $"Movie {r.TmdbId}",
                    null, null, null, m?.PosterPath, r.WatchedAt, r.Source, m?.Runtime);
            }
            var s = shows.GetValueOrDefault(r.TmdbId);
            var e = eps.GetValueOrDefault((r.TmdbId, r.SeasonNumber ?? 0, r.EpisodeNumber ?? 0));
            return new HistoryEntry(r.Id, MediaType.Episode, r.TmdbId, s?.Name ?? $"Show {r.TmdbId}",
                r.SeasonNumber, r.EpisodeNumber, e?.Name, s?.PosterPath, r.WatchedAt, r.Source, e?.Runtime ?? s?.EpisodeRuntime);
        }).ToList();
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
        var dropped = db.ShowStates.Where(x => x.UserId == userId && x.Status == ShowTrackStatus.Dropped).Select(x => x.ShowTmdbId);
        var showIds = await watchedShows.Union(watchlistShows).Where(id => !dropped.Contains(id)).ToListAsync(ct);

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

    public async Task SetShowStatusAsync(Guid userId, int showId, ShowTrackStatus status, CancellationToken ct)
    {
        var row = await db.ShowStates.FirstOrDefaultAsync(x => x.UserId == userId && x.ShowTmdbId == showId, ct);
        if (status == ShowTrackStatus.Watching)
        {
            if (row is not null) db.ShowStates.Remove(row);
        }
        else
        {
            if (await catalog.EnsureShowAsync(showId, ct) is null) throw new TrackingException("Show not found.");
            if (row is null) db.ShowStates.Add(new ShowState { UserId = userId, ShowTmdbId = showId, Status = status });
            else { row.Status = status; row.UpdatedAt = DateTimeOffset.UtcNow; }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<ShowTrackStatus> GetShowStatusAsync(Guid userId, int showId, CancellationToken ct) =>
        await db.ShowStates.Where(x => x.UserId == userId && x.ShowTmdbId == showId).Select(x => (ShowTrackStatus?)x.Status).FirstOrDefaultAsync(ct)
        ?? ShowTrackStatus.Watching;

    public async Task<DashboardDto> GetDashboardAsync(Guid userId, CancellationToken ct)
    {
        var progress = await GetProgressAsync(userId, ct);
        var upNext = progress.Where(p => p.NextEpisode is not null && p.Status == ShowTrackStatus.Watching).Take(12).ToList();
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
        var dated = watches.Where(w => !WatchDates.IsUnknown(w.WatchedAt)).ToList();
        var byMonth = dated.GroupBy(w => (w.WatchedAt.Year, w.WatchedAt.Month)).ToDictionary(g => g.Key, g => g.Count());

        var ratings = await db.Ratings.Where(r => r.UserId == userId).Select(r => r.Value).ToListAsync(ct);
        var weekdays = dated.GroupBy(w => w.WatchedAt.DayOfWeek).ToDictionary(g => g.Key, g => g.Count());

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
                new DateTime(m.Year, m.Month, 1).ToString("MMM", CultureInfo.InvariantCulture), byMonth.GetValueOrDefault(m))).ToList(),
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
