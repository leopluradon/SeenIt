using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Builds month-in-review and year-in-review summaries. Periods are calendar months/years in the
/// viewer's own time zone; plays with an unknown date are left out.
/// </summary>
public class ReviewService(AppDbContext db, TrackingService tracking)
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static TimeZoneInfo Zone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) ? tz : TimeZoneInfo.Utc;

    private sealed record Play(Guid Id, MediaType Type, int TmdbId, int? Season, int? Episode, DateTimeOffset At, DateTime Local, WatchSource Source);

    /// <summary>Which years and months have any plays, for navigation.</summary>
    public async Task<List<ReviewYear>> GetPeriodsAsync(Guid userId, string? tzId, CancellationToken ct)
    {
        var tz = Zone(tzId);
        var plays = await LoadPlaysAsync(userId, tz, ct);
        var minutes = await RuntimesAsync(plays, ct);
        return plays.GroupBy(p => p.Local.Year).OrderByDescending(g => g.Key)
            .Select(y => new ReviewYear(y.Key, y.Count(), y.Sum(p => minutes(p)),
                y.GroupBy(p => p.Local.Month).OrderBy(m => m.Key)
                    .Select(m => new ReviewMonth(m.Key, m.Count(), m.Sum(p => minutes(p)))).ToList()))
            .ToList();
    }

    public async Task<ReviewDto> BuildAsync(Guid userId, int year, int? month, string? tzId, CancellationToken ct)
    {
        if (year is < 1900 or > 2200 || month is < 1 or > 12) throw new TrackingException("That isn't a valid period.");
        var tz = Zone(tzId);

        var start = new DateTime(year, month ?? 1, 1);
        var end = month is null ? start.AddYears(1) : start.AddMonths(1);
        var prevStart = month is null ? start.AddYears(-1) : start.AddMonths(-1);

        var all = await LoadPlaysAsync(userId, tz, ct);
        var inPeriod = all.Where(p => p.Local >= start && p.Local < end).OrderBy(p => p.Local).ToList();
        var previous = all.Where(p => p.Local >= prevStart && p.Local < start).ToList();

        // Metadata for everything involved.
        var movieIds = all.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct().ToList();
        var showIds = all.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();
        var movies = await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, ct);
        var shows = await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, ct);
        var minutes = await RuntimesAsync(all, ct);

        var ratings = await db.Ratings.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
        var ratingsInPeriod = ratings.Where(r => Local(r.RatedAt, tz) is var l && l >= start && l < end).ToList();
        var titleRating = ratings.Where(r => r.SeasonNumber == null)
            .ToDictionary(r => (r.Type, r.TmdbId), r => r.Value);

        PeriodTotals Totals(List<Play> ps, int ratingCount) => new(
            ps.Count,
            ps.Count(p => p.Type == MediaType.Movie),
            ps.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct().Count(),
            ps.Count(p => p.Type == MediaType.Episode),
            ps.Where(p => p.Type == MediaType.Episode).Select(p => (p.TmdbId, p.Season, p.Episode)).Distinct().Count(),
            ps.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().Count(),
            ps.Sum(p => minutes(p)),
            ratingCount);

        var prevRatings = ratings.Count(r => Local(r.RatedAt, tz) is var l && l >= prevStart && l < start);

        // ----- Days, streaks, habits -----
        // Back-filled plays (before the user's "started tracking" day) carry the day they were
        // entered, so they'd pile onto one day; habits only look at plays logged as they happened.
        var since = await tracking.TrackingSinceAsync(userId, ct);
        var habit = since is null ? inPeriod : inPeriod.Where(p => DateOnly.FromDateTime(p.Local) >= since).ToList();
        var byHabitDay = habit.GroupBy(p => DateOnly.FromDateTime(p.Local)).ToDictionary(g => g.Key, g => g.ToList());
        var byDay = inPeriod.GroupBy(p => DateOnly.FromDateTime(p.Local)).ToDictionary(g => g.Key, g => g.ToList());
        var habitDays = byHabitDay.Keys.OrderBy(d => d).ToList();
        var streak = LongestStreak(habitDays);
        var busiest = byHabitDay.OrderByDescending(d => d.Value.Count).ThenByDescending(d => d.Value.Sum(p => minutes(p)))
            .Select(d => new ReviewDay(d.Key, d.Value.Count, d.Value.Sum(p => minutes(p)))).FirstOrDefault();
        var weekdays = Enum.GetValues<DayOfWeek>().Select(d => (d, n: habit.Count(p => p.Local.DayOfWeek == d))).ToList();
        // Letterboxd only records the day, so its plays have no real time of day.
        var timed = habit.Where(p => p.Source != WatchSource.LetterboxdImport).ToList();
        var hours = Enumerable.Range(0, 24).Select(h => (h, n: timed.Count(p => p.Local.Hour == h))).ToList();
        var daysInPeriod = (int)(end - start).TotalDays;

        var timeline = month is null
            ? Enumerable.Range(1, 12).Select(m => new CountBucket(new DateTime(year, m, 1).ToString("MMM", En), inPeriod.Count(p => p.Local.Month == m))).ToList()
            : Enumerable.Range(1, daysInPeriod).Select(d => new CountBucket(d.ToString(), byDay.GetValueOrDefault(new DateOnly(year, month.Value, d))?.Count ?? 0)).ToList();

        // ----- Titles -----
        var topShows = inPeriod.Where(p => p.Type == MediaType.Episode).GroupBy(p => p.TmdbId)
            .Select(g => new ReviewItem(MediaType.Show, g.Key, shows.GetValueOrDefault(g.Key)?.Name ?? $"Show {g.Key}",
                shows.GetValueOrDefault(g.Key)?.PosterPath, g.Count(), g.Sum(p => minutes(p)),
                titleRating.TryGetValue((MediaType.Show, g.Key), out var r) ? r : null,
                g.Select(p => (p.Season, p.Episode)).Distinct().Count() is var n && n == 1 ? "1 episode" : $"{n} episodes"))
            .OrderByDescending(i => i.Plays).ThenByDescending(i => i.Minutes).Take(10).ToList();

        var topMovies = inPeriod.Where(p => p.Type == MediaType.Movie).GroupBy(p => p.TmdbId)
            .Select(g => MovieItem(g.Key, g.Count(), g.Sum(p => minutes(p))))
            .OrderByDescending(i => i.Rating ?? 0).ThenByDescending(i => i.Plays).ThenByDescending(i => i.Minutes).Take(10).ToList();

        ReviewItem MovieItem(int id, int plays, long mins, string? detail = null)
        {
            var m = movies.GetValueOrDefault(id);
            return new ReviewItem(MediaType.Movie, id, m?.Title ?? $"Movie {id}", m?.PosterPath, plays, mins,
                titleRating.TryGetValue((MediaType.Movie, id), out var r) ? r : null, detail ?? m?.ReleaseDate?.Year.ToString());
        }

        // ----- People: count distinct titles they appear in, then plays -----
        var titles = inPeriod.GroupBy(p => (p.Type, p.TmdbId)).Select(g => (g.Key.Type, g.Key.TmdbId, Plays: g.Count())).ToList();
        var actorTally = new Dictionary<int, (PersonCredit P, int Titles, int Plays)>();
        var directorTally = new Dictionary<int, (PersonCredit P, int Titles, int Plays)>();
        foreach (var t in titles)
        {
            var cast = t.Type == MediaType.Movie ? movies.GetValueOrDefault(t.TmdbId)?.Cast : shows.GetValueOrDefault(t.TmdbId)?.Cast;
            foreach (var c in (cast ?? []).Take(10)) Tally(actorTally, c, t.Plays);
            var directors = t.Type == MediaType.Movie ? movies.GetValueOrDefault(t.TmdbId)?.Directors : shows.GetValueOrDefault(t.TmdbId)?.Creators;
            foreach (var d in directors ?? []) Tally(directorTally, d, t.Plays);
        }
        static void Tally(Dictionary<int, (PersonCredit P, int Titles, int Plays)> tally, PersonCredit p, int plays)
        {
            var cur = tally.GetValueOrDefault(p.Id, (p, 0, 0));
            tally[p.Id] = (p, cur.Item2 + 1, cur.Item3 + plays);
        }
        List<ReviewPerson> TopPeople(Dictionary<int, (PersonCredit P, int Titles, int Plays)> t) =>
            t.Values
                .OrderByDescending(v => v.Titles).ThenByDescending(v => v.Plays)
                .Take(12).Select(v => new ReviewPerson(v.P.Id, v.P.Name, v.P.ProfilePath, v.Titles, v.Plays)).ToList();

        // ----- Breakdowns (weighted by plays) -----
        var genres = new Dictionary<string, int>();
        var networks = new Dictionary<string, int>();
        var languages = new Dictionary<string, int>();
        var countries = new Dictionary<string, int>();
        var decades = new Dictionary<string, int>();
        foreach (var p in inPeriod)
        {
            if (p.Type == MediaType.Movie && movies.GetValueOrDefault(p.TmdbId) is { } m)
            {
                foreach (var g in m.Genres) Add(genres, g);
                if (m.OriginalLanguage is { } lang) Add(languages, Language(lang));
                foreach (var c in m.Countries) Add(countries, Country(c));
                if (m.ReleaseDate is { } rd) Add(decades, $"{rd.Year / 10 * 10}s");
            }
            else if (p.Type == MediaType.Episode && shows.GetValueOrDefault(p.TmdbId) is { } s)
            {
                foreach (var g in s.Genres) Add(genres, g);
                foreach (var n in s.Networks) Add(networks, n);
                if (s.OriginalLanguage is { } lang) Add(languages, Language(lang));
                foreach (var c in s.Countries) Add(countries, Country(c));
            }
        }
        static void Add(Dictionary<string, int> d, string key) => d[key] = d.GetValueOrDefault(key) + 1;
        static List<CountBucket> Top(Dictionary<string, int> d, int n = 10) =>
            d.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(n).Select(x => new CountBucket(x.Key, x.Value)).ToList();

        var sources = inPeriod.GroupBy(p => p.Source).OrderByDescending(g => g.Count())
            .Select(g => new CountBucket(g.Key switch
            {
                WatchSource.Jellyfin => "Jellyfin",
                WatchSource.TraktImport => "Trakt import",
                WatchSource.LetterboxdImport => "Letterboxd import",
                _ => "Marked in SeenIt",
            }, g.Count())).ToList();

        // ----- Ratings given this period -----
        var highest = ratingsInPeriod.Where(r => r.SeasonNumber == null).OrderByDescending(r => r.Value).ThenByDescending(r => r.RatedAt).Take(10)
            .Select(r => r.Type == MediaType.Movie
                ? new ReviewItem(MediaType.Movie, r.TmdbId, movies.GetValueOrDefault(r.TmdbId)?.Title ?? $"Movie {r.TmdbId}", movies.GetValueOrDefault(r.TmdbId)?.PosterPath, 0, 0, r.Value, null)
                : new ReviewItem(MediaType.Show, r.TmdbId, shows.GetValueOrDefault(r.TmdbId)?.Name ?? $"Show {r.TmdbId}", shows.GetValueOrDefault(r.TmdbId)?.PosterPath, 0, 0, r.Value, null))
            .ToList();
        // Ratings can be for titles watched in other periods, so fill in any missing names.
        if (highest.Any(h => h.Title.StartsWith("Movie ") || h.Title.StartsWith("Show ")))
        {
            var (mm, ss) = await tracking.LoadMetaAsync(highest.Select(h => (h.Type, h.TmdbId)), ct);
            highest = highest.Select(h => h.Type == MediaType.Movie && mm.TryGetValue(h.TmdbId, out var m2) ? h with { Title = m2.Title, PosterPath = m2.PosterPath }
                : h.Type == MediaType.Show && ss.TryGetValue(h.TmdbId, out var s2) ? h with { Title = s2.Name, PosterPath = s2.PosterPath } : h).ToList();
        }

        // ----- Firsts, rewatches, completions -----
        var firstEver = all.GroupBy(p => (p.Type, p.TmdbId, p.Season, p.Episode)).ToDictionary(g => g.Key, g => g.Min(p => p.At));
        var rewatches = inPeriod.Count(p => p.At > firstEver[(p.Type, p.TmdbId, p.Season, p.Episode)]);
        var firstShowWatch = all.Where(p => p.Type == MediaType.Episode).GroupBy(p => p.TmdbId).ToDictionary(g => g.Key, g => g.Min(p => p.Local));
        var showsStarted = firstShowWatch.Count(kv => kv.Value >= start && kv.Value < end);

        var periodShows = inPeriod.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();
        var progress = periodShows.Count == 0 ? [] : await new ProgressCalculator(db).ForUserAsync(userId, periodShows, ct);
        var completed = progress.Count(p => p.Completed && p.LastWatchedAt is { } lw && Local(lw, tz) is var l && l >= start && l < end);

        var mostRewatched = inPeriod.GroupBy(p => (p.Type, p.TmdbId, p.Season, p.Episode)).Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count()).FirstOrDefault() is { } rw
            ? rw.Key.Type == MediaType.Movie
                ? MovieItem(rw.Key.TmdbId, rw.Count(), rw.Sum(p => minutes(p)))
                : new ReviewItem(MediaType.Episode, rw.Key.TmdbId, shows.GetValueOrDefault(rw.Key.TmdbId)?.Name ?? "", shows.GetValueOrDefault(rw.Key.TmdbId)?.PosterPath,
                    rw.Count(), rw.Sum(p => minutes(p)), null, $"S{rw.Key.Season:00}E{rw.Key.Episode:00}")
            : null;

        var periodMovies = inPeriod.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct()
            .Select(id => movies.GetValueOrDefault(id)).OfType<MovieMeta>().ToList();
        ReviewItem? Pick(MovieMeta? m, string? detail) => m is null ? null : MovieItem(m.TmdbId, inPeriod.Count(p => p.Type == MediaType.Movie && p.TmdbId == m.TmdbId), 0, detail);
        var longest = Pick(periodMovies.Where(m => m.Runtime > 0).MaxBy(m => m.Runtime), null);
        if (longest is not null) longest = longest with { Detail = FormatMinutes(movies[longest.TmdbId].Runtime ?? 0) };
        var oldest = Pick(periodMovies.Where(m => m.ReleaseDate is not null).MinBy(m => m.ReleaseDate), null);
        var newest = Pick(periodMovies.Where(m => m.ReleaseDate is not null).MaxBy(m => m.ReleaseDate), null);

        var bookends = new List<WatchEvent>();
        if (inPeriod.Count > 0)
        {
            var ids = new[] { inPeriod[0].Id, inPeriod[^1].Id };
            bookends = await db.WatchEvents.AsNoTracking().Where(w => ids.Contains(w.Id)).ToListAsync(ct);
        }
        var bookendEntries = await tracking.ToHistoryAsync(bookends, ct);

        var label = month is null ? year.ToString() : start.ToString("MMMM yyyy", En);
        var prevLabel = month is null ? (year - 1).ToString() : prevStart.ToString("MMMM yyyy", En);
        var ratingValues = ratingsInPeriod.Select(r => r.Value).ToList();

        return new ReviewDto(
            year, month, label, tz.Id,
            Totals(inPeriod, ratingsInPeriod.Count), Totals(previous, prevRatings), prevLabel,
            byDay.Count, daysInPeriod, streak, busiest,
            habit.Count == 0 ? null : weekdays.MaxBy(w => w.n).d.ToString(),
            timed.Count == 0 ? null : hours.MaxBy(h => h.n).h,
            habitDays.Count == 0 ? 0 : Math.Round((double)habit.Count / habitDays.Count, 1),
            timeline,
            weekdays.Select(w => new CountBucket(w.d.ToString()[..3], w.n)).ToList(),
            hours.Select(h => new CountBucket(h.h.ToString("00"), h.n)).ToList(),
            topShows, topMovies,
            TopPeople(actorTally), TopPeople(directorTally),
            Top(genres), Top(networks), Top(languages, 8), Top(countries, 8),
            decades.OrderBy(d => d.Key).Select(d => new CountBucket(d.Key, d.Value)).ToList(),
            sources,
            ratingValues.Count > 0 ? Math.Round(ratingValues.Average(), 2) : null,
            Enumerable.Range(1, 10).Select(i => new CountBucket(i.ToString(), ratingValues.Count(v => v == i))).ToList(),
            highest,
            showsStarted, completed,
            inPeriod.Count - rewatches, rewatches,
            longest, oldest, newest,
            bookendEntries.FirstOrDefault(b => b.Id == inPeriod.FirstOrDefault()?.Id),
            bookendEntries.FirstOrDefault(b => b.Id == inPeriod.LastOrDefault()?.Id),
            mostRewatched);
    }

    private async Task<List<Play>> LoadPlaysAsync(Guid userId, TimeZoneInfo tz, CancellationToken ct)
    {
        var unknown = WatchDates.Unknown;
        var rows = await db.WatchEvents.AsNoTracking()
            .Where(w => w.UserId == userId && w.WatchedAt != unknown)
            .Select(w => new { w.Id, w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt, w.Source })
            .ToListAsync(ct);
        return rows.Select(w => new Play(w.Id, w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt, Local(w.WatchedAt, tz), w.Source)).ToList();
    }

    /// <summary>Minutes for a play: the episode's own runtime, else the show's typical one, else the movie's.</summary>
    private async Task<Func<Play, long>> RuntimesAsync(List<Play> plays, CancellationToken ct)
    {
        var movieIds = plays.Where(p => p.Type == MediaType.Movie).Select(p => p.TmdbId).Distinct().ToList();
        var showIds = plays.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();
        var movieRuntime = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, m => m.Runtime ?? 0, ct);
        var showRuntime = await db.Shows.Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, s => s.EpisodeRuntime ?? 0, ct);
        var episodeRuntime = (await db.Episodes.Where(e => showIds.Contains(e.ShowTmdbId) && e.Runtime != null)
                .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.Runtime }).ToListAsync(ct))
            .ToDictionary(e => (e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber), e => e.Runtime!.Value);
        return p => p.Type == MediaType.Movie
            ? movieRuntime.GetValueOrDefault(p.TmdbId)
            : episodeRuntime.TryGetValue((p.TmdbId, p.Season ?? 0, p.Episode ?? 0), out var r) ? r : showRuntime.GetValueOrDefault(p.TmdbId);
    }

    public static ReviewStreak LongestStreak(IReadOnlyList<DateOnly> sortedDays)
    {
        if (sortedDays.Count == 0) return new ReviewStreak(0, null, null);
        int best = 1, run = 1;
        DateOnly bestStart = sortedDays[0], bestEnd = sortedDays[0], runStart = sortedDays[0];
        for (var i = 1; i < sortedDays.Count; i++)
        {
            if (sortedDays[i] == sortedDays[i - 1].AddDays(1)) run++;
            else { run = 1; runStart = sortedDays[i]; }
            if (run > best) { best = run; bestStart = runStart; bestEnd = sortedDays[i]; }
        }
        return new ReviewStreak(best, bestStart, bestEnd);
    }

    private static DateTime Local(DateTimeOffset at, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(at, tz).DateTime;

    private static string FormatMinutes(int m) => m < 60 ? $"{m}m" : $"{m / 60}h {m % 60}m";

    private static string Language(string iso)
    {
        try { return CultureInfo.GetCultureInfo(iso).EnglishName; }
        catch (CultureNotFoundException) { return iso; }
    }

    private static string Country(string iso)
    {
        if (iso.Length != 2) return iso; // already a name (older cached rows)
        try { return new RegionInfo(iso).EnglishName; }
        catch (ArgumentException) { return iso; }
    }
}
