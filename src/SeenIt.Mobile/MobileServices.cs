using SeenIt.UI.Services;

namespace SeenIt.Mobile;

/// <summary>Keeps the access and refresh tokens in the platform keychain/keystore.</summary>
public class SecureTokenStore : ITokenStore
{
    public async Task<string?> GetAsync(string key)
    {
        try { return await SecureStorage.Default.GetAsync(key); }
        catch { return null; } // e.g. keystore reset after a backup restore
    }

    public async Task SetAsync(string key, string? value)
    {
        if (value is null) SecureStorage.Default.Remove(key);
        else await SecureStorage.Default.SetAsync(key, value);
    }
}

/// <summary>The SeenIt server this phone talks to; changeable from the sign-in screen.</summary>
public class PreferencesServerConfig : IServerConfig
{
    public const string DefaultUrl = "https://seenit.vohiyo.com/";
    private const string Key = "seenit.server";

    /// <summary>Addresses earlier builds used by default. Plain http is blocked now, so move them to the domain.</summary>
    private static readonly string[] OldDefaults = ["http://192.168.1.167:8082/"];

    public bool Configurable => true;

    public string Url
    {
        get
        {
            var url = Preferences.Default.Get(Key, DefaultUrl);
            if (OldDefaults.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                Preferences.Default.Set(Key, DefaultUrl);
                url = DefaultUrl;
            }
            return url;
        }
    }

    public Task SetAsync(string url)
    {
        Preferences.Default.Set(Key, url.EndsWith('/') ? url : url + "/");
        return Task.CompletedTask;
    }
}

public class ServerUrlHandler(IServerConfig server) : DelegatingHandler
{
    public static readonly Uri PlaceholderBase = new("https://seenit.server/");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is { } uri && uri.Host == PlaceholderBase.Host)
            request.RequestUri = new Uri(new Uri(server.Url), uri.PathAndQuery.TrimStart('/'));
        return base.SendAsync(request, ct);
    }
}

/// <summary>Saved API responses and offline changes, as files in the app's private storage.</summary>
public class FileOfflineStore : IOfflineStore
{
    private const int MaxFiles = 500;
    private readonly string _dir = Path.Combine(FileSystem.AppDataDirectory, "offline");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Enabled => true;
    private string QueuePath => Path.Combine(_dir, "queue.json");

    private string PathFor(string key) =>
        Path.Combine(_dir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".json");

    public async Task<string?> GetAsync(string key)
    {
        var path = PathFor(key);
        try { return File.Exists(path) ? await File.ReadAllTextAsync(path) : null; }
        catch (IOException) { return null; }
    }

    public async Task SetAsync(string key, string value)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_dir);
            await WriteAtomicAsync(PathFor(key), value);
            var files = new DirectoryInfo(_dir).GetFiles("*.json");
            if (files.Length > MaxFiles)
                foreach (var old in files.Where(f => f.Name != "queue.json").OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - MaxFiles + 50))
                    old.Delete();
        }
        catch (IOException) { /* the cache is best-effort */ }
        finally { _gate.Release(); }
    }

    public async Task<List<QueuedRequest>> GetQueueAsync()
    {
        try
        {
            return File.Exists(QueuePath)
                ? System.Text.Json.JsonSerializer.Deserialize<List<QueuedRequest>>(await File.ReadAllTextAsync(QueuePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return []; }
    }

    public async Task SetQueueAsync(List<QueuedRequest> queue)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_dir);
            if (queue.Count == 0) File.Delete(QueuePath);
            else await WriteAtomicAsync(QueuePath, System.Text.Json.JsonSerializer.Serialize(queue));
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        finally { _gate.Release(); }
    }

    private static async Task WriteAtomicAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Phone versions of saving files and sharing links: both go through the system share sheet.</summary>
public class MobilePlatform(IServerConfig server) : IPlatform
{
    public async Task SaveFileAsync(string fileName, string contentType, byte[] data)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(path, data);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = fileName, File = new ShareFile(path, contentType) });
    }

    public async Task<string> ShareLinkAsync(string title, string url)
    {
        await Share.Default.RequestAsync(new ShareTextRequest { Title = title, Uri = url });
        return "shared";
    }

    public string PublicUrl(string path) => new Uri(new Uri(server.Url), path.TrimStart('/')).ToString();
}

/// <summary>Pages to open from outside the web view (e.g. a tap on the home-screen widget).</summary>
public static class DeepLinks
{
    private static string? _pending;
    public static event Action? Requested;

    public static void Open(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _pending = path;
        Requested?.Invoke();
    }

    public static string? Take() => Interlocked.Exchange(ref _pending, null);
}
