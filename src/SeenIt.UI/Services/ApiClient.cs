using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeenIt.Contracts;

namespace SeenIt.UI.Services;

public class ApiException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Typed client for the SeenIt API. Used unchanged by the website and the mobile app.</summary>
public class ApiClient(HttpClient http, TokenManager tokens, IOfflineStore offline, ConnectivityState connectivity)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ---------- Auth ----------

    public Task<TokenResponse> RegisterAsync(RegisterRequest r) => SendAsync<TokenResponse>(HttpMethod.Post, "api/auth/register", r, auth: false)!;
    public Task<TokenResponse> LoginAsync(LoginRequest r) => SendAsync<TokenResponse>(HttpMethod.Post, "api/auth/login", r, auth: false)!;
    public Task<MeDto> MeAsync() => GetAsync<MeDto>("api/auth/me");
    public Task UpdateProfileAsync(UpdateProfileRequest r) => SendAsync(HttpMethod.Put, "api/auth/me", r);
    public Task SetPreferencesAsync(PreferencesRequest r) => SendAsync(HttpMethod.Put, "api/auth/preferences", r);
    public Task ChangePasswordAsync(ChangePasswordRequest r) => SendAsync(HttpMethod.Post, "api/auth/password", r);
    private Task<ServerFeatures?>? _features;

    /// <summary>What the server supports. Cached for the session; a failed lookup is retried next time.</summary>
    public Task<ServerFeatures?> FeaturesAsync()
    {
        var t = _features ??= SendAsync<ServerFeatures>(HttpMethod.Get, "api/auth/features", null, auth: false);
        if (t.IsFaulted || t.IsCanceled) _features = null;
        return t;
    }

    public Task<InviteCheck?> CheckInviteAsync(string code) => SendAsync<InviteCheck>(HttpMethod.Get, $"api/auth/invite/{Uri.EscapeDataString(code)}", null, auth: false);
    public Task<List<InviteDto>?> InvitesAsync() => SendAsync<List<InviteDto>>(HttpMethod.Get, "api/auth/invites");
    public Task<InviteDto?> CreateInviteAsync() => SendAsync<InviteDto>(HttpMethod.Post, "api/auth/invites");
    public Task DeleteInviteAsync(string code) => SendAsync(HttpMethod.Delete, $"api/auth/invites/{Uri.EscapeDataString(code)}");
    public Task ForgotPasswordAsync(ForgotPasswordRequest r) => SendAsync<object>(HttpMethod.Post, "api/auth/forgot", r, auth: false);
    public Task ResetPasswordAsync(ResetPasswordRequest r) => SendAsync<object>(HttpMethod.Post, "api/auth/reset", r, auth: false);
    public Task SendTestEmailAsync() => SendAsync(HttpMethod.Post, "api/auth/test-email", null);
    public Task DeleteAccountAsync(DeleteAccountRequest r) => SendAsync(HttpMethod.Post, "api/auth/delete", r);
    public Task<ClearDataResult?> ClearDataAsync(ClearDataRequest r) => SendAsync<ClearDataResult>(HttpMethod.Post, "api/auth/clear", r);

    // ---------- Admin ----------

    public Task<AdminOverview> AdminOverviewAsync() => GetAsync<AdminOverview>("api/admin/overview");
    public Task<List<AdminUser>> AdminUsersAsync() => GetAsync<List<AdminUser>>("api/admin/users");
    public Task<AdminActivityPage> AdminActivityAsync(string? user, string? group, int page) =>
        GetAsync<AdminActivityPage>($"api/admin/activity?page={page}"
            + (string.IsNullOrEmpty(user) ? "" : $"&user={Uri.EscapeDataString(user)}")
            + (string.IsNullOrEmpty(group) ? "" : $"&group={group}"));
    public Task<List<AdminImport>> AdminImportsAsync() => GetAsync<List<AdminImport>>("api/admin/imports");
    public Task<List<AdminJellyfinEvent>> AdminJellyfinAsync() => GetAsync<List<AdminJellyfinEvent>>("api/admin/jellyfin");
    public Task<List<AdminInvite>> AdminInvitesAsync() => GetAsync<List<AdminInvite>>("api/admin/invites");
    public Task<List<AdminLogEntry>> AdminLogsAsync() => GetAsync<List<AdminLogEntry>>("api/admin/logs");
    public Task AdminUnlockAsync(Guid id) => SendAsync(HttpMethod.Post, $"api/admin/users/{id}/unlock", null);
    public Task<AdminResetLink?> AdminResetLinkAsync(Guid id) => SendAsync<AdminResetLink>(HttpMethod.Post, $"api/admin/users/{id}/reset-link");

    // ---------- Catalogue ----------

    public Task<PagedResult<MediaSummary>> SearchAsync(string q, string? type = null, int page = 1) =>
        GetAsync<PagedResult<MediaSummary>>($"api/catalog/search?q={Uri.EscapeDataString(q)}&page={page}" + (type is null ? "" : $"&type={type}"));
    public Task<PagedResult<MediaSummary>> BrowseAsync(MediaType type, string list, int page = 1) =>
        GetAsync<PagedResult<MediaSummary>>($"api/catalog/browse/{type}/{list}?page={page}");
    public Task<MovieDetail> MovieAsync(int id) => GetAsync<MovieDetail>($"api/catalog/movie/{id}");
    public Task<ShowDetail> ShowAsync(int id) => GetAsync<ShowDetail>($"api/catalog/show/{id}");
    public Task<SeasonDetail> SeasonAsync(int id, int season) => GetAsync<SeasonDetail>($"api/catalog/show/{id}/season/{season}");
    public Task<PersonDetail> PersonAsync(int id) => GetAsync<PersonDetail>($"api/catalog/person/{id}");
    public Task<EpisodeDetail> EpisodeAsync(int id, int season, int episode) => GetAsync<EpisodeDetail>($"api/catalog/show/{id}/season/{season}/episode/{episode}");

    // ---------- Tracking ----------

    // "Now" is pinned to the moment of the tap, so a play queued offline keeps its real time.
    public Task WatchAsync(WatchRequest r) => SendQueueableAsync(HttpMethod.Post, "api/watch",
        r.When is null or WatchTime.Now && r.WatchedAt is null ? r with { When = WatchTime.Custom, WatchedAt = DateTimeOffset.UtcNow } : r);
    public Task BulkWatchAsync(BulkWatchRequest r) => SendQueueableAsync(HttpMethod.Post, "api/watch/bulk",
        r.When is null or WatchTime.Now && r.WatchedAt is null ? r with { When = WatchTime.Custom, WatchedAt = DateTimeOffset.UtcNow } : r);
    public Task UnwatchAsync(MediaType type, int tmdbId, int? season = null, int? episode = null) =>
        SendAsync(HttpMethod.Delete, $"api/watch?type={type}&tmdbId={tmdbId}" + (season is null ? "" : $"&season={season}") + (episode is null ? "" : $"&episode={episode}"));
    public Task<HistoryPage> HistoryAsync(HistoryQuery f)
    {
        var qs = new List<string> { $"page={f.Page}", $"pageSize={f.PageSize}", $"tz={Tz}" };
        if (f.Type is { } t) qs.Add($"type={t}");
        if (!string.IsNullOrWhiteSpace(f.Q)) qs.Add($"q={Uri.EscapeDataString(f.Q)}");
        if (f.Source is { } s) qs.Add($"source={s}");
        if (!string.IsNullOrWhiteSpace(f.Genre)) qs.Add($"genre={Uri.EscapeDataString(f.Genre)}");
        if (f.From is { } from) qs.Add($"from={Uri.EscapeDataString(from.ToString("O"))}");
        if (f.To is { } to) qs.Add($"to={Uri.EscapeDataString(to.ToString("O"))}");
        if (f.Oldest) qs.Add("sort=oldest");
        return GetAsync<HistoryPage>("api/history?" + string.Join('&', qs));
    }
    public Task<List<string>> HistoryGenresAsync() => GetAsync<List<string>>("api/history/genres");
    public Task DeleteHistoryAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/history/{id}");

    public Task<List<WatchlistEntry>> WatchlistAsync() => GetAsync<List<WatchlistEntry>>("api/watchlist");
    public Task AddToWatchlistAsync(MediaType type, int id) => SendQueueableAsync(HttpMethod.Post, "api/watchlist", new WatchlistRequest(type, id));
    public Task RemoveFromWatchlistAsync(MediaType type, int id) => SendAsync(HttpMethod.Delete, $"api/watchlist?type={type}&tmdbId={id}");

    public Task<List<RatingEntry>> RatingsAsync(MediaType? type = null) => GetAsync<List<RatingEntry>>("api/ratings" + (type is null ? "" : $"?type={type}"));
    public Task RateAsync(RatingRequest r) => SendQueueableAsync(HttpMethod.Post, "api/ratings", r);
    public Task UnrateAsync(MediaType type, int id, int? season = null, int? episode = null) =>
        SendAsync(HttpMethod.Delete, $"api/ratings?type={type}&tmdbId={id}" + (season is null ? "" : $"&season={season}") + (episode is null ? "" : $"&episode={episode}"));

    public Task<List<ShowProgressDto>> ProgressAsync() => GetAsync<List<ShowProgressDto>>("api/progress");
    public Task<List<CalendarEntry>> CalendarAsync(DateOnly from, int days) => GetAsync<List<CalendarEntry>>($"api/calendar?from={from:yyyy-MM-dd}&days={days}");
    public Task<DashboardDto> DashboardAsync() => GetAsync<DashboardDto>("api/dashboard");
    public Task<int> StartRewatchAsync(int showId) => SendAsync<int>(HttpMethod.Post, $"api/shows/{showId}/rewatch")!;
    public Task StopRewatchAsync(int showId) => SendAsync(HttpMethod.Delete, $"api/shows/{showId}/rewatch");
    public Task<List<OnThisDayItem>> OnThisDayAsync() => GetAsync<List<OnThisDayItem>>($"api/onthisday?tz={Tz}");

    /// <summary>A random pick, or null when nothing matches the filters.</summary>
    public Task<PickDto?> PickAsync(PickKind kind, PickSource source, int? maxMinutes, bool streaming, IEnumerable<string> exclude) =>
        SendAsync<PickDto>(HttpMethod.Get,
            $"api/pick?kind={kind}&source={source}&streaming={(streaming ? "true" : "false")}&region={DeviceRegion}"
            + (maxMinutes is { } m ? $"&maxMinutes={m}" : "")
            + $"&exclude={Uri.EscapeDataString(string.Join(',', exclude))}");

    public Task SetShowStatusAsync(int showId, ShowTrackStatus status) => SendAsync(HttpMethod.Put, $"api/shows/{showId}/status", new ShowStatusRequest(status));

    public Task<NowWatchingDto?> CheckInAsync() => SendAsync<NowWatchingDto>(HttpMethod.Get, "api/checkin");
    public Task<NowWatchingDto?> StartCheckInAsync(CheckInRequest r) => SendAsync<NowWatchingDto>(HttpMethod.Post, "api/checkin", r);
    public Task CancelCheckInAsync() => SendAsync(HttpMethod.Delete, "api/checkin");

    public Task<WatchProvidersDto> ProvidersAsync(MediaType type, int id) => GetAsync<WatchProvidersDto>($"api/catalog/providers/{type}/{id}?region={DeviceRegion}");
    public Task<List<RegionDto>> RegionsAsync() => GetAsync<List<RegionDto>>("api/catalog/regions");

    /// <summary>The device's country, used for "where to watch" until the user picks one.</summary>
    public static string DeviceRegion
    {
        get
        {
            try
            {
                var name = System.Globalization.CultureInfo.CurrentCulture.Name;
                if (name.Length > 0 && new System.Globalization.RegionInfo(name).TwoLetterISORegionName is { Length: 2 } r) return r;
            }
            catch (ArgumentException) { }
            return "US";
        }
    }

    public async Task<byte[]> ExportAsync()
    {
        using var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/export"), auth: true);
        await EnsureOkAsync(resp);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    // ---------- Social ----------

    public Task<ProfileDto> ProfileAsync(string userName) => GetAsync<ProfileDto>($"api/users/{Uri.EscapeDataString(userName)}");
    public Task<FeedDto> FeedAsync(int take = 40) => GetAsync<FeedDto>($"api/social/feed?take={take}");
    public Task<List<UserCard>> SearchUsersAsync(string q) => GetAsync<List<UserCard>>($"api/social/search?q={Uri.EscapeDataString(q)}");
    public Task<List<UserCard>> FollowingAsync() => GetAsync<List<UserCard>>("api/social/following");
    public Task<List<UserCard>> FollowersAsync() => GetAsync<List<UserCard>>("api/social/followers");
    public Task FollowAsync(string userName, bool follow) =>
        SendAsync(follow ? HttpMethod.Post : HttpMethod.Delete, $"api/social/follow/{Uri.EscapeDataString(userName)}");
    public Task<CompareDto> CompareAsync(string userName) => GetAsync<CompareDto>($"api/social/compare/{Uri.EscapeDataString(userName)}");
    public Task<StatsDto> StatsAsync() => GetAsync<StatsDto>($"api/stats?tz={Tz}");
    public Task<List<ReviewYear>> ReviewPeriodsAsync() => GetAsync<List<ReviewYear>>($"api/review/periods?tz={Tz}");
    public Task<ReviewDto> ReviewAsync(int year, int? month) =>
        GetAsync<ReviewDto>($"api/review/{year}" + (month is null ? "" : $"/{month}") + $"?tz={Tz}");

    /// <summary>The device's IANA time zone, so reviews use the viewer's own calendar days.</summary>
    private static string Tz => Uri.EscapeDataString(TimeZoneInfo.Local.Id);

    // ---------- Lists ----------

    public Task<List<ListSummary>> ListsAsync() => GetAsync<List<ListSummary>>("api/lists");
    public Task<ListDetail> ListAsync(Guid id) => GetAsync<ListDetail>($"api/lists/{id}");
    public async Task<Guid> CreateListAsync(CreateListRequest r) => (await SendAsync<IdResult>(HttpMethod.Post, "api/lists", r))!.Id;
    public Task UpdateListAsync(Guid id, CreateListRequest r) => SendAsync(HttpMethod.Put, $"api/lists/{id}", r);
    public Task DeleteListAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/lists/{id}");
    public Task AddToListAsync(Guid id, MediaType type, int tmdbId) => SendAsync(HttpMethod.Post, $"api/lists/{id}/items", new ListItemRequest(type, tmdbId));
    public Task RemoveFromListAsync(Guid id, MediaType type, int tmdbId) => SendAsync(HttpMethod.Delete, $"api/lists/{id}/items?type={type}&tmdbId={tmdbId}");

    // ---------- Integrations ----------

    public Task<JellyfinSettingsDto> JellyfinAsync() => GetAsync<JellyfinSettingsDto>("api/jellyfin");
    public Task RegenerateJellyfinTokenAsync() => SendAsync(HttpMethod.Post, "api/jellyfin/regenerate");
    public Task SetJellyfinThresholdAsync(int pct) => SendAsync(HttpMethod.Put, "api/jellyfin/threshold", new JellyfinThresholdRequest(pct));

    public Task<List<ImportJobDto>> ImportsAsync() => GetAsync<List<ImportJobDto>>("api/imports");
    public async Task UploadImportAsync(ImportKind kind, ImportParts parts, string fileName, Stream content)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/imports/{kind}?parts={(int)parts}") { Content = form }, auth: true);
        await EnsureOkAsync(resp);
    }

    public Uri BaseAddress => http.BaseAddress!;

    // ---------- Plumbing ----------

    private record IdResult(Guid Id);

    /// <summary>GETs go through the offline cache on the app: fresh copies are saved, and used when the server can't be reached.</summary>
    private async Task<T> GetAsync<T>(string url)
    {
        string json;
        try
        {
            using var resp = await SendRawAsync(() => Build(HttpMethod.Get, url, null), auth: true);
            await EnsureOkAsync(resp);
            json = await resp.Content.ReadAsStringAsync();
        }
        catch (ApiException ex) when (ex.Status == 0 && offline.Enabled)
        {
            if (await offline.GetAsync(url) is not { } cached) throw;
            connectivity.Set(offline: true);
            return JsonSerializer.Deserialize<T>(cached, Json)!;
        }
        if (offline.Enabled) await offline.SetAsync(url, json);
        return JsonSerializer.Deserialize<T>(json, Json)!;
    }

    /// <summary>Changes that can wait: when offline on the app they're queued and sent once the server is back.</summary>
    private async Task SendQueueableAsync(HttpMethod method, string url, object body)
    {
        try { await SendAsync(method, url, body); }
        catch (ApiException ex) when (ex.Status == 0 && offline.Enabled)
        {
            var queue = await offline.GetQueueAsync();
            queue.Add(new QueuedRequest(method.Method, url, JsonSerializer.Serialize(body, body.GetType(), Json), DateTimeOffset.UtcNow));
            await offline.SetQueueAsync(queue);
            connectivity.Set(offline: true, queue.Count);
        }
    }

    private readonly SemaphoreSlim _flushGate = new(1, 1);

    /// <summary>Sends changes made offline, oldest first. Returns true if the server was reachable.</summary>
    public async Task<bool> SyncAsync()
    {
        if (!offline.Enabled) return true;
        if (!await _flushGate.WaitAsync(0)) return !connectivity.Offline;
        try
        {
            var queue = await offline.GetQueueAsync();
            while (queue.Count > 0)
            {
                var q = queue[0];
                using var resp = await SendRawAsync(() => new HttpRequestMessage(new HttpMethod(q.Method), q.Url)
                {
                    Content = q.Body is null ? null : new StringContent(q.Body, System.Text.Encoding.UTF8, "application/json"),
                }, auth: true);
                // Server errors are retried later; a rejected change (e.g. the title is gone) is dropped.
                if ((int)resp.StatusCode >= 500) break;
                queue.RemoveAt(0);
                await offline.SetQueueAsync(queue);
            }
            connectivity.Set(offline: false, queue.Count);
            return true;
        }
        catch (ApiException ex) when (ex.Status == 0)
        {
            connectivity.Set(offline: true);
            return false;
        }
        finally { _flushGate.Release(); }
    }

    public async Task<bool> PingAsync()
    {
        try
        {
            using var resp = await http.GetAsync("healthz");
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var resp = await SendRawAsync(() => Build(method, url, body), auth: true);
        await EnsureOkAsync(resp);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? body = null, bool auth = true)
    {
        using var resp = await SendRawAsync(() => Build(method, url, body), auth);
        await EnsureOkAsync(resp);
        if (resp.StatusCode == HttpStatusCode.NoContent) return default;
        return await resp.Content.ReadFromJsonAsync<T>(Json);
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, object? body) =>
        new(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: Json) };

    /// <summary>Attaches the access token and, on a 401, refreshes it once and retries.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> build, bool auth)
    {
        var req = build();
        if (auth && await tokens.GetAccessTokenAsync() is { } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            connectivity.Set(offline: true);
            throw new ApiException("Can't reach the SeenIt server. Check your connection.", 0);
        }
        if (connectivity.Offline)
        {
            connectivity.Set(offline: false);
            if (connectivity.Pending > 0) _ = SyncAsync();
        }

        if (auth && resp.StatusCode == HttpStatusCode.Unauthorized && req.Content is not MultipartFormDataContent
            && await tokens.RefreshAsync(http) is { } fresh)
        {
            resp.Dispose();
            var retry = build();
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
            resp = await http.SendAsync(retry);
        }
        if (auth && resp.StatusCode == HttpStatusCode.Unauthorized)
            await tokens.SignedOutAsync();
        return resp;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        string message = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Please sign in again.",
            HttpStatusCode.NotFound => "Not found.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
            _ => $"Something went wrong ({(int)resp.StatusCode}).",
        };
        try
        {
            var problem = await resp.Content.ReadFromJsonAsync<ProblemInfo>(Json);
            if (problem?.Errors is { Count: > 0 } errs) message = string.Join(" ", errs.Values.SelectMany(v => v));
            else if (!string.IsNullOrWhiteSpace(problem?.Title)) message = problem.Title;
        }
        catch { /* not JSON */ }
        throw new ApiException(message, resp.StatusCode);
    }
}
