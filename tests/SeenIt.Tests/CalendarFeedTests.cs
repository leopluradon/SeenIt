using SeenIt.Contracts;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class CalendarFeedTests
{
    [Fact]
    public void Feed_has_all_day_events_with_escaped_text_and_folded_lines()
    {
        var ics = ReleaseAlertService.Ics([
            new CalendarEntry(new DateOnly(2026, 11, 6), MediaType.Episode, 1400, "Seinfeld", 4, 11, "The Contest; or, \\ a very long episode title that goes well past seventy-five bytes for sure", null, false),
            new CalendarEntry(new DateOnly(2026, 12, 18), MediaType.Movie, 603, "The Matrix", null, null, null, null, false),
        ], "https://seenit.example/");

        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20261106\r\n", ics);
        Assert.Contains("DTEND;VALUE=DATE:20261107\r\n", ics);
        Assert.Contains("SUMMARY:Seinfeld S04E11\r\n", ics);
        Assert.Contains("SUMMARY:The Matrix (movie)\r\n", ics);
        Assert.Contains("URL:https://seenit.example/movie/603\r\n", ics);
        var unfolded = ics.Replace("\r\n ", "");
        Assert.Contains(@"DESCRIPTION:The Contest\; or\, \\ a very long episode title", unfolded);
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
    }
}

public class FeedTests
{
    [Fact]
    public void Rss_and_json_feeds_carry_the_items()
    {
        var feed = new SeenIt.Server.Services.FeedService.Feed("Best & worst · SeenIt", "A list", "https://seenit.example/lists/1",
            [new("1:Movie:603", "The Matrix (1999)", "https://seenit.example/movie/603", new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero), "Whoa <3", "https://image.tmdb.org/t/p/w342/x.jpg")]);
        var rss = SeenIt.Server.Services.FeedService.Rss(feed);
        var doc = System.Xml.Linq.XDocument.Parse(rss);   // well-formed, with & and < escaped
        Assert.Equal("Best & worst · SeenIt", doc.Root!.Element("channel")!.Element("title")!.Value);
        Assert.Contains("<pubDate>Thu, 05 Nov 2026 12:00:00 GMT</pubDate>", rss);
        var json = System.Text.Json.JsonDocument.Parse(SeenIt.Server.Services.FeedService.JsonFeed(feed, "https://seenit.example/feed.json")).RootElement;
        Assert.Equal("https://jsonfeed.org/version/1.1", json.GetProperty("version").GetString());
        Assert.Equal("The Matrix (1999)", json.GetProperty("items")[0].GetProperty("title").GetString());
    }
}
