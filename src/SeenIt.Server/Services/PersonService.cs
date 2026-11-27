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

    /// <param name="quick">Skip the per-show episode checks when there are any (PendingShows lists those shows;
    /// SeenAsync does them): the page shows straight away and fills that part in.</param>
    public async Task<PersonDetail?> GetAsync(int id, Guid? userId, CancellationToken ct, bool quick = false)
    {
        // TMDB first, while the database work below runs.
        var fetch = tmdb.GetPersonAsync(id, ct);

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
        var p = await fetch;
        if (p is null) return null;
        var castCredits = (p.CombinedCredits?.Cast ?? []).Where(c => c.MediaType is "movie" or "tv").ToList();
        var crewCredits = (p.CombinedCredits?.Crew ?? []).Where(c => c.MediaType is "movie" or "tv").ToList();

        // What you've actually seen them in: for a show that means one of *their* episodes (a guest role is
        // checked episode by episode against TMDB), not just any episode of it. A show you've watched some of
        // without seeing theirs is "partly seen", shown apart.
        // Quick: leave those checks for later if there are any to do (movies alone need no TMDB lookups).
        var pending = quick && userId is not null
            ? castCredits.Concat(crewCredits).Where(c => c.MediaType == "tv" && watchedEpisodes.ContainsKey(c.Id)).Select(c => c.Id).Distinct().ToHashSet()
            : [];
        var watched = userId is null || pending.Count > 0 ? null : await WatchedWithAsync(id, castCredits, crewCredits, watchedMovies, watchedEpisodes, ct);
        var seenShows = watched?.Where(w => w.Type == MediaType.Show).Select(w => w.TmdbId).ToHashSet() ?? [];
        bool Watched(MediaType t, int tmdbId) => t == MediaType.Movie ? watchedMovies.ContainsKey(tmdbId) : seenShows.Contains(tmdbId);
        bool Partly(MediaType t, int tmdbId) => t == MediaType.Show && !pending.Contains(tmdbId) && !seenShows.Contains(tmdbId) && watchedEpisodes.ContainsKey(tmdbId);
        var owned = userId is { } ownerId
            ? (await db.Library.AsNoTracking().Where(l => l.UserId == ownerId).Select(l => new { l.Type, l.TmdbId }).Distinct().ToListAsync(ct))
                .Select(l => (l.Type, l.TmdbId)).ToHashSet()
            : [];
        var watching = await WatchingAsync(db, castCredits.Concat(crewCredits).Where(c => c.MediaType == "tv").Select(c => c.Id), watchedEpisodes, ct);

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
                c.PosterPath, g.Max(x => x.VoteCount ?? 0), Watched(type, c.Id), appearance, PartlySeen: Partly(type, c.Id),
                Watching: type == MediaType.Show && watching.Contains(c.Id), InLibrary: owned.Contains((type, c.Id)));
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
                    c.PosterPath, g.Max(x => x.VoteCount ?? 0), Watched(type, c.Id),
                    Jobs: g.Where(x => !string.IsNullOrWhiteSpace(x.Job)).Select(x => new CrewJob(x.Job!, x.Department)).Distinct().ToList(),
                    PartlySeen: Partly(type, c.Id), Watching: type == MediaType.Show && watching.Contains(c.Id), InLibrary: owned.Contains((type, c.Id)));
            }).OrderByDescending(x => x.Date is null).ThenByDescending(x => x.Date).ToList();

        var following = false;
        double? avg = null;
        var rated = 0;
        if (userId is { } me)
        {
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
            following, avg, rated, p.ExternalIds?.WikidataId, pending.Count > 0 ? [.. pending] : null);
    }

    /// <summary>What you've seen them in, worked out in full: the part GetAsync's quick load leaves for later.</summary>
    public async Task<PersonSeen?> SeenAsync(int id, Guid userId, CancellationToken ct)
    {
        if (await GetAsync(id, userId, ct) is not { Watched: { } watched } d) return null;
        var shows = d.Cast.Concat(d.Crew).Where(c => c.Type == MediaType.Show).ToList();
        return new PersonSeen(watched, d.WatchedMovies, d.WatchedEpisodes, d.WatchedShows, d.WatchedMinutes,
            shows.Where(c => c.Watched).Select(c => c.TmdbId).Distinct().ToList(),
            shows.Where(c => c.PartlySeen).Select(c => c.TmdbId).Distinct().ToList());
    }

    /// <summary>
    /// What two people both worked on: their filmographies (acting and crew, talk-show appearances included)
    /// put side by side. Null if either isn't on TMDB.
    /// </summary>
    public async Task<PersonPair?> PairAsync(int a, int b, Guid? userId, CancellationToken ct)
    {
        var (pa, pb) = (await GetAsync(a, userId, ct), await GetAsync(b, userId, ct));
        if (pa is null || pb is null) return null;
        static Dictionary<(MediaType, int), (PersonCreditDto Credit, string? Role)> Works(PersonDetail p) =>
            p.Cast.Select(c => (c, Role: c.Role is { Length: > 0 } r ? $"as {r}" : "Cast"))
                .Concat(p.Crew.Select(c => (c, Role: c.Role)))
                .GroupBy(x => (x.c.Type, x.c.TmdbId))
                .ToDictionary(g => g.Key, g => (g.First().c with { EpisodeCount = g.Max(x => x.c.EpisodeCount) },
                    string.Join(" · ", g.Select(x => x.Role).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct()) is { Length: > 0 } roles ? roles : null));
        var wa = Works(pa);
        var wb = Works(pb);
        var credits = wa.Keys.Intersect(wb.Keys).Select(k =>
        {
            var (ca, ra) = wa[k];
            var (cb, rb) = wb[k];
            // A movie is simply seen; a show only fully if you've seen episodes with each of them in.
            var watched = ca.Type == MediaType.Movie ? ca.Watched : ca.Watched && cb.Watched;
            var partly = !watched && ca.Type == MediaType.Show && (ca.Watched || cb.Watched || ca.PartlySeen || cb.PartlySeen);
            return new PairCredit(ca.Type, ca.TmdbId, ca.Title, ca.Date ?? cb.Date, ca.PosterPath ?? cb.PosterPath, ra, rb,
                ca.Type == MediaType.Show ? ca.EpisodeCount : null, cb.Type == MediaType.Show ? cb.EpisodeCount : null, watched, partly,
                ca.Appearance || cb.Appearance);
        }).OrderByDescending(c => c.Date is null).ThenByDescending(c => c.Date).ToList();
        static PersonHit Brief(PersonDetail p) => new(p.Id, p.Name, p.ProfilePath, p.KnownFor, []);
        return new PersonPair(Brief(pa), Brief(pb), credits);
    }

    /// <summary>
    /// Their shows you're partway through: you've seen an episode, and there's an aired regular episode you haven't.
    /// A show whose episodes aren't cached yet counts too (better to hide a count than give something away).
    /// </summary>
    internal static async Task<HashSet<int>> WatchingAsync(AppDbContext db, IEnumerable<int> showIds, Dictionary<int, Dictionary<(int, int), int>> seen, CancellationToken ct)
    {
        var started = showIds.Where(seen.ContainsKey).Distinct().ToList();
        if (started.Count == 0) return [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var aired = (await db.Episodes.AsNoTracking()
                .Where(e => started.Contains(e.ShowTmdbId) && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= today)
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber }).ToListAsync(ct))
            .GroupBy(e => e.ShowTmdbId).ToDictionary(g => g.Key, g => g.Select(e => (e.SeasonNumber, e.EpisodeNumber)).ToList());
        return started.Where(id => !aired.TryGetValue(id, out var eps) || eps.Any(e => !seen[id].ContainsKey(e))).ToHashSet();
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

    private async Task<List<WatchedWith>> WatchedWithAsync(int personId,
        List<TmdbPersonCredit> credits, List<TmdbPersonCredit> crewCredits, Dictionary<int, int> watchedMovies,
        Dictionary<int, Dictionary<(int, int), int>> watchedEpisodes, CancellationToken ct)
    {
        var result = new List<WatchedWith>();
        // Behind the camera: any job on a movie, but on a show only creating it (an episode director isn't in every episode).
        var crewByTitle = crewCredits.Where(c => c.MediaType == "movie" || c.Job == "Creator")
            .GroupBy(c => (c.MediaType, c.Id)).ToDictionary(g => g.Key, g => g.ToList());
        string? Jobs(string mediaType, int id) => crewByTitle.TryGetValue((mediaType, id), out var g)
            && string.Join(", ", g.Select(x => x.Job).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()) is { Length: > 0 } s ? s : null;

        // Movies
        var movieCredits = credits.Concat(crewByTitle.Where(kv => kv.Key.MediaType == "movie").SelectMany(kv => kv.Value))
            .Where(c => c.MediaType == "movie" && watchedMovies.ContainsKey(c.Id)).GroupBy(c => c.Id).ToList();
        var movieIds = movieCredits.Select(g => g.Key).ToList();
        var runtimes = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).Select(m => new { m.TmdbId, m.Runtime })
            .ToDictionaryAsync(m => m.TmdbId, m => m.Runtime ?? 0, ct);
        foreach (var g in movieCredits)
        {
            var c = g.First();
            var plays = watchedMovies[g.Key];
            result.Add(new WatchedWith(MediaType.Movie, g.Key, c.Title ?? "", c.PosterPath,
                JoinRoles(g), plays, (long)runtimes.GetValueOrDefault(g.Key) * plays, false, [], Jobs("movie", g.Key)));
        }

        // Shows: a guest credit lists its episodes, so only count the ones actually watched. A regular's credit
        // doesn't, so their seasons are looked up (RegularEpisodesAsync): someone who joined in season 3 isn't in
        // the first two. A show they created counts every watched episode.
        var showCredits = credits.Concat(crewByTitle.Where(kv => kv.Key.MediaType == "tv").SelectMany(kv => kv.Value))
            .Where(c => c.MediaType == "tv" && watchedEpisodes.ContainsKey(c.Id)).GroupBy(c => c.Id).ToList();
        var showIds = showCredits.Select(g => g.Key).ToList();
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var episodeMeta = (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Name, e.AirDate, e.Runtime }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber));

        var details = await Task.WhenAll(showCredits.Select(async g =>
        {
            // A credit can list episodes (a guest's) and seasons (a regular's) at once: someone who guested in a
            // season, or became a regular and then only appeared now and then (FROM's Bakta: seasons 2 and 3 as a
            // regular, then six season 4 episodes).
            var episodes = new HashSet<(int, int)>();
            var seasons = new HashSet<int>();
            foreach (var credit in g.Where(c => c.CreditId is not null && c.Job is null))   // cast credits only
            {
                try
                {
                    var d = await tmdb.GetCreditAsync(credit.CreditId!, ct);
                    foreach (var e in d?.Media?.Episodes ?? []) episodes.Add((e.SeasonNumber, e.EpisodeNumber));
                    foreach (var se in d?.Media?.Seasons ?? []) seasons.Add(se.SeasonNumber);
                }
                catch (HttpRequestException ex) { log.LogInformation(ex, "Credit {Id} lookup failed", credit.CreditId); }
            }
            var mine = watchedEpisodes[g.Key];
            var created = Jobs("tv", g.Key) is not null;
            int InSeason(int season) => episodeMeta.Keys.Count(e => e.ShowTmdbId == g.Key && e.SeasonNumber == season);
            var guest = episodes.Count > 0 && seasons.Count == 0 && !created;   // their own show: all of it
            List<(int, int)> keys;
            if (created || !g.Any(c => c.Job is null)) keys = mine.Keys.ToList();
            else if (guest) keys = mine.Keys.Where(episodes.Contains).ToList();
            else if (episodes.Count > 0)
            {
                // Both: their listed episodes, and the seasons they're a regular in (checked like any regular's).
                var regular = await RegularEpisodesAsync(personId, g.Key, mine.Keys.Where(k => seasons.Contains(k.Item1)), InSeason, ct);
                keys = mine.Keys.Where(episodes.Contains).Union(regular).ToList();
            }
            else keys = await RegularEpisodesAsync(personId, g.Key, mine.Keys, InSeason, ct);
            return (Show: g.Key, Credits: g, Guest: guest, Keys: keys);
        }));

        foreach (var (showId, group, guest, keys) in details)
        {
            var mine = watchedEpisodes[showId];
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
                keys.Sum(k => mine[k]), minutes, guest, episodeRefs, Jobs("tv", showId)));
        }

        return result.OrderByDescending(w => w.Minutes).ToList();
    }

    /// <summary>
    /// The watched episodes a show's regular is actually in. TMDB's credit for a regular doesn't list episodes, but each
    /// season's cast does say how many they're in: a season they're not in counts none, a season they're in throughout
    /// counts all of it, and part of a season is checked episode by episode. Whatever can't be looked up counts.
    /// </summary>
    private async Task<List<(int, int)>> RegularEpisodesAsync(int personId, int showId, IEnumerable<(int, int)> watched,
        Func<int, int> episodesInSeason, CancellationToken ct)
    {
        var result = new List<(int, int)>();
        foreach (var season in watched.GroupBy(k => k.Item1))
        {
            TmdbCast? inSeason;
            try
            {
                var credits = await tmdb.GetSeasonCreditsAsync(showId, season.Key, ct);
                if (credits?.Cast is null) { result.AddRange(season); continue; }
                inSeason = credits.Cast.FirstOrDefault(c => c.Id == personId);
            }
            catch (HttpRequestException ex) { log.LogInformation(ex, "Season credits for {Show} S{Season} failed", showId, season.Key); result.AddRange(season); continue; }
            if (inSeason is null) continue;

            var theirs = inSeason.TotalEpisodeCount ?? inSeason.Roles?.Sum(r => r.EpisodeCount) ?? 0;
            var total = episodesInSeason(season.Key);
            if (theirs <= 0 || total == 0 || theirs >= total) { result.AddRange(season); continue; }
            foreach (var k in season)
            {
                try
                {
                    var ep = await tmdb.GetEpisodeAsync(showId, k.Item1, k.Item2, ct);
                    if (ep is null || (ep.Credits?.Cast ?? []).Concat(ep.GuestStars ?? []).Any(c => c.Id == personId)) result.Add(k);
                }
                catch (HttpRequestException) { result.Add(k); }
            }
        }
        return result;
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
