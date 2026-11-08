using System.Text.Json;
using Microsoft.JSInterop;

namespace SeenIt.UI.Services;

/// <summary>The last few searches, kept on this device only (localStorage). Newest first.</summary>
public static class RecentSearches
{
    private const string Key = "seenit.searches";
    public const int Max = 8;

    public static async Task<List<string>> GetAsync(IJSRuntime js)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", Key);
            return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch { return []; } // storage blocked, or a value we can't read
    }

    public static async Task AddAsync(IJSRuntime js, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        var list = await GetAsync(js);
        list.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, query);
        await SaveAsync(js, list.Take(Max).ToList());
    }

    public static async Task<List<string>> RemoveAsync(IJSRuntime js, string query)
    {
        var list = await GetAsync(js);
        list.RemoveAll(q => q == query);
        await SaveAsync(js, list);
        return list;
    }

    public static Task ClearAsync(IJSRuntime js) => SaveAsync(js, []);

    private static async Task SaveAsync(IJSRuntime js, List<string> list)
    {
        try
        {
            if (list.Count == 0) await js.InvokeVoidAsync("localStorage.removeItem", Key);
            else await js.InvokeVoidAsync("localStorage.setItem", Key, JsonSerializer.Serialize(list));
        }
        catch (JSException) { }
    }
}
