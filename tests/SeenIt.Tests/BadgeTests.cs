using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class BadgeTests
{
    [Fact]
    public async Task Badges_are_worked_out_from_what_was_watched_and_pinned_ones_must_be_earned()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var me = new AppUser { Id = Guid.NewGuid(), UserName = "sam", TimeZone = "UTC", CreatedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) };
        db.Users.Add(me);
        var start = new DateTimeOffset(2026, 1, 1, 20, 0, 0, TimeSpan.Zero);
        // 60 horror movies (older than 1970 for 12 of them), one a day: a 60-day streak; one watched twice.
        for (var i = 0; i < 60; i++)
        {
            db.Movies.Add(new MovieMeta { TmdbId = i + 1, Title = $"M{i}", Runtime = 100, Genres = ["Horror"], ReleaseDate = new DateOnly(i < 12 ? 1960 : 2000, 1, 1) });
            db.WatchEvents.Add(new WatchEvent { UserId = me.Id, Type = MediaType.Movie, TmdbId = i + 1, WatchedAt = start.AddDays(i) });
        }
        db.WatchEvents.Add(new WatchEvent { UserId = me.Id, Type = MediaType.Movie, TmdbId = 1, WatchedAt = start.AddDays(100) });
        db.Ratings.Add(new Rating { UserId = me.Id, Type = MediaType.Movie, TmdbId = 1, Value = 10 });
        await db.SaveChangesAsync();

        var svc = new BadgeService(db, new MemoryCache(new MemoryCacheOptions()));
        var all = (await svc.AllAsync(me.Id, default)).ToDictionary(b => b.Id);

        Assert.Equal(("25 movies", BadgeTier.Bronze, true), (all["movies"].Label, all["movies"].Tier, all["movies"].Earned));
        Assert.Equal(("100 movies", 60L, 100L), (all["movies"].Next, all["movies"].Progress, all["movies"].Goal));
        Assert.Equal(("60-day streak", BadgeTier.Diamond), (all["streak"].Label, all["streak"].Tier));   // 60 days in a row
        Assert.Equal("Horror devotee", all["genre:Horror"].Label);
        Assert.True(all["classics"].Earned);       // 12 from before 1970
        Assert.True(all["perfect10"].Earned);
        Assert.True(all["founder"].Earned);
        Assert.False(all["episodes"].Earned);
        Assert.Equal(0L, all["episodes"].Progress);
        Assert.Equal("50 hours", all["hours"].Label);   // 61 plays × 100 min

        // Only earned badges show, in the chosen order; an old single milestone badge maps to its family.
        var pinned = await svc.PinnedAsync(me, UserPrefs.Default with { ProfileBadges = ["streak", "episodes", "movies"] }, default);
        Assert.Equal(["streak", "movies"], pinned.Select(b => b.Id));
        var old = await svc.PinnedAsync(me, UserPrefs.Default with { ProfileBadge = "50th movie" }, default);
        Assert.Equal("movies", Assert.Single(old).Id);
    }
}
