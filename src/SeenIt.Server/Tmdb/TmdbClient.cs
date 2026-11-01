using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace SeenIt.Server.Tmdb;

public class TmdbOptions
{
    /// <summary>TMDB v4 API read access token (used as a bearer token against the v3 API).</summary>
    public string ReadAccessToken { get; set; } = "";
    public string Language { get; set; } = "en-US";
}

/// <summary>
/// Thin TMDB v3 client. Limits concurrency, retries 429/5xx with backoff and caches
/// responses briefly in memory so popular pages don't hammer the API.
/// </summary>
public class TmdbClient
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly SemaphoreSlim Gate = new(8);

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TmdbClient> _log;
    private readonly string _language;

    public TmdbClient(HttpClient http, IMemoryCache cache, Microsoft.Extensions.Options.IOptions<TmdbOptions> options, ILogger<TmdbClient> log)
    {
        _http = http;
        _cache = cache;
        _log = log;
        _language = options.Value.Language;
        _http.BaseAddress ??= new Uri("https://api.themoviedb.org/3/");
        if (!string.IsNullOrWhiteSpace(options.Value.ReadAccessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ReadAccessToken);
    }

    public Task<TmdbMovie?> GetMovieAsync(int id, bool full, CancellationToken ct = default) =>
        GetAsync<TmdbMovie>($"movie/{id}?append_to_response=credits" + (full ? ",videos,recommendations" : ""),
            full ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(10), ct);

    public Task<TmdbShow?> GetShowAsync(int id, bool full, CancellationToken ct = default) =>
        GetAsync<TmdbShow>($"tv/{id}?append_to_response=aggregate_credits" + (full ? ",videos,recommendations" : ""),
            full ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(10), ct);

    /// <summary>Show plus up to 20 seasons in a single request (TMDB's append limit).</summary>
    public Task<TmdbShow?> GetShowWithSeasonsAsync(int id, IEnumerable<int> seasons, CancellationToken ct = default) =>
        GetAsync<TmdbShow>($"tv/{id}?append_to_response=" + string.Join(',', seasons.Select(s => $"season/{s}")), null, ct);

    public Task<TmdbSeason?> GetSeasonAsync(int showId, int season, CancellationToken ct = default) =>
        GetAsync<TmdbSeason>($"tv/{showId}/season/{season}", TimeSpan.FromMinutes(30), ct);

    public Task<TmdbEpisodeDetail?> GetEpisodeAsync(int showId, int season, int episode, CancellationToken ct = default) =>
        GetAsync<TmdbEpisodeDetail>($"tv/{showId}/season/{season}/episode/{episode}?append_to_response=credits", TimeSpan.FromMinutes(30), ct);

    /// <summary>Everyone who appeared in a season, with how many episodes each.</summary>
    public Task<TmdbCredits?> GetSeasonCreditsAsync(int showId, int season, CancellationToken ct = default) =>
        GetAsync<TmdbCredits>($"tv/{showId}/season/{season}/aggregate_credits", TimeSpan.FromHours(6), ct);

    public Task<TmdbPerson?> GetPersonAsync(int id, CancellationToken ct = default) =>
        GetAsync<TmdbPerson>($"person/{id}?append_to_response=combined_credits,external_ids", TimeSpan.FromHours(1), ct);

    public Task<TmdbCreditDetail?> GetCreditAsync(string creditId, CancellationToken ct = default) =>
        GetAsync<TmdbCreditDetail>($"credit/{Uri.EscapeDataString(creditId)}", TimeSpan.FromHours(12), ct);

    public Task<TmdbPage<TmdbListItem>?> SearchMultiAsync(string query, int page, CancellationToken ct = default) =>
        GetAsync<TmdbPage<TmdbListItem>>($"search/multi?query={Uri.EscapeDataString(query)}&page={page}&include_adult=false", TimeSpan.FromMinutes(30), ct);

    public Task<TmdbPage<TmdbListItem>?> SearchMovieAsync(string query, int? year, CancellationToken ct = default) =>
        GetAsync<TmdbPage<TmdbListItem>>($"search/movie?query={Uri.EscapeDataString(query)}&include_adult=false" + (year is { } y ? $"&primary_release_year={y}" : ""), TimeSpan.FromHours(6), ct);

    public Task<TmdbPage<TmdbListItem>?> SearchShowAsync(string query, int? year, CancellationToken ct = default) =>
        GetAsync<TmdbPage<TmdbListItem>>($"search/tv?query={Uri.EscapeDataString(query)}&include_adult=false" + (year is { } y ? $"&first_air_date_year={y}" : ""), TimeSpan.FromHours(6), ct);

    /// <summary>kind: movie|tv, list: trending/day, trending/week, popular, top_rated, upcoming, now_playing, on_the_air, airing_today.</summary>
    public Task<TmdbPage<TmdbListItem>?> GetListAsync(string kind, string list, int page, CancellationToken ct = default)
    {
        var path = list.StartsWith("trending/")
            ? $"trending/{kind}/{list["trending/".Length..]}?page={page}"
            : $"{kind}/{list}?page={page}";
        return GetAsync<TmdbPage<TmdbListItem>>(path, TimeSpan.FromHours(1), ct);
    }

    /// <summary>kind: movie|tv. Streaming/rent/buy offers per country (data from JustWatch).</summary>
    public Task<TmdbProvidersResult?> GetWatchProvidersAsync(string kind, int id, CancellationToken ct = default) =>
        GetAsync<TmdbProvidersResult>($"{kind}/{id}/watch/providers", TimeSpan.FromHours(6), ct);

    public Task<TmdbRegions?> GetProviderRegionsAsync(CancellationToken ct = default) =>
        GetAsync<TmdbRegions>("watch/providers/regions", TimeSpan.FromDays(1), ct);

    /// <summary>source: imdb_id | tvdb_id.</summary>
    public Task<TmdbFindResult?> FindAsync(string externalId, string source, CancellationToken ct = default) =>
        GetAsync<TmdbFindResult>($"find/{Uri.EscapeDataString(externalId)}?external_source={source}", TimeSpan.FromHours(6), ct);

    private async Task<T?> GetAsync<T>(string path, TimeSpan? cacheFor, CancellationToken ct) where T : class
    {
        path += (path.Contains('?') ? '&' : '?') + "language=" + _language;
        if (cacheFor is not null && _cache.TryGetValue(path, out T? cached))
            return cached;

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            await Gate.WaitAsync(ct);
            try { resp = await _http.GetAsync(path, ct); }
            finally { Gate.Release(); }

            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return null;

                var transient = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                if (transient && attempt < 4)
                {
                    var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                    _log.LogInformation("TMDB {Status} on {Path}; retrying in {Delay}", (int)resp.StatusCode, path, delay);
                    await Task.Delay(delay, ct);
                    continue;
                }

                resp.EnsureSuccessStatusCode();
                var value = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
                if (value is not null && cacheFor is not null)
                    _cache.Set(path, value, cacheFor.Value);
                return value;
            }
        }
    }
}
