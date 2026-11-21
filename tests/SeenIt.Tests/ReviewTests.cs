using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class ReviewTests
{
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void Longest_streak_finds_the_longest_consecutive_run()
    {
        var days = new[] { 1, 2, 3, 5, 6, 7, 8, 12 }.Select(d => new DateOnly(2026, 3, d)).ToList();
        var s = ReviewService.LongestStreak(days);
        Assert.Equal(4, s.Days);
        Assert.Equal(new DateOnly(2026, 3, 5), s.Start);
        Assert.Equal(new DateOnly(2026, 3, 8), s.End);
        Assert.Equal(0, ReviewService.LongestStreak([]).Days);
    }

    [Fact]
    public async Task Month_review_counts_plays_rewatches_and_new_shows_in_local_time()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat", Runtime = 170, Genres = ["Crime"], ReleaseDate = new DateOnly(1995, 12, 15),
            Directors = [new PersonCredit { Id = 9, Name = "Michael Mann" }], Cast = [new PersonCredit { Id = 7, Name = "Al Pacino" }] });
        db.Shows.Add(new ShowMeta { TmdbId = 2, Name = "Severance", EpisodeRuntime = 50, Genres = ["Drama"], Networks = ["Apple TV"] });
        db.Episodes.Add(new EpisodeMeta { ShowTmdbId = 2, SeasonNumber = 1, EpisodeNumber = 1, Runtime = 57, AirDate = new DateOnly(2022, 2, 18) });
        db.Episodes.Add(new EpisodeMeta { ShowTmdbId = 2, SeasonNumber = 1, EpisodeNumber = 2, AirDate = new DateOnly(2022, 2, 18) });

        void Watch(MediaType t, int id, DateTimeOffset at, int? s = null, int? e = null) =>
            db.WatchEvents.Add(new WatchEvent { UserId = User, Type = t, TmdbId = id, SeasonNumber = s, EpisodeNumber = e, WatchedAt = at });

        Watch(MediaType.Movie, 1, new(2026, 2, 10, 20, 0, 0, TimeSpan.Zero));           // February: earlier watch
        Watch(MediaType.Movie, 1, new(2026, 3, 2, 20, 0, 0, TimeSpan.Zero));            // March rewatch
        Watch(MediaType.Episode, 2, new(2026, 3, 3, 20, 0, 0, TimeSpan.Zero), 1, 1);    // show started in March
        Watch(MediaType.Episode, 2, new(2026, 3, 4, 20, 0, 0, TimeSpan.Zero), 1, 2);
        // 01:30 UTC on 1 April is still 31 March in Los Angeles.
        Watch(MediaType.Episode, 2, new(2026, 4, 1, 1, 30, 0, TimeSpan.Zero), 1, 1);
        Watch(MediaType.Movie, 1, WatchDates.Unknown);                                   // never counted
        await db.SaveChangesAsync();

        var svc = new ReviewService(db, new TrackingService(db, null!));
        var r = await svc.BuildAsync(User, 2026, 3, "America/Los_Angeles", default);

        Assert.Equal(4, r.Totals.Plays);
        Assert.Equal(1, r.Totals.MoviePlays);
        Assert.Equal(3, r.Totals.EpisodePlays);
        Assert.Equal(2, r.Totals.Episodes);
        Assert.Equal(170 + 57 + 50 + 57, r.Totals.Minutes);   // E2 falls back to the show's runtime
        Assert.Equal(2, r.Rewatches);                         // Heat again, S01E01 again
        Assert.Equal(1, r.ShowsStarted);
        Assert.Equal(3, r.LongestStreak.Days);                // 2nd–4th
        Assert.Equal(1, r.Previous.Plays);                    // February
        Assert.Equal("Severance", r.TopShows[0].Title);
        Assert.Equal("Al Pacino", Assert.Single(r.TopActors).Name);
        Assert.Contains(r.Genres, g => g.Label == "Drama" && g.Count == 3);
        Assert.Equal("1990s", Assert.Single(r.Decades).Label);
        Assert.Equal(31, r.Timeline.Count);
        Assert.Equal(1, r.Timeline[30].Count);               // the late-night play lands on the 31st

        var periods = await svc.GetPeriodsAsync(User, "America/Los_Angeles", default);
        Assert.Equal([2, 3], periods.Single().Months.Select(m => m.Month));
    }

    [Fact]
    public async Task Reviews_start_at_the_tracking_date_but_rewatches_still_see_earlier_plays()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Users.Add(new AppUser { Id = User, UserName = "me", TrackingSince = new DateOnly(2026, 3, 10) });
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat", Runtime = 170 });
        db.Movies.Add(new MovieMeta { TmdbId = 2, Title = "Ronin", Runtime = 120 });
        void Watch(int id, DateTimeOffset at) => db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Movie, TmdbId = id, WatchedAt = at });
        // Back-filled on 9 March: one film first seen years ago, one "watched" that day.
        Watch(1, new(2019, 5, 1, 20, 0, 0, TimeSpan.Zero));
        Watch(2, new(2026, 3, 9, 20, 0, 0, TimeSpan.Zero));
        // Logged as watched from the tracking day on.
        Watch(1, new(2026, 3, 12, 20, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var reviews = new ReviewService(db, new TrackingService(db, null!));
        var periods = await reviews.GetPeriodsAsync(User, "UTC", default);
        Assert.Equal([2026], periods.Select(p => p.Year));               // no review for 2019
        Assert.Equal([3], periods[0].Months.Select(m => m.Month));

        var march = await reviews.BuildAsync(User, 2026, 3, "UTC", default);
        Assert.Equal(1, march.Totals.Plays);                             // 9 March isn't counted
        Assert.Equal(1, march.Rewatches);                                // Heat was seen in 2019
        Assert.Equal(new DateOnly(2026, 3, 10), march.TrackingSince);
        Assert.Equal(0, (await reviews.BuildAsync(User, 2019, null, "UTC", default)).Totals.Plays);
    }

    [Fact]
    public async Task Busiest_day_is_the_one_with_the_most_time_watched()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat", Runtime = 170 });
        db.Shows.Add(new ShowMeta { TmdbId = 2, Name = "Parks and Recreation", EpisodeRuntime = 22 });
        // March 4: three 22-minute episodes (more plays). March 5: one 170-minute movie (more time).
        for (var e = 1; e <= 3; e++)
            db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Episode, TmdbId = 2, SeasonNumber = 1, EpisodeNumber = e, WatchedAt = new(2026, 3, 4, 18 + e, 0, 0, TimeSpan.Zero) });
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Movie, TmdbId = 1, WatchedAt = new(2026, 3, 5, 20, 0, 0, TimeSpan.Zero) });
        await db.SaveChangesAsync();

        var r = await new ReviewService(db, new TrackingService(db, null!)).BuildAsync(User, 2026, null, "UTC", default);
        Assert.Equal(new ReviewDay(new DateOnly(2026, 3, 5), 1, 170), r.BusiestDay);
    }

    [Fact]
    public async Task New_to_me_keeps_only_titles_first_watched_in_the_period()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat", Runtime = 170 });
        db.Movies.Add(new MovieMeta { TmdbId = 3, Title = "Arrival", Runtime = 116 });
        db.Movies.Add(new MovieMeta { TmdbId = 5, Title = "Alien", Runtime = 117 });
        db.Shows.Add(new ShowMeta { TmdbId = 2, Name = "Severance", EpisodeRuntime = 50 });
        db.Shows.Add(new ShowMeta { TmdbId = 4, Name = "Andor", EpisodeRuntime = 45 });
        void Watch(MediaType t, int id, DateTimeOffset at, int? s = null, int? e = null) =>
            db.WatchEvents.Add(new WatchEvent { UserId = User, Type = t, TmdbId = id, SeasonNumber = s, EpisodeNumber = e, WatchedAt = at });
        Watch(MediaType.Movie, 1, new(2026, 2, 10, 20, 0, 0, TimeSpan.Zero));           // Heat: seen in February…
        Watch(MediaType.Movie, 1, new(2026, 3, 2, 20, 0, 0, TimeSpan.Zero));            // …so March is a rewatch
        Watch(MediaType.Movie, 3, new(2026, 3, 5, 20, 0, 0, TimeSpan.Zero));            // Arrival: new in March
        Watch(MediaType.Movie, 5, WatchDates.Unknown);                                   // Alien: seen at some point
        Watch(MediaType.Movie, 5, new(2026, 3, 6, 20, 0, 0, TimeSpan.Zero));            // so not new in March
        Watch(MediaType.Episode, 2, new(2026, 3, 3, 20, 0, 0, TimeSpan.Zero), 1, 1);    // Severance: started in March
        Watch(MediaType.Episode, 4, new(2026, 2, 3, 20, 0, 0, TimeSpan.Zero), 1, 1);    // Andor: started in February
        Watch(MediaType.Episode, 4, new(2026, 3, 3, 21, 0, 0, TimeSpan.Zero), 1, 2);
        db.Ratings.Add(new Rating { UserId = User, Type = MediaType.Movie, TmdbId = 3, Value = 9, RatedAt = new(2026, 3, 5, 22, 0, 0, TimeSpan.Zero) });
        db.Ratings.Add(new Rating { UserId = User, Type = MediaType.Movie, TmdbId = 1, Value = 7, RatedAt = new(2026, 3, 2, 22, 0, 0, TimeSpan.Zero) });
        await db.SaveChangesAsync();
        var svc = new ReviewService(db, new TrackingService(db, null!));

        var all = await svc.BuildAsync(User, 2026, 3, "UTC", default);
        Assert.Equal(3, all.Totals.Movies);
        Assert.Equal(["Arrival"], (all.AllMovies ?? []).Where(m => m.New).Select(m => m.Title));
        Assert.Equal(["Severance"], (all.AllShows ?? []).Where(m => m.New).Select(m => m.Title));

        var fresh = await svc.BuildAsync(User, 2026, 3, "UTC", default, ReviewNew.Titles);
        Assert.Equal(ReviewNew.Titles, fresh.New);
        Assert.Equal((1, 1, 1), (fresh.Totals.Movies, fresh.Totals.Shows, fresh.Totals.Ratings));   // Arrival, Severance, Arrival's rating
        Assert.Equal(9, fresh.AverageRating);
        Assert.Equal(116 + 50, fresh.Totals.Minutes);
        // February compared the same way: Heat and Andor were new then.
        Assert.Equal((1, 1), (fresh.Previous.Movies, fresh.Previous.Shows));

        // First watches: Andor counts for its new episode though it started in February; Heat's rewatch and Alien
        // (first seen on an unknown date) don't, nor does Heat's rating.
        var firsts = await svc.BuildAsync(User, 2026, 3, "UTC", default, ReviewNew.Firsts);
        Assert.Equal(ReviewNew.Firsts, firsts.New);
        Assert.Equal((1, 2, 2, 1), (firsts.Totals.Movies, firsts.Totals.Shows, firsts.Totals.Episodes, firsts.Totals.Ratings));
        Assert.Equal(["Andor", "Severance"], (firsts.AllShows ?? []).Select(x => x.Title).Order());
        Assert.Equal(116 + 50 + 45, firsts.Totals.Minutes);
        Assert.Equal((1, 1), (firsts.Previous.Movies, firsts.Previous.Shows));
    }

    [Fact]
    public async Task Movies_or_tv_only()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat", Runtime = 170 });
        db.Shows.Add(new ShowMeta { TmdbId = 2, Name = "Severance", EpisodeRuntime = 50 });
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Movie, TmdbId = 1, WatchedAt = new(2026, 3, 2, 20, 0, 0, TimeSpan.Zero) });
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Episode, TmdbId = 2, SeasonNumber = 1, EpisodeNumber = 1, WatchedAt = new(2026, 3, 3, 20, 0, 0, TimeSpan.Zero) });
        db.Ratings.Add(new Rating { UserId = User, Type = MediaType.Movie, TmdbId = 1, Value = 8, RatedAt = new(2026, 3, 2, 22, 0, 0, TimeSpan.Zero) });
        db.Ratings.Add(new Rating { UserId = User, Type = MediaType.Episode, TmdbId = 2, SeasonNumber = 1, EpisodeNumber = 1, Value = 6, RatedAt = new(2026, 3, 3, 22, 0, 0, TimeSpan.Zero) });
        await db.SaveChangesAsync();
        var svc = new ReviewService(db, new TrackingService(db, null!));

        var movies = await svc.BuildAsync(User, 2026, 3, "UTC", default, kind: MediaFilter.Parse("movie"));
        Assert.Equal((1, 0, 1, 8.0), (movies.Totals.Movies, movies.Totals.Shows, movies.Totals.Ratings, movies.AverageRating!.Value));
        var tv = await svc.BuildAsync(User, 2026, 3, "UTC", default, kind: MediaFilter.Parse("tv"));
        Assert.Equal((0, 1, 1, 6.0), (tv.Totals.Movies, tv.Totals.Shows, tv.Totals.Ratings, tv.AverageRating!.Value));

        var stats = await new TrackingService(db, null!).GetStatsAsync(User, "UTC", default, MediaType.Show);
        Assert.Equal((0, 1, 1), (stats.MoviesWatched, stats.ShowsWatched, stats.Ratings));
        Assert.Null(MediaFilter.Parse("everything"));
    }
}
