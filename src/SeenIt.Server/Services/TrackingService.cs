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
                if ((source == WatchSource.Manual || WatchSources.IsScrobbler(source))
                    && await db.ShowStates.FirstOrDefaultAsync(x => x.UserId == userId && x.ShowTmdbId == req.TmdbId && x.Status == ShowTrackStatus.Paused, ct) is { } paused)
                    db.ShowStates.Remove(paused);
                await db.SaveChangesAsync(ct);
                await CompleteRewatchIfDoneAsync(userId, req.TmdbId, ct);
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

        // During a rewatch, "unwatched" means not played again yet.
        var since = await ActiveRewatchStartAsync(userId, req.ShowTmdbId, ct) ?? DateTimeOffset.MinValue;
        var watched = req.OnlyUnwatched
            ? (await db.WatchEvents
                .Where(w => w.UserId == userId && w.Type == MediaType.Episode && w.TmdbId == req.ShowTmdbId && w.WatchedAt >= since)
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
        if (added > 0) await CompleteRewatchIfDoneAsync(userId, req.ShowTmdbId, ct);
        return added;
    }

    // ---------- Rewatches ----------

    private Task<DateTimeOffset?> ActiveRewatchStartAsync(Guid userId, int showId, CancellationToken ct) =>
        db.Rewatches.Where(r => r.UserId == userId && r.ShowTmdbId == showId && r.CompletedAt == null)
            .Select(r => (DateTimeOffset?)r.StartedAt).FirstOrDefaultAsync(ct);

    /// <summary>Starts going through a show again; progress and Up Next restart from the first episode.</summary>
    public async Task<int> StartRewatchAsync(Guid userId, int showId, CancellationToken ct)
    {
        if (await ActiveRewatchStartAsync(userId, showId, ct) is not null) throw new TrackingException("You're already rewatching this show.");
        if (!await db.WatchEvents.AnyAsync(w => w.UserId == userId && w.Type == MediaType.Episode && w.TmdbId == showId && w.SeasonNumber > 0, ct))
            throw new TrackingException("Watch the show before starting a rewatch.");
        var number = await db.Rewatches.CountAsync(r => r.UserId == userId && r.ShowTmdbId == showId, ct) + 2;
        db.Rewatches.Add(new ShowRewatch { UserId = userId, ShowTmdbId = showId, Number = number, StartedAt = DateTimeOffset.UtcNow });
        // A rewatch is something you're watching, so un-pause/un-drop it.
        await db.ShowStates.Where(x => x.UserId == userId && x.ShowTmdbId == showId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return number;
    }

    /// <summary>Abandons the rewatch in progress. Its plays stay in your history.</summary>
    public async Task<bool> StopRewatchAsync(Guid userId, int showId, CancellationToken ct) =>
        await db.Rewatches.Where(r => r.UserId == userId && r.ShowTmdbId == showId && r.CompletedAt == null).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Marks the rewatch finished once every aired regular episode has been played since it started.</summary>
    public async Task CompleteRewatchIfDoneAsync(Guid userId, int showId, CancellationToken ct)
    {
        var rewatch = await db.Rewatches.FirstOrDefaultAsync(r => r.UserId == userId && r.ShowTmdbId == showId && r.CompletedAt == null, ct);
        if (rewatch is null) return;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var aired = await db.Episodes.CountAsync(e => e.ShowTmdbId == showId && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= today, ct);
        var seen = await db.WatchEvents
            .Where(w => w.UserId == userId && w.Type == MediaType.Episode && w.TmdbId == showId && w.SeasonNumber > 0 && w.WatchedAt >= rewatch.StartedAt)
            .Join(db.Episodes.Where(e => e.ShowTmdbId == showId && e.AirDate != null && e.AirDate <= today),
                w => new { S = w.SeasonNumber!.Value, E = w.EpisodeNumber!.Value }, e => new { S = e.SeasonNumber, E = e.EpisodeNumber }, (w, e) => e)
            .Select(e => new { e.SeasonNumber, e.EpisodeNumber }).Distinct().CountAsync(ct);
        if (aired == 0 || seen < aired) return;
        rewatch.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes one play; returns it as it was (for undo), or null if it isn't yours.</summary>
    public async Task<RestorePlay?> DeleteHistoryAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var q = db.WatchEvents.Where(w => w.UserId == userId && w.Id == id);
        var play = await q.Select(w => new RestorePlay(w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt, w.Source)).FirstOrDefaultAsync(ct);
        return play is not null && await q.ExecuteDeleteAsync(ct) > 0 ? play : null;
    }

    /// <summary>Moves one play to another date. Returns (before, after), or null if it isn't yours.</summary>
    public async Task<(DateTimeOffset Before, PlayDto After)?> EditPlayAsync(Guid userId, Guid id, EditPlayRequest req, CancellationToken ct)
    {
        var play = await db.WatchEvents.FirstOrDefaultAsync(w => w.UserId == userId && w.Id == id, ct);
        if (play is null) return null;
        DateOnly? released = null;
        if (req.When == WatchTime.ReleaseDate)
            released = play.Type == MediaType.Movie
                ? await db.Movies.Where(m => m.TmdbId == play.TmdbId).Select(m => m.ReleaseDate).FirstOrDefaultAsync(ct)
                : await db.Episodes.Where(e => e.ShowTmdbId == play.TmdbId && e.SeasonNumber == play.SeasonNumber && e.EpisodeNumber == play.EpisodeNumber)
                    .Select(e => e.AirDate).FirstOrDefaultAsync(ct);
        if (req.When == WatchTime.ReleaseDate && released is null) throw new TrackingException("There's no release date to use.");
        var before = play.WatchedAt;
        play.WatchedAt = PlayTime(req.When, req.WatchedAt, released);
        await db.SaveChangesAsync(ct);
        return (before, new PlayDto(play.Id, play.WatchedAt, play.Source));
    }

    /// <summary>
    /// Re-dates several plays. A custom date applies to all of them, keeping their order a minute
    /// apart (a binge stays in sequence); "release date" uses each one's own. Returns the old dates, for undo.
    /// </summary>
    public async Task<List<PlayDate>> EditPlaysAsync(Guid userId, BulkEditPlaysRequest req, CancellationToken ct)
    {
        if (req.Ids.Count > 2000) throw new TrackingException("That's too many plays at once.");
        if (req.When == WatchTime.Custom && req.WatchedAt is null) throw new TrackingException("Pick a date.");
        var plays = await db.WatchEvents.Where(w => w.UserId == userId && req.Ids.Contains(w.Id))
            .OrderBy(w => w.WatchedAt).ThenBy(w => w.CreatedAt).ToListAsync(ct);
        var before = plays.Select(p => new PlayDate(p.Id, p.WatchedAt)).ToList();

        Dictionary<int, DateOnly?> movieDates = [];
        Dictionary<(int, int?, int?), DateOnly?> episodeDates = [];
        if (req.When == WatchTime.ReleaseDate)
        {
            var movieIds = plays.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct().ToList();
            var showIds = plays.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();
            movieDates = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).Select(m => new { m.TmdbId, m.ReleaseDate })
                .ToDictionaryAsync(m => m.TmdbId, m => m.ReleaseDate, ct);
            episodeDates = (await db.Episodes.Where(e => showIds.Contains(e.ShowTmdbId))
                    .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.AirDate }).ToListAsync(ct))
                .ToDictionary(e => (e.ShowTmdbId, (int?)e.SeasonNumber, (int?)e.EpisodeNumber), e => e.AirDate);
        }

        for (var i = 0; i < plays.Count; i++)
        {
            var p = plays[i];
            p.WatchedAt = req.When switch
            {
                WatchTime.Custom => req.WatchedAt!.Value.ToUniversalTime().AddMinutes(i),
                WatchTime.ReleaseDate => PlayTime(WatchTime.ReleaseDate, null,
                    p.Type == MediaType.Movie ? movieDates.GetValueOrDefault(p.TmdbId) : episodeDates.GetValueOrDefault((p.TmdbId, p.SeasonNumber, p.EpisodeNumber))),
                _ => PlayTime(req.When, req.WatchedAt, null),
            };
        }
        await db.SaveChangesAsync(ct);
        return before;
    }

    /// <summary>Puts plays back on the dates given (undo for <see cref="EditPlaysAsync"/>).</summary>
    public async Task<int> SetPlayDatesAsync(Guid userId, IReadOnlyList<PlayDate> dates, CancellationToken ct)
    {
        if (dates.Count > 2000) throw new TrackingException("That's too many plays at once.");
        var byId = dates.ToDictionary(d => d.Id, d => d.WatchedAt.ToUniversalTime());
        var ids = byId.Keys.ToList();
        var plays = await db.WatchEvents.Where(w => w.UserId == userId && ids.Contains(w.Id)).ToListAsync(ct);
        foreach (var p in plays) p.WatchedAt = byId[p.Id];
        await db.SaveChangesAsync(ct);
        return plays.Count;
    }

    /// <summary>Deletes several plays at once; returns them as they were (for undo).</summary>
    public async Task<List<RestorePlay>> DeleteHistoryAsync(Guid userId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count > 5000) throw new TrackingException("That's too many plays at once.");
        var q = db.WatchEvents.Where(w => w.UserId == userId && ids.Contains(w.Id));
        var plays = await q.Select(w => new RestorePlay(w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt, w.Source)).ToListAsync(ct);
        await q.ExecuteDeleteAsync(ct);
        return plays;
    }

    /// <summary>Removes every play of a movie, a whole show, a season or a single episode; returns them (for undo).</summary>
    public async Task<List<RestorePlay>> UnwatchAsync(Guid userId, MediaType type, int tmdbId, int? season, int? episode, CancellationToken ct)
    {
        var q = db.WatchEvents.Where(w => w.UserId == userId && w.TmdbId == tmdbId);
        q = type == MediaType.Movie ? q.Where(w => w.Type == MediaType.Movie) : q.Where(w => w.Type == MediaType.Episode);
        if (type != MediaType.Movie && season is not null) q = q.Where(w => w.SeasonNumber == season);
        if (type != MediaType.Movie && episode is not null) q = q.Where(w => w.EpisodeNumber == episode);
        var plays = await q.Select(w => new RestorePlay(w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt, w.Source)).ToListAsync(ct);
        await q.ExecuteDeleteAsync(ct);
        return plays;
    }

    /// <summary>Puts deleted plays back exactly as they were (undo).</summary>
    public async Task<int> RestoreAsync(Guid userId, IReadOnlyList<RestorePlay> plays, CancellationToken ct)
    {
        if (plays.Count > 5000) throw new TrackingException("That's too many plays to restore at once.");
        foreach (var p in plays)
        {
            if (p.Type is not (MediaType.Movie or MediaType.Episode) || (p.Type == MediaType.Episode && (p.SeasonNumber is null || p.EpisodeNumber is null)))
                throw new TrackingException("That play can't be restored.");
            db.WatchEvents.Add(new WatchEvent
            {
                UserId = userId, Type = p.Type, TmdbId = p.TmdbId,
                SeasonNumber = p.Type == MediaType.Episode ? p.SeasonNumber : null,
                EpisodeNumber = p.Type == MediaType.Episode ? p.EpisodeNumber : null,
                WatchedAt = p.WatchedAt, Source = p.Source,
            });
        }
        await db.SaveChangesAsync(ct);
        return plays.Count;
    }

    /// <summary>
    /// Moves your plays, ratings, watchlist and list entries from one TMDB title to another, for an
    /// import or Jellyfin match that picked the wrong one. Where the right title already has a
    /// rating, watchlist entry or list entry, that one is kept and the wrong title's is dropped.
    /// For shows, episode plays keep their season/episode numbers.
    /// </summary>
    public async Task<RematchResult> RematchAsync(Guid userId, MediaType type, int from, int to, CancellationToken ct)
    {
        if (type is not (MediaType.Movie or MediaType.Show)) throw new TrackingException("Only movies and shows can be re-matched.");
        if (from == to) throw new TrackingException("That's the same title.");
        await EnsureMetaAsync(type, to, ct);
        var movie = type == MediaType.Movie;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var plays = await db.WatchEvents.Where(w => w.UserId == userId && w.TmdbId == from && (movie ? w.Type == MediaType.Movie : w.Type == MediaType.Episode))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.TmdbId, to), ct);
        await db.CheckIns.Where(c => c.UserId == userId && c.TmdbId == from && (movie ? c.Type == MediaType.Movie : c.Type != MediaType.Movie))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.TmdbId, to), ct);

        // Ratings: movie, or show/season/episode ratings of the show.
        var ratingsQ = db.Ratings.Where(r => r.UserId == userId && r.TmdbId == from && (movie ? r.Type == MediaType.Movie : r.Type != MediaType.Movie));
        await ratingsQ.Where(r => db.Ratings.Any(t => t.UserId == userId && t.TmdbId == to && t.Type == r.Type
                                                     && t.SeasonNumber == r.SeasonNumber && t.EpisodeNumber == r.EpisodeNumber))
            .ExecuteDeleteAsync(ct);
        var ratings = await ratingsQ.ExecuteUpdateAsync(s => s.SetProperty(r => r.TmdbId, to), ct);

        var watchQ = db.Watchlist.Where(w => w.UserId == userId && w.Type == type && w.TmdbId == from);
        if (await db.Watchlist.AnyAsync(w => w.UserId == userId && w.Type == type && w.TmdbId == to, ct)) await watchQ.ExecuteDeleteAsync(ct);
        var watchlist = await watchQ.ExecuteUpdateAsync(s => s.SetProperty(w => w.TmdbId, to), ct);

        var favQ = db.Favorites.Where(f => f.UserId == userId && f.Type == type && f.TmdbId == from);
        if (await db.Favorites.AnyAsync(f => f.UserId == userId && f.Type == type && f.TmdbId == to, ct)) await favQ.ExecuteDeleteAsync(ct);
        await favQ.ExecuteUpdateAsync(s => s.SetProperty(f => f.TmdbId, to), ct);

        var itemsQ = db.ListItems.Where(i => i.Type == type && i.TmdbId == from && db.Lists.Any(l => l.Id == i.ListId && l.UserId == userId));
        await itemsQ.Where(i => db.ListItems.Any(t => t.ListId == i.ListId && t.Type == type && t.TmdbId == to)).ExecuteDeleteAsync(ct);
        var listEntries = await itemsQ.ExecuteUpdateAsync(s => s.SetProperty(i => i.TmdbId, to), ct);

        if (!movie)
        {
            var stateQ = db.ShowStates.Where(s => s.UserId == userId && s.ShowTmdbId == from);
            if (await db.ShowStates.AnyAsync(s => s.UserId == userId && s.ShowTmdbId == to, ct)) await stateQ.ExecuteDeleteAsync(ct);
            await stateQ.ExecuteUpdateAsync(s => s.SetProperty(x => x.ShowTmdbId, to), ct);
            await db.Rewatches.Where(r => r.UserId == userId && r.ShowTmdbId == from).ExecuteUpdateAsync(s => s.SetProperty(r => r.ShowTmdbId, to), ct);
        }
        await tx.CommitAsync(ct);
        return new RematchResult(plays, ratings, watchlist, listEntries);
    }

    /// <summary>Plays of the same movie/episode within a day of the one before: probably logged twice.</summary>
    public async Task<List<DuplicateGroup>> DuplicatesAsync(Guid userId, CancellationToken ct)
    {
        var plays = await db.WatchEvents.Where(w => w.UserId == userId && w.WatchedAt > WatchDates.Unknown)
            .Select(w => new { w.Id, w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt, w.Source }).ToListAsync(ct);
        var groups = new List<(MediaType Type, int TmdbId, int? Season, int? Episode, List<DuplicatePlay> Plays)>();
        foreach (var item in plays.GroupBy(p => (p.Type, p.TmdbId, p.SeasonNumber, p.EpisodeNumber)).Where(g => g.Count() > 1))
        {
            List<DuplicatePlay>? run = null;
            DuplicatePlay? previous = null;
            foreach (var p in item.OrderBy(p => p.WatchedAt))
            {
                var play = new DuplicatePlay(p.Id, p.WatchedAt, p.Source);
                if (previous is not null && play.WatchedAt - previous.WatchedAt <= TimeSpan.FromDays(1))
                {
                    if (run is null)
                    {
                        run = [previous];
                        groups.Add((item.Key.Type, item.Key.TmdbId, item.Key.SeasonNumber, item.Key.EpisodeNumber, run));
                    }
                    run.Add(play);
                }
                else run = null;
                previous = play;
            }
        }

        var (movies, shows) = await LoadMetaAsync(groups.Select(g => (g.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, g.TmdbId)), ct);
        return groups
            .OrderByDescending(g => g.Plays.Max(p => p.WatchedAt))
            .Take(500)
            .Select(g => g.Type == MediaType.Movie
                ? new DuplicateGroup(g.Type, g.TmdbId, null, null, movies.GetValueOrDefault(g.TmdbId)?.Title ?? $"Movie {g.TmdbId}", movies.GetValueOrDefault(g.TmdbId)?.PosterPath, g.Plays)
                : new DuplicateGroup(g.Type, g.TmdbId, g.Season, g.Episode, shows.GetValueOrDefault(g.TmdbId)?.Name ?? $"Show {g.TmdbId}", shows.GetValueOrDefault(g.TmdbId)?.PosterPath, g.Plays))
            .ToList();
    }

    /// <summary>The user's saved time zone if they have one, otherwise the one the device sent.</summary>
    public async Task<string?> ZoneIdAsync(Guid userId, string? deviceZone, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.TimeZone).FirstOrDefaultAsync(ct) ?? deviceZone;

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
        // Future dates are allowed (logging a planned viewing ahead); only nonsense is replaced.
        var now = DateTimeOffset.UtcNow;
        if (at is null || at > now.AddYears(5)) return now;
        return at.Value < new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero) ? now : at.Value.ToUniversalTime();
    }

    // ---------- History ----------

    public async Task<HistoryPage> GetHistoryAsync(Guid userId, HistoryQuery f, CancellationToken ct)
    {
        var pageSize = Math.Clamp(f.PageSize, 1, 100);
        var page = Math.Max(f.Page, 1);
        var q = FilterHistory(db.WatchEvents.Where(w => w.UserId == userId), f);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            // Find the matching plays once. As a filter inside every query below (count, page,
            // totals, day sums) the title/episode-name search ran over every episode again each time.
            var ids = await SearchPlaysAsync(userId, f.Q, ct);
            q = q.Where(w => ids.Contains(w.Id));
        }

        if (f.Type == MediaType.Show) return await ShowHistoryAsync(q, f, page, pageSize, ct);

        var total = await q.CountAsync(ct);
        var ordered = f.Oldest
            ? q.OrderBy(w => w.WatchedAt).ThenBy(w => w.CreatedAt)
            : q.OrderByDescending(w => w.WatchedAt).ThenByDescending(w => w.CreatedAt);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var minutes = total > 0 ? await MinutesAsync(q, ct) : 0;
        var entries = await ToHistoryAsync(rows, ct);
        return new(entries, page, (total + pageSize - 1) / pageSize, total, minutes, await DayTotalsAsync(q, entries, f.TimeZone, ct));
    }

    /// <summary>
    /// The Shows view: one row per show per day (unknown dates count as one "day"), newest first,
    /// with how many episodes and which. Paged by rows, so a day's group is never split.
    /// </summary>
    private async Task<HistoryPage> ShowHistoryAsync(IQueryable<WatchEvent> q, HistoryQuery f, int page, int pageSize, CancellationToken ct)
    {
        var tz = ReviewService.Zone(f.TimeZone);
        var plays = await q.Select(w => new { w.Id, w.TmdbId, S = w.SeasonNumber ?? 0, E = w.EpisodeNumber ?? 0, w.WatchedAt, w.CreatedAt }).ToListAsync(ct);
        var groups = plays
            .GroupBy(p => (Day: WatchDates.IsUnknown(p.WatchedAt) ? (DateOnly?)null : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.WatchedAt, tz).DateTime), p.TmdbId))
            .Select(g => (Key: g.Key, Plays: g.OrderBy(p => p.WatchedAt).ThenBy(p => p.CreatedAt).ToList()))
            .ToList();
        // Newest first; "date unknown" goes last (first when oldest-first), like the plain view.
        groups = (f.Oldest
                ? groups.OrderBy(g => g.Plays[0].WatchedAt).ThenBy(g => g.Plays[0].CreatedAt)
                : groups.OrderByDescending(g => g.Plays[^1].WatchedAt).ThenByDescending(g => g.Plays[^1].CreatedAt))
            .ToList();
        var total = groups.Count;
        var pageGroups = groups.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // Titles and runtimes for the plays on this page.
        var ids = pageGroups.SelectMany(g => g.Plays.Select(p => p.Id)).ToHashSet();
        var detailed = (await ToHistoryAsync(await db.WatchEvents.AsNoTracking().Where(w => ids.Contains(w.Id)).ToListAsync(ct), ct)).ToDictionary(e => e.Id);
        var entries = pageGroups.Select(g =>
        {
            var latest = detailed[(f.Oldest ? g.Plays[0] : g.Plays[^1]).Id];
            var eps = g.Plays.Select(p => (p.S, p.E)).Distinct().Order().ToList();
            string Code((int S, int E) x) => x.S == 0 ? $"Special {x.E}" : $"S{x.S:00}E{x.E:00}";
            var range = eps.Count == 1 ? Code(eps[0]) : $"{Code(eps[0])}–{Code(eps[^1])}";
            var runtime = g.Plays.Sum(p => detailed[p.Id].Runtime ?? 0);
            return latest with { Type = MediaType.Show, SeasonNumber = null, EpisodeNumber = null, EpisodeTitle = null,
                Runtime = runtime > 0 ? runtime : null, Episodes = g.Plays.Count, Range = range };
        }).ToList();

        var minutes = total > 0 ? await MinutesAsync(q, ct) : 0;
        return new(entries, page, (total + pageSize - 1) / pageSize, total, minutes, await DayTotalsAsync(q, entries, f.TimeZone, ct));
    }

    /// <summary>Total running time of everything matched, not just this page.</summary>
    private async Task<long> MinutesAsync(IQueryable<WatchEvent> q, CancellationToken ct)
    {
        var minutes = 0L;
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
        return minutes;
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

        return q;
    }

    /// <summary>The user's plays whose movie title, show name or episode name contains the text.</summary>
    private async Task<List<Guid>> SearchPlaysAsync(Guid userId, string text, CancellationToken ct)
    {
        var term = text.Trim().ToLower();
        var mine = db.WatchEvents.AsNoTracking().Where(w => w.UserId == userId);
        var movieIds = await db.Movies.Where(m => m.Title.ToLower().Contains(term)).Select(m => m.TmdbId).ToListAsync(ct);
        var showIds = await db.Shows.Where(s => s.Name.ToLower().Contains(term)).Select(s => s.TmdbId).ToListAsync(ct);
        var byTitle = await mine.Where(w => w.Type == MediaType.Movie ? movieIds.Contains(w.TmdbId) : showIds.Contains(w.TmdbId))
            .Select(w => w.Id).ToListAsync(ct);
        // Episode names: only among shows this user has watched.
        var watchedShows = mine.Where(w => w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct();
        var byEpisode = await mine.Where(w => w.Type == MediaType.Episode)
            .Join(db.Episodes.Where(e => watchedShows.Contains(e.ShowTmdbId) && e.Name != null && e.Name.ToLower().Contains(term)),
                w => new { S = w.TmdbId, N = w.SeasonNumber ?? -1, E = w.EpisodeNumber ?? -1 },
                e => new { S = e.ShowTmdbId, N = e.SeasonNumber, E = e.EpisodeNumber }, (w, e) => w.Id)
            .ToListAsync(ct);
        return [.. byTitle.Union(byEpisode)];
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
        var movies = await LiteMovies(movieIds).ToDictionaryAsync(m => m.TmdbId, ct);
        var shows = await LiteShows(showIds).ToDictionaryAsync(s => s.TmdbId, ct);
        // Only the seasons on this page, not every episode of every show (The Simpsons has 750+).
        var seasons = rows.Where(r => r.Type == MediaType.Episode).Select(r => r.SeasonNumber ?? 0).Distinct().ToList();
        var wanted = rows.Where(r => r.Type == MediaType.Episode).Select(r => (r.TmdbId, r.SeasonNumber ?? 0, r.EpisodeNumber ?? 0)).ToHashSet();
        var eps = (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId) && seasons.Contains(e.SeasonNumber))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Name, e.Runtime }).ToListAsync(ct))
            .Where(e => wanted.Contains((e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber)))
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

    // ---------- Favorites ----------

    public async Task<List<FavoriteEntry>> GetFavoritesAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.Favorites.Where(f => f.UserId == userId).OrderBy(f => f.Rank).ThenBy(f => f.AddedAt).ToListAsync(ct);
        var (movies, shows) = await LoadMetaAsync(rows.Select(r => (r.Type, r.TmdbId)), ct);
        return rows.Select(r => r.Type == MediaType.Movie
                ? movies.TryGetValue(r.TmdbId, out var m) ? new FavoriteEntry(CatalogService.ToSummary(m), r.AddedAt) : null
                : shows.TryGetValue(r.TmdbId, out var s) ? new FavoriteEntry(CatalogService.ToSummary(s), r.AddedAt) : null)
            .OfType<FavoriteEntry>().ToList();
    }

    /// <summary>Up to four favorite movies and four favorite shows, Letterboxd-style.</summary>
    public const int MaxFavorites = FavoriteLimits.PerType;

    /// <summary>Adds to the end of the user's favorites (no-op if it's already one). Returns false if it was already there.</summary>
    public async Task<bool> AddFavoriteAsync(Guid userId, MediaType type, int tmdbId, CancellationToken ct, DateTimeOffset? addedAt = null)
    {
        if (type is not (MediaType.Movie or MediaType.Show)) throw new TrackingException("Only movies and shows can be favorites.");
        await EnsureMetaAsync(type, tmdbId, ct);
        if (await db.Favorites.AnyAsync(f => f.UserId == userId && f.Type == type && f.TmdbId == tmdbId, ct)) return false;
        if (await db.Favorites.CountAsync(f => f.UserId == userId && f.Type == type, ct) >= MaxFavorites)
            throw new TrackingException($"You already have {MaxFavorites} favorite {(type == MediaType.Movie ? "movies" : "shows")}. Remove one first (Favorites page).");
        var rank = await db.Favorites.Where(f => f.UserId == userId).MaxAsync(f => (int?)f.Rank, ct) ?? 0;
        db.Favorites.Add(new Favorite { UserId = userId, Type = type, TmdbId = tmdbId, Rank = rank + 1, AddedAt = addedAt ?? DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveFavoriteAsync(Guid userId, MediaType type, int tmdbId, CancellationToken ct) =>
        await db.Favorites.Where(f => f.UserId == userId && f.Type == type && f.TmdbId == tmdbId).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Puts the favorites in the given order; any not listed keep their relative order after them.</summary>
    public async Task ReorderFavoritesAsync(Guid userId, IReadOnlyList<FavoriteRequest> order, CancellationToken ct)
    {
        var rows = await db.Favorites.Where(f => f.UserId == userId).OrderBy(f => f.Rank).ToListAsync(ct);
        var ranked = order.Select(o => rows.FirstOrDefault(r => r.Type == o.Type && r.TmdbId == o.TmdbId)).OfType<Favorite>()
            .Concat(rows).Distinct().ToList();
        for (var i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;
        await db.SaveChangesAsync(ct);
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
        else
        {
            // A show with a season number rates that season.
            if (req.Type == MediaType.Show && req.SeasonNumber is { } sn) season = sn;
            await EnsureMetaAsync(req.Type, req.TmdbId, ct);
        }

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
        var q = db.Ratings.AsNoTracking().Where(r => r.UserId == userId);
        if (type is not null) q = q.Where(r => r.Type == type);
        return await RatingEntriesAsync(await q.OrderByDescending(r => r.RatedAt).ToListAsync(ct), ct);
    }

    /// <summary>A profile's best-rated movies and shows, picked in the database rather than after loading every rating.</summary>
    public async Task<List<RatingEntry>> TopRatedAsync(Guid userId, int count, CancellationToken ct) =>
        await RatingEntriesAsync(await db.Ratings.AsNoTracking()
            .Where(r => r.UserId == userId && r.SeasonNumber == null && (r.Type == MediaType.Movie || r.Type == MediaType.Show))
            .OrderByDescending(r => r.Value).ThenByDescending(r => r.RatedAt).Take(count).ToListAsync(ct), ct);

    private async Task<List<RatingEntry>> RatingEntriesAsync(List<Rating> rows, CancellationToken ct)
    {
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
        var shows = await LiteShows(showIds).ToDictionaryAsync(s => s.TmdbId, ct);
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

    public async Task<StatsDto> GetStatsAsync(Guid userId, string? tzId, CancellationToken ct)
    {
        var tz = ReviewService.Zone(await ZoneIdAsync(userId, tzId, ct));
        var since = await TrackingSinceAsync(userId, ct);
        var watches = await db.WatchEvents.AsNoTracking().Where(w => w.UserId == userId)
            .Select(w => new { w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt }).ToListAsync(ct);

        var movieIds = watches.Where(w => w.Type == MediaType.Movie).Select(w => w.TmdbId).Distinct().ToList();
        var showIds = watches.Where(w => w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct().ToList();
        var movies = await LiteMovies(movieIds).ToDictionaryAsync(m => m.TmdbId, ct);
        var shows = await LiteShows(showIds).ToDictionaryAsync(s => s.TmdbId, ct);
        var runtimes = (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId) && e.Runtime != null)
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

        // Plays per month and per weekday only count plays logged as they happened (from the user's
        // "started tracking" day; see AppUser.TrackingSince), on the viewer's calendar.
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var months = Enumerable.Range(0, 12).Select(i => now.AddMonths(-11 + i)).Select(d => (d.Year, d.Month)).ToList();
        var tracked = watches.Where(w => !WatchDates.IsUnknown(w.WatchedAt))
            .Select(w => TimeZoneInfo.ConvertTime(w.WatchedAt, tz).DateTime)
            .Where(l => since is null || DateOnly.FromDateTime(l) >= since)
            .ToList();
        var byMonth = tracked.GroupBy(l => (l.Year, l.Month)).ToDictionary(g => g.Key, g => g.Count());

        var ratings = await db.Ratings.Where(r => r.UserId == userId).Select(r => r.Value).ToListAsync(ct);
        var weekdays = tracked.GroupBy(l => l.DayOfWeek).ToDictionary(g => g.Key, g => g.Count());

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

    // ---------- Clearing data ----------

    /// <summary>
    /// Deletes the chosen kinds of data for movies and/or shows. History for shows also clears their
    /// rewatches and paused/dropped status; clearing everything also forgets past imports.
    /// </summary>
    public async Task<ClearDataResult> ClearAsync(Guid userId, ImportParts parts, CancellationToken ct)
    {
        bool movies = parts.HasFlag(ImportParts.Movies), shows = parts.HasFlag(ImportParts.Shows);
        if (!movies && !shows) return new(0, 0, 0, 0);
        int plays = 0, ratings = 0, watchlist = 0, lists = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (parts.HasFlag(ImportParts.History))
        {
            plays = await db.WatchEvents.Where(w => w.UserId == userId
                && (movies && w.Type == MediaType.Movie || shows && w.Type != MediaType.Movie)).ExecuteDeleteAsync(ct);
            await db.CheckIns.Where(c => c.UserId == userId
                && (movies && c.Type == MediaType.Movie || shows && c.Type != MediaType.Movie)).ExecuteDeleteAsync(ct);
            if (shows)
            {
                await db.Rewatches.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
                await db.ShowStates.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
            }
        }
        if (parts.HasFlag(ImportParts.Ratings))
            ratings = await db.Ratings.Where(r => r.UserId == userId
                && (movies && r.Type == MediaType.Movie || shows && r.Type != MediaType.Movie)).ExecuteDeleteAsync(ct);
        if (parts.HasFlag(ImportParts.Watchlist))
            watchlist = await db.Watchlist.Where(w => w.UserId == userId
                && (movies && w.Type == MediaType.Movie || shows && w.Type != MediaType.Movie)).ExecuteDeleteAsync(ct);
        if (parts.HasFlag(ImportParts.Lists))
        {
            lists += await db.Favorites.Where(f => f.UserId == userId
                && (movies && f.Type == MediaType.Movie || shows && f.Type != MediaType.Movie)).ExecuteDeleteAsync(ct);
            if (movies && shows)
                lists += await db.Lists.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
            else
                lists += await db.ListItems.Where(i => db.Lists.Any(l => l.Id == i.ListId && l.UserId == userId)
                    && (movies ? i.Type == MediaType.Movie : i.Type != MediaType.Movie)).ExecuteDeleteAsync(ct);
        }
        if (parts == ImportParts.All)
        {
            await db.ImportJobs.Where(j => j.UserId == userId).ExecuteDeleteAsync(ct);
            await db.JellyfinEvents.Where(j => j.UserId == userId).ExecuteDeleteAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(plays, ratings, watchlist, lists);
    }

    // ---------- Helpers ----------

    public Task<DateOnly?> TrackingSinceAsync(Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).Select(u => u.TrackingSince).FirstOrDefaultAsync(ct);

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
        return (await LiteMovies(movieIds).ToDictionaryAsync(m => m.TmdbId, ct),
                await LiteShows(showIds).ToDictionaryAsync(s => s.TmdbId, ct));
    }

    /// <summary>
    /// Movies with just what lists, posters and totals need: no cast, crew, studios or countries
    /// (those are JSON columns that make loading thousands of rows slow). Not tracked; don't save them.
    /// </summary>
    public IQueryable<MovieMeta> LiteMovies(IReadOnlyCollection<int> ids) => db.Movies.AsNoTracking().Where(m => ids.Contains(m.TmdbId))
        .Select(m => new MovieMeta
        {
            TmdbId = m.TmdbId, Title = m.Title, ReleaseDate = m.ReleaseDate, Runtime = m.Runtime, PosterPath = m.PosterPath,
            BackdropPath = m.BackdropPath, Genres = m.Genres, VoteAverage = m.VoteAverage, Overview = m.Overview,
        });

    /// <summary>Shows with just what lists, posters and totals need (see <see cref="LiteMovies"/>).</summary>
    public IQueryable<ShowMeta> LiteShows(IReadOnlyCollection<int> ids) => db.Shows.AsNoTracking().Where(s => ids.Contains(s.TmdbId))
        .Select(s => new ShowMeta
        {
            TmdbId = s.TmdbId, Name = s.Name, FirstAirDate = s.FirstAirDate, Status = s.Status, PosterPath = s.PosterPath,
            BackdropPath = s.BackdropPath, Genres = s.Genres, EpisodeRuntime = s.EpisodeRuntime, VoteAverage = s.VoteAverage, Overview = s.Overview,
        });
}
