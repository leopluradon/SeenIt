using System.Text.Json;
using SeenIt.Contracts;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class ScoreTests
{
    [Fact]
    public void MdbList_ratings_keep_known_sources_in_order_and_skip_empty_ones()
    {
        using var doc = JsonDocument.Parse("""
            { "title": "The Matrix", "ratings": [
                { "source": "imdb", "value": 8.7, "score": 87, "votes": 2000000 },
                { "source": "metacritic", "value": 73, "score": 73, "votes": 35, "url": "/movie/the-matrix" },
                { "source": "tomatoes", "value": 83, "score": 83, "votes": 160, "url": "/m/matrix" },
                { "source": "letterboxd", "value": 4.2, "score": 84, "votes": null },
                { "source": "trakt", "value": 0, "score": null },
                { "source": "somethingnew", "value": 5 }
            ] }
            """);
        var scores = ScoreService.ParseMdbList(doc.RootElement, MediaType.Movie, 603, "tt0133093");

        Assert.Equal(["tomatoes", "metacritic", "letterboxd"], scores.Select(s => s.Source));
        Assert.Equal("https://www.rottentomatoes.com/m/matrix", scores[0].Url);
        Assert.True(scores[0].Percent);
        Assert.Equal("https://www.metacritic.com/movie/the-matrix", scores[1].Url);   // already has its section
        using var bare = JsonDocument.Parse("""{ "ratings": [ { "source": "metacritic", "value": 84, "url": "/seinfeld" }, { "source": "metacriticuser", "value": 8.9 } ] }""");
        var show = ScoreService.ParseMdbList(bare.RootElement, MediaType.Show, 1400, null);
        Assert.Equal("https://www.metacritic.com/tv/seinfeld/", show[0].Url);
        Assert.Equal(show[0].Url, show[1].Url);
        Assert.Equal(5, scores[2].Max);
        Assert.Null(scores[2].Votes);
        Assert.Equal("https://letterboxd.com/tmdb/603/", scores[2].Url);
    }
}

public class SeasonRatingImportTests
{
    [Fact]
    public void Trakt_season_ratings_keep_their_season_and_no_episode()
    {
        var json = """[{"rated_at":"2023-10-17T23:27:41.000Z","rating":10,"type":"season","season":{"ids":{"tmdb":298958},"number":4},"show":{"ids":{"tmdb":76331,"imdb":"tt7660850"},"year":2018,"title":"Succession"}}]""";
        var parsed = SeenIt.Server.Import.TraktParser.Parse(new Dictionary<string, string> { ["ratings-seasons.json"] = json });
        var r = Assert.Single(parsed.Ratings);
        Assert.Equal((4, (int?)null, 10), (r.Season!.Value, r.Episode, r.Rating));
        Assert.Equal(76331, r.Item.TmdbId);
    }
}

public class DecimalRatingTests
{
    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(8.1, 8)]
    [InlineData(8.9, 8)]
    [InlineData(9.0, 9)]
    [InlineData(10.0, 10)]
    public void Decimal_ratings_count_in_their_whole_number_bucket(double rating, int bucket) =>
        Assert.Equal(bucket, SeenIt.Server.Services.TrackingService.Bucket(rating));
}
