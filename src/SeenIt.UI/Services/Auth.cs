using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using SeenIt.Contracts;

namespace SeenIt.UI.Services;

/// <summary>Where tokens are kept: browser localStorage on the web, SecureStorage on phones.</summary>
public interface ITokenStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value);
}

public class BrowserTokenStore(IJSRuntime js) : ITokenStore
{
    public async Task<string?> GetAsync(string key)
    {
        try { return await js.InvokeAsync<string?>("localStorage.getItem", key); }
        catch (JSException) { return null; }
    }

    public async Task SetAsync(string key, string? value)
    {
        try
        {
            if (value is null) await js.InvokeVoidAsync("localStorage.removeItem", key);
            else await js.InvokeVoidAsync("localStorage.setItem", key, value);
        }
        catch (JSException) { /* storage blocked: stay signed in for this session only */ }
    }
}

public class TokenManager(ITokenStore store)
{
    private const string AccessKey = "seenit.access";
    private const string RefreshKey = "seenit.refresh";
    private const string ExpiresKey = "seenit.expires";
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _access;
    private DateTimeOffset _expires;
    private bool _loaded;

    public event Action? Changed;

    public async Task<bool> HasSessionAsync()
    {
        await LoadAsync();
        return _access is not null;
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        await LoadAsync();
        return _access;
    }

    public async Task SaveAsync(TokenResponse t)
    {
        _access = t.AccessToken;
        _expires = DateTimeOffset.UtcNow.AddSeconds(t.ExpiresIn);
        await store.SetAsync(AccessKey, t.AccessToken);
        await store.SetAsync(RefreshKey, t.RefreshToken);
        await store.SetAsync(ExpiresKey, _expires.ToUnixTimeSeconds().ToString());
        Changed?.Invoke();
    }

    public async Task SignedOutAsync()
    {
        if (_access is null && _loaded) return;
        _access = null;
        await store.SetAsync(AccessKey, null);
        await store.SetAsync(RefreshKey, null);
        await store.SetAsync(ExpiresKey, null);
        Changed?.Invoke();
    }

    /// <summary>Trades the refresh token for a new pair. Returns the new access token, or null if signed out.</summary>
    public async Task<string?> RefreshAsync(HttpClient http)
    {
        var before = _access;
        await _refreshGate.WaitAsync();
        try
        {
            // Another request already refreshed while we waited.
            if (_access != before && _access is not null) return _access;

            var refresh = await store.GetAsync(RefreshKey);
            if (refresh is null) return null;
            using var resp = await http.PostAsJsonAsync("api/auth/refresh", new RefreshRequest(refresh), ApiClient.Json);
            if (!resp.IsSuccessStatusCode) return null;
            var t = await resp.Content.ReadFromJsonAsync<TokenResponse>(ApiClient.Json);
            if (t is null) return null;
            await SaveAsync(t);
            return t.AccessToken;
        }
        catch (HttpRequestException) { return before; }
        finally { _refreshGate.Release(); }
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        _access = await store.GetAsync(AccessKey);
        _expires = long.TryParse(await store.GetAsync(ExpiresKey), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : DateTimeOffset.MinValue;
        _loaded = true;
    }
}

/// <summary>Signed-in user state for the UI. The server is the source of truth; this just mirrors /me.</summary>
public class SessionState(ApiClient api, TokenManager tokens, IOfflineStore offline) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));
    private MeDto? _meValue;
    // Rating display is formatted all over the UI, so the scale lives on Fmt.
    private MeDto? _me { get => _meValue; set { _meValue = value; Fmt.FiveStars = value?.FiveStarRatings ?? false; } }
    private Task<AuthenticationState>? _pending;
    private bool _subscribed;

    public MeDto? Me => _me;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            tokens.Changed += () => { _pending = null; NotifyAuthenticationStateChanged(GetAuthenticationStateAsync()); };
        }
        return _pending ??= LoadAsync();
    }

    private async Task<AuthenticationState> LoadAsync()
    {
        if (!await tokens.HasSessionAsync()) { _me = null; return Anonymous; }
        try
        {
            _me = await api.MeAsync();
            // Stats and reviews use the profile's time zone so every device agrees; start it at this device's.
            if (_me.TimeZone is null && TimeZoneInfo.Local.Id is { } local && local.Contains('/'))
            {
                try
                {
                    await api.SetPreferencesAsync(new PreferencesRequest(TimeZone: local));
                    _me = _me with { TimeZone = local };
                }
                catch (ApiException) { /* the server doesn't know this zone; stats keep using the device's */ }
            }
        }
        catch (ApiException ex) when (ex.Status == 0)
        {
            // Offline: keep showing the signed-in UI with what we knew.
            if (_me is null) return Anonymous;
        }
        catch (ApiException)
        {
            _me = null;
            return Anonymous;
        }
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _me.Id.ToString()),
            new Claim(ClaimTypes.Name, _me.UserName),
        ], "seenit");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async Task LoginAsync(string userOrEmail, string password)
    {
        var t = await api.LoginAsync(new LoginRequest(userOrEmail, password));
        await offline.ClearAsync(); // don't show the previous account's saved pages
        await tokens.SaveAsync(t);
    }

    public async Task RegisterAsync(string userName, string email, string password, string? inviteCode = null)
    {
        var t = await api.RegisterAsync(new RegisterRequest(userName, email, password, inviteCode));
        await offline.ClearAsync();
        await tokens.SaveAsync(t);
    }

    public async Task LogoutAsync()
    {
        await offline.ClearAsync();
        await tokens.SignedOutAsync();
    }

    /// <summary>
    /// Re-reads the profile, then notifies with an already-finished task. Notifying with a pending
    /// task makes AuthorizeRouteView show its "authorizing" state, which tears down and rebuilds
    /// the current page (losing scroll position and any open section).
    /// </summary>
    public async void Refresh()
    {
        var state = await LoadAsync();
        _pending = Task.FromResult(state);
        NotifyAuthenticationStateChanged(_pending);
    }

    /// <summary>Applies a profile change we already know about (e.g. a saved preference) without a round trip.</summary>
    public void Update(Func<MeDto, MeDto> change)
    {
        if (_me is null) return;
        _me = change(_me);
        if (_pending is { IsCompletedSuccessfully: true } done)
            NotifyAuthenticationStateChanged(Task.FromResult(done.Result));
    }
}
