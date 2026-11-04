using System.Text.Json;
using SeenIt.Contracts;
using SeenIt.Server.Import;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class NewSourcesTests
{
    private static Dictionary<string, string> Files(params (string Name, string Text)[] files) =>
        files.ToDictionary(f => f.Name, f => f.Text, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Imdb_ratings_become_ratings_and_movie_plays()
    {
        var csv = """
            Const,Your Rating,Date Rated,Title,Original Title,URL,Title Type,IMDb Rating,Runtime (mins),Year,Genres,Num Votes,Release Date,Directors
            tt0111161,10,2024-03-02,The Shawshank Redemption,The Shawshank Redemption,https://www.imdb.com/title/tt0111161,Movie,9.3,142,1994,Drama,3000000,1994-09-23,Frank Darabont
            tt0903747,9,2023-01-15,Breaking Bad,Breaking Bad,https://www.imdb.com/title/tt0903747,TV Series,9.5,49,2008,Drama,2000000,2008-01-20,
            tt0959621,8,2023-01-16,Pilot,Pilot,https://www.imdb.com/title/tt0959621,TV Episode,8.2,58,2008,Drama,40000,2008-01-20,
            """;
        var parsed = ImdbParser.Parse(Files(("ratings.csv", csv)));
        Assert.Equal(2, parsed.Ratings.Count);   // the episode is skipped
        Assert.Equal(MediaType.Show, parsed.Ratings[1].Item.Type);
        var play = Assert.Single(parsed.History);
        Assert.Equal("tt0111161", play.Item.ImdbId);
        Assert.True(play.Approximate);
        Assert.Equal(new DateOnly(2024, 3, 2), DateOnly.FromDateTime(play.WatchedAt.UtcDateTime));
    }

    [Fact]
    public void Imdb_watchlist_and_lists_go_to_the_right_place()
    {
        const string header = "Position,Const,Created,Modified,Description,Title,URL,Title Type,IMDb Rating,Runtime (mins),Year,Genres,Num Votes,Release Date,Directors";
        var row = "1,tt1375666,2024-05-01,2024-05-01,,Inception,https://www.imdb.com/title/tt1375666,movie,8.8,148,2010,Action,2500000,2010-07-16,Christopher Nolan";
        var parsed = ImdbParser.Parse(Files(("WATCHLIST.csv", header + "\n" + row), ("ls012345678.csv", header + "\n" + row)));
        Assert.Equal("tt1375666", Assert.Single(parsed.Watchlist).Item.ImdbId);
        Assert.Equal("IMDb list ls012345678", Assert.Single(parsed.Lists).Name);
    }

    [Fact]
    public void TvTime_episodes_use_tvdb_ids_and_skip_repeats()
    {
        var seen = """
            updated_at,tv_show_name,episode_season_number,episode_number,user_id,episode_id,source,created_at
            2020-05-23 10:26:29,The Office (US),1,1,123,110413,public-api,2020-05-23 10:26:29
            2020-05-23 10:26:35,The Office (US),1,2,123,110414,public-api,2020-05-23 10:26:35
            """;
        var tracking = """
            s_no,season_number,key,user_id,runtime,episode_id,ep_id,episode_number,gsi,s_id,created_at,series_name,ep_no,is_followed,is_archived,is_for_later
            ,,user-series-1,123,,,,,,73244,2020-01-01 00:00:00,The Office (US),,true,false,false
            ,,user-series-2,123,,,,,,82066,2020-01-01 00:00:00,Fringe,,true,false,true
            """;
        var parsed = TvTimeParser.Parse(Files(("seen_episode.csv", seen), ("seen_episode_source.csv", seen), ("tracking-prod-records-v2.csv", tracking)));
        Assert.Equal(2, parsed.History.Count);
        Assert.All(parsed.History, h => Assert.Equal(73244, h.Item.TvdbId));
        Assert.Equal(new DateTimeOffset(2020, 5, 23, 10, 26, 29, TimeSpan.Zero), parsed.History[0].WatchedAt);
        var later = Assert.Single(parsed.Watchlist);
        Assert.Equal("Fringe", later.Item.Title);
        Assert.Equal(82066, later.Item.TvdbId);
    }

    [Fact]
    public void Simkl_csv_rows_become_plays_progress_ratings_and_watchlist()
    {
        var csv = """
            SIMKL_ID,Title,Type,Year,Watchlist,LastEpWatched,WatchedDate,Rating,Memo,TVDB,TMDB,IMDB
            1,Inception,movie,2010,completed,,2023-04-05 21:10:00,9,,,27205,tt1375666
            2,Dark,tv,2017,watching,s02e05,2023-06-01 22:00:00,,,,70523,tt5753856
            3,Dune: Part Two,movie,2024,plantowatch,,,,,,693134,
            """;
        var parsed = SimklParser.Parse(Files(("simkl.csv", csv)));
        var play = Assert.Single(parsed.History);
        Assert.Equal(27205, play.Item.TmdbId);
        Assert.False(play.Approximate);
        Assert.Equal(9, Assert.Single(parsed.Ratings).Rating);
        var progress = Assert.Single(parsed.Progress);
        Assert.Equal((2, 5), (progress.Season, progress.Episode));
        Assert.Equal(693134, Assert.Single(parsed.Watchlist).Item.TmdbId);
    }

    [Fact]
    public void Simkl_json_backup_has_each_episode()
    {
        var json = """
            { "shows": [ { "show": { "title": "Dark", "year": 2017, "ids": { "simkl": 1, "tmdb": "70523", "tvdb": 334824 } },
                           "status": "completed", "user_rating": 10,
                           "seasons": [ { "number": 1, "episodes": [ { "number": 1, "watched_at": "2023-06-01T20:00:00Z" }, { "number": 2, "watched_at": "2023-06-01T21:00:00Z" } ] } ] } ],
              "movies": [ { "movie": { "title": "Heat", "year": 1995, "ids": { "tmdb": 949, "imdb": "tt0113277" } }, "status": "completed", "last_watched_at": "2022-01-02T19:00:00Z" } ] }
            """;
        var parsed = SimklParser.Parse(Files(("backup.json", json)));
        Assert.Equal(3, parsed.History.Count);
        Assert.Equal(2, parsed.History.Count(h => h.Item.TmdbId == 70523 && h.Season == 1));
        Assert.Equal(10, Assert.Single(parsed.Ratings).Rating);
        Assert.Empty(parsed.Progress);
    }

    [Fact]
    public void Letterboxd_feed_entries_carry_tmdb_ids_dates_ratings_and_likes()
    {
        var xml = """
            <?xml version='1.0' encoding='utf-8'?>
            <rss version="2.0" xmlns:letterboxd="https://letterboxd.com" xmlns:tmdb="https://themoviedb.org">
              <channel><title>Letterboxd - someone</title>
                <item>
                  <title>Heat, 1995 - ★★★★½</title>
                  <guid isPermaLink="false">letterboxd-review-1</guid>
                  <pubDate>Wed, 4 Nov 2026 03:32:58 +1300</pubDate>
                  <letterboxd:watchedDate>2026-11-03</letterboxd:watchedDate>
                  <letterboxd:rewatch>Yes</letterboxd:rewatch>
                  <letterboxd:filmTitle>Heat</letterboxd:filmTitle>
                  <letterboxd:filmYear>1995</letterboxd:filmYear>
                  <letterboxd:memberRating>4.5</letterboxd:memberRating>
                  <letterboxd:memberLike>Yes</letterboxd:memberLike>
                  <tmdb:movieId>949</tmdb:movieId>
                </item>
                <item><title>A list</title><guid isPermaLink="false">letterboxd-list-9</guid><pubDate>Tue, 4 Jul 2017 08:58:00 +1200</pubDate></item>
              </channel>
            </rss>
            """;
        var e = Assert.Single(LetterboxdSyncService.ParseFeed(xml));
        Assert.Equal(949, e.TmdbId);
        Assert.Equal(new DateOnly(2026, 11, 3), e.Watched);
        Assert.Equal(4.5, e.Stars);
        Assert.True(e.Liked);
    }

    [Fact]
    public void Plex_scrobble_maps_to_a_completed_episode()
    {
        var json = """
            { "event": "media.scrobble", "Account": { "title": "me" },
              "Metadata": { "type": "episode", "title": "Pilot", "grandparentTitle": "Breaking Bad", "parentIndex": 1, "index": 1,
                            "viewOffset": 2900000, "duration": 3480000,
                            "Guid": [ { "id": "imdb://tt0959621" }, { "id": "tmdb://62085" }, { "id": "tvdb://349232" } ] } }
            """;
        using var doc = JsonDocument.Parse(json);
        var p = MediaServerPayloads.FromPlex(doc.RootElement, null)!;
        Assert.Equal("PlaybackStop", p.NotificationType);
        Assert.Equal("Episode", p.ItemType);
        Assert.Null(p.Tmdb);   // that's the episode's TMDB id, not the show's
        Assert.Equal("349232", p.Tvdb);
        Assert.True(p.Decide(90).Scrobble);
        Assert.Null(MediaServerPayloads.FromPlex(doc.RootElement, "someone-else"));
    }

    [Fact]
    public void Plex_legacy_agent_guid_gives_the_show_and_stop_uses_the_threshold()
    {
        var json = """
            { "event": "media.stop", "Metadata": { "type": "episode", "grandparentTitle": "Lost", "parentIndex": 2, "index": 3,
              "guid": "com.plexapp.agents.thetvdb://73739/2/3?lang=en", "viewOffset": 1000, "duration": 100000 } }
            """;
        using var doc = JsonDocument.Parse(json);
        var p = MediaServerPayloads.FromPlex(doc.RootElement, null)!;
        Assert.Equal("73739", p.SeriesTvdb);
        Assert.False(p.Decide(90).Scrobble);
    }

    [Fact]
    public void Kodi_end_of_movie_scrobbles()
    {
        var json = """{ "event": "end", "type": "movie", "title": "Heat", "year": 1995, "ids": { "tmdb": "949", "imdb": "tt0113277" }, "position": 10260, "duration": 10260 }""";
        using var doc = JsonDocument.Parse(json);
        var p = MediaServerPayloads.FromKodi(doc.RootElement);
        Assert.Equal("949", p.Tmdb);
        Assert.True(p.Decide(90).Scrobble);
    }

    [Fact]
    public void Kodi_episode_carries_the_show_ids()
    {
        var json = """{ "event": "stop", "type": "episode", "showtitle": "Dark", "season": 1, "episode": 2, "ids": { "tvdb": "6328806" }, "show_ids": { "tmdb": "70523" }, "position": 3000, "duration": 3200 }""";
        using var doc = JsonDocument.Parse(json);
        var p = MediaServerPayloads.FromKodi(doc.RootElement);
        Assert.Equal("70523", p.SeriesTmdb);
        Assert.True(p.Decide(90).Scrobble);   // 94%
    }
}
