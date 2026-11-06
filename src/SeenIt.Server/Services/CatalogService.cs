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
    /// <summary>
    /// How long cached movie details are trusted: a week for anything released in the last year (or
    /// not out yet: dates, posters and runtimes still change), a month for older films.
    /// </summary>
    public static TimeSpan MovieTtl(MovieMeta m) =>
        m.ReleaseDate is { } d && d > DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1) || m.ReleaseDate is null
            ? TimeSpan.FromDays(7) : TimeSpan.FromDays(30);

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
        if (m is null)
        {
            // Gone from TMDB (deleted or merged): keep what we have rather than lose people's
            // history, and don't ask again until it's due.
            if (meta is not null)
            {
                log.LogWarning("Movie {Id} ({Title}) is no longer on TMDB; keeping the cached details", id, meta.Title);
                meta.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return meta;
        }
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
        m is not null && DateTimeOffset.UtcNow - m.UpdatedAt < MovieTtl(m) && m.MetaVersion >= MetaVersions.Current;

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
        meta.CollectionId = m.BelongsToCollection?.Id;
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

    private static readonly System.Text.RegularExpressions.Regex ImdbRe = new(@"^(?:https?://)?(?:[a-z]+\.)?imdb\.com/(?:[a-z]{2}/)?(?:title|name)/((?:tt|nm)\d{5,10})|^((?:tt|nm)\d{5,10})$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex TmdbRe = new(@"^(?:https?://)?(?:www\.)?themoviedb\.org/(movie|tv|person)/(\d+)(?:-[^/?#]*)?(?:/season/(\d+)(?:/episode/(\d+))?)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Null when the query isn't an id or link (search normally then).</summary>
    public async Task<LookupResult?> LookupAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (ImdbRe.Match(query) is { Success: true } im)
        {
            var id = (im.Groups[1].Success ? im.Groups[1].Value : im.Groups[2].Value).ToLowerInvariant();
            var found = await tmdb.FindAsync(id, "imdb_id", ct);
            var titles = (found?.MovieResults ?? []).Select(r => ToSummary(r, MediaType.Movie))
                .Concat((found?.TvResults ?? []).Select(r => ToSummary(r, MediaType.Show))).ToList();
            var people = (found?.PersonResults ?? []).Select(Hit).ToList();
            var ep = found?.TvEpisodeResults?.FirstOrDefault();
            string? go = titles.Count + people.Count == 1
                ? titles.Count == 1 ? Href(titles[0]) : $"person/{people[0].Id}"
                : titles.Count + people.Count == 0 && ep is not null ? $"show/{ep.ShowId}/season/{ep.SeasonNumber}/episode/{ep.EpisodeNumber}" : null;
            return new LookupResult($"IMDb {id}", titles, people, go);
        }
        if (TmdbRe.Match(query) is { Success: true } tm)
        {
            var id = int.Parse(tm.Groups[2].Value);
            switch (tm.Groups[1].Value.ToLowerInvariant())
            {
                case "movie":
                    var movie = await MovieSummaryAsync(id, ct);
                    return new LookupResult($"TMDB movie {id}", movie is null ? [] : [movie], [], movie is null ? null : $"movie/{id}");
                case "tv":
                    var show = await ShowSummaryAsync(id, ct);
                    var go = show is null ? null
                        : tm.Groups[4].Success ? $"show/{id}/season/{tm.Groups[3].Value}/episode/{tm.Groups[4].Value}"
                        : tm.Groups[3].Success ? $"show/{id}/season/{tm.Groups[3].Value}"
                        : $"show/{id}";
                    return new LookupResult($"TMDB show {id}", show is null ? [] : [show], [], go);
                default:
                    var person = await tmdb.GetPersonAsync(id, ct);
                    var hit = person is null ? null : new PersonHit(person.Id, person.Name, person.ProfilePath, person.KnownForDepartment, []);
                    return new LookupResult($"TMDB person {id}", [], hit is null ? [] : [hit], hit is null ? null : $"person/{id}");
            }
        }
        if (query.Length <= 8 && query.All(char.IsAsciiDigit) && int.TryParse(query, out var n) && n > 0)
        {
            // A bare number might be either kind of TMDB id (or a title like "1917"): offer both, open neither.
            var movie = MovieSummaryAsync(n, ct);
            var show = ShowSummaryAsync(n, ct);
            var titles = new[] { await movie, await show }.OfType<MediaSummary>().ToList();
            return new LookupResult($"TMDB id {n}", titles, [], null);
        }
        return null;
    }

    private static string Href(MediaSummary m) => m.Type == MediaType.Movie ? $"movie/{m.TmdbId}" : $"show/{m.TmdbId}";

    private static PersonHit Hit(TmdbSearchPerson p) => new(p.Id, p.Name, p.ProfilePath, p.KnownForDepartment,
        (p.KnownFor ?? []).Select(k => k.Title ?? k.Name).OfType<string>().Take(3).ToList());

    private async Task<MediaSummary?> MovieSummaryAsync(int id, CancellationToken ct) =>
        await tmdb.GetMovieAsync(id, full: false, ct) is { } m
            ? new MediaSummary(MediaType.Movie, m.Id, m.Title, TmdbDates.Parse(m.ReleaseDate)?.Year, m.PosterPath, m.BackdropPath, m.VoteAverage, m.Overview)
            : null;

    private async Task<MediaSummary?> ShowSummaryAsync(int id, CancellationToken ct) =>
        await tmdb.GetShowAsync(id, full: false, ct) is { } s
            ? new MediaSummary(MediaType.Show, s.Id, s.Name, TmdbDates.Parse(s.FirstAirDate)?.Year, s.PosterPath, s.BackdropPath, s.VoteAverage, s.Overview)
            : null;

    public async Task<PagedResult<PersonHit>> SearchPeopleAsync(string query, int page, CancellationToken ct)
    {
        var res = await tmdb.SearchPersonAsync(query, page, ct);
        if (res is null) return new([], 1, 0, 0);
        var items = res.Results.Select(Hit).ToList();
        return new(items, res.Page, res.TotalPages, res.TotalResults);
    }

    /// <summary>What the user marked "not interested", as "Type:Id" keys.</summary>
    public async Task<HashSet<(MediaType, int)>> NotInterestedAsync(Guid? userId, CancellationToken ct) => userId is { } uid
        ? (await db.NotInterested.Where(n => n.UserId == uid).Select(n => new { n.Type, n.TmdbId }).ToListAsync(ct)).Select(n => (n.Type, n.TmdbId)).ToHashSet()
        : [];

    public async Task<PagedResult<MediaSummary>> BrowseAsync(MediaType type, string list, int page, CancellationToken ct, string region = "US")
    {
        var allowed = type == MediaType.Movie
            ? new[] { "trending/day", "trending/week", "popular", "top_rated", "upcoming", "now_playing" }
            : new[] { "trending/day", "trending/week", "popular", "top_rated", "on_the_air", "airing_today" };
        if (!allowed.Contains(list)) list = "trending/week";

        page = Math.Clamp(page, 1, 500);
        var res = type == MediaType.Movie && list is "upcoming" or "now_playing"
            ? await tmdb.GetTheatricalAsync(list == "upcoming", region, page, ct)
            : await tmdb.GetListAsync(type == MediaType.Movie ? "movie" : "tv", list, page, ct);
        // Upcoming: leave out anything already out (re-releases, or a wide release after a limited one).
        if (res is not null && type == MediaType.Movie && list == "upcoming")
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            res = res with { Results = res.Results.Where(r => string.IsNullOrEmpty(r.ReleaseDate) || string.CompareOrdinal(r.ReleaseDate, today) > 0).ToList() };
        }
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
        CollectionDto? collection = null;
        if (m.BelongsToCollection is { } belongs)
        {
            try { collection = await GetCollectionAsync(belongs.Id, userId, ct); }
            catch (HttpRequestException ex) { log.LogInformation(ex, "Couldn't load collection {Id}", belongs.Id); }
        }

        return new MovieDetail(
            m.Id, m.Title, NullIfEmpty(m.Tagline), m.Overview, TmdbDates.Parse(m.ReleaseDate), m.Runtime,
            m.Genres?.Select(g => g.Name).ToList() ?? [],
            m.PosterPath, m.BackdropPath, m.VoteAverage, m.VoteCount, m.ImdbId, m.Status,
            Cast(m.Credits?.Cast),
            (m.Credits?.Crew ?? []).Where(c => c.Job is "Director" or "Screenplay" or "Writer" or "Original Music Composer" or "Director of Photography")
                .Take(12).Select(c => new CrewMember(c.Id, c.Name, c.Job, c.ProfilePath)).ToList(),
            PickVideos(m.Videos),
            (m.Recommendations?.Results ?? []).Take(18).Select(r => ToSummary(r, MediaType.Movie)).ToList(),
            user,
            collection);
    }

    /// <summary>A franchise's movies in release order (unreleased ones last), marking which the user has seen.</summary>
    public async Task<CollectionDto?> GetCollectionAsync(int id, Guid? userId, CancellationToken ct)
    {
        var c = await tmdb.GetCollectionAsync(id, ct);
        if (c?.Parts is not { Count: > 1 } parts) return null; // a "collection" of one isn't worth showing
        var ids = parts.Select(p => p.Id).ToList();
        var seen = userId is { } uid
            ? (await db.WatchEvents.Where(w => w.UserId == uid && w.Type == MediaType.Movie && ids.Contains(w.TmdbId))
                .Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet()
            : [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new CollectionDto(c.Id, c.Name, c.PosterPath, c.BackdropPath,
            parts.Select(p => (Part: p, Date: TmdbDates.Parse(p.ReleaseDate)))
                .OrderBy(x => x.Date is null).ThenBy(x => x.Date)
                .Select(x => new CollectionPart(ToSummary(x.Part, MediaType.Movie), x.Date, x.Date is { } d && d <= today, seen.Contains(x.Part.Id)))
                .ToList());
    }

    public async Task<ShowDetail?> GetShowAsync(int id, Guid? userId, CancellationToken ct)
    {
        var s = await tmdb.GetShowAsync(id, full: true, ct);
        if (s is null) return null;

        // The page shows per-season progress, which needs the episode list cached.
        await EnsureShowAsync(id, ct);

        var totals = await ShowTotalsAsync(id, userId, ct);
        UserMediaState? user = null;
        ShowProgressDto? progress = null;
        Dictionary<int, int> watchedPerSeason = [];
        Dictionary<int, DateTimeOffset> completedPerSeason = [];
        var trackStatus = ShowTrackStatus.Watching;
        if (userId is { } uid)
        {
            user = await GetUserStateAsync(uid, MediaType.Show, id, ct);
            trackStatus = await db.ShowStates.Where(x => x.UserId == uid && x.ShowTmdbId == id)
                .Select(x => (ShowTrackStatus?)x.Status).FirstOrDefaultAsync(ct) ?? ShowTrackStatus.Watching;
            var watched = await db.WatchEvents
                .Where(w => w.UserId == uid && w.Type == MediaType.Episode && w.TmdbId == id)
                .Select(w => new { w.SeasonNumber, w.EpisodeNumber, w.WatchedAt })
                .ToListAsync(ct);
            watchedPerSeason = watched.Select(w => (w.SeasonNumber, w.EpisodeNumber)).Distinct()
                .GroupBy(w => w.SeasonNumber ?? 0).ToDictionary(g => g.Key, g => g.Count());
            foreach (var season in s.Seasons ?? [])
                if (SeasonCompleted(watched.Where(w => w.SeasonNumber == season.SeasonNumber).Select(w => (w.EpisodeNumber ?? 0, w.WatchedAt)), season.EpisodeCount) is { } done)
                    completedPerSeason[season.SeasonNumber] = done;
            if (watched.Count > 0)
                progress = (await new ProgressCalculator(db).ForUserAsync(uid, [id], ct)).FirstOrDefault();
        }

        // The next episode's exact time and channel, when TVmaze has them.
        DateTimeOffset? nextAt = null;
        string? nextNetwork = null;
        if (s.NextEpisodeToAir is { } upcoming)
        {
            nextAt = await db.Episodes.AsNoTracking()
                .Where(e => e.ShowTmdbId == id && e.SeasonNumber == upcoming.SeasonNumber && e.EpisodeNumber == upcoming.EpisodeNumber)
                .Select(e => e.AirStamp).FirstOrDefaultAsync(ct);
            nextNetwork = await db.Shows.AsNoTracking().Where(x => x.TmdbId == id).Select(x => x.AirNetwork).FirstOrDefaultAsync(ct)
                          ?? s.Networks?.FirstOrDefault()?.Name;
        }

        return new ShowDetail(
            s.Id, s.Name, NullIfEmpty(s.Tagline), s.Overview, TmdbDates.Parse(s.FirstAirDate), TmdbDates.Parse(s.LastAirDate), s.Status,
            s.Networks?.Select(n => n.Name).ToList() ?? [],
            s.Genres?.Select(g => g.Name).ToList() ?? [],
            s.EpisodeRunTime is { Count: > 0 } r ? (int)r.Average() : null,
            s.PosterPath, s.BackdropPath, s.VoteAverage, s.VoteCount, s.NumberOfSeasons, s.NumberOfEpisodes,
            (s.Seasons ?? []).OrderBy(x => x.SeasonNumber == 0 ? int.MaxValue : x.SeasonNumber)
                .Select(x => new SeasonSummary(x.SeasonNumber, x.Name ?? $"Season {x.SeasonNumber}", x.EpisodeCount, TmdbDates.Parse(x.AirDate), x.PosterPath,
                    watchedPerSeason.GetValueOrDefault(x.SeasonNumber),
                    completedPerSeason.TryGetValue(x.SeasonNumber, out var done) ? done : null)).ToList(),
            Cast(s.AggregateCredits?.Cast ?? s.Credits?.Cast),
            PickVideos(s.Videos),
            (s.Recommendations?.Results ?? []).Take(18).Select(x => ToSummary(x, MediaType.Show)).ToList(),
            s.NextEpisodeToAir is { } n ? new EpisodeRef(n.SeasonNumber, n.EpisodeNumber, n.Name, TmdbDates.Parse(n.AirDate), AirsAt: nextAt, Network: nextNetwork) : null,
            user,
            progress,
            totals.Total, totals.Aired, totals.Watched, totals.Remaining, trackStatus);
    }

    /// <summary>Running time of the whole show from the cached episode list (regular seasons only).</summary>
    private async Task<(long Total, int Aired, long Watched, long Remaining)> ShowTotalsAsync(int showId, Guid? userId, CancellationToken ct)
    {
        var meta = await db.Shows.AsNoTracking().FirstOrDefaultAsync(x => x.TmdbId == showId, ct);
        var typical = meta?.EpisodeRuntime ?? 0;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var eps = await db.Episodes.AsNoTracking().Where(e => e.ShowTmdbId == showId && e.SeasonNumber > 0)
            .Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.Runtime, e.AirDate }).ToListAsync(ct);
        var watched = userId is { } uid
            ? (await db.WatchEvents.Where(w => w.UserId == uid && w.Type == MediaType.Episode && w.TmdbId == showId)
                .Select(w => new { w.SeasonNumber, w.EpisodeNumber }).ToListAsync(ct)).Select(w => (w.SeasonNumber ?? -1, w.EpisodeNumber ?? -1)).ToHashSet()
            : [];
        long Len(int? r) => r is > 0 ? r.Value : typical;
        var aired = eps.Where(e => e.AirDate is { } d && d <= today).ToList();
        return (eps.Sum(e => Len(e.Runtime)), aired.Count,
            eps.Where(e => watched.Contains((e.SeasonNumber, e.EpisodeNumber))).Sum(e => Len(e.Runtime)),
            aired.Where(e => !watched.Contains((e.SeasonNumber, e.EpisodeNumber))).Sum(e => Len(e.Runtime)));
    }

    /// <summary>The whole cast, in billing order, with episode counts where TMDB gives them.</summary>
    private static List<CastMember> Cast(IEnumerable<TmdbCast>? cast) =>
        (cast ?? []).OrderBy(c => c.Order).DistinctBy(c => c.Id)
            .Select(c => new CastMember(c.Id, c.Name, c.AnyCharacter, c.ProfilePath,
                c.TotalEpisodeCount ?? (c.Roles is { Count: > 0 } r ? r.Sum(x => x.EpisodeCount) : null)))
            .ToList();

    public async Task<SeasonDetail?> GetSeasonAsync(int showId, int seasonNumber, Guid? userId, CancellationToken ct)
    {
        var show = await EnsureShowAsync(showId, ct);
        var seasonTask = tmdb.GetSeasonAsync(showId, seasonNumber, ct);
        var creditsTask = tmdb.GetSeasonCreditsAsync(showId, seasonNumber, ct);
        var season = await seasonTask;
        if (show is null || season is null) return null;
        var numbers = await db.Episodes.AsNoTracking().Where(e => e.ShowTmdbId == showId).Select(e => e.SeasonNumber).Distinct().ToListAsync(ct);
        var regular = numbers.Where(n => n > 0).Order().ToList();
        int? previous = seasonNumber > 0 && regular.LastOrDefault(n => n < seasonNumber) is var p and > 0 ? p : null;
        int? nextSeason = seasonNumber == 0 ? (regular.Count > 0 ? regular[0] : null)
            : regular.FirstOrDefault(n => n > seasonNumber) is var nx and > 0 ? nx : null;
        TmdbCredits? seasonCredits = null;
        try { seasonCredits = await creditsTask; }
        catch (HttpRequestException ex) { log.LogInformation(ex, "No season credits for {Show} S{Season}", showId, seasonNumber); }

        Dictionary<int, int> plays = [];
        List<(int Episode, DateTimeOffset WatchedAt)> seasonPlays = [];
        Dictionary<int, double> ratings = [];
        HashSet<int> rewatched = [];
        ShowRewatch? rewatch = null;
        double? seasonRating = null;
        if (userId is { } uid)
        {
            rewatch = await db.Rewatches.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == uid && r.ShowTmdbId == showId && r.CompletedAt == null, ct);
            if (rewatch is not null)
                rewatched = (await db.WatchEvents
                    .Where(w => w.UserId == uid && w.Type == MediaType.Episode && w.TmdbId == showId && w.SeasonNumber == seasonNumber && w.WatchedAt >= rewatch.StartedAt)
                    .Select(w => w.EpisodeNumber!.Value).ToListAsync(ct)).ToHashSet();
            seasonPlays = (await db.WatchEvents
                .Where(w => w.UserId == uid && w.Type == MediaType.Episode && w.TmdbId == showId && w.SeasonNumber == seasonNumber)
                .Select(w => new { Episode = w.EpisodeNumber!.Value, w.WatchedAt })
                .ToListAsync(ct)).Select(w => (w.Episode, w.WatchedAt)).ToList();
            plays = seasonPlays.GroupBy(w => w.Episode).ToDictionary(g => g.Key, g => g.Count());
            ratings = await db.Ratings
                .Where(r => r.UserId == uid && r.Type == MediaType.Episode && r.TmdbId == showId && r.SeasonNumber == seasonNumber)
                .ToDictionaryAsync(r => r.EpisodeNumber!.Value, r => r.Value, ct);
            seasonRating = await db.Ratings
                .Where(r => r.UserId == uid && r.Type == MediaType.Show && r.TmdbId == showId && r.SeasonNumber == seasonNumber && r.EpisodeNumber == null)
                .Select(r => (double?)r.Value).FirstOrDefaultAsync(ct);
        }

        return new SeasonDetail(showId, show.Name, seasonNumber, season.Name ?? $"Season {seasonNumber}", NullIfEmpty(season.Overview), season.PosterPath,
            (season.Episodes ?? []).Select(e => new EpisodeDto(e.Id, e.SeasonNumber, e.EpisodeNumber, e.Name, e.Overview,
                TmdbDates.Parse(e.AirDate), e.Runtime, e.StillPath, e.VoteAverage,
                plays.GetValueOrDefault(e.EpisodeNumber), ratings.TryGetValue(e.EpisodeNumber, out var r) ? r : null,
                rewatched.Contains(e.EpisodeNumber))).ToList(),
            Cast(seasonCredits?.Cast),
            rewatch?.Number,
            SeasonCompleted(seasonPlays, season.Episodes?.Count ?? 0),
            seasonRating, previous, nextSeason);
    }

    /// <summary>
    /// When a season was finished: the first play of whichever episode was watched last, once every
    /// episode has a play. Null if it isn't finished, or that day isn't known (an "unknown date" play).
    /// Rewatches don't move it.
    /// </summary>
    public static DateTimeOffset? SeasonCompleted(IEnumerable<(int Episode, DateTimeOffset WatchedAt)> plays, int episodeCount)
    {
        var firsts = plays.GroupBy(p => p.Episode)
            .Select(g => g.Where(p => !WatchDates.IsUnknown(p.WatchedAt)).Select(p => (DateTimeOffset?)p.WatchedAt).Min())
            .ToList();
        if (episodeCount == 0 || firsts.Count < episodeCount || firsts.Any(f => f is null)) return null;
        return firsts.Max();
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
        // Following another order (chronological...): previous/next come from it instead.
        if (userId is { } follower
            && (await WatchOrderService.SequencesAsync(db, follower, [showId], ct)).TryGetValue(showId, out var order)
            && order.Sequence.IndexOf((seasonNumber, episodeNumber)) is >= 0 and var at)
        {
            var all = (await db.Episodes.Where(x => x.ShowTmdbId == showId)
                    .Select(x => new { x.SeasonNumber, x.EpisodeNumber, x.Name, x.AirDate }).ToListAsync(ct))
                .ToDictionary(x => (x.SeasonNumber, x.EpisodeNumber));
            siblings = order.Sequence.Where(all.ContainsKey).Select(k => all[k]).ToList();
            index = siblings.FindIndex(x => x.SeasonNumber == seasonNumber && x.EpisodeNumber == episodeNumber);
        }
        var watchedSet = userId is { } viewer
            ? (await db.WatchEvents.Where(w => w.UserId == viewer && w.Type == MediaType.Episode && w.TmdbId == showId)
                .Select(w => new { w.SeasonNumber, w.EpisodeNumber }).ToListAsync(ct))
                .Select(w => (w.SeasonNumber ?? -1, w.EpisodeNumber ?? -1)).ToHashSet()
            : [];
        EpisodeRef? Ref(int i) => i < 0 || i >= siblings.Count ? null
            : new EpisodeRef(siblings[i].SeasonNumber, siblings[i].EpisodeNumber, siblings[i].Name, siblings[i].AirDate,
                watchedSet.Contains((siblings[i].SeasonNumber, siblings[i].EpisodeNumber)));

        // Every season of the show, for the episode page's season picker.
        var seasonRows = await db.Episodes.Where(x => x.ShowTmdbId == showId)
            .GroupBy(x => x.SeasonNumber)
            .Select(g => new { Season = g.Key, Count = g.Count(), FirstAir = g.Min(x => x.AirDate) })
            .ToListAsync(ct);
        var seasons = seasonRows
            .OrderBy(x => x.Season == 0 ? int.MaxValue : x.Season)
            .Select(x => new SeasonSummary(x.Season, x.Season == 0 ? "Specials" : $"Season {x.Season}", x.Count, x.FirstAir, null,
                watchedSet.Count(w => w.Item1 == x.Season)))
            .ToList();

        double? rating = null;
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
                .Select(r => (double?)r.Value).FirstOrDefaultAsync(ct);
        }

        return new EpisodeDetail(
            showId, show.Name, show.PosterPath, show.BackdropPath, e.SeasonNumber, e.EpisodeNumber, e.Id,
            e.Name, NullIfEmpty(e.Overview), TmdbDates.Parse(e.AirDate), e.Runtime, e.StillPath, e.VoteAverage, e.VoteCount,
            Cast(e.GuestStars),
            (e.Crew ?? []).Where(c => c.Job is "Director" or "Writer" or "Teleplay" or "Story" or "Screenplay")
                .DistinctBy(c => (c.Id, c.Job)).Select(c => new CrewMember(c.Id, c.Name, c.Job, c.ProfilePath)).ToList(),
            index < 0 ? null : Ref(index - 1),
            index < 0 ? null : Ref(index + 1),
            rating,
            plays,
            seasons,
            Cast(e.Credits?.Cast).Where(c => e.GuestStars?.All(g => g.Id != c.Id) != false).ToList());
    }

    /// <summary>
    /// The viewer's own state for a title. One round trip for the scalar bits (it used to be seven
    /// queries per page); lists and, for movies, the play history are a second and third.
    /// </summary>
    private async Task<UserMediaState> GetUserStateAsync(Guid userId, MediaType type, int id, CancellationToken ct)
    {
        var watchType = type == MediaType.Show ? MediaType.Episode : MediaType.Movie;
        var watches = db.WatchEvents.Where(w => w.UserId == userId && w.Type == watchType && w.TmdbId == id);
        var state = await db.Users.Where(u => u.Id == userId).Select(u => new
        {
            InWatchlist = db.Watchlist.Any(w => w.UserId == userId && w.Type == type && w.TmdbId == id),
            Rating = db.Ratings.Where(r => r.UserId == userId && r.Type == type && r.TmdbId == id && r.SeasonNumber == null && r.EpisodeNumber == null)
                .Select(r => (double?)r.Value).FirstOrDefault(),
            Plays = watches.Count(),
            Last = watches.Max(w => (DateTimeOffset?)w.WatchedAt),
            Favorite = db.Favorites.Any(f => f.UserId == userId && f.Type == type && f.TmdbId == id),
            NotInterested = db.NotInterested.Any(n => n.UserId == userId && n.Type == type && n.TmdbId == id),
            Owned = db.Library.Where(l => l.UserId == userId && l.Type == type && l.TmdbId == id).Select(l => (OwnedFormat?)l.Format).FirstOrDefault(),
            Note = db.Notes.Where(n => n.UserId == userId && n.Type == type && n.TmdbId == id).Select(n => n.Text).FirstOrDefault(),
        }).FirstOrDefaultAsync(ct);
        var lists = await db.ListItems
            .Where(i => i.Type == type && i.TmdbId == id && db.Lists.Any(l => l.Id == i.ListId && l.UserId == userId))
            .Select(i => i.ListId).ToListAsync(ct);
        List<PlayDto>? history = null;
        if (type == MediaType.Movie)
            history = state?.Plays > 0
                ? await watches.OrderByDescending(w => w.WatchedAt).ThenByDescending(w => w.CreatedAt)
                    .Select(w => new PlayDto(w.Id, w.WatchedAt, w.Source)).ToListAsync(ct)
                : [];
        return new UserMediaState(state?.InWatchlist ?? false, state?.Rating, state?.Plays ?? 0, state?.Last, lists, history, state?.Favorite ?? false, state?.NotInterested ?? false, state?.Owned, state?.Note);
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
    // ---------- Where to watch ----------

    public async Task<WatchProvidersDto> GetProvidersAsync(MediaType type, int id, string region, CancellationToken ct)
    {
        region = region.ToUpperInvariant();
        var all = await tmdb.GetWatchProvidersAsync(type == MediaType.Movie ? "movie" : "tv", id, ct);
        var r = all?.Results?.GetValueOrDefault(region);
        static List<ProviderDto> Map(List<TmdbProvider>? list) =>
            (list ?? []).OrderBy(p => p.DisplayPriority).DistinctBy(p => p.ProviderId)
                .Select(p => new ProviderDto(p.ProviderId, p.ProviderName, p.LogoPath)).ToList();
        return new WatchProvidersDto(region, r?.Link, Map(r?.Flatrate), Map(r?.Free), Map(r?.Ads), Map(r?.Rent), Map(r?.Buy));
    }

    public async Task<List<RegionDto>> GetRegionsAsync(CancellationToken ct) =>
        ((await tmdb.GetProviderRegionsAsync(ct))?.Results ?? [])
            .Select(r => new RegionDto(r.Iso31661, r.EnglishName)).OrderBy(r => r.Name).ToList();
}

internal static class EfExtensions
{
    public static async Task ReloadIfTrackedAsync(this Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, CancellationToken ct)
    {
        if (entry.State is not EntityState.Detached and not EntityState.Added)
            await entry.ReloadAsync(ct);
    }

}
