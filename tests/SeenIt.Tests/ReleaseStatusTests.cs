using SeenIt.Contracts;

namespace SeenIt.Tests;

public class ReleaseStatusTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static (int, DateOnly) R(int type, string date) => (type, DateOnly.Parse(date));

    [Fact]
    public void In_theaters_for_the_usual_run()
    {
        var s = ReleaseStatuses.For([R(1, "2026-10-01"), R(3, "2026-10-24"), R(4, "2026-12-09")], "US", Today);
        Assert.Equal((ReleaseKind.InTheaters, new DateOnly(2026, 10, 24), (DateOnly?)new DateOnly(2026, 12, 9)), (s!.Kind, s.Theatrical, s.Digital));
        // Out on digital and still in cinemas (Other Mommy): in theaters, with the digital date.
        var both = ReleaseStatuses.For([R(3, "2026-10-09"), R(4, "2026-11-10")], "US", Today);
        Assert.Equal((ReleaseKind.InTheaters, (DateOnly?)new DateOnly(2026, 11, 10)), (both!.Kind, both.Digital));
        Assert.Null(ReleaseStatuses.For([R(3, "2026-05-01")], "US", Today));                        // long gone; no digital date recorded
        Assert.Null(ReleaseStatuses.For([R(4, "2026-11-14")], "US", Today));                        // straight to streaming
    }

    [Fact]
    public void Coming_to_theaters_and_limited_releases_count()
    {
        var s = ReleaseStatuses.For([R(2, "2026-12-12"), R(3, "2026-12-25")], "GB", Today);
        Assert.Equal((ReleaseKind.ComingToTheaters, new DateOnly(2026, 12, 12), "GB"), (s!.Kind, s.Theatrical, s.Country));
    }
}
