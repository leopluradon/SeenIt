using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

/// <summary>Shared pages (u/{name}/watchlist, …) follow the privacy part they show.</summary>
public class SharedPageTests
{
    [Fact]
    public void New_parts_follow_the_profile_unless_given_their_own_audience()
    {
        var prefs = UserPrefs.Default.WithAudience(ProfilePart.Watchlist, ProfileVisibility.Private);
        var owner = new AppUser { Id = Guid.NewGuid(), UserName = "sam", Visibility = ProfileVisibility.Public, PrefsJson = UserPrefsStore.Write(prefs) };
        var stranger = Guid.NewGuid();

        Assert.False(SocialService.CanSee(owner, stranger, mutual: false, ProfilePart.Watchlist));   // its own: only them
        Assert.True(SocialService.CanSee(owner, stranger, mutual: false, ProfilePart.Library));      // same as the profile: everyone
        Assert.True(SocialService.CanSee(owner, owner.Id, mutual: false, ProfilePart.Watchlist));    // always yourself

        var friendsOnly = new AppUser { Id = Guid.NewGuid(), UserName = "kim", Visibility = ProfileVisibility.Friends };
        Assert.False(SocialService.CanSee(friendsOnly, stranger, mutual: false, ProfilePart.Library));
        Assert.True(SocialService.CanSee(friendsOnly, stranger, mutual: true, ProfilePart.Library));
        Assert.False(SocialService.CanSee(friendsOnly, null, mutual: false, ProfilePart.History));    // signed out

        // Saved and read back with the new keys.
        Assert.Equal(ProfileVisibility.Private, UserPrefsStore.Read(UserPrefsStore.Write(prefs)).OwnAudience(ProfilePart.Watchlist));
    }

    [Theory]
    [InlineData("progress?tab=dropped", ProfilePart.History)]
    [InlineData("history", ProfilePart.History)]
    [InlineData("ratings?type=movie", ProfilePart.Ratings)]
    [InlineData("review/2026/11", ProfilePart.Stats)]
    [InlineData("stats", ProfilePart.Stats)]
    [InlineData("watchlist", ProfilePart.Watchlist)]
    [InlineData("library", ProfilePart.Library)]
    [InlineData("favorites", ProfilePart.Favorites)]
    [InlineData("goals/2025", ProfilePart.Stats)]
    [InlineData("completionist", ProfilePart.Stats)]
    [InlineData("people?dept=Directing", ProfilePart.People)]
    public void The_share_button_shares_your_own_pages_under_your_profile(string path, ProfilePart part) =>
        Assert.Equal(part, SeenIt.UI.Services.UiLayout.SharedPage(path)?.Part);

    [Fact]
    public void Your_lists_are_shared_but_saved_and_browsed_ones_are_not()
    {
        Assert.NotNull(SeenIt.UI.Services.UiLayout.SharedPage("lists"));
        Assert.Null(SeenIt.UI.Services.UiLayout.SharedPage("lists")!.Part);   // each list has its own public switch
        Assert.Null(SeenIt.UI.Services.UiLayout.SharedPage("lists?tab=saved"));
        Assert.Null(SeenIt.UI.Services.UiLayout.SharedPage("lists?tab=browse"));
    }

    [Theory]
    [InlineData("show/1396")]
    [InlineData("")]
    [InlineData("settings")]
    [InlineData("u/sam/progress")]
    [InlineData("person/380")]
    [InlineData("recommendations")]
    public void Other_pages_are_shared_as_they_are(string path) => Assert.Null(SeenIt.UI.Services.UiLayout.SharedPage(path));
}
