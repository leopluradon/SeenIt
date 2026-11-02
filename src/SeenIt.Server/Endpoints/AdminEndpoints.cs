using Process = System.Diagnostics.Process;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Server.Endpoints;

/// <summary>Site administration: who's here, what they did, imports, deployments and server health.</summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization();
        // Signed in but not an admin: pretend there's nothing here.
        g.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<AdminService>().IsAdmin(ctx.HttpContext.User.Identity?.Name)
                ? await next(ctx)
                : Results.NotFound());

        g.MapGet("/overview", OverviewAsync);
        g.MapGet("/users", UsersAsync);
        g.MapGet("/activity", ActivityAsync);
        g.MapGet("/imports", async (AppDbContext db, CancellationToken ct) =>
            await db.ImportJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).Take(200)
                .Join(db.Users, j => j.UserId, u => u.Id, (j, u) => new AdminImport(j.Id, u.UserName!, j.Kind, j.Parts, j.FileName, j.Status,
                    j.CreatedAt, j.StartedAt, j.FinishedAt, j.Total, j.Processed, j.Imported, j.Skipped, j.Failed, j.Message, j.Unmatched))
                .ToListAsync(ct));
        g.MapGet("/jellyfin", async (AppDbContext db, CancellationToken ct) =>
            await db.JellyfinEvents.AsNoTracking().OrderByDescending(e => e.ReceivedAt).Take(200)
                .Join(db.Users, e => e.UserId, u => u.Id, (e, u) => new AdminJellyfinEvent(e.ReceivedAt, u.UserName!, e.ItemName, e.Outcome))
                .ToListAsync(ct));
        g.MapGet("/invites", async (AppDbContext db, CancellationToken ct) =>
            await db.Invites.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(200)
                .Select(i => new AdminInvite(i.Code,
                    db.Users.Where(u => u.Id == i.CreatedById).Select(u => u.UserName!).FirstOrDefault() ?? "?",
                    i.CreatedAt, i.ExpiresAt,
                    db.Users.Where(u => u.Id == i.UsedById).Select(u => u.UserName).FirstOrDefault(), i.UsedAt))
                .ToListAsync(ct));
        g.MapGet("/logs", (RecentLogs logs) => logs.Snapshot());

        g.MapPost("/users/{id:guid}/unlock", async (Guid id, UserManager<AppUser> users) =>
        {
            if (await users.FindByIdAsync(id.ToString()) is not { } u) return Results.NotFound();
            await users.SetLockoutEndDateAsync(u, null);
            await users.ResetAccessFailedCountAsync(u);
            return Results.NoContent();
        });

        // For helping someone who forgot their password when email isn't an option: hand them this link.
        g.MapPost("/users/{id:guid}/reset-link", async (Guid id, UserManager<AppUser> users, HttpContext http, IConfiguration config) =>
        {
            if (await users.FindByIdAsync(id.ToString()) is not { } u) return Results.NotFound();
            var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(u)));
            return Results.Ok(new AdminResetLink($"{AuthEndpoints.PublicBase(http, config)}reset-password?user={u.Id}&token={token}",
                DateTimeOffset.UtcNow.AddHours(2)));
        });
    }

    /// <summary>
    /// The newest Android app on the server (anyone may ask; the app shows "update available").
    /// scripts/publish-apk.sh puts SeenIt.apk and version.txt in the downloads folder.
    /// </summary>
    public static void MapAppRelease(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/app/android", (IConfiguration config) =>
        {
            if (config["SeenIt:DownloadsDir"] is not { Length: > 0 } dir) return Results.NotFound();
            var apk = new FileInfo(Path.Combine(dir, "SeenIt.apk"));
            var versionFile = Path.Combine(dir, "version.txt");
            if (!apk.Exists || !File.Exists(versionFile)) return Results.NotFound();
            var version = File.ReadAllText(versionFile).Trim();
            return Results.Ok(new AppRelease(version, $"download/SeenIt.apk?v={Uri.EscapeDataString(version)}", apk.Length, apk.LastWriteTimeUtc));
        });
    }

    private static async Task<AdminOverview> OverviewAsync(AppDbContext db, RecentLogs logs, EmailService email, InviteService invites,
        AdminService admins, BackupStatus backups, AlertService alerts, IConfiguration config, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var day = now.AddDays(-1);
        var week = now.AddDays(-7);
        var monthStart = now.UtcDateTime.Date.AddDays(-29);

        var counts = new List<CountBucket>
        {
            new("Users", await db.Users.CountAsync(ct)),
            new("Movie plays", await db.WatchEvents.CountAsync(w => w.Type == MediaType.Movie, ct)),
            new("Episode plays", await db.WatchEvents.CountAsync(w => w.Type == MediaType.Episode, ct)),
            new("Ratings", await db.Ratings.CountAsync(ct)),
            new("Watchlist entries", await db.Watchlist.CountAsync(ct)),
            new("Lists", await db.Lists.CountAsync(ct)),
            new("List entries", await db.ListItems.CountAsync(ct)),
            new("Follows", await db.Follows.CountAsync(ct)),
            new("Imports", await db.ImportJobs.CountAsync(ct)),
            new("Movies cached", await db.Movies.CountAsync(ct)),
            new("Shows cached", await db.Shows.CountAsync(ct)),
            new("Episodes cached", await db.Episodes.CountAsync(ct)),
            new("Activity log entries", await db.Activity.CountAsync(ct)),
        };

        var activityDays = (await db.Activity.Where(a => a.At >= new DateTimeOffset(monthStart, TimeSpan.Zero))
                .Select(a => a.At).ToListAsync(ct))
            .GroupBy(a => a.UtcDateTime.Date).ToDictionary(g => g.Key, g => g.Count());

        string dbSize;
        try
        {
            dbSize = (await db.Database.SqlQueryRaw<string>("select pg_size_pretty(pg_database_size(current_database())) as \"Value\"").ToListAsync(ct)).FirstOrDefault() ?? "?";
        }
        catch (Exception) { dbSize = "?"; }   // e.g. a non-Postgres test database

        var config2 = new List<AdminSetting>
        {
            new("TMDB", string.IsNullOrWhiteSpace(config["Tmdb:ReadAccessToken"]) ? "not configured" : "configured", !string.IsNullOrWhiteSpace(config["Tmdb:ReadAccessToken"])),
            new("Email (SMTP)", email.Enabled ? $"on · {config["Smtp:Host"]}" : "off: password reset unavailable", email.Enabled),
            new("Sign-ups", invites.OpenRegistration ? "open to anyone" : "invite-only", true),
            new("Public URL", config["SeenIt:PublicUrl"] is { Length: > 0 } url ? url : "not set (uses request host)", config["SeenIt:PublicUrl"] is { Length: > 0 }),
            new("Login keys", config["SeenIt:KeyRing"] is { Length: > 0 } k ? $"persisted in {k}" : "in memory: everyone is signed out on restart", config["SeenIt:KeyRing"] is { Length: > 0 }),
            new("Admins", string.Join(", ", admins.Admins), true),
        };

        var recent = logs.Snapshot();
        return new AdminOverview(
            AppInfo.Version, AppInfo.Commit, AppInfo.Subject, AppInfo.BuiltAt, AppInfo.StartedAt,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024, dbSize,
            await db.Deployments.AsNoTracking().OrderByDescending(d => d.StartedAt).Take(30)
                .Select(d => new AdminDeployment(d.Version, d.Commit, d.Subject, d.BuiltAt, d.StartedAt)).ToListAsync(ct),
            counts, config2,
            await db.Users.CountAsync(u => u.LastSeenAt >= day, ct),
            await db.Users.CountAsync(u => u.LastSeenAt >= week, ct),
            await db.Activity.CountAsync(a => a.At >= day, ct),
            Enumerable.Range(0, 30).Select(i => monthStart.AddDays(i))
                .Select(d => new CountBucket(d.ToString("MMM d"), activityDays.GetValueOrDefault(d))).ToList(),
            recent.Count(e => e.At >= day && e.Level is "Error" or "Critical"),
            backups.Health(), alerts.Recent);
    }

    private static async Task<List<AdminUser>> UsersAsync(AppDbContext db, AdminService admins, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var users = await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt).ToListAsync(ct);
        var plays = await db.WatchEvents.GroupBy(w => new { w.UserId, w.Type }).Select(g => new { g.Key.UserId, g.Key.Type, N = g.Count() }).ToListAsync(ct);
        var ratings = await db.Ratings.GroupBy(r => r.UserId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var watchlist = await db.Watchlist.GroupBy(r => r.UserId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var lists = await db.Lists.GroupBy(r => r.UserId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var following = await db.Follows.GroupBy(f => f.FollowerId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var followers = await db.Follows.GroupBy(f => f.FolloweeId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var lastAction = await db.Activity.GroupBy(a => a.UserId).Select(g => new { g.Key, At = g.Max(a => a.At) }).ToDictionaryAsync(x => x.Key, x => x.At, ct);
        var jellyfinSince = now.AddDays(-30);
        var jellyfin = (await db.JellyfinEvents.Where(e => e.ReceivedAt >= jellyfinSince).Select(e => e.UserId).Distinct().ToListAsync(ct)).ToHashSet();
        var invitedBy = await db.Invites.Where(i => i.UsedById != null)
            .Join(db.Users, i => i.CreatedById, u => u.Id, (i, u) => new { i.UsedById, u.UserName }).ToListAsync(ct);

        return users.Select(u => new AdminUser(
            u.Id, u.UserName!, u.DisplayName, u.Email!, u.EmailConfirmed, u.CreatedAt, u.LastSeenAt,
            lastAction.TryGetValue(u.Id, out var la) ? la : null,
            plays.Where(p => p.UserId == u.Id && p.Type == MediaType.Movie).Sum(p => p.N),
            plays.Where(p => p.UserId == u.Id && p.Type == MediaType.Episode).Sum(p => p.N),
            ratings.GetValueOrDefault(u.Id), watchlist.GetValueOrDefault(u.Id), lists.GetValueOrDefault(u.Id),
            following.GetValueOrDefault(u.Id), followers.GetValueOrDefault(u.Id),
            invitedBy.FirstOrDefault(i => i.UsedById == u.Id)?.UserName, u.Visibility,
            jellyfin.Contains(u.Id), u.LockoutEnd > now, admins.IsAdmin(u.UserName), u.TrackingSince)).ToList();
    }

    private static async Task<AdminActivityPage> ActivityAsync(string? user, ActivityKind? kind, string? group, int? page,
        AppDbContext db, CancellationToken ct)
    {
        const int size = 50;
        var q = db.Activity.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(user)) q = q.Where(a => a.UserName == user);
        if (kind is { } k) q = q.Where(a => a.Kind == k);
        if (Group(group) is { Length: > 0 } kinds) q = q.Where(a => kinds.Contains(a.Kind));

        var total = await q.CountAsync(ct);
        var current = Math.Max(1, page ?? 1);
        var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Skip((current - 1) * size).Take(size).ToListAsync(ct);

        var movieIds = rows.Where(r => r.Type == MediaType.Movie && r.TmdbId != null).Select(r => r.TmdbId!.Value).Distinct().ToList();
        var showIds = rows.Where(r => r.Type is MediaType.Show or MediaType.Episode && r.TmdbId != null).Select(r => r.TmdbId!.Value).Distinct().ToList();
        var movies = await db.Movies.Where(m => movieIds.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, m => m.Title, ct);
        var shows = await db.Shows.Where(s => showIds.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId, s => s.Name, ct);

        return new AdminActivityPage(rows.Select(r => new AdminActivity(r.Id, r.At, r.UserName, r.Kind, r.Source, r.Type, r.TmdbId,
                r.SeasonNumber, r.EpisodeNumber,
                r.TmdbId is { } id ? (r.Type == MediaType.Movie ? movies.GetValueOrDefault(id) : shows.GetValueOrDefault(id)) : null,
                r.Detail)).ToList(),
            current, Math.Max(1, (total + size - 1) / size), total);
    }

    /// <summary>Filter chips on the admin page.</summary>
    private static ActivityKind[] Group(string? group) => group switch
    {
        "tracking" => [ActivityKind.Watch, ActivityKind.BulkWatch, ActivityKind.Unwatch, ActivityKind.DeletePlay,
            ActivityKind.ShowStatus, ActivityKind.RewatchStart, ActivityKind.RewatchStop, ActivityKind.CheckIn, ActivityKind.CheckInCancel],
        "ratings" => [ActivityKind.Rate, ActivityKind.Unrate],
        "lists" => [ActivityKind.WatchlistAdd, ActivityKind.WatchlistRemove, ActivityKind.ListCreate, ActivityKind.ListEdit,
            ActivityKind.ListDelete, ActivityKind.ListAdd, ActivityKind.ListRemove],
        "account" => [ActivityKind.Import, ActivityKind.ClearData, ActivityKind.SignUp, ActivityKind.AccountDeleted,
            ActivityKind.PasswordChanged, ActivityKind.PasswordReset, ActivityKind.Follow, ActivityKind.Unfollow],
        _ => [],
    };
}
