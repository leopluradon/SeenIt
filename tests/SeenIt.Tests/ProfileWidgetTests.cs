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
            new("a", "genres", 20, 0),
            new("b", "genres", 1, 1),             // only one of these
            new("c", "nonsense", 1, 1),
            new("d", "bio", 3, 1, new string('x', 2000)),
            new("e", "bio", 3, 1, "  "),          // several bios are fine; blank text is no text
            new("e", "spotlight", 1, 2, Media: new MediaRef(MediaType.Episode, 5)),   // repeated id; episodes can't be spotlit
        ]);

        Assert.Equal(["genres", "bio", "bio", "spotlight"], clean.Select(w => w.Type));
        Assert.Equal((12, 1), (clean[0].W, clean[0].H));
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

public class ProfileWidgetWidthTests
{
    [Theory]
    [InlineData(6, 6)]    // half
    [InlineData(5, 4)]    // between a third and half: nearest
    [InlineData(1, 3)]    // narrowest is a quarter
    [InlineData(11, 12)]
    public void Widths_snap_to_quarters_thirds_and_halves(int w, int snapped) =>
        Assert.Equal(snapped, ProfileWidgets.Clean([new("a", "genres", w, 2)])[0].W);
}

public class CustomizationTests
{
    [Fact]
    public void Ranges_default_and_only_apply_to_stats_widgets()
    {
        var clean = ProfileWidgets.Clean(
        [
            new("a", "genres", 4, 2),                      // no range: all time
            new("b", "top-people", 8, 2, Range: "all"),     // people have no all-time view: back to this year
            new("c", "recent", 12, 2, Range: "month"),      // not a stats widget
            new("d", "tiles", 12, 1, Range: "month", Title: "  This month  "),
        ]);
        Assert.Equal(["all", "year", null, "month"], clean.Select(w => w.Range));
        Assert.Equal("This month", clean[3].Title);
    }

    [Fact]
    public void Old_spotlight_headings_move_from_text_to_title()
    {
        var w = ProfileWidgets.Clean([new("s", "spotlight", 4, 2, Text: "Currently obsessed with", Media: new MediaRef(MediaType.Show, 1))])[0];
        Assert.Equal(("Currently obsessed with", (string?)null), (w.Title, w.Text));
    }

    [Fact]
    public void Prefs_keep_only_known_accents_pages_and_views()
    {
        var p = new UserPrefs(Accent: "chartreuse",
            PageLayouts: new Dictionary<string, ListLayout> { ["watchlist"] = ListLayout.Rows, ["nowhere"] = ListLayout.Rows },
            PageSorts: new Dictionary<string, string> { ["ratings"] = "high", ["nowhere"] = "x" },
            SavedViews: [new("history", " Movies ", "?type=Movie"), new("nowhere", "x", ""), new("watchlist", " ", "q=a")],
            ProfileBanner: new MediaRef(MediaType.Episode, 3)).Clean();
        Assert.Null(p.Accent);
        Assert.Equal(ListLayout.Rows, p.LayoutFor("watchlist"));
        Assert.Equal(ListLayout.Posters, p.LayoutFor("ratings"));
        Assert.Equal(["watchlist"], p.PageLayouts!.Keys);
        Assert.Equal("high", p.SortFor("ratings"));
        Assert.Equal([new SavedView("history", "Movies", "type=Movie")], p.SavedViews);
        Assert.Null(p.ProfileBanner);
        Assert.Equal("blue", new UserPrefs(Accent: "blue").Clean().Accent);
    }

    [Fact]
    public void Period_ranges_map_to_calendar_periods()
    {
        var today = new DateOnly(2026, 11, 11);
        Assert.Equal((2026, (int?)11), ProfileWidgets.Period("month", today));
        Assert.Equal((2025, (int?)null), ProfileWidgets.Period("last-year", today));
        Assert.Null(ProfileWidgets.Period("all", today));
        Assert.Equal("November", ProfileWidgets.RangeName("month", today));
    }
}
