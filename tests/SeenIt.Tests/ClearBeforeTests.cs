using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

/// <summary>
/// Clear data uses bulk deletes, which the in-memory provider can't run. Set SEENIT_TEST_PG to a connection
/// string for an empty, throwaway Postgres database to run this; without it the test does nothing.
/// </summary>
public class ClearBeforeTests
{
    [Fact]
    public async Task Clearing_before_a_day_keeps_that_day_undated_plays_and_lists()
    {
        if (Environment.GetEnvironmentVariable("SEENIT_TEST_PG") is not { Length: > 0 } cs) return;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var me = Guid.NewGuid();
        var cutoff = DateTimeOffset.Parse("2026-11-01T04:00:00Z");   // what the endpoint passes: local midnight, given in UTC below
        db.Users.Add(new AppUser { Id = me, UserName = "me", NormalizedUserName = "ME" });
        WatchEvent Play(MediaType t, int id, DateTimeOffset at) => new() { UserId = me, Type = t, TmdbId = id, SeasonNumber = t == MediaType.Episode ? 1 : null, EpisodeNumber = t == MediaType.Episode ? 1 : null, WatchedAt = at };
        db.WatchEvents.AddRange(Play(MediaType.Movie, 1, cutoff.AddSeconds(-1)), Play(MediaType.Movie, 2, cutoff), Play(MediaType.Movie, 3, WatchDates.Unknown),
            Play(MediaType.Episode, 10, cutoff.AddDays(-5)));
        db.Ratings.AddRange(new Rating { UserId = me, Type = MediaType.Movie, TmdbId = 1, Value = 8, RatedAt = cutoff.AddDays(-1) },
            new Rating { UserId = me, Type = MediaType.Movie, TmdbId = 2, Value = 7, RatedAt = cutoff.AddHours(1) });
        db.Watchlist.Add(new WatchlistItem { UserId = me, Type = MediaType.Movie, TmdbId = 4, AddedAt = cutoff.AddDays(-2) });
        var list = new UserList { UserId = me, Name = "Mine" };
        db.Lists.Add(list);
        await db.SaveChangesAsync();
        db.ListItems.AddRange(new UserListItem { ListId = list.Id, Type = MediaType.Movie, TmdbId = 1, AddedAt = cutoff.AddDays(-9) },
            new UserListItem { ListId = list.Id, Type = MediaType.Movie, TmdbId = 2, AddedAt = cutoff.AddDays(1) });
        db.ShowStates.Add(new ShowState { UserId = me, ShowTmdbId = 10, Status = ShowTrackStatus.Paused });
        await db.SaveChangesAsync();

        // Movies only, everything dated before Nov 1.
        var r = await new TrackingService(db, null!).ClearAsync(me, ImportParts.All & ~ImportParts.Shows, default, cutoff.ToOffset(TimeSpan.FromHours(-4)));

        Assert.Equal((1, 1, 1, 1), (r.Plays, r.Ratings, r.Watchlist, r.Lists));
        db.ChangeTracker.Clear();
        Assert.Equal([2, 3, 10], (await db.WatchEvents.Where(w => w.UserId == me).Select(w => w.TmdbId).ToListAsync()).Order());
        Assert.Equal(2, (await db.Ratings.SingleAsync(x => x.UserId == me)).TmdbId);
        Assert.Equal(2, (await db.ListItems.SingleAsync(i => i.ListId == list.Id)).TmdbId);
        Assert.True(await db.Lists.AnyAsync(l => l.Id == list.Id));
        Assert.True(await db.ShowStates.AnyAsync(s => s.UserId == me));
        await db.Database.EnsureDeletedAsync();
    }
}
