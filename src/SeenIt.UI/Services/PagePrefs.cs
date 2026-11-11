using SeenIt.Contracts;

namespace SeenIt.UI.Services;

/// <summary>
/// Choices a page remembers on its own: posters or rows, the last sort picked, and saved views.
/// Saved to the account in the background; the page has already changed.
/// </summary>
public static class PagePrefs
{
    public static Task SetLayoutAsync(SessionState session, ApiClient api, string page, ListLayout layout)
    {
        var layouts = (Fmt.Prefs.PageLayouts ?? new Dictionary<string, ListLayout>()).ToDictionary(kv => kv.Key, kv => kv.Value);
        layouts[page] = layout;
        return SaveAsync(session, api, Fmt.Prefs with { PageLayouts = layouts });
    }

    public static Task SetSortAsync(SessionState session, ApiClient api, string page, string sort)
    {
        if (Fmt.Prefs.SortFor(page) == sort) return Task.CompletedTask;
        var sorts = (Fmt.Prefs.PageSorts ?? new Dictionary<string, string>()).ToDictionary(kv => kv.Key, kv => kv.Value);
        sorts[page] = sort;
        return SaveAsync(session, api, Fmt.Prefs with { PageSorts = sorts });
    }

    public static IReadOnlyList<SavedView> Views(string page) => (Fmt.Prefs.SavedViews ?? []).Where(v => v.Page == page).ToList();

    public static Task AddViewAsync(SessionState session, ApiClient api, SavedView view) =>
        SaveAsync(session, api, Fmt.Prefs with { SavedViews = [.. (Fmt.Prefs.SavedViews ?? []).Where(v => !(v.Page == view.Page && v.Name == view.Name)), view] });

    public static Task RemoveViewAsync(SessionState session, ApiClient api, SavedView view) =>
        SaveAsync(session, api, Fmt.Prefs with { SavedViews = (Fmt.Prefs.SavedViews ?? []).Where(v => v != view).ToList() });

    private static async Task SaveAsync(SessionState session, ApiClient api, UserPrefs prefs)
    {
        prefs = prefs.Clean();
        session.Update(me => me with { Prefs = prefs });
        try { await api.SavePrefsAsync(prefs); } catch { /* offline: it's kept for this session */ }
    }
}
