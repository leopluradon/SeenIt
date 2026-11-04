using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class MoreStatsTests
{
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public async Task Release_gap_milestones_and_heatmap_respect_the_tracking_date()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Users.Add(new AppUser { Id = User, UserName = "me", TimeZone = "UTC", TrackingSince = today.AddDays(-100) });
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "New", Runtime = 100, ReleaseDate = today.AddDays(-50), Studios = ["A24"] });
        db.Movies.Add(new MovieMeta { TmdbId = 2, Title = "Old", Runtime = 180, ReleaseDate = new DateOnly(1980, 1, 1), Studios = ["A24", "MGM"] });
        db.Movies.Add(new MovieMeta { TmdbId = 3, Title = "Backfilled", Runtime = 80, ReleaseDate = new DateOnly(2000, 1, 1) });
        DateTimeOffset At(DateOnly d) => new(d.ToDateTime(new TimeOnly(20, 0)), TimeSpan.Zero);
        void Watch(int id, DateOnly d) => db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Movie, TmdbId = id, WatchedAt = At(d) });
        Watch(3, today.AddDays(-300));     // before tracking: no gap, no heatmap, but still a milestone
        Watch(1, today.AddDays(-50));      // on release day
        Watch(2, today.AddDays(-10));      // decades later
        Watch(2, today.AddDays(-5));       // rewatch: not a "first watch"
        await db.SaveChangesAsync();

        var m = await new MoreStatsService(db, new TrackingService(db, null!)).GetAsync(User, null, default);

        Assert.Equal(2, m.ReleaseGap.Count);
        Assert.Equal(50, m.ReleaseGap.DayOnePercent);
        Assert.Equal(1, m.ReleaseGap.Buckets.Single(b => b.Label == "Same day").Count);
        Assert.Equal(1, m.ReleaseGap.Buckets.Single(b => b.Label == "20+ years").Count);

        Assert.Equal(today.AddDays(-100), m.Heatmap[0].Date);                   // starts at the tracking date
        Assert.Equal(180, m.Heatmap.Single(d => d.Date == today.AddDays(-10)).Minutes);
        Assert.Equal(3, m.Runtime.Movies);
        Assert.Equal(3, m.Studios.Single(s => s.Label == "A24").Count);   // plays: "New" once + "Old" twice
        var first = Assert.Single(m.Milestones, x => x.Label == "1st movie");
        Assert.True(first.BeforeTracking);
        Assert.Equal("Backfilled", first.Item?.Title);
    }
}
