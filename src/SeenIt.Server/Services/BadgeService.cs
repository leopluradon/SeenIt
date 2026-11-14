using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Profile badges, worked out from what someone has actually done (so a pinned badge is always earned). Most come in
/// tiers (bronze → platinum) and move up by themselves: pinning "movies" shows whichever movie tier you're at.
/// Worked out once per person every few minutes (cached); the profile shows the ones they pinned (UserPrefs.ProfileBadges).
/// </summary>
public class BadgeService(AppDbContext db, IMemoryCache cache)
{
    private sealed record Ladder(string Id, string Icon, string Description, long[] Goals, Func<long, string> Label);

    // Tiered families: goals for bronze, silver, gold, platinum.
    private static readonly Ladder[] Ladders =
    [
        new("movies", "film", "Different movies watched", [50, 250, 1000, 2500], n => $"{n:N0} movies"),
        new("episodes", "tv", "Different episodes watched", [500, 2500, 10000, 25000], n => $"{n:N0} episodes"),
        new("hours", "clock", "Hours spent watching, counting rewatches", [100, 1000, 5000, 10000], n => $"{n:N0} hours"),
        new("finished", "check", "Shows watched to the end (or every episode out so far)", [5, 25, 100, 250], n => $"{n:N0} shows finished"),
        new("ratings", "star", "Movies, shows and episodes rated", [50, 250, 1000, 2500], n => $"{n:N0} ratings"),
        new("streak", "target", "Most days in a row with something watched", [7, 30, 100, 365], n => $"{n:N0}-day streak"),
        new("dayone", "calendar", "Episodes watched the day they aired", [25, 100, 500, 2000], n => $"{n:N0} on air day"),
        new("rewatch", "repeat", "Movies watched more than once", [10, 50, 150, 400], n => $"{n:N0} rewatched"),
        new("classics", "history", "Movies from before 1970", [10, 50, 150, 400], n => $"{n:N0} classics"),
        new("recommender", "send", "Titles recommended to friends", [10, 50, 150, 400], n => $"{n:N0} recommended"),
        new("tastemaker", "users", "Recommendations friends went on to watch", [5, 25, 75, 200], n => $"{n:N0} recs watched"),
        new("curator", "list", "Public lists with at least 5 titles", [3, 10, 25, 50], n => $"{n:N0} lists"),
    ];
    private static readonly string[] GenreTitles = ["fan", "devotee", "connoisseur", "legend"];
    private static readonly long[] GenreGoals = [25, 100, 250, 500];

