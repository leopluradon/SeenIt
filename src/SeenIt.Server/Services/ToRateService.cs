using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// "To rate" on Home: what you watched in the last few weeks and haven't rated, in the kinds you picked
/// (Settings → Display → Ratings). Movies and episodes count once played; a season once every episode of it
/// has aired and you've seen them all; a show once it has ended and you've seen every episode. Imports are
/// left out (they bring their own ratings, and years of history would bury the row), and so is anything
/// you waved away.
/// </summary>
public class ToRateService(AppDbContext db)
{
    /// <summary>How far back a play still counts as "just watched".</summary>
    public const int Days = 60;

    public async Task<List<ToRateItem>> GetAsync(Guid userId, int max, CancellationToken ct)
    {
        var prefs = UserPrefsStore.Read(await db.Users.Where(u => u.Id == userId).Select(u => u.PrefsJson).FirstOrDefaultAsync(ct));
        bool Want(string kind) => prefs.RatesOnHome(kind);
        var moviesWanted = Want("movie");
        var episodesWanted = Want("episode") || Want("season") || Want("show");
        if (!moviesWanted && !episodesWanted) return [];

        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-Days);
        var recent = await db.WatchEvents.AsNoTracking()
            .Where(w => w.UserId == userId && w.ImportJobId == null && w.WatchedAt >= since && w.WatchedAt <= now
                        && ((w.Type == MediaType.Movie && moviesWanted) || (w.Type == MediaType.Episode && episodesWanted)))
            .Select(w => new { w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt })
            .ToListAsync(ct);
        if (recent.Count == 0) return [];

        var done = (await db.Ratings.AsNoTracking().Where(r => r.UserId == userId)
                .Select(r => new { r.Type, r.TmdbId, r.SeasonNumber, r.EpisodeNumber }).ToListAsync(ct))
            .Select(r => (r.Type, r.TmdbId, r.SeasonNumber, r.EpisodeNumber)).ToHashSet();
        foreach (var s in await db.RateSkips.AsNoTracking().Where(s => s.UserId == userId)
                     .Select(s => new { s.Type, s.TmdbId, s.SeasonNumber, s.EpisodeNumber }).ToListAsync(ct))
            done.Add((s.Type, s.TmdbId, s.SeasonNumber, s.EpisodeNumber));
        bool Open(MediaType type, int id, int? season = null, int? episode = null) => !done.Contains((type, id, season, episode));

        var items = new List<ToRateItem>();

        if (moviesWanted)
        {
            var movies = recent.Where(w => w.Type == MediaType.Movie).GroupBy(w => w.TmdbId)
                .Select(g => (Id: g.Key, At: g.Max(w => w.WatchedAt))).Where(m => Open(MediaType.Movie, m.Id)).ToList();
            var ids = movies.Select(m => m.Id).ToList();
            var meta = await db.Movies.AsNoTracking().Where(m => ids.Contains(m.TmdbId))
                .Select(m => new { m.TmdbId, m.Title, m.ReleaseDate, m.PosterPath }).ToDictionaryAsync(m => m.TmdbId, ct);
            foreach (var (id, at) in movies)
                if (meta.TryGetValue(id, out var m))
                    items.Add(new ToRateItem("movie", MediaType.Movie, id, null, null, m.Title, m.ReleaseDate?.Year.ToString(), m.PosterPath, at));
        }

