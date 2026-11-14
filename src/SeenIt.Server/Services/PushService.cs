using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using WebPushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace SeenIt.Server.Services;

/// <summary>
/// Alerts as push notifications on browsers and installed web apps (iPhone Home Screen icons need iOS 16.4+),
/// through the standard Web Push protocol. Needs VAPID keys (SeenIt:VapidPublicKey / VapidPrivateKey); without
/// them push is simply off. Devices the push service says are gone are forgotten.
/// </summary>
public class PushService(AppDbContext db, IHttpClientFactory http, IConfiguration config, ILogger<PushService> log)
{
    public const string AppPrefix = "app:";
    public string? PublicKey => config["SeenIt:VapidPublicKey"] is { Length: > 0 } k && config["SeenIt:VapidPrivateKey"] is { Length: > 0 } ? k : null;
    /// <summary>Web push is set up (VAPID keys). The Android app's notifications work either way.</summary>
    public bool Enabled => PublicKey is not null;

    /// <summary>The Android app on one phone: it collects its notifications with TakeAppMessagesAsync.</summary>
    public async Task RegisterAppAsync(Guid userId, AppPushRegisterRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(req.InstallId, out var install)) throw new TrackingException("That isn't an app install id.");
        var endpoint = AppPrefix + install.ToString("N");
        var row = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (row is null) db.PushSubscriptions.Add(row = new Data.PushSubscription { Endpoint = endpoint, P256dh = "-", Auth = "-" });
        row.UserId = userId;
        row.Device = req.Device is { Length: > 0 } d ? (d.Length > 100 ? d[..100] : d) : "Android app";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Hands the app its waiting notifications (oldest first) and forgets them.</summary>
    public async Task<List<PushPayload>> TakeAppMessagesAsync(Guid userId, string installId, CancellationToken ct)
    {
        if (!Guid.TryParse(installId, out var install)) return [];
        var endpoint = AppPrefix + install.ToString("N");
        var sub = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint && s.UserId == userId, ct);
        if (sub is null) return [];
        var rows = await db.AppPushMessages.Where(m => m.SubscriptionId == sub.Id).OrderBy(m => m.CreatedAt).Take(50).ToListAsync(ct);
        db.AppPushMessages.RemoveRange(rows);
        sub.LastSentAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return rows.Select(r => JsonSerializer.Deserialize<PushPayload>(r.PayloadJson, JsonSerializerOptions.Web)).OfType<PushPayload>().ToList();
    }

    public async Task SubscribeAsync(Guid userId, PushSubscribeRequest req, CancellationToken ct)
    {
        if (!Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var u) || u.Scheme != "https" || req.Endpoint.Length > 1000)
            throw new TrackingException("That isn't a push address.");
        if (string.IsNullOrWhiteSpace(req.P256dh) || string.IsNullOrWhiteSpace(req.Auth) || req.P256dh.Length > 200 || req.Auth.Length > 100)
            throw new TrackingException("That push subscription is missing its keys.");
        // One row per device: subscribing again (or as someone else on a shared browser) takes it over.
        var row = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint, ct);
        if (row is null) db.PushSubscriptions.Add(row = new Data.PushSubscription { Endpoint = req.Endpoint });
        row.UserId = userId;
        row.P256dh = req.P256dh;
        row.Auth = req.Auth;
        row.Device = req.Device is { Length: > 0 } d ? (d.Length > 100 ? d[..100] : d) : null;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnsubscribeAsync(Guid userId, string endpoint, CancellationToken ct)
    {
        if (await db.PushSubscriptions.FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == endpoint, ct) is not { } row) return;
        db.PushSubscriptions.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<PushDeviceDto>> DevicesAsync(Guid userId, CancellationToken ct) =>
        db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.CreatedAt)
            .Select(s => new PushDeviceDto(s.Endpoint, s.Device, s.CreatedAt, s.LastSentAt)).ToListAsync(ct);

    /// <summary>Sends to every device of this user. Returns how many got it.</summary>
    public async Task<int> SendAsync(Guid userId, PushPayload payload, CancellationToken ct)
    {
        var subs = await db.PushSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
        var body = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        var sent = 0;
        // The Android app: waits in its outbox until the app next checks (every 15 minutes or so).
        foreach (var app in subs.Where(s => s.Endpoint.StartsWith(AppPrefix, StringComparison.Ordinal)))
        {
            db.AppPushMessages.Add(new AppPushMessage { SubscriptionId = app.Id, PayloadJson = body });
            sent++;
        }
        subs = subs.Where(s => !s.Endpoint.StartsWith(AppPrefix, StringComparison.Ordinal)).ToList();
        if (!Enabled || subs.Count == 0) { await db.SaveChangesAsync(ct); return sent; }
        var client = new PushServiceClient(http.CreateClient("webpush"))
        {
            DefaultAuthentication = new VapidAuthentication(PublicKey!, config["SeenIt:VapidPrivateKey"]!)
            {
                // Apple wants a real contact: the site's address.
                Subject = (config["SeenIt:PublicUrl"] is { Length: > 0 } site ? site : "https://seenit.local/").TrimEnd('/'),
            },
        };
        foreach (var s in subs)
        {
            var target = new WebPushSubscription { Endpoint = s.Endpoint };
            target.SetKey(PushEncryptionKeyName.P256DH, s.P256dh);
            target.SetKey(PushEncryptionKeyName.Auth, s.Auth);
            try
            {
                await client.RequestPushMessageDeliveryAsync(target,
                    new PushMessage(body) { TimeToLive = 60 * 60 * 24, Urgency = PushMessageUrgency.Normal, Topic = TopicOf(payload.Tag) }, ct);
                s.LastSentAt = DateTimeOffset.UtcNow;
                sent++;
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                db.PushSubscriptions.Remove(s);   // unsubscribed, app removed, or permission taken back
                log.LogInformation("Push subscription for {User} is gone; forgot it", userId);
            }
            catch (Exception ex) when (ex is PushServiceClientException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning(ex, "Push to {Host} failed for {User}", new Uri(s.Endpoint).Host, userId);
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>A Topic replaces an undelivered push with the same one (max 32 URL-safe characters).</summary>
    private static string? TopicOf(string? tag) =>
        tag is { Length: > 0 } t ? new string(t.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray()) : null;
}

/// <summary>Sends new alerts as push notifications, every few seconds. Release alerts wait for daytime (8:00–22:00
/// in the person's time zone) and go out together; recommendations and "Sam watched Heat" go straight away.</summary>
public class PushWorker(IServiceScopeFactory scopes, ILogger<PushWorker> log) : BackgroundService
{
    private const int DayStarts = 8, DayEnds = 22;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<PushService>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Push pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    public static async Task RunOnceAsync(AppDbContext db, PushService push, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // An app that hasn't checked in for 3 days (uninstalled, signed out): drop what was waiting for it.
        var expired = await db.AppPushMessages.Where(m => m.CreatedAt < now.AddDays(-3)).ToListAsync(ct);
        if (expired.Count > 0) db.AppPushMessages.RemoveRange(expired);
        var pending = await db.Notifications.Where(n => n.PushedAt == null).OrderBy(n => n.CreatedAt).Take(500).ToListAsync(ct);
        if (pending.Count == 0) { if (expired.Count > 0) await db.SaveChangesAsync(ct); return; }
        var users = pending.Select(n => n.UserId).Distinct().ToList();
        var webOn = push.Enabled;
        var withDevices = (await db.PushSubscriptions
            .Where(s => users.Contains(s.UserId) && (webOn || s.Endpoint.StartsWith(PushService.AppPrefix)))
            .Select(s => s.UserId).Distinct().ToListAsync(ct)).ToHashSet();
        var people = await db.Users.Where(u => users.Contains(u.Id)).Select(u => new { u.Id, u.TimeZone, u.PrefsJson }).ToDictionaryAsync(u => u.Id, ct);

        foreach (var g in pending.GroupBy(n => n.UserId))
        {
            // No devices, a kind they don't want pushed (Settings → Alerts), or more than two days old (push was off,
            // the server was down): nothing to send.
            var prefs = UserPrefsStore.Read(people.GetValueOrDefault(g.Key)?.PrefsJson);
            var skip = g.Where(n => !withDevices.Contains(g.Key) || !prefs.PushesAlert(n.Kind) || now - n.CreatedAt > TimeSpan.FromDays(2)).ToList();
            foreach (var n in skip) n.PushedAt = now;
            var due = g.Except(skip).ToList();
            if (due.Count == 0) continue;

            foreach (var n in due.Where(n => n.RecommendationId is not null || n.WatchedRecommendationId is not null))
            {
                await push.SendAsync(g.Key, n.WatchedRecommendationId is not null
                    ? new PushPayload(n.Title, n.Detail ?? "", Fmt(n), $"rec-watched-{n.Id:N}")
                    : new PushPayload(n.Title, n.Detail is { } note ? $"“{note}”" : "Recommended to you", "recommendations", $"rec-{n.Id:N}"), ct);
                n.PushedAt = now;
            }

            var releases = due.Where(n => n.RecommendationId is null && n.WatchedRecommendationId is null).ToList();
            if (releases.Count == 0) continue;
            var zone = ReviewService.Zone(people.GetValueOrDefault(g.Key)?.TimeZone);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var today = DateOnly.FromDateTime(local.DateTime);
            foreach (var stale in releases.Where(n => n.Day < today.AddDays(-1)).ToList()) { stale.PushedAt = now; releases.Remove(stale); }
            if (releases.Count == 0 || local.Hour < DayStarts || local.Hour >= DayEnds) continue;   // wait for the morning

            var payload = releases.Count == 1
                ? new PushPayload(releases[0].Title, ReleaseLine(releases[0]), Fmt(releases[0]), $"release-{releases[0].Id:N}")
                : new PushPayload($"{releases.Count} new releases",
                    string.Join(", ", releases.Select(r => r.Type == MediaType.Episode && Code(r) is { } c ? $"{r.Title} {c}" : r.Title).Take(6))
                        + (releases.Count > 6 ? $" and {releases.Count - 6} more" : ""),
                    "notifications", $"releases-{today:yyyyMMdd}");
            await push.SendAsync(g.Key, payload, ct);
            foreach (var n in releases) n.PushedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"S02E05": the episode title stays off the lock screen (spoilers).</summary>
    private static string? Code(Notification n) => n.SeasonNumber is { } s && n.EpisodeNumber is { } e ? (s == 0 ? $"Special {e}" : $"S{s:00}E{e:00}") : null;

    private static string ReleaseLine(Notification n) => n.Type == MediaType.Episode
        ? $"{Code(n) ?? "New episode"} is out today"
        : n.Detail is { Length: > 0 } d && d != "Out today" ? d : "Out today";

    /// <summary>The page a notification opens, relative to the site.</summary>
    private static string Fmt(Notification n) => n.Type switch
    {
        MediaType.Episode => $"show/{n.TmdbId}/season/{n.SeasonNumber}/episode/{n.EpisodeNumber}",
        MediaType.Show => $"show/{n.TmdbId}",
        _ => $"movie/{n.TmdbId}",
    };
}
