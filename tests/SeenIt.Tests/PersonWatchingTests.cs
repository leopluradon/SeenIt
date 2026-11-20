using Microsoft.EntityFrameworkCore;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

/// <summary>Which of someone's shows you're partway through: their episode counts are hidden there by the spoiler setting.</summary>
public class PersonWatchingTests
{
    private const int Finished = 1, Partway = 2, Untouched = 3, Uncached = 4, CaughtUp = 5;

    [Fact]
    public async Task Started_shows_with_an_aired_episode_left_count_as_partway()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var past = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1);
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        foreach (var show in new[] { Finished, Partway, Untouched, CaughtUp })
            for (var e = 1; e <= 3; e++)
                db.Episodes.Add(new EpisodeMeta { ShowTmdbId = show, SeasonNumber = 1, EpisodeNumber = e, AirDate = show == CaughtUp && e == 3 ? future : past });
        db.Episodes.Add(new EpisodeMeta { ShowTmdbId = Finished, SeasonNumber = 0, EpisodeNumber = 1, AirDate = past });   // an unseen special doesn't count
        await db.SaveChangesAsync();

        var seen = new Dictionary<int, Dictionary<(int, int), int>>
        {
            [Finished] = new() { [(1, 1)] = 1, [(1, 2)] = 1, [(1, 3)] = 1 },
            [Partway] = new() { [(1, 1)] = 1 },
            [Uncached] = new() { [(1, 1)] = 1 },
            [CaughtUp] = new() { [(1, 1)] = 1, [(1, 2)] = 1 },   // the third hasn't aired
        };
        var watching = await PersonService.WatchingAsync(db, [Finished, Partway, Untouched, Uncached, CaughtUp, Partway], seen, default);
        Assert.Equal(new HashSet<int> { Partway, Uncached }, watching);
    }
}
