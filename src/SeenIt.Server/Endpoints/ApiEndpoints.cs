using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Server.Endpoints;

public static class ApiEndpoints
{
    public static void MapApi(this IEndpointRouteBuilder app)
    {
        MapCatalog(app.MapGroup("/api/catalog"));

        var api = app.MapGroup("/api").RequireAuthorization();
        MapTracking(api);
        MapLists(app);
        MapJellyfin(app);
        MapImports(api.MapGroup("/imports"));
        MapSocial(app);

        api.MapGet("/export", async (ClaimsPrincipal p, ExportService export, CancellationToken ct) =>
            Results.File(await export.BuildAsync(p.UserId(), ct), "application/zip", $"seenit-export-{DateTime.UtcNow:yyyy-MM-dd}.zip"));
    }

    // ---------- Catalogue: public, with your own state when signed in ----------

    private static void MapCatalog(RouteGroupBuilder g)
    {
        g.MapGet("/search", (string q, string? type, int? page, CatalogService c, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q)
                ? Task.FromResult(new PagedResult<MediaSummary>([], 1, 0, 0))
                : c.SearchAsync(q.Trim(), type, page ?? 1, ct));

        g.MapGet("/browse/{type}/{*list}", (MediaType type, string list, int? page, CatalogService c, CancellationToken ct) =>
            c.BrowseAsync(type, list, page ?? 1, ct));

        g.MapGet("/movie/{id:int}", async (int id, ClaimsPrincipal p, CatalogService c, CancellationToken ct) =>
            await c.GetMovieAsync(id, p.OptionalUserId(), ct) is { } m ? Results.Ok(m) : Results.NotFound());

        g.MapGet("/show/{id:int}", async (int id, ClaimsPrincipal p, CatalogService c, CancellationToken ct) =>
            await c.GetShowAsync(id, p.OptionalUserId(), ct) is { } s ? Results.Ok(s) : Results.NotFound());

        g.MapGet("/show/{id:int}/season/{season:int}", async (int id, int season, ClaimsPrincipal p, CatalogService c, CancellationToken ct) =>
            await c.GetSeasonAsync(id, season, p.OptionalUserId(), ct) is { } s ? Results.Ok(s) : Results.NotFound());

        g.MapGet("/person/{id:int}", async (int id, ClaimsPrincipal p, PersonService people, CancellationToken ct) =>
            await people.GetAsync(id, p.OptionalUserId(), ct) is { } person ? Results.Ok(person) : Results.NotFound());

        g.MapGet("/providers/{type}/{id:int}", async (MediaType type, int id, string? region, ClaimsPrincipal p, AppDbContext db, CatalogService c, CancellationToken ct) =>
        {
            var saved = p.OptionalUserId() is { } uid ? await db.Users.Where(u => u.Id == uid).Select(u => u.Region).FirstOrDefaultAsync(ct) : null;
            var code = saved ?? (region is { Length: 2 } r && r.All(char.IsLetter) ? r : "US");
            return await c.GetProvidersAsync(type, id, code, ct);
        });
        g.MapGet("/regions", (CatalogService c, CancellationToken ct) => c.GetRegionsAsync(ct));

        g.MapGet("/show/{id:int}/season/{season:int}/episode/{episode:int}", async (int id, int season, int episode, ClaimsPrincipal p, CatalogService c, CancellationToken ct) =>
            await c.GetEpisodeAsync(id, season, episode, p.OptionalUserId(), ct) is { } e ? Results.Ok(e) : Results.NotFound());
    }

    // ---------- Tracking ----------

