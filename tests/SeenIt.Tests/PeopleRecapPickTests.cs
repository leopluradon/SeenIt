using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;
using SeenIt.Server.Tmdb;

namespace SeenIt.Tests;

public class PeopleRecapPickTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static AppDbContext Db()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Shows.Add(new ShowMeta { TmdbId = 1, Name = "Show", Status = "Returning Series" });
        // Season 1: four aired episodes. Season 2 premieres in 40 days.
        foreach (var (s, e, days) in new[] { (1, 1, -400), (1, 2, -393), (1, 3, -386), (1, 4, -379), (2, 1, 40), (2, 2, 47) })
            db.Episodes.Add(new EpisodeMeta { ShowTmdbId = 1, SeasonNumber = s, EpisodeNumber = e, Name = $"S{s}E{e}", Overview = $"Plot {s}.{e}", AirDate = Today.AddDays(days), Runtime = 50 });
        db.SaveChanges();
        return db;
    }

    private static void Watch(AppDbContext db, int s, int e, int daysAgo) =>
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Episode, TmdbId = 1, SeasonNumber = s, EpisodeNumber = e, WatchedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo) });

    [Fact]
    public async Task Recap_shows_the_last_two_episodes_before_next_after_a_long_break()
    {
        using var db = Db();
        Watch(db, 1, 1, 90);
        Watch(db, 1, 2, 89);
        Watch(db, 1, 3, 88);
        await db.SaveChangesAsync();
        var recap = await new TrackingService(db, null!).RecapAsync(User, 1, default);
        Assert.NotNull(recap);
        Assert.Equal([(1, 2), (1, 3)], recap.Episodes.Select(e => (e.SeasonNumber, e.EpisodeNumber)));
        Assert.Equal("Plot 1.3", recap.Episodes[1].Overview);
        Assert.Equal((1, 4), (recap.Next.SeasonNumber, recap.Next.EpisodeNumber));
    }

    [Fact]
    public async Task No_recap_mid_binge_or_when_caught_up()
    {
        using var db = Db();
        Watch(db, 1, 1, 3);
        await db.SaveChangesAsync();
        Assert.Null(await new TrackingService(db, null!).RecapAsync(User, 1, default));   // watched 3 days ago

        Watch(db, 1, 2, 100); Watch(db, 1, 3, 100); Watch(db, 1, 4, 100);
        await db.SaveChangesAsync();
        db.WatchEvents.RemoveRange(db.WatchEvents.Where(w => w.EpisodeNumber == 1));
        Watch(db, 1, 1, 100);
        await db.SaveChangesAsync();
        Assert.Null(await new TrackingService(db, null!).RecapAsync(User, 1, default));   // nothing aired left to watch
    }

    [Fact]
    public async Task Caught_up_shows_say_when_they_come_back()
    {
        using var db = Db();
        for (var e = 1; e <= 4; e++) Watch(db, 1, e, 300);
        await db.SaveChangesAsync();
        var p = Assert.Single(await new ProgressCalculator(db).ForUserAsync(User, null, default));
        Assert.Null(p.NextEpisode);
        Assert.Equal("Returning Series", p.ShowStatus);
        Assert.Equal((2, 1, Today.AddDays(40)), (p.Returning!.SeasonNumber, p.Returning.EpisodeNumber, p.Returning.AirDate!.Value));
    }

    [Theory]
    [InlineData(new[] { 50, 50, 50 }, 120, 2, 100)]
    [InlineData(new[] { 22, 22, 22, 22, 22, 22 }, 90, 4, 88)]
    [InlineData(new[] { 70, 30 }, 60, 0, 0)]        // in order: the first doesn't fit, so none do
    [InlineData(new[] { 45, 0, 45 }, 180, 1, 45)]   // an unknown length stops the count
    public void Episodes_fit_in_order(int[] runtimes, int budget, int count, int minutes) =>
        Assert.Equal((count, minutes), PickService.Fit(runtimes, budget));

    [Fact]
    public void Upcoming_work_skips_released_appearances_and_minor_crew_jobs()
    {
        var today = new DateOnly(2026, 11, 8);
        TmdbPersonCredit Credit(int id, string type, string? date, string? character = null, string? job = null, List<int>? genres = null) =>
            new(id, type, type == "movie" ? $"Movie {id}" : null, type == "tv" ? $"Show {id}" : null,
                type == "movie" ? date : null, type == "tv" ? date : null, character, job, null, null, "/p.jpg", 10, $"c{id}", genres);
        var person = new TmdbPerson(7, "Someone", null, null, null, null, null, "Directing", new TmdbPersonCredits(
            Cast: [Credit(1, "movie", "2027-07-17", "Lead"), Credit(2, "movie", "2020-01-01", "Lead"), Credit(3, "tv", "2027-01-01", "Herself"),
                   Credit(4, "tv", "2027-02-01", "Guest", genres: [10767]), Credit(5, "movie", null, "Cameo"), Credit(9, "movie", null, genres: [99])],
            Crew: [Credit(1, "movie", "2027-07-17", job: "Director"), Credit(6, "movie", "2027-03-01", job: "Executive Producer"), Credit(8, "tv", "2027-05-05", job: "Creator")]), null);

        var up = PersonService.Upcoming(person, today).OrderBy(u => u.TmdbId).ToList();
        Assert.Equal([1, 5, 8], up.Select(u => u.TmdbId));
        Assert.Equal("Lead / Director", up[0].Role);
        Assert.Null(up[1].Date);                        // announced, no date yet
        Assert.Equal(MediaType.Show, up[2].Type);
    }
}
