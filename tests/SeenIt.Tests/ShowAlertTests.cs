using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class ShowAlertTests
{
    private const int Watching = 1, Paused = 2, Dropped = 3, OnUnstarted = 4, TurnedOff = 5, DroppedButOn = 6;

    [Fact]
    public async Task Paused_and_dropped_shows_dont_alert_unless_turned_on_and_off_keeps_a_show_out()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var me = Guid.NewGuid();
        db.Users.Add(new AppUser { Id = me, UserName = "sam", TimeZone = "UTC" });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var id in new[] { Watching, Paused, Dropped, OnUnstarted, TurnedOff, DroppedButOn })
        {
            db.Shows.Add(new ShowMeta { TmdbId = id, Name = $"Show {id}" });
            db.Episodes.Add(new EpisodeMeta { ShowTmdbId = id, SeasonNumber = 1, EpisodeNumber = 1, AirDate = today.AddDays(-7) });
            db.Episodes.Add(new EpisodeMeta { ShowTmdbId = id, SeasonNumber = 1, EpisodeNumber = 2, AirDate = today, Name = "Out today" });
            if (id != OnUnstarted)
                db.WatchEvents.Add(new WatchEvent { UserId = me, Type = MediaType.Episode, TmdbId = id, SeasonNumber = 1, EpisodeNumber = 1, WatchedAt = DateTimeOffset.UtcNow.AddDays(-6) });
        }
        db.ShowStates.Add(new ShowState { UserId = me, ShowTmdbId = Paused, Status = ShowTrackStatus.Paused });
        db.ShowStates.Add(new ShowState { UserId = me, ShowTmdbId = Dropped, Status = ShowTrackStatus.Dropped });
        db.ShowStates.Add(new ShowState { UserId = me, ShowTmdbId = DroppedButOn, Status = ShowTrackStatus.Dropped });
        db.ShowAlerts.Add(new ShowAlert { UserId = me, ShowTmdbId = OnUnstarted, On = true });
        db.ShowAlerts.Add(new ShowAlert { UserId = me, ShowTmdbId = DroppedButOn, On = true });
        db.ShowAlerts.Add(new ShowAlert { UserId = me, ShowTmdbId = TurnedOff, On = false });
        await db.SaveChangesAsync();

        var tracking = new TrackingService(db, null!);
        var calendar = (await tracking.GetCalendarAsync(me, today, 1, default)).Select(e => e.TmdbId).Order();
        Assert.Equal([Watching, Paused, OnUnstarted, DroppedButOn], calendar);   // paused stays on the calendar

        var alerts = new ReleaseAlertService(db, tracking, null!, new ConfigurationBuilder().Build(), NullLogger<ReleaseAlertService>.Instance);
        await alerts.ScanAsync(await db.Users.SingleAsync(), default);
        Assert.Equal([Watching, OnUnstarted, DroppedButOn], db.Notifications.Select(n => n.TmdbId).OrderBy(x => x).ToList());

        Assert.Equal(new ShowAlertState(ShowAlerts.Auto, true), await tracking.ShowAlertsAsync(me, Watching, default));
        Assert.Equal(new ShowAlertState(ShowAlerts.Auto, false), await tracking.ShowAlertsAsync(me, Paused, default));
        Assert.Equal(new ShowAlertState(ShowAlerts.On, true), await tracking.ShowAlertsAsync(me, DroppedButOn, default));
        Assert.Equal(new ShowAlertState(ShowAlerts.Off, false), await tracking.ShowAlertsAsync(me, TurnedOff, default));
    }
}
