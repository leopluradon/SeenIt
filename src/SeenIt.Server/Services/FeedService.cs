using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// RSS and JSON Feed versions of lists, the watchlist and history, for feed readers and automations.
/// Public lists need no key; everything else takes the owner's private key (the calendar token).
/// </summary>
public class FeedService(AppDbContext db, ListService lists, TrackingService tracking, ReleaseAlertService alerts)
{
    public sealed record FeedItem(string Id, string Title, string Url, DateTimeOffset Date, string? Text, string? Image);
    public sealed record Feed(string Title, string Description, string Url, IReadOnlyList<FeedItem> Items);

    public async Task<Feed?> ListAsync(Guid listId, string? key, CancellationToken ct)
    {
        var row = await db.Lists.AsNoTracking().Where(l => l.Id == listId).Select(l => new { l.UserId, l.IsPublic }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        if (!row.IsPublic && !await KeyMatchesAsync(row.UserId, key, ct)) return null;
        var list = await lists.GetAsync(listId, row.UserId, ct);
        if (list is null) return null;
        var site = alerts.BaseUrl();
        return new Feed($"{list.Name} · SeenIt", list.Description ?? $"A list by {list.OwnerUserName} on SeenIt", $"{site}lists/{list.Id}",
            list.Items.Select(i => new FeedItem($"{list.Id}:{i.Media.Type}:{i.Media.TmdbId}", Title(i.Media), site + Fmt(i.Media), i.AddedAt,
                i.Note ?? i.Media.Overview, Img.Poster(i.Media.PosterPath))).ToList());
    }

    public async Task<Feed?> WatchlistAsync(string key, CancellationToken ct)
    {
        if (await UserByKeyAsync(key, ct) is not { } user) return null;
        var site = alerts.BaseUrl();
        var items = await tracking.GetWatchlistAsync(user.Id, ct);
        return new Feed($"{user.UserName}'s watchlist · SeenIt", "Movies and shows to watch", $"{site}watchlist",
            items.Select(w => new FeedItem($"wl:{w.Media.Type}:{w.Media.TmdbId}", Title(w.Media), site + Fmt(w.Media), w.AddedAt, w.Media.Overview, Img.Poster(w.Media.PosterPath))).ToList());
    }

    public async Task<Feed?> HistoryAsync(string key, CancellationToken ct)
    {
        if (await UserByKeyAsync(key, ct) is not { } user) return null;
        var site = alerts.BaseUrl();
        var page = await tracking.GetHistoryAsync(user.Id, new HistoryQuery(PageSize: 50, TimeZone: user.TimeZone), ct);
        return new Feed($"{user.UserName}'s history · SeenIt", "Recently watched", $"{site}history",
            page.Items.Where(h => !WatchDates.IsUnknown(h.WatchedAt)).Select(h => new FeedItem($"play:{h.Id}",
                h.Type == MediaType.Episode ? $"{h.Title} S{h.SeasonNumber:00}E{h.EpisodeNumber:00}{(h.EpisodeTitle is { } et ? " · " + et : "")}" : h.Title,
                site + (h.Type == MediaType.Movie ? $"movie/{h.TmdbId}" : $"show/{h.TmdbId}/season/{h.SeasonNumber}/episode/{h.EpisodeNumber}"),
                h.WatchedAt, null, Img.Poster(h.PosterPath))).ToList());
    }

    private Task<AppUser?> UserByKeyAsync(string key, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(key) ? Task.FromResult<AppUser?>(null) : db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.CalendarToken == key, ct);

    private async Task<bool> KeyMatchesAsync(Guid userId, string? key, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(key) && await db.Users.AnyAsync(u => u.Id == userId && u.CalendarToken == key, ct);

    private static string Title(MediaSummary m) => m.Year is { } y ? $"{m.Title} ({y})" : m.Title;
    private static string Fmt(MediaSummary m) => m.Type == MediaType.Movie ? $"movie/{m.TmdbId}" : $"show/{m.TmdbId}";

    private static class Img
    {
        public static string? Poster(string? path) => path is null ? null : $"https://image.tmdb.org/t/p/w342{path}";
    }

    public static string Rss(Feed f)
    {
        XNamespace media = "http://search.yahoo.com/mrss/";
        var channel = new XElement("channel",
            new XElement("title", f.Title), new XElement("link", f.Url), new XElement("description", f.Description),
            new XElement("generator", "SeenIt"),
            f.Items.Select(i => new XElement("item",
                new XElement("title", i.Title), new XElement("link", i.Url),
                new XElement("guid", new XAttribute("isPermaLink", "false"), i.Id),
                new XElement("pubDate", i.Date.ToUniversalTime().ToString("r")),
                i.Text is { } t ? new XElement("description", t) : null,
                i.Image is { } img ? new XElement(media + "thumbnail", new XAttribute("url", img)) : null)));
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "media", media), channel));
        return doc.Declaration + "\n" + doc.Root;
    }

    public static string JsonFeed(Feed f, string feedUrl) => JsonSerializer.Serialize(new
    {
        version = "https://jsonfeed.org/version/1.1",
        title = f.Title,
        home_page_url = f.Url,
        feed_url = feedUrl,
        description = f.Description,
        items = f.Items.Select(i => new { id = i.Id, url = i.Url, title = i.Title, content_text = i.Text ?? i.Title, image = i.Image, date_published = i.Date.ToUniversalTime() }),
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, WriteIndented = true });
}

