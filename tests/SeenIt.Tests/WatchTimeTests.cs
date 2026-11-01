using SeenIt.Contracts;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class WatchTimeTests
{
    private static readonly DateOnly Past = new(2022, 2, 18);

    [Fact]
    public void Release_date_is_noon_utc_on_the_day()
    {
        Assert.Equal(new DateTimeOffset(2022, 2, 18, 12, 0, 0, TimeSpan.Zero), TrackingService.PlayTime(WatchTime.ReleaseDate, null, Past));
    }

    [Fact]
    public void Release_date_falls_back_to_now_when_unknown_or_in_the_future()
    {
        var before = DateTimeOffset.UtcNow;
        Assert.InRange(TrackingService.PlayTime(WatchTime.ReleaseDate, null, null), before, DateTimeOffset.UtcNow);
        Assert.InRange(TrackingService.PlayTime(WatchTime.ReleaseDate, null, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)), before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Unknown_is_the_sentinel_and_custom_is_kept()
    {
        Assert.True(WatchDates.IsUnknown(TrackingService.PlayTime(WatchTime.Unknown, null, Past)));
        var custom = new DateTimeOffset(2023, 7, 1, 21, 30, 0, TimeSpan.FromHours(-7));
        Assert.Equal(custom, TrackingService.PlayTime(WatchTime.Custom, custom, Past));
        // Future custom dates are clamped to now.
        Assert.True(TrackingService.PlayTime(WatchTime.Custom, DateTimeOffset.UtcNow.AddDays(3), Past) <= DateTimeOffset.UtcNow);
    }
}
