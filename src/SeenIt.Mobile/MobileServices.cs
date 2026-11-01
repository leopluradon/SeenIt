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
    // Change this to your public SeenIt address before publishing the app.
    public const string DefaultUrl = "http://192.168.1.167:8082/";
    private const string Key = "seenit.server";

    public bool Configurable => true;
    public string Url => Preferences.Default.Get(Key, DefaultUrl);

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
