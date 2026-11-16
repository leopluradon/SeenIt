using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class SourcePrivacyTests
{
    [Fact]
    public void Private_sources_read_as_logged_by_hand_to_others()
    {
        var shy = new AppUser { UserName = "shy", PrefsJson = UserPrefsStore.Write(UserPrefs.Default with { HideSourcesFromOthers = true }) };
        Assert.True(SocialService.HidesSources(shy));
        Assert.False(SocialService.HidesSources(new AppUser { UserName = "open" }));

        var play = new HistoryEntry(Guid.NewGuid(), MediaType.Episode, 1396, "Breaking Bad", 5, 16, "Felina", null, DateTimeOffset.UtcNow, WatchSource.Jellyfin);
        Assert.Equal(WatchSource.Manual, SocialService.Unsourced(play).Source);
        var now = new NowWatchingDto(MediaType.Movie, 949, "Heat", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(3), WatchSource.Plex);
        Assert.Equal(WatchSource.Manual, SocialService.Unsourced(now).Source);
    }
}
