using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class SmartListTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static async Task<SmartListService> Setup()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Alien", ReleaseDate = new DateOnly(1979, 5, 25), Genres = ["Horror", "Science Fiction"], VoteAverage = 8.2, OriginalLanguage = "en" });
        db.Movies.Add(new MovieMeta { TmdbId = 2, Title = "Aliens", ReleaseDate = new DateOnly(1986, 7, 18), Genres = ["Action", "Science Fiction"], VoteAverage = 7.9, OriginalLanguage = "en" });
        db.Movies.Add(new MovieMeta { TmdbId = 3, Title = "Paddington", ReleaseDate = new DateOnly(2014, 11, 28), Genres = ["Comedy", "Family"], VoteAverage = 7.2, OriginalLanguage = "en" });
        db.Shows.Add(new ShowMeta { TmdbId = 10, Name = "Severance", FirstAirDate = new DateOnly(2022, 2, 18), Genres = ["Sci-Fi & Fantasy", "Drama"], VoteAverage = 8.4, OriginalLanguage = "en" });
        foreach (var id in new[] { 1, 2, 3 })
            db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Movie, TmdbId = id, WatchedAt = DateTimeOffset.UtcNow.AddDays(-id) });
        db.WatchEvents.Add(new WatchEvent { UserId = User, Type = MediaType.Episode, TmdbId = 10, SeasonNumber = 1, EpisodeNumber = 1, WatchedAt = DateTimeOffset.UtcNow });
        db.Ratings.Add(new Rating { UserId = User, Type = MediaType.Movie, TmdbId = 1, Value = 9 });
        db.Ratings.Add(new Rating { UserId = User, Type = MediaType.Movie, TmdbId = 2, Value = 7.5 });
        db.Watchlist.Add(new WatchlistItem { UserId = User, Type = MediaType.Movie, TmdbId = 3 });
        await db.SaveChangesAsync();
        return new SmartListService(db, null!, new TrackingService(db, null!));
    }

    [Fact]
    public async Task Genres_match_across_movie_and_show_names()
    {
        var s = await Setup();
        var found = await s.EvaluateAsync(User, new SmartRules(SmartSource.History, Genres: ["Science Fiction"], Sort: SmartSort.Title), default);
        Assert.Equal(["Alien", "Aliens", "Severance"], found.Select(m => m.Title));   // Severance is "Sci-Fi & Fantasy"
    }

    [Fact]
    public async Task Years_type_and_my_rating_narrow_it()
    {
        var s = await Setup();
        var eighties = await s.EvaluateAsync(User, new SmartRules(SmartSource.History, MediaType.Movie, YearFrom: 1980, YearTo: 1989), default);
        Assert.Equal(["Aliens"], eighties.Select(m => m.Title));
        var loved = await s.EvaluateAsync(User, new SmartRules(SmartSource.History, MinMyRating: 8), default);
        Assert.Equal(["Alien"], loved.Select(m => m.Title));
    }

    [Fact]
    public async Task Watchlist_can_be_limited_to_unseen()
    {
        var s = await Setup();
        Assert.Single(await s.EvaluateAsync(User, new SmartRules(SmartSource.Watchlist), default));
        Assert.Empty(await s.EvaluateAsync(User, new SmartRules(SmartSource.Watchlist, Watched: false), default));   // Paddington was watched
    }
}
