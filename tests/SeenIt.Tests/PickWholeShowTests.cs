using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

/// <summary>Pick for me's "Only shows I can finish": how long each show has left for you.</summary>
public class PickWholeShowTests
{
    [Fact]
    public async Task Minutes_left_are_the_aired_regular_episodes_you_havent_seen()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var me = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        const int Short = 1, Unstarted = 2, NoList = 3;
        db.Episodes.AddRange(
            new EpisodeMeta { ShowTmdbId = Short, SeasonNumber = 1, EpisodeNumber = 1, Runtime = 30, AirDate = today.AddYears(-1) },
            new EpisodeMeta { ShowTmdbId = Short, SeasonNumber = 1, EpisodeNumber = 2, Runtime = 30, AirDate = today.AddYears(-1) },
            new EpisodeMeta { ShowTmdbId = Short, SeasonNumber = 1, EpisodeNumber = 3, Runtime = null, AirDate = today.AddYears(-1) },   // the show's usual length
            new EpisodeMeta { ShowTmdbId = Short, SeasonNumber = 1, EpisodeNumber = 4, Runtime = 30, AirDate = today.AddDays(7) },       // not out yet
            new EpisodeMeta { ShowTmdbId = Short, SeasonNumber = 0, EpisodeNumber = 1, Runtime = 30, AirDate = today.AddYears(-1) },     // a special
            new EpisodeMeta { ShowTmdbId = Unstarted, SeasonNumber = 1, EpisodeNumber = 1, Runtime = 45, AirDate = today.AddYears(-1) },
            new EpisodeMeta { ShowTmdbId = Unstarted, SeasonNumber = 2, EpisodeNumber = 1, Runtime = 50, AirDate = today.AddYears(-1) });
        db.WatchEvents.Add(new WatchEvent { UserId = me, Type = MediaType.Episode, TmdbId = Short, SeasonNumber = 1, EpisodeNumber = 1, WatchedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var left = await new PickService(db, null!, NullLogger<PickService>.Instance)
            .MinutesLeftAsync(me, new Dictionary<int, int> { [Short] = 25, [Unstarted] = 45, [NoList] = 30 }, today, default);

        Assert.Equal(30 + 25, left[Short]);        // E2, and E3 at the usual 25
        Assert.Equal(45 + 50, left[Unstarted]);    // all of it
        Assert.False(left.ContainsKey(NoList));    // no episode list: can't be timed, so not offered
    }
}
