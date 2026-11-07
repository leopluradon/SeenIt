using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class PrefsTests
{
    [Fact]
    public void Scores_can_be_hidden_per_source_and_kind()
    {
        var p = new UserPrefs(Scores: ["imdb", "letterboxd"], HiddenScores: ["imdb/episode", "tmdb/season", "friends/movie", "bogus/movie", "imdb/nope"]).Clean();
        Assert.Equal(["imdb/episode", "tmdb/season", "friends/movie"], p.HiddenScores);
        Assert.True(p.ShowsScore("imdb", "movie"));
        Assert.False(p.ShowsScore("imdb", "episode"));
        Assert.False(p.ShowsScore("tomatoes", "movie"));     // left out of Scores: off everywhere
        Assert.True(p.ShowsScore("tmdb", "movie"));          // TMDB and friends aren't in Scores
        Assert.False(p.ShowsScore("tmdb", "season"));
        Assert.False(p.ShowsScore("friends", "movie"));
        Assert.True(p.ShowsScore("friends", "episode"));
        Assert.True(UserPrefs.Default.ShowsScore("metacritic", "show"));
        Assert.Equal("season", UserPrefs.Kind(MediaType.Show, season: true));
    }

    [Fact]
    public void Parts_follow_the_profile_unless_given_their_own_audience()
    {
        var owner = new AppUser { Id = Guid.NewGuid(), Visibility = ProfileVisibility.Friends };
        owner.PrefsJson = UserPrefsStore.Write(new UserPrefs(PartVisibility: new Dictionary<ProfilePart, ProfileVisibility>
        {
            [ProfilePart.Ratings] = ProfileVisibility.Public, [ProfilePart.History] = ProfileVisibility.Private,
        }));
        var stranger = Guid.NewGuid();
        Assert.True(SocialService.CanSee(owner, stranger, mutual: false, ProfilePart.Ratings));     // its own: everyone
        Assert.False(SocialService.CanSee(owner, stranger, mutual: true, ProfilePart.History));     // its own: only me, even for friends
        Assert.False(SocialService.CanSee(owner, stranger, mutual: false, ProfilePart.Stats));      // same as profile: friends only
        Assert.True(SocialService.CanSee(owner, stranger, mutual: true, ProfilePart.Stats));
        Assert.True(SocialService.CanSee(owner, owner.Id, mutual: false, ProfilePart.History));     // you always see your own
    }

    [Fact]
    public void Prefs_are_cleaned_to_allowed_values()
    {
        var p = new UserPrefs(StartPage: "admin", ComingUpDays: 99, Scores: ["imdb", "nope", "imdb"],
            HomeCounts: new Dictionary<string, int> { ["upnext"] = 500, ["bogus"] = 5 }).Clean();
        Assert.Equal("home", p.StartPage);
        Assert.Equal(14, p.ComingUpDays);
        Assert.Equal(["imdb"], p.Scores);
        Assert.Equal(40, p.Count("upnext"));
        Assert.Equal(UserPrefs.DefaultRowCount, p.Count("bogus"));
    }

    [Fact]
    public async Task Stats_can_count_first_watches_only()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var user = new AppUser { Id = Guid.NewGuid(), UserName = "u", PrefsJson = UserPrefsStore.Write(new UserPrefs(CountRewatches: false)) };
        db.Users.Add(user);
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat", Runtime = 170 });
        for (var i = 0; i < 3; i++)
            db.WatchEvents.Add(new WatchEvent { UserId = user.Id, Type = MediaType.Movie, TmdbId = 1, WatchedAt = DateTimeOffset.UtcNow.AddDays(-i) });
        await db.SaveChangesAsync();
        var stats = await new TrackingService(db, null!).GetStatsAsync(user.Id, null, default);
        Assert.Equal((1, 170L), (stats.MoviePlays, stats.MinutesMovies));
    }
}

public class TraktScoreTests
{
    [Theory]
    [InlineData("""{"rating":8.37554,"votes":5051,"distribution":{}}""", 8.37554, 5051)]
    [InlineData("""{"trakt":{"rating":8.50597,"votes":5014},"tmdb":{"rating":null,"votes":null}}""", 8.50597, 5014)]
    public void Reads_episode_and_season_shapes(string json, double rating, int votes)
    {
        var parsed = SeenIt.Server.Services.TraktScores.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement);
        Assert.Equal((rating, votes), parsed);
    }
}