/// <summary>Emails each member who asked for it their full export on the 1st of every month (their time zone).</summary>
public class MonthlyBackupWorker(IServiceScopeFactory scopes, ILogger<MonthlyBackupWorker> log) : BackgroundService
{
    private const int MaxAttachmentBytes = 20 * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var email = scope.ServiceProvider.GetRequiredService<EmailService>();
                if (email.Enabled)
                {
                    foreach (var user in await db.Users.Where(u => u.MonthlyBackup).ToListAsync(stoppingToken))
                    {
                        var period = PeriodStart(user);
                        if (user.LastMonthlyBackup >= period || user.Email is not { Length: > 0 } to) continue;
                        await SendAsync(scope.ServiceProvider, db, email, user, to, period, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Monthly backup loop failed"); }
            await Task.Delay(TimeSpan.FromHours(3), stoppingToken);
        }
    }

    /// <summary>The first day of the user's current backup period: this month, or this week (from Monday), in their time zone.</summary>
    public static DateOnly PeriodStart(AppUser user)
    {
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ReviewService.Zone(user.TimeZone)).DateTime);
        return user.Prefs().BackupEvery == BackupEvery.Weekly
            ? local.AddDays(-(((int)local.DayOfWeek + 6) % 7))
            : new DateOnly(local.Year, local.Month, 1);
    }

    public static async Task SendAsync(IServiceProvider services, AppDbContext db, EmailService email, AppUser user, string to, DateOnly period, CancellationToken ct)
    {
        var prefs = user.Prefs();
        var weekly = prefs.BackupEvery == BackupEvery.Weekly;
        var zip = await services.GetRequiredService<ExportService>().BuildAsync(user.Id, ct, prefs.BackupFormat);
        var site = services.GetRequiredService<ReleaseAlertService>().BaseUrl();
        var name = weekly ? $"seenit-backup-{period:yyyy-MM-dd}.zip" : $"seenit-backup-{period:yyyy-MM}.zip";
        var label = weekly ? $"the week of {period:MMMM d, yyyy}" : $"{period:MMMM yyyy}";
        var kinds = prefs.BackupFormat switch { ExportFormat.Json => "JSON", ExportFormat.Csv => "CSV", _ => "JSON and CSV" };
        var big = zip.Length > MaxAttachmentBytes;
        var note = big
            ? $"Your backup is {zip.Length / 1024 / 1024} MB, too big to attach. Download it any time from Settings → Export: {site}settings#export"
            : $"Your SeenIt backup for {label} is attached ({Math.Max(1, zip.Length / 1024):N0} KB): history, ratings, watchlist, lists and settings as {kinds}.";
        email.Queue(new EmailMessage(to, $"Your SeenIt backup for {label}",
            $"{note}\n\nTurn these off in Settings → Export: {site}settings#export\n",
            $"<p>{WebUtility.HtmlEncode(note)}</p><p style=\"color:#888;font-size:12px\">Turn these off in <a href=\"{site}settings#export\">Settings → Export</a>.</p>",
            big ? null : [new EmailAttachment(name, "application/zip", zip)]));
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastMonthlyBackup, period), ct);
    }
}
