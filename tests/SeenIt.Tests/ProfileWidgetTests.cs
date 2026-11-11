using System.Text.Json;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;
using Xunit;

namespace SeenIt.Tests;

public class ProfileWidgetTests
{
    [Fact]
    public void Clean_drops_unknown_and_repeated_widgets_and_clamps_sizes()
    {
        var clean = ProfileWidgets.Clean(
        [
            new("a", "genres", 9, 0),
            new("b", "genres", 1, 1),             // only one of these
            new("c", "nonsense", 1, 1),
            new("d", "bio", 3, 1, new string('x', 2000)),
            new("e", "bio", 3, 1, "  "),          // several bios are fine; blank text is no text
            new("e", "spotlight", 1, 2, Media: new MediaRef(MediaType.Episode, 5)),   // repeated id; episodes can't be spotlit
        ]);

        Assert.Equal(["genres", "bio", "bio", "spotlight"], clean.Select(w => w.Type));
        Assert.Equal((3, 1), (clean[0].W, clean[0].H));
        Assert.Equal(ProfileWidgets.MaxText, clean[1].Text!.Length);
        Assert.Null(clean[2].Text);
        Assert.Null(clean[3].Media);
        Assert.Equal(clean.Count, clean.Select(w => w.Id).Distinct().Count());
    }

    [Fact]
    public void Visitors_only_get_widgets_for_parts_they_may_see()
    {
        var layout = new List<ProfileWidget> { new("1", "genres", 1, 2), new("2", "recent", 3, 2), new("3", "lists", 3, 2), new("4", "bio", 3, 1, "hi") };
        var owner = new AppUser
        {
            Id = Guid.NewGuid(), UserName = "them", Visibility = ProfileVisibility.Public,
            ProfileLayoutJson = JsonSerializer.Serialize(layout, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            PrefsJson = UserPrefsStore.Write(new UserPrefs(PartVisibility: new Dictionary<ProfilePart, ProfileVisibility> { [ProfilePart.Stats] = ProfileVisibility.Private })),
        };
        Assert.Equal(["recent", "lists", "bio"], ProfileWidgetService.Visible(owner, Guid.NewGuid(), mutual: false).Select(w => w.Type));
        Assert.Equal(4, ProfileWidgetService.Visible(owner, owner.Id, mutual: false).Count);

        // A private profile still shows its public lists, and nothing else.
        owner.Visibility = ProfileVisibility.Private;
        owner.PrefsJson = null;
        Assert.Equal(["lists"], ProfileWidgetService.Visible(owner, null, mutual: false).Select(w => w.Type));
    }

    [Fact]
    public void No_saved_layout_means_the_classic_profile()
    {
        var owner = new AppUser { UserName = "x" };
        Assert.Equal(ProfileWidgets.Default.Select(w => w.Type), ProfileWidgetService.Layout(owner).Select(w => w.Type));
    }
}
