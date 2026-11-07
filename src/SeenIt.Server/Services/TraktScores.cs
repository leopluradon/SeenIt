using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SeenIt.Contracts;

namespace SeenIt.Server.Services;

public class TraktOptions
{
    /// <summary>Client ID of a Trakt API app (trakt.tv/oauth/applications). Only public data is read, so no secret.</summary>
    public string? ClientId { get; set; }
}

/// <summary>
/// Trakt's ratings for single seasons and episodes, which MDBList (whole titles only) doesn't have.
/// Shown as a percentage, the way Trakt shows them. Kept in memory for a day.
/// </summary>
public class TraktScores(IHttpClientFactory http, IMemoryCache cache, IOptions<TraktOptions> options, ILogger<TraktScores> log)
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);
    private static readonly TimeSpan KeepMissing = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.ClientId);

    public Task<ExternalScore?> SeasonAsync(int showId, int season, CancellationToken ct) =>
        RatingAsync(showId, $"seasons/{season}", $"seasons/{season}", ct);

    public Task<ExternalScore?> EpisodeAsync(int showId, int season, int episode, CancellationToken ct) =>
        RatingAsync(showId, $"seasons/{season}/episodes/{episode}", $"seasons/{season}/episodes/{episode}", ct);

    private async Task<ExternalScore?> RatingAsync(int showId, string apiPath, string webPath, CancellationToken ct)
    {
        if (!Enabled) return null;
        var key = $"trakt:{showId}:{apiPath}";
        if (cache.TryGetValue(key, out ExternalScore? hit)) return hit;
        try
        {
            if (await SlugAsync(showId, ct) is not { } slug) return null;
            using var resp = await Client().GetAsync($"shows/{slug}/{apiPath}/ratings", ct);
            ExternalScore? score = null;
            if (resp.IsSuccessStatusCode && Parse(await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct)) is { Votes: > 0, Rating: > 0 } r)
                score = new ExternalScore("trakt", "Trakt", Math.Round(r.Rating * 10), 100, true, r.Votes, $"https://trakt.tv/shows/{slug}/{webPath}");
            else if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                log.LogWarning("Trakt answered {Status} for {Path}", (int)resp.StatusCode, apiPath);
                return null;   // try again next time
            }
            cache.Set(key, score, score is null ? KeepMissing : Keep);
            return score;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Trakt lookup for show {Show} {Path} failed: {Message}", showId, apiPath, ex.Message);
            return null;
        }
    }

    /// <summary>Trakt's slug for a TMDB show id ("breaking-bad").</summary>
    private async Task<string?> SlugAsync(int showId, CancellationToken ct)
    {
        var key = $"trakt-slug:{showId}";
        if (cache.TryGetValue(key, out string? slug)) return slug;
        var found = await Client().GetFromJsonAsync<List<SearchHit>>($"search/tmdb/{showId}?type=show", Json, ct);
        slug = found?.FirstOrDefault(h => h.Show?.Ids?.Slug is not null)?.Show!.Ids!.Slug;
        cache.Set(key, slug, slug is null ? KeepMissing : TimeSpan.FromDays(7));
        return slug;
    }

    private HttpClient Client()
    {
        var c = http.CreateClient("trakt");
        c.DefaultRequestHeaders.Remove("trakt-api-key");
        c.DefaultRequestHeaders.Add("trakt-api-key", options.Value.ClientId);
        return c;
    }


    /// <summary>Episodes answer {"rating", "votes"}; seasons nest Trakt's own under "trakt" (next to TMDB's, IMDb's...).</summary>
    public static (double Rating, int Votes)? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("trakt", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
        return root.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number
            && root.TryGetProperty("votes", out var v) && v.ValueKind == JsonValueKind.Number
            ? (r.GetDouble(), v.GetInt32()) : null;
    }
    private sealed record SearchHit(SearchShow? Show);
    private sealed record SearchShow(SearchIds? Ids);
    private sealed record SearchIds(int? Trakt, string? Slug);
}
