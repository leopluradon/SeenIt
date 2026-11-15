using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>
/// Exact air times, channels and earlier release dates from TVmaze's free API (TMDB only knows the day, and
/// dates a show by its TV airing even when it streams first). A show is matched once by its TVDB or IMDb id,
/// then its episodes' dates and times are copied onto the cached episodes.
/// </summary>
public class TvmazeService(AppDbContext db, TmdbClient tmdb, IHttpClientFactory http, ILogger<TvmazeService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record TvmazeShow(int Id, TvmazeChannel? Network, TvmazeChannel? WebChannel);
    private sealed record TvmazeChannel(string? Name);
    public sealed record TvmazeEpisode(int? Season, int? Number, string? Airdate, string? Airtime, string? Airstamp, string? Type);

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
        var cached = await db.Episodes.Where(e => e.ShowTmdbId == show.TmdbId && e.SeasonNumber > 0).ToListAsync(ct);
        var set = Apply(cached, episodes);
        await db.SaveChangesAsync(ct);
        return set;
    }

    /// <summary>
    /// Copies TVmaze's dates and times onto the cached episodes. Returns how many changed. The episode's date
    /// becomes TVmaze's when that's a few days earlier than TMDB's (AirDates.Effective); a time is only kept
    /// when TVmaze has one (a blank time comes back as a made-up noon UTC) and it falls within a day of that date.
    /// </summary>
    public static int Apply(List<EpisodeMeta> cached, IEnumerable<TvmazeEpisode> episodes)
    {
        var byKey = episodes.Where(e => e.Season is not null && e.Number is not null && e.Type is null or "regular")
            .GroupBy(e => (e.Season!.Value, e.Number!.Value)).ToDictionary(g => g.Key, g => g.First());
        var set = 0;
        foreach (var e in cached)
        {
            if (!byKey.TryGetValue((e.SeasonNumber, e.EpisodeNumber), out var tv)) continue;
            var day = DateOnly.TryParseExact(tv.Airdate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
            var tmdbDay = e.TmdbAirDate ?? e.AirDate;
            var airs = AirDates.Effective(tmdbDay, day);
            if (e.TvmazeAirDate != day || e.AirDate != airs || e.TmdbAirDate != tmdbDay) set++;
            e.TmdbAirDate = tmdbDay;
            e.TvmazeAirDate = day;
            e.AirDate = airs;

            DateTimeOffset? at = !string.IsNullOrWhiteSpace(tv.Airtime) && DateTimeOffset.TryParse(tv.Airstamp, out var stamp) ? stamp.ToUniversalTime() : null;
            // A day either way is just the time zone (10 PM Eastern is 3 AM UTC); more means the sources disagree, so show no time.
            if (at is { } a && e.AirDate is { } on && Math.Abs(DateOnly.FromDateTime(a.UtcDateTime).DayNumber - on.DayNumber) > 1) at = null;
            if (e.AirStamp != at) { e.AirStamp = at; set++; }
        }
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
