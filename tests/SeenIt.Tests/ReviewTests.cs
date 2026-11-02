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
}
