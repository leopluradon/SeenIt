using SeenIt.Contracts;

namespace SeenIt.Tests;

public class NavPrefsTests
{
    [Fact]
    public void Top_links_keep_known_pages_once_without_home()
    {
        var p = (UserPrefs.Default with { DesktopNav = ["home", "library", "bogus", "stats", "library", "review-month"] }).Clean();
        Assert.Equal(["library", "stats", "review-month"], p.DesktopNav);
        Assert.Null(UserPrefs.Default.Clean().DesktopNav);   // not chosen: the usual links
        // Everything on the More page can be picked.
        foreach (var key in new[] { "notifications", "for-you", "pick", "favorites", "library", "goals", "recommendations", "people", "duplicates", "settings" })
            Assert.Contains(UiPrefs.Tabs, t => t.Key == key);
    }
}
