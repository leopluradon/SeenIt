using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SeenIt.Contracts;
using SeenIt.Server.Import;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class TraktParserTests
{
    private const string History = """
        [
          {"id":1,"watched_at":"2024-03-01T20:15:00.000Z","action":"watch","type":"movie",
           "movie":{"title":"Dune","year":2021,"ids":{"trakt":1,"slug":"dune-2021","imdb":"tt1160419","tmdb":438631}}},
          {"id":2,"watched_at":"2024-03-02T21:00:00.000Z","action":"scrobble","type":"episode",
           "episode":{"season":1,"number":3,"title":"x","ids":{"tmdb":999}},
           "show":{"title":"Severance","year":2022,"ids":{"tmdb":95396,"imdb":"tt11280740"}}}
        ]
        """;

    [Fact]
    public void Reads_history_ratings_watchlist_and_lists_by_shape()
    {
        var files = new Dictionary<string, string>
        {
            ["watched-history.json"] = History,
            ["ratings-movies.json"] = """[{"rated_at":"2024-01-01T00:00:00Z","rating":9,"type":"movie","movie":{"title":"Dune","year":2021,"ids":{"tmdb":438631}}}]""",
            ["ratings-episodes.json"] = """[{"rated_at":"2024-01-01T00:00:00Z","rating":8,"type":"episode","episode":{"season":1,"number":3},"show":{"title":"Severance","ids":{"tmdb":95396}}}]""",
            ["lists-watchlist.json"] = """[{"rank":1,"listed_at":"2024-02-01T00:00:00Z","type":"show","show":{"title":"Andor","year":2022,"ids":{"tmdb":83867}}}]""",
            ["lists-lists.json"] = """[{"name":"Sci-fi favorites","description":"best","ids":{"trakt":5,"slug":"sci-fi-favorites"}}]""",
            ["lists-sci-fi-favorites.json"] = """[{"rank":1,"listed_at":"2024-02-01T00:00:00Z","type":"movie","movie":{"title":"Dune","year":2021,"ids":{"tmdb":438631}}}]""",
            ["user-profile.json"] = """{"username":"x"}""",
        };

        var p = TraktParser.Parse(files);

        Assert.Equal(2, p.History.Count);
        Assert.Equal(new ImportRef(MediaType.Movie, 438631, "tt1160419", "Dune", 2021), p.History[0].Item);
        Assert.Equal((1, 3), (p.History[1].Season!.Value, p.History[1].Episode!.Value));
        Assert.Equal(95396, p.History[1].Item.TmdbId);
        Assert.Equal(MediaType.Show, p.History[1].Item.Type);

        Assert.Equal(2, p.Ratings.Count);
        Assert.Contains(p.Ratings, r => r.Season == 1 && r.Episode == 3 && r.Rating == 8);

        Assert.Single(p.Watchlist);
        Assert.Equal(83867, p.Watchlist[0].Item.TmdbId);

        var list = Assert.Single(p.Lists);
        Assert.Equal("Sci-fi favorites", list.Name);
        Assert.Equal("best", list.Description);
    }

    [Fact]
    public void Uses_watched_summaries_only_when_there_is_no_history()
    {
        var summaries = """
            [{"plays":2,"last_watched_at":"2024-01-01T00:00:00Z","movie":{"title":"Heat","year":1995,"ids":{"tmdb":949}}},
             {"plays":1,"last_watched_at":"2024-01-01T00:00:00Z","show":{"title":"Fargo","ids":{"tmdb":60622}},
              "seasons":[{"number":1,"episodes":[{"number":1,"plays":1,"last_watched_at":"2023-06-01T00:00:00Z"},{"number":2,"plays":1}]}]}]
            """;

        var onlySummaries = TraktParser.Parse(new Dictionary<string, string> { ["watched.json"] = summaries });
        Assert.Equal(4, onlySummaries.History.Count); // 2 plays of Heat + 2 episodes

        var both = TraktParser.Parse(new Dictionary<string, string> { ["watched.json"] = summaries, ["history.json"] = History });
        Assert.Equal(2, both.History.Count);
    }

    [Fact]
    public void Ignores_garbage_files()
    {
        var p = TraktParser.Parse(new Dictionary<string, string> { ["a.json"] = "not json", ["b.json"] = "[]", ["c.json"] = "{}" });
        Assert.Equal(0, p.Total);
    }
}

