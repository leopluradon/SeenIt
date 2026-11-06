using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// The deeper stats under the totals on the Stats page. Anything about habits (when you watch,
/// how soon after release, trends over time) only counts plays from the "started tracking" day;
/// back-filled plays carry the day they were entered. Totals-style figures use everything.
/// </summary>
public class MoreStatsService(AppDbContext db, TrackingService tracking)
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private sealed record P(MediaType Type, int TmdbId, int? Season, int? Episode, DateTimeOffset At, DateTime Local, WatchSource Source);
    private sealed record Ep(DateOnly? AirDate, int? Runtime);

    private sealed class Ctx
    {
        public required TimeZoneInfo Tz;
        public required DateOnly? Since;
        public required DateOnly Today;
        public required List<P> All, Dated, Tracked;
        public required Dictionary<int, MovieMeta> Movies;
        public required Dictionary<int, ShowMeta> Shows;
        public required Dictionary<(int, int, int), Ep> Episodes;
        public int Minutes(P p) => p.Type == MediaType.Movie
            ? Movies.GetValueOrDefault(p.TmdbId)?.Runtime ?? 0
            : Episodes.GetValueOrDefault((p.TmdbId, p.Season ?? 0, p.Episode ?? 0))?.Runtime ?? Shows.GetValueOrDefault(p.TmdbId)?.EpisodeRuntime ?? 0;
        public DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Tz).DateTime;
    }

    public async Task<MoreStatsDto> GetAsync(Guid userId, string? tzId, CancellationToken ct)
    {
        var tz = ReviewService.Zone(await tracking.ZoneIdAsync(userId, tzId, ct));
        var since = await tracking.TrackingSinceAsync(userId, ct);
        DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, tz).DateTime;

        var all = (await db.WatchEvents.AsNoTracking().Where(w => w.UserId == userId)
                .Select(w => new { w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt, w.Source }).ToListAsync(ct))
            .Select(w => new P(w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt, Local(w.WatchedAt), w.Source)).ToList();
        var dated = all.Where(p => !WatchDates.IsUnknown(p.At)).OrderBy(p => p.At).ToList();
        var movieIds = all.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct().ToList();
        var showIds = all.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();

        var c = new Ctx
        {
            Tz = tz, Since = since, Today = DateOnly.FromDateTime(Local(DateTimeOffset.UtcNow)),
            All = all, Dated = dated, Tracked = dated.Where(p => since is null || DateOnly.FromDateTime(p.Local) >= since).ToList(),
            // Everything but cast and crew, which are large JSON columns nothing here reads.
            Movies = await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId))
                .Select(m => new MovieMeta
                {
                    TmdbId = m.TmdbId, Title = m.Title, ReleaseDate = m.ReleaseDate, Runtime = m.Runtime, PosterPath = m.PosterPath,
                    BackdropPath = m.BackdropPath, Genres = m.Genres, VoteAverage = m.VoteAverage, Overview = m.Overview,
                    Studios = m.Studios, Countries = m.Countries, OriginalLanguage = m.OriginalLanguage, CollectionId = m.CollectionId,
                }).ToDictionaryAsync(m => m.TmdbId, ct),
            Shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId))
                .Select(s => new ShowMeta
                {
                    TmdbId = s.TmdbId, Name = s.Name, FirstAirDate = s.FirstAirDate, Status = s.Status, PosterPath = s.PosterPath,
                    BackdropPath = s.BackdropPath, Genres = s.Genres, EpisodeRuntime = s.EpisodeRuntime, VoteAverage = s.VoteAverage,
                    Overview = s.Overview, Networks = s.Networks, Countries = s.Countries, OriginalLanguage = s.OriginalLanguage,
                }).ToDictionaryAsync(s => s.TmdbId, ct),
            Episodes = (await db.Episodes.AsNoTracking().Where(e => showIds.Contains(e.ShowTmdbId))
                    .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.AirDate, e.Runtime }).ToListAsync(ct))
                .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber), e => new Ep(e.AirDate, e.Runtime)),
        };
        var progress = showIds.Count == 0 ? [] : await new ProgressCalculator(db).ForUserAsync(userId, showIds, ct);

        return new MoreStatsDto(
            since,
            ReleaseGap(c),
            Completion(c, progress),
            Heatmap(c),
            WeekHour(c),
            Runtime(c),
            Era(c),
            Studios(c),
            await FranchisesAsync(c, ct),
            await BacklogAsync(userId, c, progress, ct),
            await RatingsVsTmdbAsync(userId, ct),
            await FriendsAsync(userId, c, ct),
            await MilestonesAsync(userId, c, ct));
    }

    private static int Percent(int part, int whole) => whole == 0 ? 0 : (int)Math.Round(100.0 * part / whole);

    private static double? Median(List<int> xs)
    {
        if (xs.Count == 0) return null;
        xs.Sort();
        return xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2.0;
    }

    // ----- How soon after release (first watch of each movie / episode, tracked plays only) -----

    private static ReleaseGapStats ReleaseGap(Ctx c)
    {
        var gaps = new List<int>();
        // First-ever watch of each title (Dated is in time order), counted only if that was a tracked play.
        foreach (var g in c.Dated.GroupBy(p => (p.Type, p.TmdbId, p.Season, p.Episode)))
        {
            var first = g.First();
            if (c.Since is { } s && DateOnly.FromDateTime(first.Local) < s) continue;
            DateOnly? released = first.Type == MediaType.Movie
                ? c.Movies.GetValueOrDefault(first.TmdbId)?.ReleaseDate
                : c.Episodes.GetValueOrDefault((first.TmdbId, first.Season ?? 0, first.Episode ?? 0))?.AirDate;
            if (released is not { } r) continue;
            gaps.Add(Math.Max(0, DateOnly.FromDateTime(first.Local).DayNumber - r.DayNumber));
        }
        var buckets = new (string Label, int Max)[] { ("Same day", 0), ("Within a week", 7), ("Within a month", 31), ("Within a year", 365),
            ("1–5 years", 5 * 365), ("5–20 years", 20 * 365), ("20+ years", int.MaxValue) };
        var counts = new int[buckets.Length];
        foreach (var gap in gaps) counts[Array.FindIndex(buckets, b => gap <= b.Max)]++;
        var median = Median(gaps.ToList());
        return new ReleaseGapStats(gaps.Count, median is { } m ? (int)Math.Round(m) : null,
            Percent(gaps.Count(g => g <= 1), gaps.Count), Percent(gaps.Count(g => g <= 30), gaps.Count),
            buckets.Select((b, i) => new CountBucket(b.Label, counts[i])).ToList());
    }

    // ----- Finished vs dropped -----

    private static CompletionStats Completion(Ctx c, List<ShowProgressDto> progress)
    {
        var staleBefore = DateTimeOffset.UtcNow.AddDays(-180);
        string State(ShowProgressDto p) =>
            p.Completed ? (c.Shows.GetValueOrDefault(p.ShowTmdbId)?.IsOngoing == true ? "caught" : "finished")
            : p.Status == ShowTrackStatus.Paused ? "paused"
            : p.Status == ShowTrackStatus.Dropped ? "dropped"
            : p.LastWatchedAt is { } lw && lw < staleBefore ? "stalled"
            : "watching";
        var states = progress.Where(p => p.Watched > 0).Select(p => (P: p, S: State(p))).ToList();
        var abandoned = states.Where(x => x.S is "dropped" or "stalled").ToList();

        List<CompletionRow> By(Func<ShowMeta, IEnumerable<string>> keys) => states
            .SelectMany(x => (c.Shows.GetValueOrDefault(x.P.ShowTmdbId) is { } s ? keys(s) : []).Distinct().Select(k => (Key: k, x.S)))
            .GroupBy(x => x.Key)
            .Select(g => new CompletionRow(g.Key, g.Count(), g.Count(x => x.S is "finished" or "caught"), g.Count(x => x.S is "dropped" or "stalled")))
            .Where(r => r.Started >= 3).OrderByDescending(r => r.Started).Take(8).ToList();

        return new CompletionStats(
            states.Count(x => x.S == "finished"), states.Count(x => x.S == "watching"), states.Count(x => x.S == "caught"),
            states.Count(x => x.S == "paused"), states.Count(x => x.S == "dropped"), states.Count(x => x.S == "stalled"),
            abandoned.Count(x => x.P.Watched == 1), Median(abandoned.Select(x => x.P.Watched).ToList()),
            By(s => s.Genres), By(s => s.Networks));
    }

    // ----- Calendar heatmap and week × hour grid (tracked plays) -----

    private static List<HeatmapDay> Heatmap(Ctx c)
    {
        var start = c.Today.AddDays(-364);
        if (c.Since is { } s && s > start) start = s;
        var byDay = c.Tracked.GroupBy(p => DateOnly.FromDateTime(p.Local)).ToDictionary(g => g.Key, g => (Minutes: g.Sum(c.Minutes), Plays: g.Count()));
        var days = new List<HeatmapDay>();
        for (var d = start; d <= c.Today; d = d.AddDays(1))
            days.Add(byDay.TryGetValue(d, out var v) ? new HeatmapDay(d, v.Minutes, v.Plays) : new HeatmapDay(d, 0, 0));
        return days;
    }

    private static List<IReadOnlyList<int>> WeekHour(Ctx c)
    {
        var grid = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        // Letterboxd only records the day, so its plays have no real time.
        foreach (var p in c.Tracked.Where(p => WatchSources.HasTimeOfDay(p.Source)))
            grid[(int)p.Local.DayOfWeek][p.Local.Hour]++;
        return grid.Select(r => (IReadOnlyList<int>)r).ToList();
    }

    // ----- Movies: length, era, studios -----

    private static RuntimeStats Runtime(Ctx c)
    {
        var lengths = c.All.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct()
            .Select(id => c.Movies.GetValueOrDefault(id)?.Runtime).OfType<int>().Where(r => r > 0).ToList();
        var byYear = c.Tracked.Where(p => p.Type == MediaType.Movie)
            .GroupBy(p => p.Local.Year).OrderBy(g => g.Key).TakeLast(6)
            .Select(g => (g.Key, Runtimes: g.Select(p => c.Movies.GetValueOrDefault(p.TmdbId)?.Runtime).OfType<int>().Where(r => r > 0).ToList()))
            .Where(x => x.Runtimes.Count > 0)
            .Select(x => new CountBucket(x.Key.ToString(), (int)Math.Round(x.Runtimes.Average()))).ToList();
        return new RuntimeStats(lengths.Count, lengths.Count > 0 ? (int)Math.Round(lengths.Average()) : null,
            Percent(lengths.Count(r => r < 90), lengths.Count), Percent(lengths.Count(r => r >= 150), lengths.Count), byYear);
    }

    private static EraStats Era(Ctx c)
    {
        int? Year(P p) => c.Movies.GetValueOrDefault(p.TmdbId)?.ReleaseDate?.Year;
        var years = c.All.Where(p => p.Type == MediaType.Movie).Select(Year).OfType<int>().ToList();
        var first = new DateTime(c.Today.Year, c.Today.Month, 1).AddMonths(-11);
        var byMonth = Enumerable.Range(0, 12).Select(i => first.AddMonths(i)).Select(m =>
        {
            var ys = c.Tracked.Where(p => p.Type == MediaType.Movie && p.Local.Year == m.Year && p.Local.Month == m.Month).Select(Year).OfType<int>().ToList();
            return new CountBucket(m.ToString("MMM", En), ys.Count > 0 ? (int)Math.Round(ys.Average()) : 0);
        }).ToList();
        return new EraStats(years.Count > 0 ? (int)Math.Round(years.Average()) : null, byMonth);
    }

    private static List<CountBucket> Studios(Ctx c) => c.All.Where(p => p.Type == MediaType.Movie)
        .SelectMany(p => c.Movies.GetValueOrDefault(p.TmdbId)?.Studios ?? [])
        .GroupBy(s => s).Select(g => new CountBucket(g.Key, g.Count()))
        .OrderByDescending(b => b.Count).Take(12).ToList();

    // ----- Franchises -----

    private async Task<FranchiseStats> FranchisesAsync(Ctx c, CancellationToken ct)
    {
        var seen = c.All.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).ToHashSet();
        var ids = c.Movies.Values.Where(m => seen.Contains(m.TmdbId) && m.CollectionId != null).Select(m => m.CollectionId!.Value).Distinct().ToList();
        var collections = await db.Collections.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var items = collections.Select(col =>
            {
                var released = col.Parts.Where(p => p.ReleaseDate is { } d && d <= c.Today).OrderBy(p => p.ReleaseDate).ToList();
                var next = released.FirstOrDefault(p => !seen.Contains(p.TmdbId));
                return new FranchiseProgress(col.Id, col.Name, col.PosterPath, released.Count(p => seen.Contains(p.TmdbId)), released.Count,
                    next is null ? null : new MediaSummary(MediaType.Movie, next.TmdbId, next.Title, next.ReleaseDate?.Year, next.PosterPath, null, null, null));
            })
            .Where(f => f.Released >= 2 && f.Seen > 0)
            .OrderBy(f => f.Seen == f.Released)                       // in progress first
            .ThenByDescending(f => (double)f.Seen / f.Released).ThenByDescending(f => f.Released)
            .ToList();
        return new FranchiseStats(items.Count, items.Count(f => f.Seen == f.Released), items);
    }

    // ----- Episodes left -----

    private async Task<BacklogStats> BacklogAsync(Guid userId, Ctx c, List<ShowProgressDto> progress, CancellationToken ct)
    {
        // Shows you're actually watching: not finished, paused, dropped, or stalled (no episode in 6 months).
        var staleBefore = DateTimeOffset.UtcNow.AddDays(-180);
        var active = progress.Where(p => p.Status == ShowTrackStatus.Watching && !p.Completed && p.Aired > p.Watched
                                         && p.LastWatchedAt is { } lw && lw >= staleBefore).ToList();
        var ids = active.Select(p => p.ShowTmdbId).ToList();
        var watched = c.All.Where(p => p.Type == MediaType.Episode && ids.Contains(p.TmdbId))
            .Select(p => (p.TmdbId, p.Season ?? 0, p.Episode ?? 0)).ToHashSet();
        var aired = await db.Episodes.AsNoTracking()
            .Where(e => ids.Contains(e.ShowTmdbId) && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= c.Today)
            .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Runtime }).ToListAsync(ct);
        var left = aired.Where(e => !watched.Contains((e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber))).ToList();
        long minutes = left.Sum(e => (long)(e.Runtime ?? c.Shows.GetValueOrDefault(e.ShowTmdbId)?.EpisodeRuntime ?? 0));

        // Pace: episode minutes per day over the last 30 days.
        var monthAgo = DateTimeOffset.UtcNow.AddDays(-30);
        var pace = (int)Math.Round(c.Dated.Where(p => p.Type == MediaType.Episode && p.At >= monthAgo).Sum(c.Minutes) / 30.0);
        DateOnly? by = pace > 0 && minutes > 0 ? c.Today.AddDays((int)Math.Ceiling((double)minutes / pace)) : null;
        return new BacklogStats(active.Count, left.Count, minutes, pace, by);
    }

    // ----- Your ratings vs TMDB's -----

    private async Task<RatingsVsTmdb> RatingsVsTmdbAsync(Guid userId, CancellationToken ct)
    {
        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => r.UserId == userId && r.SeasonNumber == null && (r.Type == MediaType.Movie || r.Type == MediaType.Show)).ToListAsync(ct);
        var (movies, shows) = await tracking.LoadMetaAsync(ratings.Select(r => (r.Type, r.TmdbId)), ct);
        var rows = ratings.Select(r => r.Type == MediaType.Movie
                ? movies.TryGetValue(r.TmdbId, out var m) && m.VoteAverage is > 0 ? (Media: CatalogService.ToSummary(m), You: r.Value, Tmdb: m.VoteAverage.Value) : default
                : shows.TryGetValue(r.TmdbId, out var s) && s.VoteAverage is > 0 ? (Media: CatalogService.ToSummary(s), You: r.Value, Tmdb: s.VoteAverage.Value) : default)
            .Where(x => x.Media is not null).ToList();
        HotTake Take((MediaSummary Media, double You, double Tmdb) x) => new(x.Media, x.You, Math.Round(x.Tmdb, 1));
        return new RatingsVsTmdb(rows.Count,
            rows.Count > 0 ? Math.Round(rows.Average(x => x.You - x.Tmdb), 1) : null,
            rows.Where(x => x.You - x.Tmdb >= 1.5).OrderByDescending(x => x.You - x.Tmdb).Take(10).Select(Take).ToList(),
            rows.Where(x => x.Tmdb - x.You >= 1.5).OrderByDescending(x => x.Tmdb - x.You).Take(10).Select(Take).ToList());
    }

    // ----- Friends this month -----

    private async Task<FriendsBoard> FriendsAsync(Guid userId, Ctx c, CancellationToken ct)
    {
        var following = await db.Follows.Where(f => f.FollowerId == userId).Select(f => f.FolloweeId).ToListAsync(ct);
        var followers = (await db.Follows.Where(f => f.FolloweeId == userId).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        var people = await db.Users.AsNoTracking().Where(u => following.Contains(u.Id) || u.Id == userId).ToListAsync(ct);
        // Only people whose profile you can see (public, or friends who follow you back).
        people = people.Where(u => SocialService.CanView(u, userId, followers.Contains(u.Id))).ToList();
        var ids = people.Select(u => u.Id).ToList();

        var monthStartLocal = new DateTime(c.Today.Year, c.Today.Month, 1);
        var monthStart = new DateTimeOffset(monthStartLocal, c.Tz.GetUtcOffset(monthStartLocal));
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var recent = await db.WatchEvents.AsNoTracking()
            .Where(w => ids.Contains(w.UserId) && w.WatchedAt >= (monthStart < weekAgo ? monthStart : weekAgo))
            .Select(w => new { w.UserId, w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt }).ToListAsync(ct);
        var (movies, shows) = await tracking.LoadMetaAsync(recent.Select(w => (w.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, w.TmdbId)), ct);
        var epIds = recent.Where(w => w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct().ToList();
        var epRuntimes = (await db.Episodes.AsNoTracking().Where(e => epIds.Contains(e.ShowTmdbId))
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Runtime }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber), e => e.Runtime);
        int Mins(int type, int tmdb, int? s, int? e) => type == (int)MediaType.Movie
            ? movies.GetValueOrDefault(tmdb)?.Runtime ?? 0
            : epRuntimes.GetValueOrDefault((tmdb, s ?? 0, e ?? 0)) ?? shows.GetValueOrDefault(tmdb)?.EpisodeRuntime ?? 0;

        var month = recent.Where(w => w.WatchedAt >= monthStart).ToList();
        var rows = people.Select(u =>
            {
                var mine = month.Where(w => w.UserId == u.Id).ToList();
                return new LeaderRow(new UserCard(u.UserName!, u.DisplayName, following.Contains(u.Id), followers.Contains(u.Id)),
                    mine.Sum(w => (long)Mins((int)w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber)), mine.Count, u.Id == userId);
            })
            .OrderByDescending(r => r.Minutes).ToList();

        var top = recent.Where(w => w.UserId != userId && w.WatchedAt >= weekAgo)
            .GroupBy(w => (Type: w.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, w.TmdbId))
            .Select(g => (g.Key, Friends: g.Select(w => w.UserId).Distinct().Count()))
            .OrderByDescending(x => x.Friends).FirstOrDefault();
        MediaSummary? topMedia = top.Friends == 0 ? null
            : top.Key.Type == MediaType.Movie ? movies.TryGetValue(top.Key.TmdbId, out var tm) ? CatalogService.ToSummary(tm) : null
            : shows.TryGetValue(top.Key.TmdbId, out var ts) ? CatalogService.ToSummary(ts) : null;
        return new FriendsBoard(monthStartLocal.ToString("MMMM", En), rows.Count > 1 ? rows : [], topMedia, top.Friends);
    }

    // ----- Milestones -----

    private async Task<List<Milestone>> MilestonesAsync(Guid userId, Ctx c, CancellationToken ct)
    {
        var list = new List<Milestone>();
        bool Before(DateTimeOffset at) => c.Since is { } s && DateOnly.FromDateTime(c.Local(at)) < s;
        MediaSummary? Summ(P p) => p.Type == MediaType.Movie
            ? c.Movies.TryGetValue(p.TmdbId, out var m) ? CatalogService.ToSummary(m) : null
            : c.Shows.TryGetValue(p.TmdbId, out var s) ? CatalogService.ToSummary(s) : null;
        static string Ordinal(int n) => n.ToString("N0") + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

        void Firsts(MediaType type, string noun, int[] marks)
        {
            var firsts = c.Dated.Where(p => p.Type == type).DistinctBy(p => type == MediaType.Movie ? (p.TmdbId, 0, 0) : (p.TmdbId, p.Season ?? 0, p.Episode ?? 0)).ToList();
            foreach (var n in marks.Where(n => n <= firsts.Count))
            {
                var p = firsts[n - 1];
                list.Add(new Milestone($"{Ordinal(n)} {noun}", p.At, Summ(p), Before(p.At)));
            }
        }
        Firsts(MediaType.Movie, "movie", [1, 10, 25, 50, 100, 250, 500, 1000, 2000, 3000, 5000]);
        Firsts(MediaType.Episode, "episode", [1, 100, 500, 1000, 2500, 5000, 10000, 15000, 25000, 50000]);

        // Hours watched, counting every play.
        long total = 0;
        var hourMarks = new Queue<int>([24, 100, 250, 500, 1000, 2500, 5000, 10000]);
        foreach (var p in c.Dated)
        {
            total += c.Minutes(p);
            while (hourMarks.Count > 0 && total >= hourMarks.Peek() * 60L)
            {
                var h = hourMarks.Dequeue();
                list.Add(new Milestone(h == 24 ? "First full day of watching" : $"{h:N0} hours watched", p.At, Summ(p), Before(p.At)));
            }
        }

        var firstTen = await db.Ratings.AsNoTracking().Where(r => r.UserId == userId && r.Value == 10 && r.SeasonNumber == null)
            .OrderBy(r => r.RatedAt).FirstOrDefaultAsync(ct);
        if (firstTen is not null)
        {
            var (movies, shows) = await tracking.LoadMetaAsync([(firstTen.Type, firstTen.TmdbId)], ct);
            var media = firstTen.Type == MediaType.Movie
                ? movies.TryGetValue(firstTen.TmdbId, out var m) ? CatalogService.ToSummary(m) : null
                : shows.TryGetValue(firstTen.TmdbId, out var s) ? CatalogService.ToSummary(s) : null;
            list.Add(new Milestone("First 10/10", firstTen.RatedAt, media, Before(firstTen.RatedAt)));
        }
        // Milestones reached before the "started tracking" day carry the day the plays were entered,
        // not when they happened, so they're left out.
        return list.Where(m => !m.BeforeTracking).OrderByDescending(m => m.At).ToList();
    }
}
