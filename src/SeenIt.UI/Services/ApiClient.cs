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
public class ApiClient(HttpClient http, TokenManager tokens)
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
    public Task DeleteAccountAsync(DeleteAccountRequest r) => SendAsync(HttpMethod.Post, "api/auth/delete", r);

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

    public Task WatchAsync(WatchRequest r) => SendAsync(HttpMethod.Post, "api/watch", r);
    public Task BulkWatchAsync(BulkWatchRequest r) => SendAsync(HttpMethod.Post, "api/watch/bulk", r);
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
    public Task AddToWatchlistAsync(MediaType type, int id) => SendAsync(HttpMethod.Post, "api/watchlist", new WatchlistRequest(type, id));
    public Task RemoveFromWatchlistAsync(MediaType type, int id) => SendAsync(HttpMethod.Delete, $"api/watchlist?type={type}&tmdbId={id}");

    public Task<List<RatingEntry>> RatingsAsync(MediaType? type = null) => GetAsync<List<RatingEntry>>("api/ratings" + (type is null ? "" : $"?type={type}"));
    public Task RateAsync(RatingRequest r) => SendAsync(HttpMethod.Post, "api/ratings", r);
    public Task UnrateAsync(MediaType type, int id, int? season = null, int? episode = null) =>
        SendAsync(HttpMethod.Delete, $"api/ratings?type={type}&tmdbId={id}" + (season is null ? "" : $"&season={season}") + (episode is null ? "" : $"&episode={episode}"));

    public Task<List<ShowProgressDto>> ProgressAsync() => GetAsync<List<ShowProgressDto>>("api/progress");
    public Task<List<CalendarEntry>> CalendarAsync(DateOnly from, int days) => GetAsync<List<CalendarEntry>>($"api/calendar?from={from:yyyy-MM-dd}&days={days}");
    public Task<DashboardDto> DashboardAsync() => GetAsync<DashboardDto>("api/dashboard");
    public Task<StatsDto> StatsAsync() => GetAsync<StatsDto>("api/stats");
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
    public async Task UploadImportAsync(ImportKind kind, string fileName, Stream content)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/imports/{kind}") { Content = form }, auth: true);
        await EnsureOkAsync(resp);
    }

    public Uri BaseAddress => http.BaseAddress!;

    // ---------- Plumbing ----------

    private record IdResult(Guid Id);

    private async Task<T> GetAsync<T>(string url) => (await SendAsync<T>(HttpMethod.Get, url))!;

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var resp = await SendRawAsync(() => Build(method, url, body), auth: true);
        await EnsureOkAsync(resp);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? body = null, bool auth = true)
    {
        using var resp = await SendRawAsync(() => Build(method, url, body), auth);
        await EnsureOkAsync(resp);
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
        catch (HttpRequestException) { throw new ApiException("Can't reach the SeenIt server. Check your connection.", 0); }

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
