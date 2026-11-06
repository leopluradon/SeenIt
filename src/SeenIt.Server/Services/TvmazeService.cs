using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>
/// Exact air times and channels from TVmaze's free API (TMDB only knows the day). A show is matched
/// once by its TVDB or IMDb id, then its episodes' air times are copied onto the cached episodes.
/// </summary>
public class TvmazeService(AppDbContext db, TmdbClient tmdb, IHttpClientFactory http, ILogger<TvmazeService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record TvmazeShow(int Id, TvmazeChannel? Network, TvmazeChannel? WebChannel);
    private sealed record TvmazeChannel(string? Name);
    private sealed record TvmazeEpisode(int? Season, int? Number, string? Airstamp, string? Type);

    /// <summary>Refreshes one show. Returns how many episodes got a time.</summary>
    public async Task<int> RefreshAsync(ShowMeta show, CancellationToken ct)
    {
        var client = http.CreateClient("tvmaze");
        show.TvmazeCheckedAt = DateTimeOffset.UtcNow;
        if (show.TvmazeId is null)
        {
            var ids = await tmdb.GetExternalIdsAsync("tv", show.TmdbId, ct);
            TvmazeShow? found = null;
            if (ids?.TvdbId is { } tvdb) found = await GetAsync<TvmazeShow>(client, $"lookup/shows?thetvdb={tvdb}", ct);
            if (found is null && ids?.ImdbId is { Length: > 0 } imdb) found = await GetAsync<TvmazeShow>(client, $"lookup/shows?imdb={imdb}", ct);
            if (found is null) { await db.SaveChangesAsync(ct); return 0; }   // TVmaze doesn't know it; try again next time round
            show.TvmazeId = found.Id;
            show.AirNetwork = Trim(found.Network?.Name ?? found.WebChannel?.Name);
        }

        var episodes = await GetAsync<List<TvmazeEpisode>>(client, $"shows/{show.TvmazeId}/episodes", ct) ?? [];
        var times = episodes.Where(e => e.Season is not null && e.Number is not null && e.Type is null or "regular")
            .Select(e => (e.Season!.Value, e.Number!.Value, At: DateTimeOffset.TryParse(e.Airstamp, out var at) ? at.ToUniversalTime() : (DateTimeOffset?)null))
            .Where(e => e.At is not null)
            .GroupBy(e => (e.Item1, e.Item2)).ToDictionary(g => g.Key, g => g.First().At);
        var cached = await db.Episodes.Where(e => e.ShowTmdbId == show.TmdbId && e.SeasonNumber > 0).ToListAsync(ct);
        var set = 0;
        foreach (var e in cached)
        {
            if (!times.TryGetValue((e.SeasonNumber, e.EpisodeNumber), out var at)) continue;
            // TVmaze sometimes lists an episode on a different day than TMDB (time zones, schedule changes); trust it only near TMDB's day.
            if (e.AirDate is { } day && at is { } a && Math.Abs(DateOnly.FromDateTime(a.UtcDateTime).DayNumber - day.DayNumber) > 2) continue;
            if (e.AirStamp != at) { e.AirStamp = at; set++; }
        }
        await db.SaveChangesAsync(ct);
        return set;
    }

    private async Task<T?> GetAsync<T>(HttpClient client, string path, CancellationToken ct) where T : class
    {
        using var resp = await client.GetAsync(path, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.StatusCode == (HttpStatusCode)429)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);   // TVmaze allows ~20 calls per 10 seconds
            return null;
        }
        if (!resp.IsSuccessStatusCode) { log.LogInformation("TVmaze {Path}: {Status}", path, (int)resp.StatusCode); return null; }
        return await JsonSerializer.DeserializeAsync<T>(await resp.Content.ReadAsStreamAsync(ct), Json, ct);
    }

    private static string? Trim(string? s) => s is null ? null : s.Length > 100 ? s[..100] : s;
}

/// <summary>Every few hours: air times for shows with episodes coming up that anyone follows.</summary>
public class TvmazeWorker(IServiceScopeFactory scopes, ILogger<TvmazeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var tvmaze = scope.ServiceProvider.GetRequiredService<TvmazeService>();
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var soon = today.AddDays(31);
                var followed = db.WatchEvents.Where(w => w.Type == MediaType.Episode).Select(w => w.TmdbId)
                    .Union(db.Watchlist.Where(w => w.Type == MediaType.Show).Select(w => w.TmdbId));
                var upcoming = db.Episodes.Where(e => e.AirDate >= today.AddDays(-1) && e.AirDate <= soon).Select(e => e.ShowTmdbId);
                var stale = DateTimeOffset.UtcNow.AddHours(-12);
                var shows = await db.Shows.Where(s => followed.Contains(s.TmdbId) && upcoming.Contains(s.TmdbId)
                        && (s.TvmazeCheckedAt == null || s.TvmazeCheckedAt < stale))
                    .OrderBy(s => s.TvmazeCheckedAt).Take(150).ToListAsync(stoppingToken);
                var timed = 0;
                foreach (var show in shows)
                {
                    try { timed += await tvmaze.RefreshAsync(show, stoppingToken); }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
                    { log.LogInformation("TVmaze refresh for {Show} failed: {Message}", show.Name, ex.Message); }
                    await Task.Delay(TimeSpan.FromMilliseconds(1100), stoppingToken);   // well under TVmaze's rate limit
                }
                if (shows.Count > 0) log.LogInformation("TVmaze: checked {Shows} shows, {Episodes} episode times updated", shows.Count, timed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "TVmaze loop failed"); }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