public class LetterboxdParserTests
{
    [Fact]
    public void Reads_diary_watched_ratings_watchlist_and_lists()
    {
        var files = new Dictionary<string, string>
        {
            ["diary.csv"] = "Date,Name,Year,Letterboxd URI,Rating,Rewatch,Tags,Watched Date\n" +
                            "2024-01-05,\"Crouching Tiger, Hidden Dragon\",2000,https://boxd.it/a,4.5,,,2024-01-04\n" +
                            "2024-02-01,Heat,1995,https://boxd.it/b,5,Yes,,2024-01-31\n",
            ["watched.csv"] = "Date,Name,Year,Letterboxd URI\n" +
                              "2024-01-05,\"Crouching Tiger, Hidden Dragon\",2000,https://boxd.it/c\n" +
                              "2023-05-05,Alien,1979,https://boxd.it/d\n",
            ["ratings.csv"] = "Date,Name,Year,Letterboxd URI,Rating\n2024-01-05,Heat,1995,https://boxd.it/e,4.5\n2024-01-05,Alien,1979,https://boxd.it/f,0.5\n",
            ["watchlist.csv"] = "Date,Name,Year,Letterboxd URI\n2024-03-01,Past Lives,2023,https://boxd.it/g\n",
            ["lists/heists.csv"] = "Letterboxd list export v7\nDate,Name,Tags,URL,Description\n2024-01-01,Heist Films,,https://boxd.it/h,\"Great, tense\"\n\n" +
                                   "Position,Name,Year,URL,Description\n1,Heat,1995,https://boxd.it/i,\n2,Rififi,1955,https://boxd.it/j,\n",
            ["deleted/diary.csv"] = "Date,Name,Year,Letterboxd URI,Rating,Rewatch,Tags,Watched Date\n2024-01-05,Ghost,1990,x,,,,2024-01-04\n",
        };

        var p = LetterboxdParser.Parse(files);

        // Two diary entries plus Alien from watched.csv; Crouching Tiger isn't double-counted.
        Assert.Equal(3, p.History.Count);
        var tiger = p.History.Single(h => h.Item.Title == "Crouching Tiger, Hidden Dragon");
        Assert.Equal(new DateTimeOffset(2024, 1, 4, 12, 0, 0, TimeSpan.Zero), tiger.WatchedAt);
        Assert.DoesNotContain(p.History, h => h.Item.Title == "Ghost");

        Assert.Equal([9, 1], p.Ratings.Select(r => r.Rating));
        Assert.Equal("Past Lives", Assert.Single(p.Watchlist).Item.Title);

        var list = Assert.Single(p.Lists);
        Assert.Equal("Heist Films", list.Name);
        Assert.Equal("Great, tense", list.Description);
        Assert.Equal(["Heat", "Rififi"], list.Items.Select(i => i.Item.Title));
    }

    [Fact]
    public void Single_csv_is_recognised_by_its_header()
    {
        var p = LetterboxdParser.Parse(new Dictionary<string, string>
        {
            ["export.csv"] = "Date,Name,Year,Letterboxd URI,Rating\n2024-01-05,Heat,1995,x,3\n",
        });
        Assert.Equal(6, Assert.Single(p.Ratings).Rating);
    }

    [Fact]
    public void Reads_files_from_a_zip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("letterboxd-user/watched.csv").Open(), new UTF8Encoding(true));
            w.Write("Date,Name,Year,Letterboxd URI\n2023-05-05,Alien,1979,x\n");
        }
        var files = ImportFiles.Read("export.zip", ms.ToArray());
        var p = LetterboxdParser.Parse(files);
        Assert.Equal("Alien", Assert.Single(p.History).Item.Title);
    }

    [Fact]
    public void Csv_handles_quotes_and_newlines()
    {
        var rows = Csv.Parse("a,b\n\"x, \"\"y\"\"\",\"multi\nline\"\n").ToList();
        Assert.Equal(["x, \"y\"", "multi\nline"], rows[1]);
    }
}

public class JellyfinPayloadTests
{
    private static JellyfinPayload P(string json) => JellyfinPayload.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Parses_templated_strings_and_raw_numbers()
    {
        var p = P("""
            {"NotificationType":"PlaybackStop","ItemType":"Episode","SeriesName":"Severance",
             "SeasonNumber":"1","EpisodeNumber":3,"Provider_tvdb":"8000000","Provider_tmdb":"",
             "PlaybackPositionTicks":"27000000000","RunTimeTicks":30000000000,"PlayedToCompletion":"False"}
            """);
        Assert.Equal(1, p.SeasonNumber);
        Assert.Equal(3, p.EpisodeNumber);
        Assert.Null(p.Tmdb); // blank template variable
        Assert.Equal("8000000", p.Tvdb);
        Assert.False(p.PlayedToCompletion);
        Assert.Equal("Severance S01E03", p.DisplayName);
    }

    [Theory]
    [InlineData("PlaybackStop", "Movie", "true", 0, 100, 90, true)]
    [InlineData("PlaybackStop", "Movie", "false", 95, 100, 90, true)]
    [InlineData("PlaybackStop", "Movie", "false", 50, 100, 90, false)]
    [InlineData("PlaybackStop", "Movie", "false", 85, 100, 80, true)]
    [InlineData("PlaybackStart", "Movie", "true", 100, 100, 90, false)]
    [InlineData("PlaybackStop", "Audio", "true", 100, 100, 90, false)]
    public void Decides_when_something_counts_as_watched(string type, string item, string completed, long pos, long run, int threshold, bool expected)
    {
        var p = P($$"""{"NotificationType":"{{type}}","ItemType":"{{item}}","PlayedToCompletion":"{{completed}}","PlaybackPositionTicks":{{pos}},"RunTimeTicks":{{run}}}""");
        Assert.Equal(expected, p.Decide(threshold).Scrobble);
    }

    [Fact]
    public void Mark_played_counts_but_mark_unplayed_does_not()
    {
        Assert.True(P("""{"NotificationType":"UserDataSaved","ItemType":"Movie","SaveReason":"TogglePlayed","Played":"True"}""").Decide(90).Scrobble);
        Assert.False(P("""{"NotificationType":"UserDataSaved","ItemType":"Movie","SaveReason":"TogglePlayed","Played":"False"}""").Decide(90).Scrobble);
        Assert.False(P("""{"NotificationType":"UserDataSaved","ItemType":"Movie","SaveReason":"PlaybackProgress","Played":"True"}""").Decide(90).Scrobble);
    }
}
