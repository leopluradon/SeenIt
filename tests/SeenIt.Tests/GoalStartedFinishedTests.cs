using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class GoalStartedFinishedTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private const int Counts = 1, StartedLastYear = 2, StillRunning = 3, NotFinished = 4, UndatedEarlier = 5;

    [Fact]
    public async Task Only_ended_shows_started_and_finished_in_the_year_count()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Users.Add(new AppUser { Id = Me, UserName = "sam", TimeZone = "UTC" });
        var aired = new DateOnly(2024, 1, 1);
        foreach (var (id, status) in new[] { (Counts, "Ended"), (StartedLastYear, "Ended"), (StillRunning, "Returning Series"), (NotFinished, "Canceled"), (UndatedEarlier, "Ended") })
        {
            db.Shows.Add(new ShowMeta { TmdbId = id, Name = $"Show {id}", Status = status, EpisodeRuntime = 30 });
            for (var e = 1; e <= 2; e++) db.Episodes.Add(new EpisodeMeta { ShowTmdbId = id, SeasonNumber = 1, EpisodeNumber = e, AirDate = aired });
        }
        void Play(int show, int ep, DateTimeOffset at) =>
            db.WatchEvents.Add(new WatchEvent { UserId = Me, Type = MediaType.Episode, TmdbId = show, SeasonNumber = 1, EpisodeNumber = ep, WatchedAt = at });
        var march = new DateTimeOffset(2026, 3, 1, 20, 0, 0, TimeSpan.Zero);
        Play(Counts, 1, march); Play(Counts, 2, march.AddDays(3));                                  // both in 2026, ended: counts
        Play(StartedLastYear, 1, march.AddYears(-1)); Play(StartedLastYear, 2, march);              // started in 2025
        Play(StillRunning, 1, march); Play(StillRunning, 2, march.AddDays(1));                      // caught up, not ended
        Play(NotFinished, 1, march);                                                                // one of two
        Play(UndatedEarlier, 1, WatchDates.Unknown); Play(UndatedEarlier, 1, march); Play(UndatedEarlier, 2, march.AddDays(1));   // seen at some point before
        await db.SaveChangesAsync();

        var tracking = new TrackingService(db, null!);
        var goals = new GoalService(db, tracking, new ReviewService(db, tracking));
        var progress = await goals.ProgressAsync(Me, 2026, TimeZoneInfo.Utc, new HashSet<GoalKind> { GoalKind.ShowsStartedAndFinished, GoalKind.ShowsFinished }, default);

        Assert.Equal(1, progress[GoalKind.ShowsStartedAndFinished]);
        Assert.Equal(4, progress[GoalKind.ShowsFinished]);   // the plain goal counts last year's start, being caught up and the undated one too
    }
}
