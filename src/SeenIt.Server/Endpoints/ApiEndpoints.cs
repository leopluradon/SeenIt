using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Import;
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
        FeatureEndpoints.Map(api);
        MapSocial(app);

        api.MapGet("/export", async (ClaimsPrincipal p, ExportService export, CancellationToken ct) =>
            Results.File(await export.BuildAsync(p.UserId(), ct), "application/zip", $"seenit-export-{DateTime.UtcNow:yyyy-MM-dd}.zip"));
    }

    // ---------- Catalog: public, with your own state when signed in ----------

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
        g.MapPost("/watch", async (WatchRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var added = await t.WatchAsync(p.UserId(), req, WatchSource.Manual, ct);
            var what = req.EpisodeNumber is null && req.Type != MediaType.Movie ? $"{added} episode{(added == 1 ? "" : "s")}" : null;
            await a.LogAsync(p, ActivityKind.Watch, req.Type, req.TmdbId, req.SeasonNumber, req.EpisodeNumber,
                string.Join(", ", new[] { what, ActivityService.When(req.When, req.WatchedAt) }.OfType<string>()) is { Length: > 0 } d ? d : null);
            return Results.Ok(new { added });
        });

        g.MapPost("/watch/bulk", async (BulkWatchRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var added = await t.BulkWatchAsync(p.UserId(), req, ct);
            await a.LogAsync(p, ActivityKind.BulkWatch, MediaType.Show, req.ShowTmdbId, req.SeasonNumber, null,
                $"{added} episode{(added == 1 ? "" : "s")}" + (ActivityService.When(req.When, req.WatchedAt) is { } w ? ", " + w : ""));
            return Results.Ok(new { added });
        });

        g.MapDelete("/watch", async (MediaType type, int tmdbId, int? season, int? episode, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var plays = await t.UnwatchAsync(p.UserId(), type, tmdbId, season, episode, ct);
            await a.LogAsync(p, ActivityKind.Unwatch, type, tmdbId, season, episode, $"{plays.Count} play{(plays.Count == 1 ? "" : "s")} removed");
            return Results.Ok(new RemovedPlays(plays.Count, plays));
        });

        g.MapGet("/history", (MediaType? type, string? q, WatchSource? source, string? genre, DateTimeOffset? from, DateTimeOffset? to,
                string? sort, int? page, int? pageSize, string? tz, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            t.GetHistoryAsync(p.UserId(), new HistoryQuery(type, q, source, genre, from, to, sort == "oldest", page ?? 1, pageSize ?? 50, tz), ct));
        g.MapGet("/history/genres", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetHistoryGenresAsync(p.UserId(), ct));

        g.MapDelete("/history/{id:guid}", async (Guid id, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            if (await t.DeleteHistoryAsync(p.UserId(), id, ct) is not { } play) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.DeletePlay, play.Type, play.TmdbId, play.SeasonNumber, play.EpisodeNumber,
                WatchDates.IsUnknown(play.WatchedAt) ? "play with unknown date" : $"play from {play.WatchedAt:yyyy-MM-dd HH:mm} UTC");
            return Results.Ok(new RemovedPlays(1, [play]));
        });

        g.MapPut("/history/{id:guid}", async (Guid id, EditPlayRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            if (await t.EditPlayAsync(p.UserId(), id, req, ct) is not { } r) return Results.NotFound();
            string When(DateTimeOffset d) => WatchDates.IsUnknown(d) ? "unknown date" : $"{d:yyyy-MM-dd HH:mm} UTC";
            await a.LogAsync(p, ActivityKind.EditPlay, detail: $"{When(r.Before)} → {When(r.After.WatchedAt)}");
            return Results.Ok(r.After);
        });

        g.MapPost("/history/edit", async (BulkEditPlaysRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var before = await t.EditPlaysAsync(p.UserId(), req, ct);
            await a.LogAsync(p, ActivityKind.EditPlay, detail: $"{before.Count} plays → {req.When}{(req.WatchedAt is { } at ? $" {at:yyyy-MM-dd HH:mm zzz}" : "")}");
            return Results.Ok(before);
        });
        g.MapPost("/history/dates", async (List<PlayDate> dates, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var n = await t.SetPlayDatesAsync(p.UserId(), dates, ct);
            await a.LogAsync(p, ActivityKind.EditPlay, detail: $"{n} plays back to their old dates (undo)");
            return Results.Ok(new { restored = n });
        });

        g.MapPost("/history/delete", async (List<Guid> ids, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var plays = await t.DeleteHistoryAsync(p.UserId(), ids, ct);
            if (plays.Count > 0) await a.LogAsync(p, ActivityKind.DeletePlay, detail: $"{plays.Count} plays (selected)");
            return Results.Ok(new RemovedPlays(plays.Count, plays));
        });

        // Undo for the deletes above: the plays come back with their original dates and sources.
        g.MapPost("/history/restore", async (List<RestorePlay> plays, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var n = await t.RestoreAsync(p.UserId(), plays, ct);
            if (plays.Count > 0)
                await a.LogAsync(p, ActivityKind.RestorePlays, plays[0].Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, plays[0].TmdbId,
                    detail: $"{n} play{(n == 1 ? "" : "s")} (undo)");
            return Results.Ok(new { restored = n });
        });

        g.MapGet("/duplicates", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.DuplicatesAsync(p.UserId(), ct));

        g.MapPost("/rematch", async (RematchRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var r = await t.RematchAsync(p.UserId(), req.Type, req.FromTmdbId, req.ToTmdbId, ct);
            await a.LogAsync(p, ActivityKind.Rematch, req.Type, req.ToTmdbId,
                detail: $"from TMDB {req.FromTmdbId}: {r.Plays} plays, {r.Ratings} ratings, {r.Watchlist} watchlist, {r.ListEntries} list entries");
            return Results.Ok(r);
        });

        g.MapGet("/watchlist", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetWatchlistAsync(p.UserId(), ct));
        g.MapPost("/watchlist", async (WatchlistRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            await t.AddToWatchlistAsync(p.UserId(), req.Type, req.TmdbId, ct);
            await a.LogAsync(p, ActivityKind.WatchlistAdd, req.Type, req.TmdbId);
            return Results.NoContent();
        });
        g.MapDelete("/watchlist", async (MediaType type, int tmdbId, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            await t.RemoveFromWatchlistAsync(p.UserId(), type, tmdbId, ct);
            await a.LogAsync(p, ActivityKind.WatchlistRemove, type, tmdbId);
            return Results.NoContent();
        });

        g.MapGet("/favorites", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetFavoritesAsync(p.UserId(), ct));
        g.MapPost("/favorites", async (FavoriteRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            if (await t.AddFavoriteAsync(p.UserId(), req.Type, req.TmdbId, ct)) await a.LogAsync(p, ActivityKind.FavoriteAdd, req.Type, req.TmdbId);
            return Results.NoContent();
        });
        g.MapDelete("/favorites", async (MediaType type, int tmdbId, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            if (await t.RemoveFavoriteAsync(p.UserId(), type, tmdbId, ct)) await a.LogAsync(p, ActivityKind.FavoriteRemove, type, tmdbId);
            return Results.NoContent();
        });
        g.MapPut("/favorites/order", async (List<FavoriteRequest> order, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
        {
            await t.ReorderFavoritesAsync(p.UserId(), order, ct);
            return Results.NoContent();
        });

        g.MapGet("/ratings", (MediaType? type, ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetRatingsAsync(p.UserId(), type, ct));
        g.MapPost("/ratings", async (RatingRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            await t.RateAsync(p.UserId(), req, ct);
            await a.LogAsync(p, ActivityKind.Rate, req.Type, req.TmdbId, req.SeasonNumber, req.EpisodeNumber, $"{req.Rating}/10");
            return Results.NoContent();
        });
        g.MapDelete("/ratings", async (MediaType type, int tmdbId, int? season, int? episode, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            await t.UnrateAsync(p.UserId(), type, tmdbId, season, episode, ct);
            await a.LogAsync(p, ActivityKind.Unrate, type, tmdbId, season, episode);
            return Results.NoContent();
        });

        g.MapGet("/progress", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetProgressAsync(p.UserId(), ct));
        g.MapGet("/pick", async (PickKind? kind, PickSource? source, int? maxMinutes, bool? streaming, string? region, string? exclude,
            ClaimsPrincipal p, AppDbContext db, PickService picks, CancellationToken ct) =>
        {
            var uid = p.UserId();
            var saved = await db.Users.Where(u => u.Id == uid).Select(u => u.Region).FirstOrDefaultAsync(ct);
            var code = saved ?? (region is { Length: 2 } r && r.All(char.IsLetter) ? r : "US");
            var skip = (exclude ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var pick = await picks.PickAsync(uid, kind ?? PickKind.Any, source ?? PickSource.Both, maxMinutes is > 0 ? maxMinutes : null,
                streaming ?? false, code, skip, ct);
            return pick is null ? Results.NoContent() : Results.Ok(pick);
        });
        g.MapGet("/onthisday", (string? tz, ClaimsPrincipal p, OnThisDayService s, CancellationToken ct) => s.GetAsync(p.UserId(), tz, ct));

        g.MapPost("/shows/{id:int}/rewatch", async (int id, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            var r = await t.StartRewatchAsync(p.UserId(), id, ct);
            await a.LogAsync(p, ActivityKind.RewatchStart, MediaType.Show, id);
            return Results.Ok(r);
        });
        g.MapDelete("/shows/{id:int}/rewatch", async (int id, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            if (!await t.StopRewatchAsync(p.UserId(), id, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.RewatchStop, MediaType.Show, id);
            return Results.NoContent();
        });

        g.MapPut("/shows/{id:int}/status", async (int id, ShowStatusRequest req, ClaimsPrincipal p, TrackingService t, ActivityService a, CancellationToken ct) =>
        {
            await t.SetShowStatusAsync(p.UserId(), id, req.Status, ct);
            await a.LogAsync(p, ActivityKind.ShowStatus, MediaType.Show, id, detail: req.Status.ToString());
            return Results.NoContent();
        });

        g.MapGet("/checkin", async (ClaimsPrincipal p, CheckInService c, CancellationToken ct) =>
            await c.GetAsync(p.UserId(), ct) is { } now ? Results.Ok(now) : Results.NoContent());
        g.MapPost("/checkin", async (CheckInRequest req, ClaimsPrincipal p, CheckInService c, ActivityService a, CancellationToken ct) =>
        {
            var now = await c.StartAsync(p.UserId(), req, WatchSource.Manual, null, ct);
            await a.LogAsync(p, ActivityKind.CheckIn, req.Type, req.TmdbId, req.SeasonNumber, req.EpisodeNumber);
            return now;
        });
        g.MapDelete("/checkin", async (ClaimsPrincipal p, CheckInService c, ActivityService a, CancellationToken ct) =>
        {
            if (await c.CancelAsync(p.UserId(), ct)) await a.LogAsync(p, ActivityKind.CheckInCancel);
            return Results.NoContent();
        });
        g.MapGet("/calendar", (DateOnly? from, int? days, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            t.GetCalendarAsync(p.UserId(), from ?? DateOnly.FromDateTime(DateTime.UtcNow), days ?? 30, ct));
        g.MapGet("/dashboard", (ClaimsPrincipal p, TrackingService t, CancellationToken ct) => t.GetDashboardAsync(p.UserId(), ct));
        // These read the whole history; they're kept until the user's plays or ratings change (UserDataCache).
        g.MapGet("/stats/more", (string? tz, ClaimsPrincipal p, MoreStatsService s, UserDataCache c, CancellationToken ct) =>
            c.GetAsync(p.UserId(), $"more:{tz}", () => s.GetAsync(p.UserId(), tz, ct), ct));
        g.MapGet("/stats", (string? tz, ClaimsPrincipal p, TrackingService t, UserDataCache c, CancellationToken ct) =>
            c.GetAsync(p.UserId(), $"stats:{tz}", () => t.GetStatsAsync(p.UserId(), tz, ct), ct));

        g.MapGet("/review/periods", (string? tz, ClaimsPrincipal p, ReviewService r, UserDataCache c, CancellationToken ct) =>
            c.GetAsync(p.UserId(), $"periods:{tz}", () => r.GetPeriodsAsync(p.UserId(), tz, ct), ct));
        g.MapGet("/review/{year:int}", (int year, string? tz, ClaimsPrincipal p, ReviewService r, UserDataCache c, CancellationToken ct) =>
            c.GetAsync(p.UserId(), $"review:{year}:{tz}", () => r.BuildAsync(p.UserId(), year, null, tz, ct), ct));
        g.MapGet("/review/{year:int}/{month:int}", (int year, int month, string? tz, ClaimsPrincipal p, ReviewService r, UserDataCache c, CancellationToken ct) =>
            c.GetAsync(p.UserId(), $"review:{year}:{month}:{tz}", () => r.BuildAsync(p.UserId(), year, month, tz, ct), ct));
    }

    // ---------- Lists ----------

    private static void MapLists(IEndpointRouteBuilder app)
    {
        // Public lists can be viewed without signing in.
        app.MapGet("/api/lists/{id:guid}", async (Guid id, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.GetAsync(id, p.OptionalUserId(), ct) is { } list ? Results.Ok(list) : Results.NotFound());

        var g = app.MapGroup("/api/lists").RequireAuthorization();
        g.MapGet("", (ClaimsPrincipal p, ListService l, CancellationToken ct) => l.GetMineAsync(p.UserId(), ct));
        static Task<string?> ListName(AppDbContext db, Guid id, CancellationToken ct) =>
            db.Lists.Where(l => l.Id == id).Select(l => l.Name).FirstOrDefaultAsync(ct);

        g.MapPost("", async (CreateListRequest req, ClaimsPrincipal p, ListService l, ActivityService a, CancellationToken ct) =>
        {
            var list = await l.CreateAsync(p.UserId(), req, ct);
            await a.LogAsync(p, ActivityKind.ListCreate, detail: $"\"{list.Name}\"" + (req.IsPublic ? " (public)" : ""));
            return Results.Ok(new { list.Id });
        });
        g.MapPut("/{id:guid}", async (Guid id, CreateListRequest req, ClaimsPrincipal p, ListService l, ActivityService a, CancellationToken ct) =>
        {
            if (!await l.UpdateAsync(p.UserId(), id, req, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.ListEdit, detail: $"\"{req.Name}\"" + (req.IsPublic ? " (public)" : ""));
            return Results.NoContent();
        });
        g.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal p, ListService l, AppDbContext db, ActivityService a, CancellationToken ct) =>
        {
            var name = await ListName(db, id, ct);
            if (!await l.DeleteAsync(p.UserId(), id, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.ListDelete, detail: $"\"{name}\"");
            return Results.NoContent();
        });
        g.MapPost("/{id:guid}/items", async (Guid id, ListItemRequest req, ClaimsPrincipal p, ListService l, AppDbContext db, ActivityService a, CancellationToken ct) =>
        {
            if (!await l.AddItemAsync(p.UserId(), id, req.Type, req.TmdbId, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.ListAdd, req.Type, req.TmdbId, detail: $"to \"{await ListName(db, id, ct)}\"");
            return Results.NoContent();
        });
        g.MapPut("/{id:guid}/order", async (Guid id, ListOrderRequest req, ClaimsPrincipal p, ListService l, ActivityService a, CancellationToken ct) =>
        {
            if (!await l.ReorderAsync(p.UserId(), id, req.ItemIds, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.ListReorder, detail: $"{req.ItemIds.Count} items");
            return Results.NoContent();
        });
        g.MapPut("/{id:guid}/items/{itemId:guid}/note", async (Guid id, Guid itemId, ListNoteRequest req, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.SetNoteAsync(p.UserId(), id, itemId, req.Note, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/{id:guid}/items", async (Guid id, MediaType type, int tmdbId, ClaimsPrincipal p, ListService l, AppDbContext db, ActivityService a, CancellationToken ct) =>
        {
            if (!await l.RemoveItemAsync(p.UserId(), id, type, tmdbId, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.ListRemove, type, tmdbId, detail: $"from \"{await ListName(db, id, ct)}\"");
            return Results.NoContent();
        });
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

        // Plex posts multipart/form-data with the JSON in "payload" (and a thumbnail we don't need).
        app.MapPost("/api/plex/webhook/{token}", async (string token, HttpRequest request, JellyfinService j, CancellationToken ct) =>
        {
            string? json = null;
            if (request.HasFormContentType) json = (await request.ReadFormAsync(ct))["payload"].FirstOrDefault();
            else { using var reader = new StreamReader(request.Body); json = await reader.ReadToEndAsync(ct); }
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new ProblemInfo("No payload."));
            try { return await j.HandlePlexAsync(token, json, ct) ? Results.Ok() : Results.NotFound(); }
            catch (JsonException) { return Results.BadRequest(new ProblemInfo("The payload isn't JSON.")); }
        }).RequireRateLimiting("webhook").DisableAntiforgery();

        app.MapPost("/api/kodi/webhook/{token}", async (string token, HttpRequest request, JellyfinService j, CancellationToken ct) =>
        {
            JsonElement body;
            try { body = (await JsonDocument.ParseAsync(request.Body, cancellationToken: ct)).RootElement; }
            catch (JsonException) { return Results.BadRequest(new ProblemInfo("Body must be JSON.")); }
            return await j.HandleKodiAsync(token, body, ct) ? Results.Ok() : Results.NotFound();
        }).RequireRateLimiting("webhook");

        var g = app.MapGroup("/api/jellyfin").RequireAuthorization();
        g.MapGet("", async (ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var uid = p.UserId();
            var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
            var events = await db.JellyfinEvents.Where(e => e.UserId == uid).OrderByDescending(e => e.ReceivedAt).Take(20)
                .Select(e => new JellyfinEventDto(e.ReceivedAt, e.ItemName, e.Outcome)).ToListAsync(ct);
            return new JellyfinSettingsDto($"/api/jellyfin/webhook/{user.JellyfinToken}", user.JellyfinThresholdPercent, events,
                $"/api/plex/webhook/{user.JellyfinToken}", user.PlexUsername);
        });
        g.MapPut("/plex-user", async (PlexUserRequest req, ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var uid = p.UserId();
            var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
            user.PlexUsername = string.IsNullOrWhiteSpace(req.UserName) ? null : req.UserName.Trim()[..Math.Min(req.UserName.Trim().Length, 100)];
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        // The Kodi add-on, with this server's address and the user's token already filled in.
        g.MapGet("/kodi-addon", async (ClaimsPrincipal p, AppDbContext db, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            var uid = p.UserId();
            var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
            var bytes = KodiAddon.Build(AuthEndpoints.PublicBase(http, config).TrimEnd('/'), user.JellyfinToken);
            return Results.File(bytes, "application/zip", "service.seenit.zip");
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
        g.MapGet("", (ClaimsPrincipal p, ImportService s, CancellationToken ct) => s.RecentAsync(p.UserId(), ct));
        g.MapPost("/{id:guid}/undo", async (Guid id, ClaimsPrincipal p, ImportService s, ActivityService a, CancellationToken ct) =>
        {
            if (await s.UndoAsync(p.UserId(), id, ct) is not { } r) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.ImportUndo, detail: $"{r.Plays} plays, {r.Ratings} ratings, {r.Watchlist} watchlist, {r.ListItems} list entries, {r.Favorites} favorites, {r.Lists} lists");
            return Results.Ok(r);
        });
        g.MapPost("/{id:guid}/resolve", async (Guid id, ResolveImportRequest req, ClaimsPrincipal p, ImportService s, ActivityService a, CancellationToken ct) =>
        {
            if (await s.ResolveAsync(p.UserId(), id, req, ct) is not { } r) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.ImportResolve, detail: $"{req.Key} → TMDB {req.TmdbId}: {r.Imported} added");
            return Results.Ok(r);
        });

        g.MapPost("/{kind}", async (ImportKind kind, int? parts, IFormFile file, ClaimsPrincipal p, AppDbContext db, ActivityService a, CancellationToken ct) =>
        {
            var what = (ImportParts)(parts ?? (int)ImportParts.All) & ImportParts.All;
            if ((what & (ImportParts.Movies | ImportParts.Shows)) == 0 || (what & ~(ImportParts.Movies | ImportParts.Shows)) == 0)
                return Results.BadRequest(new ProblemInfo("Pick at least one kind of title and one kind of data to import."));
            if (file.Length == 0) return Results.BadRequest(new ProblemInfo("That file is empty."));
            if (file.Length > MaxImportBytes) return Results.BadRequest(new ProblemInfo("Exports over 50 MB aren't supported."));
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".zip" or ".json" or ".csv")) return Results.BadRequest(new ProblemInfo("Upload the .zip export (or a .json/.csv file from it)."));

            var uid = p.UserId();
            if (await db.ImportJobs.CountAsync(j => j.UserId == uid && (j.Status == ImportStatus.Queued || j.Status == ImportStatus.Running), ct) >= 2)
                return Results.BadRequest(new ProblemInfo("Wait for your current imports to finish first."));

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var job = new ImportJob { UserId = uid, Kind = kind, Parts = what, FileName = Path.GetFileName(file.FileName), Payload = ms.ToArray(), Status = ImportStatus.Queued };
            db.ImportJobs.Add(job);
            await db.SaveChangesAsync(ct);
            await a.LogAsync(p, ActivityKind.Import, detail: $"{kind} upload {job.FileName} ({file.Length / 1024:N0} KB), {job.Parts}");
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
        g.MapPost("/follow/{name}", async (string name, ClaimsPrincipal p, SocialService s, ActivityService a, CancellationToken ct) =>
        {
            if (!await s.FollowAsync(p.UserId(), name, true, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.Follow, detail: name);
            return Results.NoContent();
        });
        g.MapDelete("/follow/{name}", async (string name, ClaimsPrincipal p, SocialService s, ActivityService a, CancellationToken ct) =>
        {
            if (!await s.FollowAsync(p.UserId(), name, false, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.Unfollow, detail: name);
            return Results.NoContent();
        });
        g.MapGet("/compare/{a}/{b}", async (string a, string b, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.CompareAsync(p.UserId(), a, b, ct) is { } c ? Results.Ok(c) : Results.NotFound());
        // Older app versions: you vs them.
        g.MapGet("/compare/{name}", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.CompareAsync(p.UserId(), p.Identity?.Name ?? "", name, ct) is { } c ? Results.Ok(c) : Results.NotFound());
    }
}
