using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Tests;

public class RecommendationWatchAlertTests
{
    private static readonly Guid Sam = Guid.NewGuid(), Alex = Guid.NewGuid();
    private static readonly DateTimeOffset Sent = DateTimeOffset.Parse("2026-11-01T18:00:00Z");

    private static async Task<AppDbContext> Setup(bool alertsOn = true)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Users.Add(new AppUser { Id = Sam, UserName = "sam", TimeZone = "UTC",
            PrefsJson = alertsOn ? null : UserPrefsStore.Write(UserPrefs.Default with { AlertRecWatched = false }) });
        db.Users.Add(new AppUser { Id = Alex, UserName = "alex", DisplayName = "Alex" });
        db.Movies.Add(new MovieMeta { TmdbId = 949, Title = "Heat", PosterPath = "/heat.jpg" });
        db.Shows.Add(new ShowMeta { TmdbId = 95396, Name = "Severance" });
        db.Recommendations.Add(new Recommendation { FromId = Sam, ToId = Alex, Type = MediaType.Movie, TmdbId = 949, CreatedAt = Sent });
        db.Recommendations.Add(new Recommendation { FromId = Sam, ToId = Alex, Type = MediaType.Show, TmdbId = 95396, CreatedAt = Sent });
        await db.SaveChangesAsync();
        return db;
    }

    private static WatchEvent Play(MediaType t, int id, DateTimeOffset at, int? s = null, int? e = null) =>
        new() { UserId = Alex, Type = t, TmdbId = id, SeasonNumber = s, EpisodeNumber = e, WatchedAt = at };

    [Fact]
    public async Task Watching_a_recommended_movie_or_starting_a_show_tells_the_sender_once()
    {
        var db = await Setup();
        await RecommendationWatchAlerts.NotifyAsync(db, [Play(MediaType.Movie, 949, Sent.AddDays(2)),
            Play(MediaType.Episode, 95396, Sent.AddDays(3), 1, 1), Play(MediaType.Episode, 95396, Sent.AddDays(3), 1, 2)], default);
        var alerts = await db.Notifications.Where(n => n.UserId == Sam).OrderBy(n => n.Title).ToListAsync();
        Assert.Equal(["Alex started Severance", "Alex watched Heat"], alerts.Select(a => a.Title));
        Assert.All(alerts, a => Assert.NotNull(a.WatchedRecommendationId));
        Assert.Equal("You recommended it on Nov 1", alerts[1].Detail);

        // Watching again (or more episodes) doesn't alert a second time.
        await RecommendationWatchAlerts.NotifyAsync(db, [Play(MediaType.Movie, 949, Sent.AddDays(9)), Play(MediaType.Episode, 95396, Sent.AddDays(9), 1, 3)], default);
        Assert.Equal(2, await db.Notifications.CountAsync(n => n.UserId == Sam));
    }

    [Fact]
    public async Task Old_history_and_turned_off_alerts_stay_quiet()
    {
        var db = await Setup();
        await RecommendationWatchAlerts.NotifyAsync(db, [Play(MediaType.Movie, 949, Sent.AddYears(-3))], default);   // an import of a 2023 watch
        Assert.Equal(0, await db.Notifications.CountAsync());
        await RecommendationWatchAlerts.NotifyAsync(db, [Play(MediaType.Movie, 949, WatchDates.Unknown)], default);   // no date: counts
        Assert.Equal(1, await db.Notifications.CountAsync());

        var off = await Setup(alertsOn: false);
        await RecommendationWatchAlerts.NotifyAsync(off, [Play(MediaType.Movie, 949, Sent.AddDays(1))], default);
        Assert.Equal(0, await off.Notifications.CountAsync());
    }
}