    /// <summary>Every badge for this person: earned ones at their tier, locked ones with progress. Cached for a few minutes.</summary>
    public Task<List<BadgeDto>> AllAsync(Guid userId, CancellationToken ct) =>
        cache.GetOrCreateAsync($"badges:{userId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await ComputeAsync(userId, ct);
        })!;

    /// <summary>The pinned badges, in the owner's order (an older single milestone badge maps onto its family).</summary>
    public async Task<List<BadgeDto>> PinnedAsync(AppUser owner, UserPrefs prefs, CancellationToken ct)
    {
        var ids = prefs.ProfileBadges ?? (prefs.ProfileBadge is { } old ? FromMilestone(old) : null);
        if (ids is not { Count: > 0 }) return [];
        var all = (await AllAsync(owner.Id, ct)).Where(b => b.Earned).ToDictionary(b => b.Id);
        return ids.Select(id => all.GetValueOrDefault(id)).OfType<BadgeDto>().Take(UserPrefs.MaxBadges).ToList();
    }

    /// <summary>The old single badge was a milestone label ("1,000th episode"): pin its family instead.</summary>
    private static List<string>? FromMilestone(string label) =>
        label.Contains("movie", StringComparison.OrdinalIgnoreCase) ? ["movies"]
        : label.Contains("episode", StringComparison.OrdinalIgnoreCase) ? ["episodes"]
        : label.Contains("hour", StringComparison.OrdinalIgnoreCase) || label.Contains("day", StringComparison.OrdinalIgnoreCase) ? ["hours"]
        : label.Contains("10/10") ? ["perfect10"]
        : null;

    private async Task<List<BadgeDto>> ComputeAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.TimeZone, u.CreatedAt }).FirstAsync(ct);
        var tz = ReviewService.Zone(user.TimeZone);
        var plays = await db.WatchEvents.AsNoTracking().Where(w => w.UserId == userId)
            .Select(w => new { w.Type, w.TmdbId, S = w.SeasonNumber ?? 0, E = w.EpisodeNumber ?? 0, w.WatchedAt }).ToListAsync(ct);
        var dated = plays.Where(p => !WatchDates.IsUnknown(p.WatchedAt)).ToList();
        DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime);

        var movieIds = plays.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct().ToList();
        var showIds = plays.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();
        var movies = await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId))
            .Select(m => new { m.TmdbId, m.Runtime, m.ReleaseDate, m.Genres }).ToDictionaryAsync(m => m.TmdbId, ct);
        var showRuntime = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, s => s.EpisodeRuntime, ct);
        var episodes = (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Runtime, e.AirDate }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber));

        var counts = new Dictionary<string, long>
        {
            ["movies"] = movieIds.Count,
            ["episodes"] = plays.Where(p => p.Type == MediaType.Episode).Select(p => (p.TmdbId, p.S, p.E)).Distinct().LongCount(),
            ["hours"] = plays.Sum(p => (long)(p.Type == MediaType.Movie
                ? movies.GetValueOrDefault(p.TmdbId)?.Runtime ?? 0
                : episodes.GetValueOrDefault((p.TmdbId, p.S, p.E))?.Runtime ?? showRuntime.GetValueOrDefault(p.TmdbId) ?? 0)) / 60,
            ["rewatch"] = plays.Where(p => p.Type == MediaType.Movie).GroupBy(p => p.TmdbId).LongCount(g => g.Count() > 1),
            ["classics"] = movieIds.LongCount(id => movies.GetValueOrDefault(id)?.ReleaseDate is { Year: < 1970 }),
            ["ratings"] = await db.Ratings.LongCountAsync(r => r.UserId == userId, ct),
            ["recommender"] = await db.Recommendations.LongCountAsync(r => r.FromId == userId, ct),
            ["curator"] = await db.Lists.Where(l => l.UserId == userId && l.IsPublic && db.ListItems.Count(i => i.ListId == l.Id) >= 5).LongCountAsync(ct),
        };
        // Days in a row, in their own time zone.
        counts["streak"] = ReviewService.LongestStreak(dated.Select(p => Day(p.WatchedAt)).Distinct().Order().ToList()).Days;
        // Watched on the day it aired (the first watch only).
        counts["dayone"] = dated.Where(p => p.Type == MediaType.Episode).GroupBy(p => (p.TmdbId, p.S, p.E))
            .LongCount(g => episodes.GetValueOrDefault(g.Key)?.AirDate is { } aired && g.Min(p => Day(p.WatchedAt)) == aired);
        // Finished: every episode out so far, from the same progress the Progress page uses.
        counts["finished"] = showIds.Count == 0 ? 0 : (await new ProgressCalculator(db).ForUserAsync(userId, showIds, ct)).LongCount(p => p.Completed);
        // Recommendations the person went on to play, after you sent them.
        var sent = await db.Recommendations.AsNoTracking().Where(r => r.FromId == userId).Select(r => new { r.ToId, r.Type, r.TmdbId, r.CreatedAt }).ToListAsync(ct);
        counts["tastemaker"] = 0;
        foreach (var r in sent)
        {
            var type = r.Type == MediaType.Show ? MediaType.Episode : MediaType.Movie;
            if (await db.WatchEvents.AnyAsync(w => w.UserId == r.ToId && w.Type == type && w.TmdbId == r.TmdbId && w.WatchedAt >= r.CreatedAt, ct))
                counts["tastemaker"]++;
        }

        var badges = Ladders.Select(l => FromLadder(l.Id, l.Icon, l.Description, l.Goals, counts[l.Id], l.Label)).ToList();

        // Genre badges: the genres they watch most (movies), named by tier: "Horror fan" → "Horror legend".
        var genres = movieIds.SelectMany(id => movies.GetValueOrDefault(id)?.Genres ?? []).GroupBy(g => g)
            .Select(g => (Genre: g.Key, Count: (long)g.Count())).OrderByDescending(g => g.Count).Take(4).ToList();
        foreach (var (genre, n) in genres)
            badges.Add(FromLadder($"genre:{genre}", "heart", $"{genre} movies watched", GenreGoals, n, _ => genre, i => $"{genre} {GenreTitles[i]}"));

        // One-offs.
        var nights = dated.Count(p => TimeZoneInfo.ConvertTime(p.WatchedAt, tz).Hour < 5);
        badges.Add(Special("nightowl", "Night owl", "eye", "A quarter of everything watched between midnight and 5 am (100 plays or more)",
            dated.Count >= 100 && nights * 4 >= dated.Count, nights, Math.Max(100, (dated.Count + 3) / 4)));
        var bestDay = dated.Where(p => p.Type == MediaType.Episode).GroupBy(p => (p.TmdbId, Day(p.WatchedAt))).Select(g => g.Count()).DefaultIfEmpty(0).Max();
        badges.Add(Special("binger", "Binge watcher", "play", "10 or more episodes of one show in a single day", bestDay >= 10, bestDay, 10));
        var tens = await db.Ratings.CountAsync(r => r.UserId == userId && r.Value == 10, ct);
        badges.Add(Special("perfect10", "Perfect 10", "star", "Gave something a 10/10", tens > 0, tens, 1));
        badges.Add(Special("founder", "Founding member", "shield", "Joined SeenIt in its first year", user.CreatedAt < new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null));
        return badges;
    }

    private static BadgeDto FromLadder(string id, string icon, string description, long[] goals, long n, Func<long, string> label, Func<int, string>? tierLabel = null)
    {
        var reached = Array.FindLastIndex(goals, g => n >= g);   // -1: not yet
        var next = reached + 1 < goals.Length ? goals[reached + 1] : (long?)null;
        string Name(int i) => tierLabel?.Invoke(i) ?? label(goals[i]);
        return reached < 0
            ? new BadgeDto(id, Name(0), icon, BadgeTier.Bronze, description, false, n, goals[0], Name(0))
            : new BadgeDto(id, Name(reached), icon, (BadgeTier)reached, description, true, next is null ? null : n, next, next is null ? null : Name(reached + 1));
    }

    private static BadgeDto Special(string id, string label, string icon, string description, bool earned, long? progress, long? goal) =>
        new(id, label, icon, BadgeTier.Special, description, earned, earned ? null : progress, earned ? null : goal);
}