        var plays = recent.Where(w => w.Type == MediaType.Episode && w.SeasonNumber is not null && w.EpisodeNumber is not null).ToList();
        var showIds = plays.Select(w => w.TmdbId).Distinct().ToList();
        if (showIds.Count > 0)
        {
            var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId))
                .Select(s => new { s.TmdbId, s.Name, s.Status, s.PosterPath }).ToDictionaryAsync(s => s.TmdbId, ct);
            var episodes = await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.AirDate, e.Name }).ToListAsync(ct);
            var names = episodes.ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber), e => e.Name);

            if (Want("episode"))
            {
                foreach (var g in plays.GroupBy(w => (w.TmdbId, Season: w.SeasonNumber!.Value, Episode: w.EpisodeNumber!.Value)))
                {
                    if (!shows.TryGetValue(g.Key.TmdbId, out var show) || !Open(MediaType.Episode, g.Key.TmdbId, g.Key.Season, g.Key.Episode)) continue;
                    items.Add(new ToRateItem("episode", MediaType.Episode, g.Key.TmdbId, g.Key.Season, g.Key.Episode, show.Name,
                        names.GetValueOrDefault((g.Key.TmdbId, g.Key.Season, g.Key.Episode)), show.PosterPath, g.Max(w => w.WatchedAt)));
                }
            }

            if (Want("season") || Want("show"))
            {
                // Every episode ever seen of these shows (imports included): finishing is about the whole season or show.
                var seen = (await db.WatchEvents.AsNoTracking()
                        .Where(w => w.UserId == userId && w.Type == MediaType.Episode && showIds.Contains(w.TmdbId) && w.SeasonNumber != null && w.EpisodeNumber != null)
                        .Select(w => new { w.TmdbId, w.SeasonNumber, w.EpisodeNumber }).Distinct().ToListAsync(ct))
                    .Select(w => (w.TmdbId, w.SeasonNumber!.Value, w.EpisodeNumber!.Value)).ToHashSet();
                var today = DateOnly.FromDateTime(now.UtcDateTime);

                foreach (var id in showIds)
                {
                    if (!shows.TryGetValue(id, out var show)) continue;
                    var ended = show.Status is "Ended" or "Canceled";
                    // Specials (season 0) don't count toward finishing anything.
                    var mine = episodes.Where(e => e.ShowTmdbId == id && e.SeasonNumber > 0).ToList();
                    bool Aired(DateOnly? d) => d is { } x && x <= today;
                    // An episode with no date yet holds a season open, unless the show is over (a stray TBA row).
                    bool Finished(IEnumerable<(int Season, int Episode, DateOnly? AirDate)> eps)
                    {
                        var list = eps.ToList();
                        return list.Any(e => Aired(e.AirDate))
                            && list.All(e => Aired(e.AirDate) ? seen.Contains((id, e.Season, e.Episode)) : e.AirDate is null && ended);
                    }

                    var seasons = mine.GroupBy(e => e.SeasonNumber).ToList();
                    var showDone = ended && Want("show") && Open(MediaType.Show, id)
                        && Finished(mine.Select(e => (e.SeasonNumber, e.EpisodeNumber, e.AirDate)));
                    if (showDone)
                        items.Add(new ToRateItem("show", MediaType.Show, id, null, null, show.Name, null, show.PosterPath,
                            plays.Where(w => w.TmdbId == id).Max(w => w.WatchedAt)));

                    // With only one season, the show card already covers it.
                    if (!Want("season") || (showDone && seasons.Count == 1)) continue;
                    foreach (var season in seasons)
                    {
                        var recentInSeason = plays.Where(w => w.TmdbId == id && w.SeasonNumber == season.Key).ToList();
                        if (recentInSeason.Count == 0 || !Open(MediaType.Show, id, season.Key)) continue;
                        if (!Finished(season.Select(e => (e.SeasonNumber, e.EpisodeNumber, e.AirDate)))) continue;
                        items.Add(new ToRateItem("season", MediaType.Show, id, season.Key, null, show.Name, null, show.PosterPath,
                            recentInSeason.Max(w => w.WatchedAt)));
                    }
                }
            }
        }

        // Newest first; finishing a show's last episode puts the show before its season before the episode.
        static int Rank(string kind) => kind switch { "show" => 0, "season" => 1, _ => 2 };
        return items.OrderByDescending(i => i.FinishedAt).ThenBy(i => Rank(i.Kind)).Take(Math.Clamp(max, 1, 40)).ToList();
    }

    public async Task SkipAsync(Guid userId, ToRateSkip s, CancellationToken ct)
    {
        if (await db.RateSkips.AnyAsync(x => x.UserId == userId && x.Type == s.Type && x.TmdbId == s.TmdbId
                                             && x.SeasonNumber == s.SeasonNumber && x.EpisodeNumber == s.EpisodeNumber, ct)) return;
        db.RateSkips.Add(new RateSkip { UserId = userId, Type = s.Type, TmdbId = s.TmdbId, SeasonNumber = s.SeasonNumber, EpisodeNumber = s.EpisodeNumber });
        await db.SaveChangesAsync(ct);
    }

    public async Task UnskipAsync(Guid userId, ToRateSkip s, CancellationToken ct)
    {
        db.RateSkips.RemoveRange(await db.RateSkips.Where(x => x.UserId == userId && x.Type == s.Type && x.TmdbId == s.TmdbId
            && x.SeasonNumber == s.SeasonNumber && x.EpisodeNumber == s.EpisodeNumber).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
