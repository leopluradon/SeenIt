using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Import;

namespace SeenIt.Server.Services;

/// <summary>One diary entry (or review) from a Letterboxd RSS feed.</summary>
public record LetterboxdFeedEntry(string Guid, int TmdbId, string Title, int? Year, DateOnly? Watched, double? Stars, bool Liked, DateTimeOffset Published);

/// <summary>
/// Letterboxd has no API for this, but every member's RSS feed (letterboxd.com/{name}/rss/) carries
/// their latest ~50 diary entries with TMDB ids, watch dates, ratings and likes. Polling it brings new
/// logs in without re-importing the export.
/// </summary>
public class LetterboxdSyncService(AppDbContext db, IHttpClientFactory http, ImportRunner runner, ActivityService activity, ILogger<LetterboxdSyncService> log)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(3);
    private const int RememberedEntries = 1000;
    private static readonly XNamespace Lb = "https://letterboxd.com";
    private static readonly XNamespace Tmdb = "https://themoviedb.org";

    public static bool ValidUsername(string? name) => name is not null && Regex.IsMatch(name, "^[A-Za-z0-9_]{1,40}$");

    public static List<LetterboxdFeedEntry> ParseFeed(string xml)
    {
        var doc = XDocument.Parse(xml);
        var entries = new List<LetterboxdFeedEntry>();
        foreach (var item in doc.Descendants("item"))
        {
            // Lists in the feed have no film.
            if (!int.TryParse(item.Element(Tmdb + "movieId")?.Value, out var tmdbId) || tmdbId <= 0) continue;
            var guid = item.Element("guid")?.Value?.Trim();
            if (string.IsNullOrEmpty(guid)) continue;
            DateOnly? watched = DateOnly.TryParseExact(item.Element(Lb + "watchedDate")?.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            double? stars = double.TryParse(item.Element(Lb + "memberRating")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
            var published = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? p : DateTimeOffset.UtcNow;
            entries.Add(new LetterboxdFeedEntry(guid, tmdbId,
                item.Element(Lb + "filmTitle")?.Value ?? item.Element("title")?.Value ?? $"#{tmdbId}",
                int.TryParse(item.Element(Lb + "filmYear")?.Value, out var y) ? y : null,
                watched, stars, item.Element(Lb + "memberLike")?.Value == "Yes", published));
        }
        return entries;
    }

    /// <summary>Fetches the feed. Throws TrackingException with a readable reason when it can't.</summary>
    public async Task<List<LetterboxdFeedEntry>> FetchAsync(string username, CancellationToken ct)
    {
        using var resp = await http.CreateClient("letterboxd").GetAsync($"https://letterboxd.com/{Uri.EscapeDataString(username)}/rss/", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new TrackingException($"There's no Letterboxd member called {username}.");
        if (!resp.IsSuccessStatusCode) throw new TrackingException($"Letterboxd answered {(int)resp.StatusCode}. Try again later.");
        var xml = await resp.Content.ReadAsStringAsync(ct);
        try { return ParseFeed(xml); }
        catch (System.Xml.XmlException) { throw new TrackingException("Letterboxd sent something that isn't a feed. Try again later."); }
    }

    /// <summary>Brings in entries not seen before. Returns how many plays, ratings and likes were added.</summary>
    public async Task<string> SyncAsync(LetterboxdSync sync, CancellationToken ct)
    {
        sync.LastCheckedAt = DateTimeOffset.UtcNow;
        List<LetterboxdFeedEntry> entries;
        try { entries = await FetchAsync(sync.Username, ct); }
        catch (Exception ex) when (ex is TrackingException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            sync.LastFailed = true;
            sync.LastResult = ex is TrackingException ? ex.Message : "Couldn't reach Letterboxd.";
            await db.SaveChangesAsync(ct);
            return sync.LastResult;
        }

        var seen = sync.SeenGuids.ToHashSet();
        var fresh = entries.Where(e => !seen.Contains(e.Guid)).OrderBy(e => e.Published).ToList();
        sync.LastFailed = false;
        if (fresh.Count == 0)
        {
            sync.LastResult = "Up to date.";
            await db.SaveChangesAsync(ct);
            return sync.LastResult;
        }

        var parsed = new ParsedImport();
        var liked = new List<ImportListItem>();
        foreach (var e in fresh)
        {
            var film = new ImportRef(MediaType.Movie, e.TmdbId, null, e.Title, e.Year);
            if (e.Watched is { } day)
                parsed.History.Add(new ImportWatch(film, null, null, new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)));
            if (e.Stars is { } stars)
                parsed.Ratings.Add(new ImportRating(film, null, null, Math.Clamp((int)Math.Round(stars * 2, MidpointRounding.AwayFromZero), 1, 10), e.Published));
            if (e.Liked) liked.Add(new ImportListItem(film, e.Published));
        }
        if (liked.Count > 0) parsed.Lists.Add(new ImportList("Liked on Letterboxd", "Films you hearted on Letterboxd.", liked));

        var job = new ImportJob { UserId = sync.UserId, Kind = ImportKind.Letterboxd, FileName = "RSS", Parts = ImportParts.All };
        if (parsed.Total > 0)
        {
            try { await runner.RunAsync(job, parsed, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Letterboxd sync for {User} failed", sync.Username);
                sync.LastFailed = true;
                sync.LastResult = "Something went wrong adding the new entries. It'll try again later.";
                await db.SaveChangesAsync(ct);
                return sync.LastResult;
            }
        }

        sync.SeenGuids = [.. sync.SeenGuids.Concat(fresh.Select(f => f.Guid)).TakeLast(RememberedEntries)];
        sync.LastResult = job.Imported == 0 ? "Up to date." : $"Added {job.Imported:N0} new entr{(job.Imported == 1 ? "y" : "ies")}.";
        if (job.Imported > 0)
        {
            sync.LastAddedAt = DateTimeOffset.UtcNow;
            await activity.LogAsync(sync.UserId, ActivityKind.LetterboxdSync, detail: $"{sync.Username}: {job.Imported} added, {job.Skipped} already here",
                source: WatchSource.LetterboxdImport, userName: null);
        }
        await db.SaveChangesAsync(ct);
        return sync.LastResult;
    }

    public static LetterboxdSyncDto ToDto(LetterboxdSync? s) => s is null
        ? new LetterboxdSyncDto(null, null, null, null, false)
        : new LetterboxdSyncDto(s.Username, s.LastCheckedAt, s.LastAddedAt, s.LastResult, s.LastFailed);
}

/// <summary>Checks everyone's Letterboxd feed every few hours.</summary>
public class LetterboxdSyncWorker(IServiceScopeFactory scopes, ILogger<LetterboxdSyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var due = DateTimeOffset.UtcNow - LetterboxdSyncService.Interval;
                var ids = await db.LetterboxdSyncs.Where(s => s.LastCheckedAt == null || s.LastCheckedAt < due)
                    .OrderBy(s => s.LastCheckedAt).Select(s => s.UserId).Take(20).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    // A fresh scope each time: the import runner caches matches per run.
                    await using var userScope = scopes.CreateAsyncScope();
                    var udb = userScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    if (await udb.LetterboxdSyncs.FirstOrDefaultAsync(s => s.UserId == id, stoppingToken) is not { } sync) continue;
                    await userScope.ServiceProvider.GetRequiredService<LetterboxdSyncService>().SyncAsync(sync, stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);   // be gentle with Letterboxd
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Letterboxd sync loop failed"); }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
