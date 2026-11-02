using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class FixDataTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTimeOffset Day = new(2025, 3, 1, 20, 0, 0, TimeSpan.Zero);

    private static AppDbContext Db() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static void Play(AppDbContext db, int movie, DateTimeOffset at, WatchSource source = WatchSource.Manual) =>
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Movie, TmdbId = movie, WatchedAt = at, Source = source });

    [Fact]
    public async Task Duplicates_are_plays_within_a_day_and_rewatches_are_not()
    {
        await using var db = Db();
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat" });
        Play(db, 1, Day);
        Play(db, 1, Day.AddHours(5), WatchSource.TraktImport);   // same viewing, imported
        Play(db, 1, Day.AddDays(40));                            // a real rewatch
        Play(db, 2, Day);                                        // different movie
        Play(db, 1, WatchDates.Unknown);                         // unknown date: never a duplicate
        await db.SaveChangesAsync();

        var groups = await new TrackingService(db, null!).DuplicatesAsync(User, default);
        var g = Assert.Single(groups);
        Assert.Equal("Heat", g.Title);
        Assert.Equal([Day, Day.AddHours(5)], g.Plays.Select(p => p.WatchedAt));
    }

    [Fact]
    public async Task A_chain_of_close_plays_is_one_group()
    {
        await using var db = Db();
        Play(db, 1, Day);
        Play(db, 1, Day.AddHours(20));
        Play(db, 1, Day.AddHours(40));   // 20h after the previous one
        await db.SaveChangesAsync();
        Assert.Equal(3, Assert.Single(await new TrackingService(db, null!).DuplicatesAsync(User, default)).Plays.Count);
    }

    [Fact]
    public void Backup_health_reads_the_status_files()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var status = new BackupStatus(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SeenIt:StatusDir"] = dir }).Build());
            Assert.False(Assert.Single(status.Health()).Ok);   // nothing reported yet

            File.WriteAllText(Path.Combine(dir, "backup-ok"), "seenit-x.dump (2M)");
            Assert.True(Assert.Single(status.Health()).Ok);

            File.WriteAllText(Path.Combine(dir, "backup-failed"), "pg_dump failed");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "backup-failed"), DateTime.UtcNow.AddMinutes(1));
            var failed = Assert.Single(status.Health());
            Assert.False(failed.Ok);
            Assert.StartsWith("FAILED", failed.Value);

            File.WriteAllText(Path.Combine(dir, "offsite-enabled"), "app_user@host:");
            Assert.Equal(2, status.Health().Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
