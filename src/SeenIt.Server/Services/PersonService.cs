using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>Actor/crew pages: TMDB filmography plus everything the viewer has watched them in.</summary>
public class PersonService(AppDbContext db, TmdbClient tmdb, ILogger<PersonService> log)
{
    private static readonly HashSet<int> AppearanceGenres = [10767, 10763]; // Talk, News
    private const int Documentary = 99;

    public async Task<PersonDetail?> GetAsync(int id, Guid? userId, CancellationToken ct)
    {
        var p = await tmdb.GetPersonAsync(id, ct);
        if (p is null) return null;

        var watchedMovies = new Dictionary<int, int>();
        var watchedEpisodes = new Dictionary<int, Dictionary<(int, int), int>>();
        if (userId is { } uid)
        {
            watchedMovies = await db.WatchEvents.Where(w => w.UserId == uid && w.Type == MediaType.Movie)
                .GroupBy(w => w.TmdbId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            watchedEpisodes = (await db.WatchEvents.Where(w => w.UserId == uid && w.Type == MediaType.Episode)
                    .GroupBy(w => new { w.TmdbId, w.SeasonNumber, w.EpisodeNumber })
                    .Select(g => new { g.Key.TmdbId, g.Key.SeasonNumber, g.Key.EpisodeNumber, N = g.Count() }).ToListAsync(ct))
                .GroupBy(x => x.TmdbId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => (x.SeasonNumber ?? -1, x.EpisodeNumber ?? -1), x => x.N));
        }
        bool Watched(MediaType t, int tmdbId) => t == MediaType.Movie ? watchedMovies.ContainsKey(tmdbId) : watchedEpisodes.ContainsKey(tmdbId);

        var castCredits = (p.CombinedCredits?.Cast ?? []).Where(c => c.MediaType is "movie" or "tv").ToList();

        // One row per title: TV people often have several credits on the same show.
        var cast = castCredits.GroupBy(c => (c.MediaType, c.Id)).Select(g =>
        {
            var c = g.First();
            var type = c.MediaType == "movie" ? MediaType.Movie : MediaType.Show;
            var roles = string.Join(" / ", g.Select(x => x.Character).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            var appearance = (c.GenreIds ?? []).Any(AppearanceGenres.Contains)
                             || g.All(x => x.Character is { } ch && (ch.Contains("Self", StringComparison.OrdinalIgnoreCase) || ch is "Himself" or "Herself"))
                             || ((c.GenreIds ?? []).Contains(Documentary) && g.All(x => string.IsNullOrWhiteSpace(x.Character)));
            return new PersonCreditDto(type, c.Id, (type == MediaType.Movie ? c.Title : c.Name) ?? "",
                TmdbDates.Parse(type == MediaType.Movie ? c.ReleaseDate : c.FirstAirDate),
                string.IsNullOrEmpty(roles) ? null : roles,
                type == MediaType.Show ? g.Sum(x => x.EpisodeCount ?? 0) : null,
                c.PosterPath, g.Max(x => x.VoteCount ?? 0), Watched(type, c.Id), appearance);
        }).OrderByDescending(x => x.Date is null).ThenByDescending(x => x.Date).ToList();

        var crew = (p.CombinedCredits?.Crew ?? []).Where(c => c.MediaType is "movie" or "tv")
            .GroupBy(c => (c.MediaType, c.Id)).Select(g =>
            {
                var c = g.First();
                var type = c.MediaType == "movie" ? MediaType.Movie : MediaType.Show;
                return new PersonCreditDto(type, c.Id, (type == MediaType.Movie ? c.Title : c.Name) ?? "",
                    TmdbDates.Parse(type == MediaType.Movie ? c.ReleaseDate : c.FirstAirDate),
                    string.Join(", ", g.Select(x => x.Job).Where(x => x is not null).Distinct()),
                    type == MediaType.Show ? g.Sum(x => x.EpisodeCount ?? 0) : null,
                    c.PosterPath, g.Max(x => x.VoteCount ?? 0), Watched(type, c.Id));
            }).OrderByDescending(x => x.Date is null).ThenByDescending(x => x.Date).ToList();

        List<WatchedWith>? watched = null;
        var following = false;
        double? avg = null;
        var rated = 0;
        if (userId is { } me)
        {
            watched = await WatchedWithAsync(castCredits, watchedMovies, watchedEpisodes, ct);
            following = await db.PersonFollows.AnyAsync(f => f.UserId == me && f.PersonId == id, ct);
            // Your ratings of their work: whole movies and shows (not seasons or episodes), talk-show appearances left out.
            var credited = cast.Where(c => !c.Appearance).Concat(crew).ToList();
            var movieIds = credited.Where(c => c.Type == MediaType.Movie).Select(c => c.TmdbId).Distinct().ToList();
            var showIds = credited.Where(c => c.Type == MediaType.Show).Select(c => c.TmdbId).Distinct().ToList();
            var ratings = await db.Ratings.Where(r => r.UserId == me && r.SeasonNumber == null && r.EpisodeNumber == null
                    && ((r.Type == MediaType.Movie && movieIds.Contains(r.TmdbId)) || (r.Type == MediaType.Show && showIds.Contains(r.TmdbId))))
                .Select(r => r.Value).ToListAsync(ct);
            rated = ratings.Count;
            if (rated > 0) avg = Math.Round(ratings.Average(), 1);
        }

        return new PersonDetail(
            p.Id, p.Name, string.IsNullOrWhiteSpace(p.Biography) ? null : p.Biography,
            TmdbDates.Parse(p.Birthday), TmdbDates.Parse(p.Deathday), p.PlaceOfBirth, p.ProfilePath, p.KnownForDepartment,
            p.ExternalIds?.ImdbId, cast, crew, watched,
            watched?.Count(w => w.Type == MediaType.Movie) ?? 0,
            watched?.Where(w => w.Type == MediaType.Show).Sum(w => w.Episodes.Count > 0 ? w.Episodes.Count : w.Plays) ?? 0,
            watched?.Count(w => w.Type == MediaType.Show) ?? 0,
            watched?.Sum(w => w.Minutes) ?? 0,
            following, avg, rated);
    }

    // ---------- Following people ----------

    /// <summary>Crew jobs worth hearing about. A director's producer and "thanks" credits are mostly noise.</summary>
    private static readonly HashSet<string> NotableJobs = new(StringComparer.OrdinalIgnoreCase)
        { "Director", "Screenplay", "Writer", "Story", "Creator", "Novel", "Original Music Composer", "Director of Photography" };

    /// <summary>Their movies and new shows that haven't come out yet (dated or only announced).</summary>
    public static List<PersonUpcoming> Upcoming(TmdbPerson p, DateOnly today)
    {
        var cast = (p.CombinedCredits?.Cast ?? [])
            .Where(c => !(c.GenreIds ?? []).Any(AppearanceGenres.Contains)
                        && !(c.Character is { } ch && (ch.Contains("Self", StringComparison.OrdinalIgnoreCase) || ch is "Himself" or "Herself"))
                        // Making-of documentaries list the subject in the cast with no character.
                        && !(string.IsNullOrWhiteSpace(c.Character) && (c.GenreIds ?? []).Contains(Documentary)))
            .Select(c => (Credit: c, Role: c.Character));
        var crew = (p.CombinedCredits?.Crew ?? []).Where(c => c.Job is { } j && NotableJobs.Contains(j)).Select(c => (Credit: c, Role: c.Job));
        return cast.Concat(crew)
            .Where(x => x.Credit.MediaType is "movie" or "tv")
            .GroupBy(x => (x.Credit.MediaType, x.Credit.Id))
            .Select(g =>
            {
                var c = g.First().Credit;
                var movie = c.MediaType == "movie";
                var date = TmdbDates.Parse(movie ? c.ReleaseDate : c.FirstAirDate);
                var roles = string.Join(" / ", g.Select(x => x.Role).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct());
                return new PersonUpcoming
                {
                    PersonId = p.Id, Type = movie ? MediaType.Movie : MediaType.Show, TmdbId = c.Id,
                    Title = Trim((movie ? c.Title : c.Name) ?? "", 300), PosterPath = c.PosterPath, Date = date,
                    Role = roles.Length > 0 ? Trim(roles, 300) : null,
                };
            })
            .Where(u => u.Title.Length > 0 && (u.Date is null || u.Date > today))
            .ToList();
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;

    public async Task<bool> FollowAsync(Guid userId, int personId, bool follow, CancellationToken ct)
    {
        var row = await db.PersonFollows.FirstOrDefaultAsync(f => f.UserId == userId && f.PersonId == personId, ct);
        if (!follow)
        {
            if (row is null) return true;
            db.PersonFollows.Remove(row);
            await db.SaveChangesAsync(ct);
            // Nobody follows them any more: their upcoming list isn't needed.
            if (!await db.PersonFollows.AnyAsync(f => f.PersonId == personId, ct))
                await db.PersonUpcoming.Where(u => u.PersonId == personId).ExecuteDeleteAsync(ct);
            return true;
        }
        if (row is not null) return true;
        var p = await tmdb.GetPersonAsync(personId, ct);
        if (p is null) return false;
        db.PersonFollows.Add(new PersonFollow { UserId = userId, PersonId = p.Id, Name = Trim(p.Name, 300), ProfilePath = p.ProfilePath, Department = p.KnownForDepartment });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return true; }   // a double tap got there first
        await SaveUpcomingAsync(p, ct);
        return true;
    }

    /// <summary>Re-reads one person from TMDB: their name and photo for everyone following them, and their upcoming work.</summary>
    public async Task RefreshAsync(int personId, CancellationToken ct)
    {
        var p = await tmdb.GetPersonAsync(personId, ct);
        if (p is null) return;
        await db.PersonFollows.Where(f => f.PersonId == personId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, Trim(p.Name, 300)).SetProperty(f => f.ProfilePath, p.ProfilePath), ct);
        await SaveUpcomingAsync(p, ct);
    }

