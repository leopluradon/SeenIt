using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class HistoryFilterTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static async Task<TrackingService> Setup()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat", Runtime = 170, Genres = ["Crime", "Drama"] });
        db.Movies.Add(new MovieMeta { TmdbId = 2, Title = "Paddington 2", Runtime = 104, Genres = ["Comedy", "Family"] });
        db.Shows.Add(new ShowMeta { TmdbId = 3, Name = "Severance", Genres = ["Drama", "Mystery"] });
        db.Episodes.Add(new EpisodeMeta { ShowTmdbId = 3, SeasonNumber = 1, EpisodeNumber = 1, Name = "Good News About Hell", Runtime = 57 });
        db.Episodes.Add(new EpisodeMeta { ShowTmdbId = 3, SeasonNumber = 1, EpisodeNumber = 2, Name = "Half Loop", Runtime = 55 });
        void W(MediaType t, int id, string at, WatchSource src = WatchSource.Manual, int? s = null, int? e = null) =>
            db.WatchEvents.Add(new WatchEvent { UserId = User, Type = t, TmdbId = id, SeasonNumber = s, EpisodeNumber = e, WatchedAt = DateTimeOffset.Parse(at), Source = src });
        W(MediaType.Movie, 1, "2025-12-31T20:00:00Z", WatchSource.TraktImport);
        W(MediaType.Movie, 2, "2026-03-01T20:00:00Z");
        W(MediaType.Episode, 3, "2026-03-02T20:00:00Z", WatchSource.Jellyfin, 1, 1);
        W(MediaType.Episode, 3, "2026-03-03T20:00:00Z", WatchSource.Jellyfin, 1, 2);
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Movie, TmdbId = 1, WatchedAt = WatchDates.Unknown });
        await db.SaveChangesAsync();
        return new TrackingService(db, null!);
    }

    [Theory]
    [InlineData("heat", 2)]           // title, either case, both plays
    [InlineData("SEVER", 2)]          // show name
    [InlineData("half loop", 1)]      // episode title only matches that episode
    [InlineData("nothing like it", 0)]
    public async Task Search_matches_titles_shows_and_episode_names(string q, int expected)
    {
        var t = await Setup();
        Assert.Equal(expected, (await t.GetHistoryAsync(User, new HistoryQuery(Q: q), default)).TotalResults);
    }

    [Fact]
    public async Task Filters_combine_and_dates_exclude_unknown()
    {
        var t = await Setup();
        Assert.Equal(4, (await t.GetHistoryAsync(User, new HistoryQuery(Genre: "Drama"), default)).TotalResults);       // Heat ×2 + both Severance episodes
        Assert.Equal(2, (await t.GetHistoryAsync(User, new HistoryQuery(Source: WatchSource.Jellyfin), default)).TotalResults);
        Assert.Equal(1, (await t.GetHistoryAsync(User, new HistoryQuery(Type: MediaType.Movie, From: DateTimeOffset.Parse("2026-01-01T00:00:00Z")), default)).TotalResults);

        var march = await t.GetHistoryAsync(User, new HistoryQuery(From: DateTimeOffset.Parse("2026-03-01T00:00:00Z"), To: DateTimeOffset.Parse("2026-04-01T00:00:00Z"), Oldest: true), default);
        Assert.Equal(3, march.TotalResults);
        Assert.Equal("Paddington 2", march.Items[0].Title);   // oldest first
        Assert.Equal(104 + 57 + 55, march.TotalMinutes);

        // An end bound alone must not sweep in plays with an unknown date.
        Assert.Equal(1, (await t.GetHistoryAsync(User, new HistoryQuery(To: DateTimeOffset.Parse("2026-01-01T00:00:00Z")), default)).TotalResults);
    }
}
