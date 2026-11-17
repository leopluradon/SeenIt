using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class SourcePrivacyTests
{
    [Fact]
    public void Private_sources_read_as_logged_by_hand_to_others()
    {
        // Where you watched and which app are parts with their own audience; the older switch reads as Only me.
        var old = UserPrefs.Default with { HideSourcesFromOthers = true };
        Assert.Equal(ProfileVisibility.Private, old.For(ProfilePart.Sources, ProfileVisibility.Public));
        Assert.Equal(ProfileVisibility.Private, old.For(ProfilePart.WatchingApp, ProfileVisibility.Public));
        Assert.Equal(ProfileVisibility.Public, old.For(ProfilePart.History, ProfileVisibility.Public));
        // Changing one of them from the old switch keeps the other at Only me, and drops the switch.
        var moved = old.WithAudience(ProfilePart.Sources, ProfileVisibility.Friends);
        Assert.Equal((false, ProfileVisibility.Friends, ProfileVisibility.Private),
            (moved.HideSourcesFromOthers, moved.For(ProfilePart.Sources, ProfileVisibility.Public), moved.For(ProfilePart.WatchingApp, ProfileVisibility.Public)));
        Assert.Equal(ProfileVisibility.Public, moved.WithAudience(ProfilePart.WatchingApp, null).For(ProfilePart.WatchingApp, ProfileVisibility.Public));

        var friendsOnly = UserPrefs.Default.WithAudience(ProfilePart.Sources, ProfileVisibility.Friends);
        Assert.Null(friendsOnly.Clean().PartVisibility);   // kept out of PartVisibility, which older apps read
        var owner = new AppUser { Id = Guid.NewGuid(), UserName = "o", Visibility = ProfileVisibility.Public, PrefsJson = UserPrefsStore.Write(friendsOnly) };
        Assert.True(SocialService.CanSee(owner, Guid.NewGuid(), mutual: true, ProfilePart.Sources));
        Assert.False(SocialService.CanSee(owner, Guid.NewGuid(), mutual: false, ProfilePart.Sources));
        Assert.True(SocialService.CanSee(owner, Guid.NewGuid(), mutual: false, ProfilePart.WatchingApp));   // its own part: same as profile

        var play = new HistoryEntry(Guid.NewGuid(), MediaType.Episode, 1396, "Breaking Bad", 5, 16, "Felina", null, DateTimeOffset.UtcNow, WatchSource.Jellyfin);
        Assert.Equal(WatchSource.Manual, SocialService.Unsourced(play).Source);
        var now = new NowWatchingDto(MediaType.Movie, 949, "Heat", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(3), WatchSource.Plex);
        Assert.Equal(WatchSource.Manual, SocialService.Unsourced(now).Source);
    }
}
