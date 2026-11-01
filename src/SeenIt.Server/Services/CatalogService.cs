using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>
/// Reads TMDB and keeps a local copy of the metadata tracking depends on (titles, runtimes,
/// genres, episode lists), so history, progress, calendar and stats never call TMDB.
/// </summary>
public class CatalogService(AppDbContext db, TmdbClient tmdb, ILogger<CatalogService> log)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly TimeSpan MovieTtl = TimeSpan.FromDays(7);

    public static TimeSpan ShowTtl(ShowMeta s) => s.IsOngoing ? TimeSpan.FromHours(12) : TimeSpan.FromDays(14);

    // ---------- Metadata cache ----------

    public async Task<MovieMeta?> EnsureMovieAsync(int id, CancellationToken ct = default)
    {
        var meta = await db.Movies.FindAsync([id], ct);
        if (IsFresh(meta)) return meta;

        using var _ = await LockAsync($"m{id}", ct);
        meta = await db.Movies.FindAsync([id], ct);
        if (IsFresh(meta)) return meta;

        var m = await tmdb.GetMovieAsync(id, full: false, ct);
        if (m is null) return meta;
        meta = UpsertMovie(m, meta);
        await db.SaveChangesAsync(ct);
        return meta;
    }

    /// <summary>Makes sure the show and its full episode list are cached and fresh enough.</summary>
    public async Task<ShowMeta?> EnsureShowAsync(int id, CancellationToken ct = default)
    {
        var meta = await db.Shows.FindAsync([id], ct);
        if (IsFresh(meta)) return meta;

        using var _ = await LockAsync($"s{id}", ct);
        await db.Entry(meta ?? new ShowMeta()).ReloadIfTrackedAsync(ct);
        meta = await db.Shows.FindAsync([id], ct);
        if (IsFresh(meta)) return meta;

        try
        {
            return await RefreshShowAsync(id, meta, ct) ?? meta;
        }
        catch (Exception ex) when (meta is not null && ex is not OperationCanceledException)
        {
            // A stale copy beats failing the request.
            log.LogWarning(ex, "Refreshing show {Id} failed; using cached copy", id);
            return meta;
        }
    }

    private static bool IsFresh(ShowMeta? s) =>
        s?.EpisodesUpdatedAt is { } at && DateTimeOffset.UtcNow - at < ShowTtl(s) && s.MetaVersion >= MetaVersions.Current;

    private static bool IsFresh(MovieMeta? m) =>
        m is not null && DateTimeOffset.UtcNow - m.UpdatedAt < MovieTtl && m.MetaVersion >= MetaVersions.Current;

    public async Task<ShowMeta?> RefreshShowAsync(int id, ShowMeta? meta, CancellationToken ct)
    {
        var show = await tmdb.GetShowAsync(id, full: false, ct);
        if (show is null) return null;

        meta = UpsertShow(show, meta);

        var seasonNumbers = (show.Seasons ?? []).Select(s => s.SeasonNumber).ToList();
        var fetched = new List<TmdbEpisode>();
        foreach (var chunk in seasonNumbers.Chunk(20))
        {
            var withSeasons = await tmdb.GetShowWithSeasonsAsync(id, chunk, ct);
            if (withSeasons?.Extra is null) continue;
            foreach (var n in chunk)
            {
                if (!withSeasons.Extra.TryGetValue($"season/{n}", out var el) || el.ValueKind != JsonValueKind.Object) continue;
                var season = el.Deserialize<TmdbSeason>(TmdbClient.Json);
                if (season?.Episodes is not null) fetched.AddRange(season.Episodes);
            }
        }

        var existing = await db.Episodes.Where(e => e.ShowTmdbId == id).ToListAsync(ct);
        var byKey = existing.ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber));
        var seen = new HashSet<(int, int)>();
        foreach (var e in fetched)
        {
            if (!seen.Add((e.SeasonNumber, e.EpisodeNumber))) continue;
            if (!byKey.TryGetValue((e.SeasonNumber, e.EpisodeNumber), out var row))
            {
                row = new EpisodeMeta { ShowTmdbId = id, SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber };
                db.Episodes.Add(row);
            }
            row.TmdbId = e.Id;
            row.Name = e.Name;
            row.Overview = e.Overview;
            row.AirDate = TmdbDates.Parse(e.AirDate);
            row.Runtime = e.Runtime;
            row.StillPath = e.StillPath;
            row.VoteAverage = e.VoteAverage;
        }
        // Only prune when the fetch clearly worked, so a partial TMDB response can't wipe a show.
        if (fetched.Count > 0)
            db.Episodes.RemoveRange(existing.Where(e => !seen.Contains((e.SeasonNumber, e.EpisodeNumber))));

        meta.EpisodesUpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return meta;
    }

    private MovieMeta UpsertMovie(TmdbMovie m, MovieMeta? meta)
    {
        if (meta is null)
        {
            meta = new MovieMeta { TmdbId = m.Id };
            db.Movies.Add(meta);
        }
        meta.Title = m.Title;
        meta.ReleaseDate = TmdbDates.Parse(m.ReleaseDate);
        meta.Runtime = m.Runtime is > 0 ? m.Runtime : meta.Runtime;
        meta.PosterPath = m.PosterPath;
        meta.BackdropPath = m.BackdropPath;
        meta.Genres = m.Genres?.Select(g => g.Name).ToList() ?? [];
        meta.ImdbId = m.ImdbId;
        meta.VoteAverage = m.VoteAverage;
        meta.Overview = m.Overview;
        meta.UpdatedAt = DateTimeOffset.UtcNow;
        meta.OriginalLanguage = m.OriginalLanguage;
        meta.Countries = m.ProductionCountries?.Select(c => c.Iso31661 ?? c.Name).ToList() ?? []; // ISO codes, like shows
        meta.Studios = m.ProductionCompanies?.Select(c => c.Name).Take(5).ToList() ?? [];
        if (m.Credits is { } credits)
        {
            meta.Cast = (credits.Cast ?? []).OrderBy(c => c.Order).Take(15).Select(Person).ToList();
            meta.Directors = (credits.Crew ?? []).Where(c => c.Job == "Director").DistinctBy(c => c.Id)
                .Select(c => new PersonCredit { Id = c.Id, Name = c.Name, ProfilePath = c.ProfilePath }).ToList();
            meta.MetaVersion = MetaVersions.Current;
        }
        return meta;
    }

    private ShowMeta UpsertShow(TmdbShow s, ShowMeta? meta)
    {
        if (meta is null)
        {
            meta = new ShowMeta { TmdbId = s.Id };
            db.Shows.Add(meta);
        }
        meta.Name = s.Name;
        meta.FirstAirDate = TmdbDates.Parse(s.FirstAirDate);
        meta.Status = s.Status;
        meta.PosterPath = s.PosterPath;
        meta.BackdropPath = s.BackdropPath;
        meta.Genres = s.Genres?.Select(g => g.Name).ToList() ?? [];
        meta.EpisodeRuntime = s.EpisodeRunTime is { Count: > 0 } r ? (int)r.Average() : meta.EpisodeRuntime;
        meta.VoteAverage = s.VoteAverage;
        meta.Overview = s.Overview;
        meta.UpdatedAt = DateTimeOffset.UtcNow;
        meta.OriginalLanguage = s.OriginalLanguage;
        meta.Countries = s.OriginCountry ?? [];
        meta.Networks = s.Networks?.Select(n => n.Name).ToList() ?? [];
        meta.Creators = s.CreatedBy?.Select(c => new PersonCredit { Id = c.Id, Name = c.Name, ProfilePath = c.ProfilePath }).ToList() ?? [];
        if ((s.AggregateCredits ?? s.Credits) is { } credits)
        {
            meta.Cast = (credits.Cast ?? []).OrderBy(c => c.Order).Take(15).Select(Person).ToList();
            meta.MetaVersion = MetaVersions.Current;
        }
        return meta;
    }

    private static PersonCredit Person(TmdbCast c) => new() { Id = c.Id, Name = c.Name, ProfilePath = c.ProfilePath };

    private static async Task<IDisposable> LockAsync(string key, CancellationToken ct)
    {
        var sem = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        public void Dispose() => sem.Release();
    }

    // ---------- Browse ----------

    public static MediaSummary ToSummary(TmdbListItem i, MediaType? fallback = null)
    {
        var type = i.MediaType switch
        {
            "movie" => MediaType.Movie,
            "tv" => MediaType.Show,
            _ => fallback ?? (i.Title is not null ? MediaType.Movie : MediaType.Show),
        };
        var date = TmdbDates.Parse(type == MediaType.Movie ? i.ReleaseDate : i.FirstAirDate);
        return new MediaSummary(type, i.Id, (type == MediaType.Movie ? i.Title : i.Name) ?? i.Title ?? i.Name ?? "",
            date?.Year, i.PosterPath, i.BackdropPath, i.VoteAverage, i.Overview);
    }

    public static MediaSummary ToSummary(MovieMeta m) =>
        new(MediaType.Movie, m.TmdbId, m.Title, m.ReleaseDate?.Year, m.PosterPath, m.BackdropPath, m.VoteAverage, m.Overview);

    public static MediaSummary ToSummary(ShowMeta s) =>
        new(MediaType.Show, s.TmdbId, s.Name, s.FirstAirDate?.Year, s.PosterPath, s.BackdropPath, s.VoteAverage, s.Overview);

    public async Task<PagedResult<MediaSummary>> SearchAsync(string query, string? type, int page, CancellationToken ct)
    {
        TmdbPage<TmdbListItem>? res = type switch
        {
            "movie" => await tmdb.SearchMovieAsync(query, null, ct),
            "show" => await tmdb.SearchShowAsync(query, null, ct),
            _ => await tmdb.SearchMultiAsync(query, page, ct),
        };
        if (res is null) return new([], 1, 0, 0);
        var fallback = type == "movie" ? MediaType.Movie : type == "show" ? MediaType.Show : (MediaType?)null;
        var items = res.Results
            .Where(r => r.MediaType is null or "movie" or "tv")
            .Select(r => ToSummary(r, fallback))
            .ToList();
        return new(items, res.Page, res.TotalPages, res.TotalResults);
    }

    public async Task<PagedResult<MediaSummary>> BrowseAsync(MediaType type, string list, int page, CancellationToken ct)
    {
        var allowed = type == MediaType.Movie
            ? new[] { "trending/day", "trending/week", "popular", "top_rated", "upcoming", "now_playing" }
            : new[] { "trending/day", "trending/week", "popular", "top_rated", "on_the_air", "airing_today" };
        if (!allowed.Contains(list)) list = "trending/week";

        var res = await tmdb.GetListAsync(type == MediaType.Movie ? "movie" : "tv", list, Math.Clamp(page, 1, 500), ct);
        if (res is null) return new([], 1, 0, 0);
        return new(res.Results.Select(r => ToSummary(r, type)).ToList(), res.Page, res.TotalPages, res.TotalResults);
    }

    // ---------- Detail pages ----------

    public async Task<MovieDetail?> GetMovieAsync(int id, Guid? userId, CancellationToken ct)
    {
        var m = await tmdb.GetMovieAsync(id, full: true, ct);
        if (m is null) return null;

        // Opportunistically refresh the cached copy.
        var meta = await db.Movies.FindAsync([id], ct);
        if (meta is null || DateTimeOffset.UtcNow - meta.UpdatedAt > TimeSpan.FromDays(1) || meta.MetaVersion < MetaVersions.Current)
        {
            UpsertMovie(m, meta);
            await SaveIgnoringRacesAsync(ct);
        }

        var user = userId is { } uid ? await GetUserStateAsync(uid, MediaType.Movie, id, ct) : null;

        return new MovieDetail(
            m.Id, m.Title, NullIfEmpty(m.Tagline), m.Overview, TmdbDates.Parse(m.ReleaseDate), m.Runtime,
            m.Genres?.Select(g => g.Name).ToList() ?? [],
            m.PosterPath, m.BackdropPath, m.VoteAverage, m.VoteCount, m.ImdbId, m.Status,
            (m.Credits?.Cast ?? []).OrderBy(c => c.Order).Take(20).Select(c => new CastMember(c.Id, c.Name, c.AnyCharacter, c.ProfilePath)).ToList(),
            (m.Credits?.Crew ?? []).Where(c => c.Job is "Director" or "Screenplay" or "Writer" or "Original Music Composer" or "Director of Photography")
                .Take(12).Select(c => new CrewMember(c.Id, c.Name, c.Job, c.ProfilePath)).ToList(),
            PickVideos(m.Videos),
            (m.Recommendations?.Results ?? []).Take(18).Select(r => ToSummary(r, MediaType.Movie)).ToList(),
            user);
    }

    public async Task<ShowDetail?> GetShowAsync(int id, Guid? userId, CancellationToken ct)
    {
        var s = await tmdb.GetShowAsync(id, full: true, ct);
        if (s is null) return null;

        // The page shows per-season progress, which needs the episode list cached.
        await EnsureShowAsync(id, ct);

        UserMediaState? user = null;
        ShowProgressDto? progress = null;
        Dictionary<int, int> watchedPerSeason = [];
        if (userId is { } uid)
        {
            user = await GetUserStateAsync(uid, MediaType.Show, id, ct);
            var watched = await db.WatchEvents
                .Where(w => w.UserId == uid && w.Type == MediaType.Episode && w.TmdbId == id)
                .Select(w => new { w.SeasonNumber, w.EpisodeNumber, w.WatchedAt })
                .ToListAsync(ct);
            watchedPerSeason = watched.Select(w => (w.SeasonNumber, w.EpisodeNumber)).Distinct()
                .GroupBy(w => w.SeasonNumber ?? 0).ToDictionary(g => g.Key, g => g.Count());
            if (watched.Count > 0)
                progress = (await new ProgressCalculator(db).ForUserAsync(uid, [id], ct)).FirstOrDefault();
        }

        return new ShowDetail(
            s.Id, s.Name, NullIfEmpty(s.Tagline), s.Overview, TmdbDates.Parse(s.FirstAirDate), TmdbDates.Parse(s.LastAirDate), s.Status,
            s.Networks?.Select(n => n.Name).ToList() ?? [],
            s.Genres?.Select(g => g.Name).ToList() ?? [],
            s.EpisodeRunTime is { Count: > 0 } r ? (int)r.Average() : null,
            s.PosterPath, s.BackdropPath, s.VoteAverage, s.VoteCount, s.NumberOfSeasons, s.NumberOfEpisodes,
            (s.Seasons ?? []).OrderBy(x => x.SeasonNumber == 0 ? int.MaxValue : x.SeasonNumber)
                .Select(x => new SeasonSummary(x.SeasonNumber, x.Name ?? $"Season {x.SeasonNumber}", x.EpisodeCount, TmdbDates.Parse(x.AirDate), x.PosterPath,
                    watchedPerSeason.GetValueOrDefault(x.SeasonNumber))).ToList(),
            (s.AggregateCredits?.Cast ?? s.Credits?.Cast ?? []).OrderBy(c => c.Order).Take(20)
                .Select(c => new CastMember(c.Id, c.Name, c.AnyCharacter, c.ProfilePath)).ToList(),
            PickVideos(s.Videos),
            (s.Recommendations?.Results ?? []).Take(18).Select(x => ToSummary(x, MediaType.Show)).ToList(),
            s.NextEpisodeToAir is { } n ? new EpisodeRef(n.SeasonNumber, n.EpisodeNumber, n.Name, TmdbDates.Parse(n.AirDate)) : null,
            user,
            progress);
    }

    public async Task<SeasonDetail?> GetSeasonAsync(int showId, int seasonNumber, Guid? userId, CancellationToken ct)
    {
        var show = await EnsureShowAsync(showId, ct);
        var season = await tmdb.GetSeasonAsync(showId, seasonNumber, ct);
        if (show is null || season is null) return null;

        Dictionary<int, int> plays = [];
        Dictionary<int, int> ratings = [];
        if (userId is { } uid)
        {
            plays = await db.WatchEvents
                .Where(w => w.UserId == uid && w.Type == MediaType.Episode && w.TmdbId == showId && w.SeasonNumber == seasonNumber)
                .GroupBy(w => w.EpisodeNumber!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            ratings = await db.Ratings
                .Where(r => r.UserId == uid && r.Type == MediaType.Episode && r.TmdbId == showId && r.SeasonNumber == seasonNumber)
                .ToDictionaryAsync(r => r.EpisodeNumber!.Value, r => r.Value, ct);
        }

        return new SeasonDetail(showId, show.Name, seasonNumber, season.Name ?? $"Season {seasonNumber}", NullIfEmpty(season.Overview), season.PosterPath,
            (season.Episodes ?? []).Select(e => new EpisodeDto(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Name, e.Overview,
                TmdbDates.Parse(e.AirDate), e.Runtime, e.StillPath, e.VoteAverage,
                plays.GetValueOrDefault(e.EpisodeNumber), ratings.TryGetValue(e.EpisodeNumber, out var r) ? r : null)).ToList());
    }

    public async Task<EpisodeDetail?> GetEpisodeAsync(int showId, int seasonNumber, int episodeNumber, Guid? userId, CancellationToken ct)
    {
        var show = await EnsureShowAsync(showId, ct);
        var e = await tmdb.GetEpisodeAsync(showId, seasonNumber, episodeNumber, ct);
        if (show is null || e is null) return null;

        // Previous/next across season boundaries, from the cached episode list (specials stay within season 0).
        var siblings = await db.Episodes
            .Where(x => x.ShowTmdbId == showId && (seasonNumber == 0 ? x.SeasonNumber == 0 : x.SeasonNumber > 0))
            .OrderBy(x => x.SeasonNumber).ThenBy(x => x.EpisodeNumber)
            .Select(x => new { x.SeasonNumber, x.EpisodeNumber, x.Name, x.AirDate })
            .ToListAsync(ct);
        var index = siblings.FindIndex(x => x.SeasonNumber == seasonNumber && x.EpisodeNumber == episodeNumber);
        var watchedSet = userId is { } viewer
            ? (await db.WatchEvents.Where(w => w.UserId == viewer && w.Type == MediaType.Episode && w.TmdbId == showId)
                .Select(w => new { w.SeasonNumber, w.EpisodeNumber }).ToListAsync(ct))
                .Select(w => (w.SeasonNumber ?? -1, w.EpisodeNumber ?? -1)).ToHashSet()
            : [];
        EpisodeRef? Ref(int i) => i < 0 || i >= siblings.Count ? null
            : new EpisodeRef(siblings[i].SeasonNumber, siblings[i].EpisodeNumber, siblings[i].Name, siblings[i].AirDate,
                watchedSet.Contains((siblings[i].SeasonNumber, siblings[i].EpisodeNumber)));

        int? rating = null;
        List<PlayDto>? plays = null;
        if (userId is { } uid)
        {
            plays = await db.WatchEvents
                .Where(w => w.UserId == uid && w.Type == MediaType.Episode && w.TmdbId == showId
                            && w.SeasonNumber == seasonNumber && w.EpisodeNumber == episodeNumber)
                .OrderByDescending(w => w.WatchedAt)
                .Select(w => new PlayDto(w.Id, w.WatchedAt, w.Source))
                .ToListAsync(ct);
            rating = await db.Ratings
                .Where(r => r.UserId == uid && r.Type == MediaType.Episode && r.TmdbId == showId
                            && r.SeasonNumber == seasonNumber && r.EpisodeNumber == episodeNumber)
                .Select(r => (int?)r.Value).FirstOrDefaultAsync(ct);
        }

        return new EpisodeDetail(
            showId, show.Name, show.PosterPath, show.BackdropPath, e.SeasonNumber, e.EpisodeNumber, e.Id,
            e.Name, NullIfEmpty(e.Overview), TmdbDates.Parse(e.AirDate), e.Runtime, e.StillPath, e.VoteAverage, e.VoteCount,
            (e.GuestStars ?? []).OrderBy(c => c.Order).Take(20).Select(c => new CastMember(c.Id, c.Name, c.AnyCharacter, c.ProfilePath)).ToList(),
            (e.Crew ?? []).Where(c => c.Job is "Director" or "Writer" or "Teleplay" or "Story" or "Screenplay")
                .DistinctBy(c => (c.Id, c.Job)).Select(c => new CrewMember(c.Id, c.Name, c.Job, c.ProfilePath)).ToList(),
            index < 0 ? null : Ref(index - 1),
            index < 0 ? null : Ref(index + 1),
            rating,
            plays);
    }

    private async Task<UserMediaState> GetUserStateAsync(Guid userId, MediaType type, int id, CancellationToken ct)
    {
        var inWatchlist = await db.Watchlist.AnyAsync(w => w.UserId == userId && w.Type == type && w.TmdbId == id, ct);
        var rating = await db.Ratings
            .Where(r => r.UserId == userId && r.Type == type && r.TmdbId == id && r.SeasonNumber == null && r.EpisodeNumber == null)
            .Select(r => (int?)r.Value).FirstOrDefaultAsync(ct);
        var watchType = type == MediaType.Show ? MediaType.Episode : MediaType.Movie;
        var watches = db.WatchEvents.Where(w => w.UserId == userId && w.Type == watchType && w.TmdbId == id);
        var plays = await watches.CountAsync(ct);
        var last = await watches.MaxAsync(w => (DateTimeOffset?)w.WatchedAt, ct);
        var lists = await db.ListItems
            .Where(i => i.Type == type && i.TmdbId == id && db.Lists.Any(l => l.Id == i.ListId && l.UserId == userId))
            .Select(i => i.ListId).ToListAsync(ct);
        return new UserMediaState(inWatchlist, rating, plays, last, lists);
    }

    private static List<Video> PickVideos(TmdbVideos? v) =>
        (v?.Results ?? []).Where(x => x.Site == "YouTube" && x.Type is "Trailer" or "Teaser")
            .OrderByDescending(x => x.Type == "Trailer").ThenByDescending(x => x.Official)
            .Take(3).Select(x => new Video(x.Key, x.Name, x.Site, x.Type)).ToList();

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private async Task SaveIgnoringRacesAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            // Another request cached the same title first; theirs is just as good.
            log.LogDebug(ex, "Ignored metadata upsert race");
            db.ChangeTracker.Clear();
        }
    }
}

internal static class EfExtensions
{
    public static async Task ReloadIfTrackedAsync(this Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, CancellationToken ct)
    {
        if (entry.State is not EntityState.Detached and not EntityState.Added)
            await entry.ReloadAsync(ct);
    }
}