    private static void MapTracking(RouteGroupBuilder g)
    {
        g.MapPost("/watch", async (WatchRequest req, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            Results.Ok(new { added = await t.WatchAsync(p.UserId(), req, WatchSource.Manual, ct) }));

        g.MapPost("/watch/bulk", async (BulkWatchRequest req, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            Results.Ok(new { added = await t.BulkWatchAsync(p.UserId(), req, ct) }));

        g.MapDelete("/watch", async (MediaType type, int tmdbId, int? season, int? episode, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            Results.Ok(new { removed = await t.UnwatchAsync(p.UserId(), type, tmdbId, season, episode, ct) }));

        g.MapGet("/history", (MediaType? type, string? q, WatchSource? source, string? genre, DateTimeOffset? from, DateTimeOffset? to,
                string? sort, int? page, int? pageSize, string? tz, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            t.GetHistoryAsync(p.UserId(), new HistoryQuery(type, q, source, genre, from, to, sort == "oldest", page ?? 1, pageSize ?? 50, tz), ct));
        g.MapGet("/history/genres", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetHistoryGenresAsync(p.UserId(), ct));

        g.MapDelete("/history/{id:guid}", async (Guid id, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            await t.DeleteHistoryAsync(p.UserId(), id, ct) ? Results.NoContent() : Results.NotFound());

        g.MapGet("/watchlist", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetWatchlistAsync(p.UserId(), ct));
        g.MapPost("/watchlist", async (WatchlistRequest req, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
        {
            await t.AddToWatchlistAsync(p.UserId(), req.Type, req.TmdbId, ct);
            return Results.NoContent();
        });
        g.MapDelete("/watchlist", async (MediaType type, int tmdbId, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
        {
            await t.RemoveFromWatchlistAsync(p.UserId(), type, tmdbId, ct);
            return Results.NoContent();
        });

        g.MapGet("/ratings", (MediaType? type, ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetRatingsAsync(p.UserId(), type, ct));
        g.MapPost("/ratings", async (RatingRequest req, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
        {
            await t.RateAsync(p.UserId(), req, ct);
            return Results.NoContent();
        });
        g.MapDelete("/ratings", async (MediaType type, int tmdbId, int? season, int? episode, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
        {
            await t.UnrateAsync(p.UserId(), type, tmdbId, season, episode, ct);
            return Results.NoContent();
        });

        g.MapGet("/progress", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetProgressAsync(p.UserId(), ct));
        g.MapPut("/shows/{id:int}/status", async (int id, ShowStatusRequest req, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
        {
            await t.SetShowStatusAsync(p.UserId(), id, req.Status, ct);
            return Results.NoContent();
        });

        g.MapGet("/checkin", async (ClaimsPrincipal p, CheckInService c, CancellationToken ct) =>
            await c.GetAsync(p.UserId(), ct) is { } now ? Results.Ok(now) : Results.NoContent());
        g.MapPost("/checkin", (CheckInRequest req, ClaimsPrincipal p, CheckInService c, CancellationToken ct) =>
            c.StartAsync(p.UserId(), req, WatchSource.Manual, null, ct));
        g.MapDelete("/checkin", async (ClaimsPrincipal p, CheckInService c, CancellationToken ct) =>
        {
            await c.CancelAsync(p.UserId(), ct);
            return Results.NoContent();
        });
        g.MapGet("/calendar", (DateOnly? from, int? days, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            t.GetCalendarAsync(p.UserId(), from ?? DateOnly.FromDateTime(DateTime.UtcNow), days ?? 30, ct));
        g.MapGet("/dashboard", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetDashboardAsync(p.UserId(), ct));
        g.MapGet("/stats", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetStatsAsync(p.UserId(), ct));

        g.MapGet("/review/periods", (string? tz, ClaimsPrincipal p, ReviewService r, CancellationToken ct) => r.GetPeriodsAsync(p.UserId(), tz, ct));
        g.MapGet("/review/{year:int}", (int year, string? tz, ClaimsPrincipal p, ReviewService r, CancellationToken ct) => r.BuildAsync(p.UserId(), year, null, tz, ct));
        g.MapGet("/review/{year:int}/{month:int}", (int year, int month, string? tz, ClaimsPrincipal p, ReviewService r, CancellationToken ct) => r.BuildAsync(p.UserId(), year, month, tz, ct));
    }

    // ---------- Lists ----------

    private static void MapLists(IEndpointRouteBuilder app)
    {
        // Public lists can be viewed without signing in.
        app.MapGet("/api/lists/{id:guid}", async (Guid id, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.GetAsync(id, p.OptionalUserId(), ct) is { } list ? Results.Ok(list) : Results.NotFound());

        var g = app.MapGroup("/api/lists").RequireAuthorization();
        g.MapGet("", (ClaimsPrincipal p, ListService l, CancellationToken ct) => l.GetMineAsync(p.UserId(), ct));
        g.MapPost("", async (CreateListRequest req, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
        {
            var list = await l.CreateAsync(p.UserId(), req, ct);
            return Results.Ok(new { list.Id });
        });
        g.MapPut("/{id:guid}", async (Guid id, CreateListRequest req, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.UpdateAsync(p.UserId(), id, req, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.DeleteAsync(p.UserId(), id, ct) ? Results.NoContent() : Results.NotFound());
        g.MapPost("/{id:guid}/items", async (Guid id, ListItemRequest req, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.AddItemAsync(p.UserId(), id, req.Type, req.TmdbId, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/{id:guid}/items", async (Guid id, MediaType type, int tmdbId, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.RemoveItemAsync(p.UserId(), id, type, tmdbId, ct) ? Results.NoContent() : Results.NotFound());
    }

    // ---------- Jellyfin ----------

    private static void MapJellyfin(IEndpointRouteBuilder app)
    {
        // The webhook authenticates by the secret token in its URL.
        app.MapPost("/api/jellyfin/webhook/{token}", async (string token, HttpRequest request, JellyfinService j, CancellationToken ct) =>
        {
            JsonElement body;
            try { body = (await JsonDocument.ParseAsync(request.Body, cancellationToken: ct)).RootElement; }
            catch (JsonException) { return Results.BadRequest(new ProblemInfo("Body must be JSON. Check the webhook template.")); }
            return await j.HandleAsync(token, body, ct) ? Results.Ok() : Results.NotFound();
        }).RequireRateLimiting("webhook");

        var g = app.MapGroup("/api/jellyfin").RequireAuthorization();
        g.MapGet("", async (ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var uid = p.UserId();
            var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
            var events = await db.JellyfinEvents.Where(e => e.UserId == uid).OrderByDescending(e => e.ReceivedAt).Take(20)
                .Select(e => new JellyfinEventDto(e.ReceivedAt, e.ItemName, e.Outcome)).ToListAsync(ct);
            return new JellyfinSettingsDto($"/api/jellyfin/webhook/{user.JellyfinToken}", user.JellyfinThresholdPercent, events);
        });
        g.MapPost("/regenerate", async (ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var uid = p.UserId();
            var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
            user.JellyfinToken = AppUser.NewToken();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        g.MapPut("/threshold", async (JellyfinThresholdRequest req, ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var uid = p.UserId();
            var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
            user.JellyfinThresholdPercent = Math.Clamp(req.WatchedThresholdPercent, 50, 100);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    // ---------- Imports ----------

    private const long MaxImportBytes = 50 * 1024 * 1024;

    private static void MapImports(RouteGroupBuilder g)
    {
        g.MapGet("", async (ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var uid = p.UserId();
            return await db.ImportJobs.Where(j => j.UserId == uid).OrderByDescending(j => j.CreatedAt).Take(20)
                .Select(j => new ImportJobDto(j.Id, j.Kind, j.FileName, j.Status, j.CreatedAt, j.FinishedAt,
                    j.Total, j.Processed, j.Imported, j.Skipped, j.Failed, j.Message, j.Unmatched))
                .ToListAsync(ct);
        });

        g.MapPost("/{kind}", async (ImportKind kind, IFormFile file, ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            if (file.Length == 0) return Results.BadRequest(new ProblemInfo("That file is empty."));
            if (file.Length > MaxImportBytes) return Results.BadRequest(new ProblemInfo("Exports over 50 MB aren't supported."));
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".zip" or ".json" or ".csv")) return Results.BadRequest(new ProblemInfo("Upload the .zip export (or a .json/.csv file from it)."));

            var uid = p.UserId();
            if (await db.ImportJobs.CountAsync(j => j.UserId == uid && (j.Status == ImportStatus.Queued || j.Status == ImportStatus.Running), ct) >= 2)
                return Results.BadRequest(new ProblemInfo("Wait for your current imports to finish first."));

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var job = new ImportJob { UserId = uid, Kind = kind, FileName = Path.GetFileName(file.FileName), Payload = ms.ToArray(), Status = ImportStatus.Queued };
            db.ImportJobs.Add(job);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { job.Id });
        }).DisableAntiforgery();
    }

    // ---------- Social ----------

    private static void MapSocial(IEndpointRouteBuilder app)
    {
        // Profiles respect the owner's visibility setting; signed-out visitors see public ones only.
        app.MapGet("/api/users/{name}", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.GetProfileAsync(name, p.OptionalUserId(), ct) is { } profile ? Results.Ok(profile) : Results.NotFound());

        var g = app.MapGroup("/api/social").RequireAuthorization();
        g.MapGet("/feed", (int? take, ClaimsPrincipal p, SocialService s, CancellationToken ct) => s.FeedAsync(p.UserId(), take ?? 40, ct));
        g.MapGet("/search", (string q, ClaimsPrincipal p, SocialService s, CancellationToken ct) => s.SearchAsync(p.UserId(), q, ct));
        g.MapGet("/following", async (ClaimsPrincipal p, SocialService s, CancellationToken ct) => (await s.ConnectionsAsync(p.UserId(), ct)).Following);
        g.MapGet("/followers", async (ClaimsPrincipal p, SocialService s, CancellationToken ct) => (await s.ConnectionsAsync(p.UserId(), ct)).Followers);
        g.MapPost("/follow/{name}", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.FollowAsync(p.UserId(), name, true, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/follow/{name}", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.FollowAsync(p.UserId(), name, false, ct) ? Results.NoContent() : Results.NotFound());
        g.MapGet("/compare/{name}", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.CompareAsync(p.UserId(), name, ct) is { } c ? Results.Ok(c) : Results.NotFound());
    }
}
