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
    /// decides who can open it (none for your lists: each list has its own public switch); null for any other page.
    /// </summary>
    public static SharedTarget? SharedPage(string path)
    {
        var parts = path.Split('#')[0].Split('?', 2);
        var page = parts[0].Trim('/');
        var first = page.Split('/')[0];
        // Lists: only your own (not the saved or browse tabs, which aren't yours).
        if (page == "lists") return parts.Length > 1 && parts[1].Split('&').Any(q => q is "tab=saved" or "tab=browse") ? null : new SharedTarget(null);
        ProfilePart? part = first switch
        {
            "progress" or "history" => ProfilePart.History,
            "ratings" => ProfilePart.Ratings,
            "stats" or "review" or "goals" or "completionist" => ProfilePart.Stats,
            "favorites" => ProfilePart.Favorites,
            "watchlist" => ProfilePart.Watchlist,
            "library" => ProfilePart.Library,
            "people" => ProfilePart.People,
            _ => null,
        };
        return part is null ? null : new SharedTarget(part);
    }
}

/// <summary>A page of yours that has a shared copy under your profile, and the privacy part deciding who can open it.</summary>
public sealed record SharedTarget(ProfilePart? Part);

/// <summary>Titles you've seen on Discover and lists: shown as usual, faded, or left out.</summary>
public enum SeenMode { Show, Fade, Hide }
