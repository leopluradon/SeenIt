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

    private static void Watch(AppDbContext db, int s, int e, DateTimeOffset? at = null) =>
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Episode, TmdbId = 1, SeasonNumber = s, EpisodeNumber = e, WatchedAt = at ?? DateTimeOffset.UtcNow });

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

    [Fact]
    public async Task Rewatch_counts_only_plays_since_it_started()
    {
        using var db = Db();
        var before = DateTimeOffset.UtcNow.AddDays(-10);
        foreach (var (s, e) in new[] { (1, 1), (1, 2), (1, 3), (2, 1) }) Watch(db, s, e, before);
        db.Rewatches.Add(new ShowRewatch { UserId = User, ShowTmdbId = 1, Number = 2, StartedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();

        var p = Assert.Single(await new ProgressCalculator(db).ForUserAsync(User, null, default));
        Assert.Equal(2, p.Rewatch);
        Assert.Equal(0, p.Watched);
        Assert.False(p.Completed);
        Assert.Equal((1, 1), (p.NextEpisode!.SeasonNumber, p.NextEpisode.EpisodeNumber));

        Watch(db, 1, 1);
        await db.SaveChangesAsync();
        p = Assert.Single(await new ProgressCalculator(db).ForUserAsync(User, null, default));
        Assert.Equal(1, p.Watched);
        Assert.Equal((1, 2), (p.NextEpisode!.SeasonNumber, p.NextEpisode.EpisodeNumber));
    }

    [Fact]
    public async Task Finished_rewatches_are_counted_and_no_longer_filter()
    {
        using var db = Db();
        foreach (var (s, e) in new[] { (1, 1), (1, 2), (1, 3), (2, 1) }) Watch(db, s, e, DateTimeOffset.UtcNow.AddDays(-10));
        db.Rewatches.Add(new ShowRewatch { UserId = User, ShowTmdbId = 1, Number = 2, StartedAt = DateTimeOffset.UtcNow.AddDays(-5), CompletedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var p = Assert.Single(await new ProgressCalculator(db).ForUserAsync(User, null, default));
        Assert.Null(p.Rewatch);
        Assert.Equal(1, p.RewatchesCompleted);
        Assert.True(p.Completed);
    }
}
