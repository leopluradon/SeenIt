using SeenIt.Contracts;
using SeenIt.Server.Import;

namespace SeenIt.Tests;

public class LibraryTests
{
    private static OwnedCopy C(OwnedFormat f, int? s = null) => new(f, s, WatchSource.Manual, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Copies_read_as_formats_and_season_ranges()
    {
        Assert.Equal("DVD · Media server", OwnedFormats.Describe([C(OwnedFormat.MediaServer), C(OwnedFormat.Dvd)]));
        Assert.Equal("Blu-ray (S1–3, S5) · Media server",
            OwnedFormats.Describe([C(OwnedFormat.BluRay, 1), C(OwnedFormat.BluRay, 2), C(OwnedFormat.BluRay, 3), C(OwnedFormat.BluRay, 5), C(OwnedFormat.MediaServer)]));
        // The whole show in a format doesn't list seasons of it.
        Assert.Equal("Digital", OwnedFormats.Describe([C(OwnedFormat.Digital), C(OwnedFormat.Digital, 2)]));
        Assert.Equal("S1, S3–4, Specials", OwnedFormats.Seasons([4, 0, 1, 3, 3]));
    }

    [Fact]
    public void Jellyfin_reports_csv_becomes_media_server_copies()
    {
        // The Reports plugin's movie export: just Name and Year (with a byte-order mark).
        var movies = MediaServerParser.Parse(new Dictionary<string, string>
        {
            ["report.csv"] = "﻿Name,Year\n3:10 to Yuma,2007\n\"Crouching Tiger, Hidden Dragon\",2000\n,\n",
        });
        Assert.Equal(["3:10 to Yuma (2007)", "Crouching Tiger, Hidden Dragon (2000)"], movies.Library.Select(l => l.Item.Label));
        Assert.All(movies.Library, l => Assert.Equal((MediaType.Movie, OwnedFormat.MediaServer, (int?)null), (l.Item.Type, l.Format, l.Season)));

        // TV with series and season columns: a copy per season, and no separate whole-show copy.
        var tv = MediaServerParser.Parse(new Dictionary<string, string>
        {
            ["tv.csv"] = "Type,Name,Series,Season,TMDb\nSeries,Severance,,,95396\nEpisode,Good News About Hell,Severance,1,\nEpisode,Half Loop,Severance,1,\nEpisode,Hello Ms. Cobel,Severance,2,\nSeries,Andor,,,83867\nMovie,Heat,,,949\n",
        });
        Assert.Equal(["Severance:1", "Severance:2", "Andor:", "Heat:"], tv.Library.Select(l => $"{l.Item.Title}:{l.Season}"));
        Assert.Equal(83867, tv.Library.Single(l => l.Item.Title == "Andor").Item.TmdbId);
        Assert.Equal(MediaType.Movie, tv.Library.Single(l => l.Item.Title == "Heat").Item.Type);
    }
}
