using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>Actor/crew pages: TMDB filmography plus everything the viewer has watched them in.</summary>
public class PersonService(AppDbContext db, TmdbClient tmdb, ILogger<PersonService> log)
{
    private static readonly HashSet<int> AppearanceGenres = [10767, 10763]; // Talk, News

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
                             || g.All(x => x.Character is { } ch && (ch.Contains("Self", StringComparison.OrdinalIgnoreCase) || ch is "Himself" or "Herself"));
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
        if (userId is not null)
            watched = await WatchedWithAsync(castCredits, watchedMovies, watchedEpisodes, ct);

        return new PersonDetail(
            p.Id, p.Name, string.IsNullOrWhiteSpace(p.Biography) ? null : p.Biography,
            TmdbDates.Parse(p.Birthday), TmdbDates.Parse(p.Deathday), p.PlaceOfBirth, p.ProfilePath, p.KnownForDepartment,
            p.ExternalIds?.ImdbId, cast, crew, watched,
            watched?.Count(w => w.Type == MediaType.Movie) ?? 0,
            watched?.Where(w => w.Type == MediaType.Show).Sum(w => w.Episodes.Count > 0 ? w.Episodes.Count : w.Plays) ?? 0,
            watched?.Count(w => w.Type == MediaType.Show) ?? 0,
            watched?.Sum(w => w.Minutes) ?? 0);
    }

    private async Task<List<WatchedWith>> WatchedWithAsync(
        List<TmdbPersonCredit> credits, Dictionary<int, int> watchedMovies,
        Dictionary<int, Dictionary<(int, int), int>> watchedEpisodes, CancellationToken ct)
    {
        var result = new List<WatchedWith>();

        // Movies
        var movieCredits = credits.Where(c => c.MediaType == "movie" && watchedMovies.ContainsKey(c.Id)).GroupBy(c => c.Id).ToList();
        var movieIds = movieCredits.Select(g => g.Key).ToList();
        var runtimes = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, m => m.Runtime ?? 0, ct);
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
