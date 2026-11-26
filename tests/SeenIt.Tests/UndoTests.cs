using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

/// <summary>
/// Undoing a watch deletes plays with a bulk delete, which the in-memory provider can't run. Set SEENIT_TEST_PG to a
/// connection string for an empty, throwaway Postgres database to run this; without it the test does nothing.
/// </summary>
public class UndoTests
{
    private sealed class Factory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(o);
    }

    [Fact]
    public async Task Recent_actions_undo_once_and_log_it()
    {
        if (Environment.GetEnvironmentVariable("SEENIT_TEST_PG") is not { Length: > 0 } cs) return;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        var me = Guid.NewGuid();
        db.Users.Add(new AppUser { Id = me, UserName = "sam", NormalizedUserName = "SAM" });
        db.Movies.Add(new MovieMeta { TmdbId = 949, Title = "Heat", Runtime = 170 });
        db.Shows.Add(new ShowMeta { TmdbId = 1396, Name = "Breaking Bad" });
        var play = new WatchEvent { UserId = me, Type = MediaType.Movie, TmdbId = 949, WatchedAt = DateTimeOffset.UtcNow.AddHours(-2).ToUniversalTime() };
        db.WatchEvents.Add(play);
        db.Ratings.Add(new Rating { UserId = me, Type = MediaType.Movie, TmdbId = 949, Value = 9 });
        db.ShowStates.Add(new ShowState { UserId = me, ShowTmdbId = 1396, Status = ShowTrackStatus.Dropped });
        await db.SaveChangesAsync();

        var tracking = new TrackingService(db, null!);
        var activity = new ActivityService(new Factory(options), NullLogger<ActivityService>.Instance);
        var undo = new UndoService(db, tracking, null!, null!, null!, activity);

        // A watch (its play), a re-rating from 9 to 4, and a drop from Watching: each logged with how to reverse it.
        await activity.LogAsync(me, ActivityKind.Watch, MediaType.Movie, 949, undo: new UndoData(PlayIds: [play.Id]));
        (await db.Ratings.SingleAsync()).Value = 4;
        await db.SaveChangesAsync();
        await activity.LogAsync(me, ActivityKind.Rate, MediaType.Movie, 949, detail: "4/10", undo: new UndoData(Rating: 9, Had: true));
        await activity.LogAsync(me, ActivityKind.ShowStatus, MediaType.Show, 1396, detail: "Dropped", undo: new UndoData(Status: ShowTrackStatus.Watching));
        await activity.LogAsync(me, ActivityKind.ListDelete, detail: "gone");   // nothing to undo with

        var recent = await undo.RecentAsync(me, default);
        Assert.Equal(4, recent.Count);
        Assert.Equal("Heat", recent.Single(r => r.Kind == ActivityKind.Watch).Title);
        Assert.False(recent.Single(r => r.Kind == ActivityKind.ListDelete).CanUndo);

        await undo.UndoAsync(me, recent.Single(r => r.Kind == ActivityKind.Watch).Id, default);
        await undo.UndoAsync(me, recent.Single(r => r.Kind == ActivityKind.Rate).Id, default);
        await undo.UndoAsync(me, recent.Single(r => r.Kind == ActivityKind.ShowStatus).Id, default);
        db.ChangeTracker.Clear();
        Assert.Empty(db.WatchEvents);                                  // the play's gone
        Assert.Equal(9, (await db.Ratings.SingleAsync()).Value);       // back to 9
        Assert.Empty(db.ShowStates);                                   // watching again (no row)

        // Done once; the undo itself is in the list, and can't be undone a second time.
        var after = await undo.RecentAsync(me, default);
        Assert.Equal(3, after.Count(r => r.Kind == ActivityKind.Undo));
        Assert.False(after.Single(r => r.Kind == ActivityKind.Rate).CanUndo);
        await Assert.ThrowsAsync<TrackingException>(() => undo.UndoAsync(me, recent.Single(r => r.Kind == ActivityKind.Rate).Id, default));
        await Assert.ThrowsAsync<TrackingException>(() => undo.UndoAsync(me, recent.Single(r => r.Kind == ActivityKind.ListDelete).Id, default));
    }
}
