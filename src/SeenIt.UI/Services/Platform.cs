using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SeenIt.UI.Services;

/// <summary>Things the website and the app do differently: saving files, sharing links, offline storage.</summary>
public interface IPlatform
{
    /// <summary>Saves (web: downloads; app: opens the share sheet for) a generated file.</summary>
    Task SaveFileAsync(string fileName, string contentType, byte[] data);
    /// <summary>Returns "shared", "copied" or "failed".</summary>
    Task<string> ShareLinkAsync(string title, string url);
    /// <summary>The public address of a page, for links people can open elsewhere.</summary>
    string PublicUrl(string path);
    /// <summary>The installed app's version (e.g. "1.44"); null on the website.</summary>
    string? AppVersion { get; }
    /// <summary>Opens a link outside SeenIt (the browser / the app that handles it).</summary>
    Task OpenExternalAsync(string url);
}

public class BrowserPlatform(IJSRuntime js, NavigationManager nav) : IPlatform
{
    public async Task SaveFileAsync(string fileName, string contentType, byte[] data) =>
        await js.InvokeVoidAsync("seenit.download", fileName, contentType, Convert.ToBase64String(data));

    public async Task<string> ShareLinkAsync(string title, string url)
    {
        try { return await js.InvokeAsync<string>("seenit.share", title, url); }
        catch (JSException) { return "failed"; }
    }

    public string PublicUrl(string path) => nav.BaseUri + path.TrimStart('/');

    public string? AppVersion => null;

    public async Task OpenExternalAsync(string url) => await js.InvokeVoidAsync("open", url, "_blank", "noopener");
}

/// <summary>Keeps the last copy of API responses, and changes made while offline, on the device.</summary>
public interface IOfflineStore
{
    bool Enabled { get; }
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task<List<QueuedRequest>> GetQueueAsync();
    Task SetQueueAsync(List<QueuedRequest> queue);
    Task ClearAsync();
}

public record QueuedRequest(string Method, string Url, string? Body, DateTimeOffset QueuedAt);

/// <summary>The website always needs the network, so it stores nothing.</summary>
public class NoOfflineStore : IOfflineStore
{
    public bool Enabled => false;
    public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
    public Task SetAsync(string key, string value) => Task.CompletedTask;
    public Task<List<QueuedRequest>> GetQueueAsync() => Task.FromResult(new List<QueuedRequest>());
    public Task SetQueueAsync(List<QueuedRequest> queue) => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
}

/// <summary>Whether the last request reached the server, and how many offline changes are waiting.</summary>
public class ConnectivityState
{
    public bool Offline { get; private set; }
    public int Pending { get; private set; }
    public DateTimeOffset? CachedAt { get; private set; }
    public event Action? Changed;

    public void Set(bool offline, int? pending = null)
    {
        var p = pending ?? Pending;
        if (offline == Offline && p == Pending) return;
        Offline = offline;
        Pending = p;
        Changed?.Invoke();
    }
}

internal static class OfflineJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
