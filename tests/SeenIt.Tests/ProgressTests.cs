using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class ProgressTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static AppDbContext Db()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Shows.Add(new ShowMeta { TmdbId = 1, Name = "Show", Status = "Returning Series" });
        // Season 1: 3 aired episodes. Season 2: 1 aired, 1 future. Specials don't count.
        foreach (var (s, e, days) in new[] { (1, 1, -30), (1, 2, -23), (1, 3, -16), (2, 1, -2), (2, 2, 5), (0, 1, -40) })
            db.Episodes.Add(new EpisodeMeta { ShowTmdbId = 1, SeasonNumber = s, EpisodeNumber = e, Name = $"S{s}E{e}", AirDate = Today.AddDays(days) });
        db.SaveChanges();
        return db;
    }

    private static void Watch(AppDbContext db, int s, int e) =>
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Episode, TmdbId = 1, SeasonNumber = s, EpisodeNumber = e, WatchedAt = DateTimeOffset.UtcNow });

    [Fact]
    public async Task Next_episode_follows_the_furthest_watched()
    {
        using var db = Db();
        Watch(db, 1, 1);
        Watch(db, 1, 2);
        Watch(db, 1, 2); // rewatch doesn't inflate progress
        Watch(db, 0, 1); // specials don't count
        await db.SaveChangesAsync();

        var p = Assert.Single(await new ProgressCalculator(db).ForUserAsync(User, null, default));
        Assert.Equal(4, p.Aired);
        Assert.Equal(2, p.Watched);
        Assert.Equal((1, 3), (p.NextEpisode!.SeasonNumber, p.NextEpisode.EpisodeNumber));
        Assert.False(p.Completed);
    }

    [Fact]
    public async Task Falls_back_to_earliest_gap_and_completes_when_all_aired_are_watched()
    {
        using var db = Db();
        Watch(db, 2, 1);
        await db.SaveChangesAsync();
        var p = Assert.Single(await new ProgressCalculator(db).ForUserAsync(User, null, default));
        Assert.Equal((1, 1), (p.NextEpisode!.SeasonNumber, p.NextEpisode.EpisodeNumber));

        Watch(db, 1, 1); Watch(db, 1, 2); Watch(db, 1, 3);
        await db.SaveChangesAsync();
        p = Assert.Single(await new ProgressCalculator(db).ForUserAsync(User, null, default));
        Assert.True(p.Completed);
        Assert.Null(p.NextEpisode); // S02E02 hasn't aired
    }
}
