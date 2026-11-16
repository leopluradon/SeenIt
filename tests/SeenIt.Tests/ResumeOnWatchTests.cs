using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class ResumeOnWatchTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly DateTimeOffset DroppedAt = DateTimeOffset.Parse("2026-11-10T19:18:00Z");

    private static async Task<(TrackingService, AppDbContext)> Setup(ShowTrackStatus status)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.ShowStates.Add(new ShowState { UserId = Me, ShowTmdbId = 59659, Status = status, UpdatedAt = DroppedAt });   // Halt and Catch Fire
        await db.SaveChangesAsync();
        return (new TrackingService(db, null!), db);
    }

    [Theory]
    [InlineData(ShowTrackStatus.Dropped)]
    [InlineData(ShowTrackStatus.Paused)]
    public async Task Watching_after_dropping_or_pausing_picks_the_show_back_up(ShowTrackStatus status)
    {
        var (t, db) = await Setup(status);
        await t.ResumeOnWatchAsync(Me, 59659, DroppedAt.AddDays(6), default);
        await db.SaveChangesAsync();
        Assert.Empty(db.ShowStates);
    }

    [Fact]
    public async Task Logging_older_viewings_leaves_it_dropped()
    {
        var (t, db) = await Setup(ShowTrackStatus.Dropped);
        await t.ResumeOnWatchAsync(Me, 59659, DroppedAt.AddDays(-30), default);   // catching up history
        await t.ResumeOnWatchAsync(Me, 59659, WatchDates.Unknown, default);       // "date unknown"
        await t.ResumeOnWatchAsync(Me, 1396, DroppedAt.AddDays(1), default);       // another show
        await db.SaveChangesAsync();
        Assert.Equal(ShowTrackStatus.Dropped, Assert.Single(db.ShowStates).Status);
    }
}
