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
        Assert.Equal("https://www.metacritic.com/movie/the-matrix", scores[1].Url);
        Assert.Equal(5, scores[2].Max);
        Assert.Null(scores[2].Votes);
        Assert.Equal("https://letterboxd.com/tmdb/603/", scores[2].Url);
    }
}
