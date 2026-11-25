using SeenIt.Contracts;

namespace SeenIt.UI.Services;

/// <summary>Reads the user's layout choices (tab bar, hidden home sections) with sensible defaults.</summary>
public static class UiLayout
{
    public static IReadOnlyList<(string Key, string Label, string Icon, string Href)> Tabs(MeDto? me)
    {
        var keys = me?.Tabs is { Count: > 0 } chosen ? chosen : UiPrefs.DefaultTabs;
        return keys.Select(k => UiPrefs.Tabs.FirstOrDefault(t => t.Key == k)).Where(t => t.Key is not null).ToList();
    }

    /// <summary>The website's links at the top, after Home: your pick (Settings → Layout), or the usual ones.</summary>
    public static IReadOnlyList<(string Key, string Label, string Icon, string Href)> DesktopNav(UserPrefs prefs) =>
        (prefs.DesktopNav ?? UiPrefs.DefaultDesktopNav).Select(k => UiPrefs.Tabs.FirstOrDefault(t => t.Key == k)).Where(t => t.Key is not null).ToList();

    /// <summary>Where a tab goes; the review tabs point at the current month or year.</summary>
    public static string Href((string Key, string Label, string Icon, string Href) t) => t.Href switch
    {
        UiPrefs.ReviewMonthHref => $"review/{DateTime.Today.Year}/{DateTime.Today.Month}",
        UiPrefs.ReviewYearHref => $"review/{DateTime.Today.Year}",
        var h => h,
    };

    public static bool Shows(MeDto? me, string section) => me?.HiddenHomeSections?.Contains(section) != true;

    /// <summary>
    /// Your own pages that have a shared copy under your profile (u/{you}/progress, …), with the privacy part that
    /// decides who can open it; null for any other page.
    /// </summary>
    public static ProfilePart? SharedPage(string path)
    {
        var page = path.Split('?', '#')[0].Trim('/');
        var first = page.Split('/')[0];
        return first switch
        {
            "progress" or "history" => ProfilePart.History,
            "ratings" => ProfilePart.Ratings,
            "stats" or "review" => ProfilePart.Stats,
            "favorites" => ProfilePart.Favorites,
            "watchlist" => ProfilePart.Watchlist,
            "library" => ProfilePart.Library,
            _ => null,
        };
    }
}

/// <summary>Titles you've seen on Discover and lists: shown as usual, faded, or left out.</summary>
public enum SeenMode { Show, Fade, Hide }