    private async Task SaveUpcomingAsync(TmdbPerson p, CancellationToken ct)
    {
        var rows = Upcoming(p, DateOnly.FromDateTime(DateTime.UtcNow));
        await db.PersonUpcoming.Where(u => u.PersonId == p.Id).ExecuteDeleteAsync(ct);
        db.PersonUpcoming.AddRange(rows);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<FollowedPerson>> FollowedAsync(Guid userId, CancellationToken ct)
    {
        var people = await db.PersonFollows.AsNoTracking().Where(f => f.UserId == userId).OrderBy(f => f.Name).ToListAsync(ct);
        var ids = people.Select(f => f.PersonId).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var upcoming = (await db.PersonUpcoming.AsNoTracking().Where(u => ids.Contains(u.PersonId) && (u.Date == null || u.Date >= today)).ToListAsync(ct))
            .ToLookup(u => u.PersonId);
        return people.Select(f => new FollowedPerson(f.PersonId, f.Name, f.ProfilePath, f.Department, f.CreatedAt,
            upcoming[f.PersonId].OrderBy(u => u.Date is null).ThenBy(u => u.Date)
                .Select(u => new UpcomingWork(u.Type, u.TmdbId, u.Title, u.PosterPath, u.Date, u.Role)).ToList())).ToList();
    }

    private async Task<List<WatchedWith>> WatchedWithAsync(
        List<TmdbPersonCredit> credits, Dictionary<int, int> watchedMovies,
        Dictionary<int, Dictionary<(int, int), int>> watchedEpisodes, CancellationToken ct)
    {
        var result = new List<WatchedWith>();

        // Movies
        var movieCredits = credits.Where(c => c.MediaType == "movie" && watchedMovies.ContainsKey(c.Id)).GroupBy(c => c.Id).ToList();
        var movieIds = movieCredits.Select(g => g.Key).ToList();
        var runtimes = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).Select(m => new { m.TmdbId, m.Runtime })
            .ToDictionaryAsync(m => m.TmdbId, m => m.Runtime ?? 0, ct);
        foreach (var g in movieCredits)
        {
            var c = g.First();
            var plays = watchedMovies[g.Key];
            result.Add(new WatchedWith(MediaType.Movie, g.Key, c.Title ?? "", c.PosterPath,
                JoinRoles(g), plays, (long)runtimes.GetValueOrDefault(g.Key) * plays, false, []));
        }

        // Shows: a guest credit lists its episodes, so only count the ones actually watched;
        // a regular's credit doesn't, so every watched episode counts.
        var showCredits = credits.Where(c => c.MediaType == "tv" && watchedEpisodes.ContainsKey(c.Id)).GroupBy(c => c.Id).ToList();
        var showIds = showCredits.Select(g => g.Key).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var episodeMeta = (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Name, e.AirDate, e.Runtime }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber));

        var details = await Task.WhenAll(showCredits.Select(async g =>
        {
            var episodes = new HashSet<(int, int)>();
            foreach (var credit in g.Where(c => c.CreditId is not null))
            {
                try
                {
                    var d = await tmdb.GetCreditAsync(credit.CreditId!, ct);
                    foreach (var e in d?.Media?.Episodes ?? []) episodes.Add((e.SeasonNumber, e.EpisodeNumber));
                }
                catch (HttpRequestException ex) { log.LogInformation(ex, "Credit {Id} lookup failed", credit.CreditId); }
            }
            return (Show: g.Key, Credits: g, GuestEpisodes: episodes);
        }));

        foreach (var (showId, group, guestEpisodes) in details)
        {
            var mine = watchedEpisodes[showId];
            var guest = guestEpisodes.Count > 0;
            var keys = guest ? mine.Keys.Where(guestEpisodes.Contains).ToList() : mine.Keys.ToList();
            if (keys.Count == 0) continue; // watched the show, but none of their episodes

            var typical = shows.GetValueOrDefault(showId)?.EpisodeRuntime ?? 0;
            var minutes = keys.Sum(k => (long)(episodeMeta.TryGetValue((showId, k.Item1, k.Item2), out var m) && m.Runtime is > 0 ? m.Runtime.Value : typical) * mine[k]);
            var episodeRefs = guest
                ? keys.OrderBy(k => k).Select(k => episodeMeta.TryGetValue((showId, k.Item1, k.Item2), out var m)
                    ? new EpisodeRef(k.Item1, k.Item2, m.Name, m.AirDate, true)
                    : new EpisodeRef(k.Item1, k.Item2, null, null, true)).ToList()
                : [];
            var c = group.First();
            result.Add(new WatchedWith(MediaType.Show, showId, shows.GetValueOrDefault(showId)?.Name ?? c.Name ?? "",
                shows.GetValueOrDefault(showId)?.PosterPath ?? c.PosterPath, JoinRoles(group),
                keys.Sum(k => mine[k]), minutes, guest, episodeRefs));
        }

        return result.OrderByDescending(w => w.Minutes).ToList();
    }

    private static string? JoinRoles(IEnumerable<TmdbPersonCredit> g) =>
        string.Join(" / ", g.Select(x => x.Character).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()) is { Length: > 0 } s ? s : null;
}

/// <summary>Once a day, re-reads everyone somebody follows, so newly announced work and new dates reach calendars and alerts.</summary>
public class PersonFollowWorker(IServiceScopeFactory scopes, ILogger<PersonFollowWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<int> ids;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    ids = await db.PersonFollows.Select(f => f.PersonId).Distinct().ToListAsync(stoppingToken);
                    // People nobody follows any more (accounts deleted): their upcoming list isn't needed.
                    await db.PersonUpcoming.Where(u => !db.PersonFollows.Any(f => f.PersonId == u.PersonId)).ExecuteDeleteAsync(stoppingToken);
                }
                foreach (var id in ids)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    try { await scope.ServiceProvider.GetRequiredService<PersonService>().RefreshAsync(id, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Refreshing person {Id} failed", id); }
                }
                if (ids.Count > 0) log.LogInformation("Checked {Count} followed people for new work", ids.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Followed people pass failed"); }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
