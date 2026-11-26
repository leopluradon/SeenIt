using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class ChallengeTests
{
    [Fact]
    public async Task Challenges_fill_from_the_years_plays()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var me = Guid.NewGuid();
        db.Users.Add(new AppUser { Id = me, UserName = "sam", TimeZone = "UTC" });
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Metropolis", ReleaseDate = new(1927, 1, 10), Countries = ["DE"], OriginalLanguage = "de", Runtime = 153, Genres = ["Drama"] });
        db.Movies.Add(new MovieMeta { TmdbId = 2, Title = "Seven Samurai", ReleaseDate = new(1954, 4, 26), Countries = ["JP"], OriginalLanguage = "ja", Runtime = 207, Genres = ["Action", "Drama"] });
        db.Movies.Add(new MovieMeta { TmdbId = 3, Title = "New One", ReleaseDate = new(2026, 2, 1), Countries = ["US"], OriginalLanguage = "en", Runtime = 100, Genres = ["Documentary"] });
        db.Movies.Add(new MovieMeta { TmdbId = 4, Title = "Last Year's", ReleaseDate = new(1999, 1, 1), Countries = ["FR"], OriginalLanguage = "fr" });
        db.Shows.Add(new ShowMeta { TmdbId = 50, Name = "The Simpsons", Countries = ["US"] });
        for (var s = 1; s <= 10; s++) db.Episodes.Add(new EpisodeMeta { ShowTmdbId = 50, SeasonNumber = s, EpisodeNumber = 1 });
        var at = new DateTimeOffset(2026, 3, 1, 20, 0, 0, TimeSpan.Zero);
        foreach (var id in new[] { 1, 2, 3 }) db.WatchEvents.Add(new WatchEvent { UserId = me, Type = MediaType.Movie, TmdbId = id, WatchedAt = at.AddDays(id) });
        db.WatchEvents.Add(new WatchEvent { UserId = me, Type = MediaType.Movie, TmdbId = 4, WatchedAt = at.AddYears(-1) });   // 2025: doesn't count
        db.WatchEvents.Add(new WatchEvent { UserId = me, Type = MediaType.Episode, TmdbId = 50, SeasonNumber = 3, EpisodeNumber = 1, WatchedAt = at });
        await db.SaveChangesAsync();

        var tracking = new TrackingService(db, null!);
        var all = (await new ChallengeService(db, tracking, new ReviewService(db, tracking)).GetAsync(me, 2026, "UTC", default)).ToDictionary(c => c.Key);

        Assert.Equal(11, all["decades"].Slots.Count);                       // every decade has a slot
        Assert.Equal(["1920s", "1950s", "2020s"], all["decades"].Slots.Where(s => s.Item is not null).Select(s => s.Label));
        Assert.Equal(3, all["countries"].Count);                            // DE, JP, US (not last year's FR)
        Assert.Equal(2, all["languages"].Count);                            // German, Japanese
        Assert.Equal(1, all["silent"].Count);
        Assert.Equal(["Seven Samurai"], all["epics"].Slots.Select(s => s.Item!.Title));
        Assert.Equal(1, all["docs"].Count);
        Assert.Equal(1, all["fresh"].Count);                                // released this year
        Assert.Equal(1, all["longrunner"].Count);                           // a show with 10 seasons
    }
}
