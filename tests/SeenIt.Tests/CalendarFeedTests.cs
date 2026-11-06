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
