using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// "Out today" alerts for episodes of shows you watch (or have on your watchlist) and watchlist movies,
/// shown under the bell and, if you ask for it, emailed once a day. Also the private iCal feed.
/// </summary>
public class ReleaseAlertService(AppDbContext db, TrackingService tracking, EmailService email, IConfiguration config, ILogger<ReleaseAlertService> log)
{
    private const int EmailHour = 8;   // local time the daily digest goes out at the earliest

    /// <summary>The kinds this person wants in their alerts list (Settings → Alerts); push-only and off kinds stay out of it.</summary>
    private async Task<List<AlertKind>> ListedKindsAsync(Guid userId, CancellationToken ct)
    {
        var prefs = UserPrefsStore.Read(await db.Users.Where(u => u.Id == userId).Select(u => u.PrefsJson).FirstOrDefaultAsync(ct));
        return Enum.GetValues<AlertKind>().Where(prefs.ListsAlert).ToList();
    }

    public async Task<NotificationsDto> ListAsync(Guid userId, CancellationToken ct)
    {
        var kinds = await ListedKindsAsync(userId, ct);
        var rows = await db.Notifications.AsNoTracking().Where(n => n.UserId == userId && kinds.Contains(n.Kind))
            .OrderByDescending(n => n.Day).ThenByDescending(n => n.CreatedAt).Take(60).ToListAsync(ct);
        var unread = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null && kinds.Contains(n.Kind), ct);
        return new NotificationsDto(rows.Select(n => new NotificationDto(n.Id, n.Type, n.TmdbId, n.SeasonNumber, n.EpisodeNumber,
            n.Title, n.Detail, n.PosterPath, n.Day, n.ReadAt is not null, n.RecommendationId is not null, n.WatchedRecommendationId is not null)).ToList(), unread);
    }

    public async Task<int> UnreadAsync(Guid userId, CancellationToken ct)
    {
        var kinds = await ListedKindsAsync(userId, ct);
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null && kinds.Contains(n.Kind), ct);
    }

    public Task MarkAllReadAsync(Guid userId, CancellationToken ct) =>
        db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);

    /// <summary>Adds today's releases for one user (in their time zone) and emails the digest if due. Returns how many were new.</summary>
    public async Task<int> ScanAsync(AppUser user, CancellationToken ct)
    {
        var tz = ReviewService.Zone(user.TimeZone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var today = DateOnly.FromDateTime(now.DateTime);

        var releases = (await tracking.GetCalendarAsync(user.Id, today, 1, ct)).Where(e => !e.Watched).ToList();
        var existing = (await db.Notifications.Where(n => n.UserId == user.Id && n.RecommendationId == null && n.WatchedRecommendationId == null && n.Day >= today.AddDays(-1))
                .Select(n => new { n.Type, n.TmdbId, n.SeasonNumber, n.EpisodeNumber }).ToListAsync(ct))
            .Select(n => (n.Type, n.TmdbId, n.SeasonNumber, n.EpisodeNumber)).ToHashSet();
        var added = 0;
        foreach (var r in releases)
        {
            if (!existing.Add((r.Type, r.TmdbId, r.SeasonNumber, r.EpisodeNumber))) continue;
            // An episode that already had an alert on an earlier day (date moved) keeps that one.
            if (await db.Notifications.AnyAsync(n => n.UserId == user.Id && n.RecommendationId == null && n.WatchedRecommendationId == null && n.Type == r.Type && n.TmdbId == r.TmdbId
                                                     && n.SeasonNumber == r.SeasonNumber && n.EpisodeNumber == r.EpisodeNumber, ct)) continue;
            db.Notifications.Add(new Notification
            {
                UserId = user.Id, Type = r.Type, TmdbId = r.TmdbId, SeasonNumber = r.SeasonNumber, EpisodeNumber = r.EpisodeNumber,
                Title = Trim(r.Title, 300), PosterPath = r.PosterPath, Day = r.Date,
                Kind = r.Via is not null ? AlertKind.Person : r.Type == MediaType.Movie ? AlertKind.Movie : AlertKind.Episode,
                Detail = r.Type == MediaType.Episode ? Trim($"{Code(r.SeasonNumber, r.EpisodeNumber)}{(r.EpisodeTitle is { Length: > 0 } t ? " · " + t : "")}", 400)
                    : Trim((r.Type == MediaType.Show ? "New series out today" : "Out today") + (r.Via is { } via ? " · " + via : ""), 400),
            });
            added++;
        }
        if (added > 0)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); added = 0; }   // another scan got there first
        }

        if (user.ReleaseEmails && email.Enabled && now.Hour >= EmailHour && user.LastReleaseEmail != today && user.Email is { Length: > 0 } to)
        {
            // Kinds turned off in Settings → Alerts stay out of the email too.
            var prefs = UserPrefsStore.Read(user.PrefsJson);
            var emailed = Enum.GetValues<AlertKind>().Where(k => prefs.DeliveryFor(k) != AlertDelivery.Off).ToList();
            var todays = await db.Notifications.AsNoTracking().Where(n => n.UserId == user.Id && n.RecommendationId == null && n.WatchedRecommendationId == null && n.Day == today && emailed.Contains(n.Kind))
                .OrderBy(n => n.Title).ThenBy(n => n.SeasonNumber).ThenBy(n => n.EpisodeNumber).ToListAsync(ct);
            if (todays.Count > 0)
            {
                email.Queue(Digest(to, todays));
                await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastReleaseEmail, today), ct);
                log.LogInformation("Queued release digest for {User} ({Count} items)", user.UserName, todays.Count);
            }
        }
        return added;
    }

    private EmailMessage Digest(string to, List<Notification> items)
    {
        var site = BaseUrl();
        var subject = items.Count == 1 ? $"Out today: {items[0].Title}" : $"Out today: {items[0].Title} and {items.Count - 1} more";
        var text = new StringBuilder("Out today on things you follow:\n\n");
        var html = new StringBuilder("<p>Out today on things you follow:</p><ul>");
        foreach (var n in items)
        {
            var link = site + Href(n);
            text.Append($"• {n.Title}{(n.Detail is { } d && n.Type == MediaType.Episode ? " — " + d : "")}\n  {link}\n");
            html.Append($"<li><a href=\"{WebUtility.HtmlEncode(link)}\"><b>{WebUtility.HtmlEncode(n.Title)}</b></a>")
                .Append(n.Type == MediaType.Episode && n.Detail is { } dd ? $" — {WebUtility.HtmlEncode(dd)}" : n.Type == MediaType.Show ? " (new series)" : " (movie)").Append("</li>");
        }
        var settings = site + "settings#alerts";
        text.Append($"\nTurn these emails off in Settings → Alerts: {settings}\n");
        html.Append($"</ul><p style=\"color:#888;font-size:12px\">Turn these emails off in <a href=\"{settings}\">Settings → Alerts</a>.</p>");
        return new EmailMessage(to, subject, text.ToString(), html.ToString());
    }

    // ---------- Calendar feed ----------

    public async Task<string?> CalendarAsync(string token, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.CalendarToken == token, ct);
        if (user is null) return null;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ReviewService.Zone(user.TimeZone)).DateTime);
        // Two weeks back (so recent ones don't vanish at midnight) and about three months ahead.
        var past = await tracking.GetCalendarAsync(user.Id, today.AddDays(-14), 14, ct);
        var ahead = await tracking.GetCalendarAsync(user.Id, today, 92, ct);
        return Ics(past.Concat(ahead).ToList(), BaseUrl());
    }

    public static string Ics(IReadOnlyList<CalendarEntry> entries, string site)
    {
        var sb = new StringBuilder();
        void Line(string s)
        {
            // Lines longer than 75 bytes are folded (CRLF + space), as RFC 5545 asks.
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length <= 75) { sb.Append(s).Append("\r\n"); return; }
            var current = new StringBuilder();
            var size = 0;
            var first = true;
            foreach (var rune in s.EnumerateRunes())
            {
                var n = rune.Utf8SequenceLength;
                if (size + n > (first ? 75 : 74))
                {
                    sb.Append(first ? "" : " ").Append(current).Append("\r\n");
                    current.Clear(); size = 0; first = false;
                }
                current.Append(rune.ToString()); size += n;
            }
            sb.Append(first ? "" : " ").Append(current).Append("\r\n");
        }
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//SeenIt//Releases//EN");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("X-WR-CALNAME:SeenIt");
        Line("X-WR-CALDESC:New episodes and movie releases you follow on SeenIt");
        Line("REFRESH-INTERVAL;VALUE=DURATION:PT6H");
        Line("X-PUBLISHED-TTL:PT6H");
        foreach (var e in entries.DistinctBy(e => (e.Type, e.TmdbId, e.SeasonNumber, e.EpisodeNumber)))
        {
            var summary = e.Type switch
            {
                MediaType.Episode => $"{e.Title} {Code(e.SeasonNumber, e.EpisodeNumber)}",
                MediaType.Show => $"{e.Title} (new series)",
                _ => $"{e.Title} (movie)",
            };
            var href = e.Type switch
            {
                MediaType.Movie => $"movie/{e.TmdbId}",
                MediaType.Show => $"show/{e.TmdbId}",
                _ => $"show/{e.TmdbId}/season/{e.SeasonNumber}/episode/{e.EpisodeNumber}",
            };
            Line("BEGIN:VEVENT");
            Line($"UID:seenit-{e.Type.ToString().ToLowerInvariant()}-{e.TmdbId}-{e.SeasonNumber}-{e.EpisodeNumber}@seenit");
            Line($"DTSTAMP:{stamp}");
            if (e.AirsAt is { } at)
            {
                // A real air time (TVmaze): a timed event, an hour long.
                Line($"DTSTART:{at.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
                Line($"DTEND:{at.UtcDateTime.AddHours(1):yyyyMMdd'T'HHmmss'Z'}");
            }
            else
            {
                Line($"DTSTART;VALUE=DATE:{e.Date:yyyyMMdd}");
                Line($"DTEND;VALUE=DATE:{e.Date.AddDays(1):yyyyMMdd}");
            }
            if (e.Network is { Length: > 0 } net) Line($"LOCATION:{Esc(net)}");
            Line($"SUMMARY:{Esc(summary)}");
            if (e.Type == MediaType.Episode && !string.IsNullOrWhiteSpace(e.EpisodeTitle)) Line($"DESCRIPTION:{Esc(e.EpisodeTitle!)}");
            else if (e.Via is { } via) Line($"DESCRIPTION:{Esc(via)}");
            Line($"URL:{site}{href}");
            Line("TRANSP:TRANSPARENT");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    public string BaseUrl() => config["SeenIt:PublicUrl"] is { Length: > 0 } url ? url.TrimEnd('/') + "/" : "https://seenit.vohiyo.com/";

    private static string Code(int? s, int? e) => s == 0 ? $"Special {e}" : $"S{s:00}E{e:00}";
    private static string Href(Notification n) => n.Type switch
    {
        MediaType.Movie => $"movie/{n.TmdbId}",
        MediaType.Show => $"show/{n.TmdbId}",
        _ => $"show/{n.TmdbId}/season/{n.SeasonNumber}/episode/{n.EpisodeNumber}",
    };
    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>Checks everyone's releases hourly (each user's "today" is in their own time zone).</summary>
public class ReleaseAlertWorker(IServiceScopeFactory scopes, ILogger<ReleaseAlertWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<Guid> ids;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    ids = await db.Users.Select(u => u.Id).ToListAsync(stoppingToken);
                    // Old alerts go after two months.
                    var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60);
                    await db.Notifications.Where(n => n.Day < cutoff).ExecuteDeleteAsync(stoppingToken);
                    // Lists deleted over a day ago (past their Undo) go for good.
                    await ListService.PurgeDeletedAsync(db, stoppingToken);
                }
                foreach (var id in ids)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    if (await db.Users.FirstOrDefaultAsync(u => u.Id == id, stoppingToken) is not { } user) continue;
                    try { await scope.ServiceProvider.GetRequiredService<ReleaseAlertService>().ScanAsync(user, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Release alerts for {User} failed", user.UserName); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Release alert loop failed"); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
