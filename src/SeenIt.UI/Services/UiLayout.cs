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

    public static bool Shows(MeDto? me, string section) => me?.HiddenHomeSections?.Contains(section) != true;
}
