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
    /// <summary>History's ?sort=: "title" and "episodes" only order the one-per-show view ("oldest" is its own flag).</summary>
    private static string? ShowSortOf(string? sort) => sort is "title" or "episodes" ? sort : null;

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

        api.MapGet("/export", async (ExportFormat? format, ClaimsPrincipal p, ExportService export, CancellationToken ct) =>
            Results.File(await export.BuildAsync(p.UserId(), ct, format ?? ExportFormat.Both), "application/zip", $"seenit-export-{DateTime.UtcNow:yyyy-MM-dd}.zip"));
    }

    // ---------- Catalog: public, with your own state when signed in ----------

    private static void MapCatalog(RouteGroupBuilder g)
    {
        g.MapGet("/search", (string q, string? type, int? page, CatalogService c, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q)
                ? Task.FromResult(new PagedResult<MediaSummary>([], 1, 0, 0))
                : c.SearchAsync(q.Trim(), type, page ?? 1, ct));

        // Other sites' scores (IMDb, Rotten Tomatoes, Metacritic...), loaded after the page.
        g.MapGet("/movie/{id:int}/scores", (int id, ScoreService s, CancellationToken ct) => s.GetAsync(MediaType.Movie, id, ct));
        g.MapGet("/show/{id:int}/scores", (int id, ScoreService s, CancellationToken ct) => s.GetAsync(MediaType.Show, id, ct));
        g.MapGet("/show/{id:int}/season/{season:int}/episode/{episode:int}/scores", (int id, int season, int episode, ScoreService s, CancellationToken ct) =>
            s.EpisodeAsync(id, season, episode, ct));
        g.MapPost("/scores/badges", (BadgeRequest req, ScoreService s, CancellationToken ct) => s.BadgesAsync(req, ct));
        g.MapGet("/show/{id:int}/season/{season:int}/scores", (int id, int season, ScoreService s, CancellationToken ct) => s.SeasonAsync(id, season, ct));

        g.MapGet("/lookup", async (string q, CatalogService c, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) || await c.LookupAsync(q, ct) is not { } found ? Results.NoContent() : Results.Ok(found));

        g.MapGet("/genres", (SmartListService s, CancellationToken ct) => s.GenresAsync(ct));

        // Search-as-you-type. Short-lived browser caching makes backspacing free.
        g.MapGet("/suggest", async (string? q, HttpContext http, SuggestService s, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return string.IsNullOrWhiteSpace(q) || q.Length > 100 ? new List<Suggestion>() : await s.SuggestAsync(q, ct);
        });

        g.MapGet("/search/people", (string q, int? page, CatalogService c, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q)
                ? Task.FromResult(new PagedResult<PersonHit>([], 1, 0, 0))
                : c.SearchPeopleAsync(q.Trim(), page ?? 1, ct));

        g.MapGet("/browse/{type}/{*list}", async (MediaType type, string list, int? page, string? region, ClaimsPrincipal p, AppDbContext db, CatalogService c, CancellationToken ct) =>
        {
            // In theaters / upcoming depend on the country, like where to watch.
            var saved = p.OptionalUserId() is { } uid ? await db.Users.Where(u => u.Id == uid).Select(u => u.Region).FirstOrDefaultAsync(ct) : null;
            var code = saved ?? (region is { Length: 2 } r && r.All(char.IsLetter) ? r.ToUpperInvariant() : "US");
            var result = await c.BrowseAsync(type, list, page ?? 1, ct, code);
            var hidden = await c.NotInterestedAsync(p.OptionalUserId(), ct);
            return hidden.Count == 0 ? result : result with { Items = result.Items.Where(m => !hidden.Contains((m.Type, m.TmdbId))).ToList() };
        });

        // "You might also like" leaves out what you marked not interested.
        g.MapGet("/movie/{id:int}", async (int id, ClaimsPrincipal p, CatalogService c, CancellationToken ct) =>
        {
            if (await c.GetMovieAsync(id, p.OptionalUserId(), ct) is not { } m) return Results.NotFound();
            var hidden = await c.NotInterestedAsync(p.OptionalUserId(), ct);
            return Results.Ok(hidden.Count == 0 ? m : m with { Recommendations = m.Recommendations.Where(r => !hidden.Contains((r.Type, r.TmdbId))).ToList() });
        });

        g.MapGet("/show/{id:int}", async (int id, ClaimsPrincipal p, CatalogService c, CancellationToken ct) =>
        {
            if (await c.GetShowAsync(id, p.OptionalUserId(), ct) is not { } s) return Results.NotFound();
            var hidden = await c.NotInterestedAsync(p.OptionalUserId(), ct);
            return Results.Ok(hidden.Count == 0 ? s : s with { Recommendations = s.Recommendations.Where(r => !hidden.Contains((r.Type, r.TmdbId))).ToList() });
        });

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
                string? sort, int? page, int? pageSize, string? tz, string? group, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            t.GetHistoryAsync(p.UserId(), new HistoryQuery(type, q, source, genre, from, to, sort == "oldest", page ?? 1, pageSize ?? 50, tz, group == "show", ShowSortOf(sort)), ct));
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
        g.MapGet("/watchlist/contains", async (MediaType type, int tmdbId, ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var me = p.UserId();
            return new { OnWatchlist = await db.Watchlist.AnyAsync(w => w.UserId == me && w.Type == type && w.TmdbId == tmdbId, ct) };
        });
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
        g.MapGet("/live", (HttpContext http, ClaimsPrincipal p, LiveHub hub, CancellationToken ct) => LiveHub.StreamAsync(http, p.UserId(), hub, ct));
        g.MapGet("/status/{type}/{id:int}", (MediaType type, int id, int? season, int? episode, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            t.ItemStatusAsync(p.UserId(), type, id, season, episode, ct));
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

        // Alternative watch orders (TMDB episode groups).
        g.MapGet("/shows/{id:int}/orders", (int id, ClaimsPrincipal p, WatchOrderService w, CancellationToken ct) => w.OptionsAsync(id, p.UserId(), ct));
        g.MapGet("/shows/{id:int}/order", async (int id, ClaimsPrincipal p, WatchOrderService w, CancellationToken ct) =>
            await w.DetailAsync(p.UserId(), id, ct) is { } order ? Results.Ok(order) : Results.NoContent());
        g.MapPut("/shows/{id:int}/order", async (int id, SetWatchOrderRequest req, ClaimsPrincipal p, WatchOrderService w, ActivityService a, CancellationToken ct) =>
        {
            await w.SetAsync(p.UserId(), id, req.OrderId, ct);
            await a.LogAsync(p, ActivityKind.WatchOrder, MediaType.Show, id, detail: req.OrderId ?? "TMDB seasons");
            return Results.NoContent();
        });
        g.MapPost("/shows/{id:int}/watch-through", async (int id, WatchThroughRequest req, ClaimsPrincipal p, WatchOrderService w, ActivityService a, CancellationToken ct) =>
        {
            var n = await w.WatchThroughAsync(p.UserId(), id, req, ct);
            if (n > 0) await a.LogAsync(p, ActivityKind.BulkWatch, MediaType.Show, id, req.SeasonNumber, req.EpisodeNumber,
                $"{n} episodes up to here{(req.InOrder ? " in watch order" : "")}");
            return Results.Ok(new { added = n });
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
        // "Not interested" (also takes the title off the watchlist) and recommendations.
        g.MapGet("/not-interested", async (ClaimsPrincipal p, AppDbContext db, TrackingService t, CancellationToken ct) =>
        {
            var rows = await db.NotInterested.AsNoTracking().Where(n => n.UserId == p.UserId()).OrderByDescending(n => n.CreatedAt).ToListAsync(ct);
            var (movies, shows) = await t.LoadMetaAsync(rows.Select(r => (r.Type, r.TmdbId)), ct);
            return rows.Select(r => r.Type == MediaType.Movie
                    ? movies.TryGetValue(r.TmdbId, out var m) ? new NotInterestedEntry(CatalogService.ToSummary(m), r.CreatedAt) : null
                    : shows.TryGetValue(r.TmdbId, out var s) ? new NotInterestedEntry(CatalogService.ToSummary(s), r.CreatedAt) : null)
                .OfType<NotInterestedEntry>().ToList();
        });
        g.MapPost("/not-interested", async (FavoriteRequest req, ClaimsPrincipal p, AppDbContext db, CatalogService c, TrackingService t, CancellationToken ct) =>
        {
            if (req.Type is not (MediaType.Movie or MediaType.Show)) return Results.BadRequest(new ProblemInfo("Only movies and shows."));
            var meta = req.Type == MediaType.Movie ? (object?)await c.EnsureMovieAsync(req.TmdbId, ct) : await c.EnsureShowAsync(req.TmdbId, ct);
            if (meta is null) return Results.NotFound();
            if (!await db.NotInterested.AnyAsync(n => n.UserId == p.UserId() && n.Type == req.Type && n.TmdbId == req.TmdbId, ct))
            {
                db.NotInterested.Add(new NotInterested { UserId = p.UserId(), Type = req.Type, TmdbId = req.TmdbId });
                await db.SaveChangesAsync(ct);
            }
            await t.RemoveFromWatchlistAsync(p.UserId(), req.Type, req.TmdbId, ct);
            return Results.NoContent();
        });
        g.MapDelete("/not-interested", async (MediaType type, int tmdbId, ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            await db.NotInterested.Where(n => n.UserId == p.UserId() && n.Type == type && n.TmdbId == tmdbId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
        // Your library: what you own (by hand, or added by Plex/Jellyfin).
        g.MapGet("/library", (ClaimsPrincipal p, LibraryService l, CancellationToken ct) => l.ListAsync(p.UserId(), ct));
        g.MapPost("/library", async (LibraryRequest req, ClaimsPrincipal p, LibraryService l, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(req.Format)) return Results.BadRequest(new ProblemInfo("Unknown format."));
            await l.AddAsync(p.UserId(), req.Type, req.TmdbId, req.Format, WatchSource.Manual, ct);
            return Results.NoContent();
        });
        g.MapDelete("/library", async (MediaType type, int tmdbId, ClaimsPrincipal p, LibraryService l, CancellationToken ct) =>
        {
            await l.RemoveAsync(p.UserId(), type, tmdbId, ct);
            return Results.NoContent();
        });
        // Private notes on movies and shows.
        g.MapPut("/notes", async (NoteRequest req, ClaimsPrincipal p, AppDbContext db, TrackingService t, CancellationToken ct) =>
        {
            if (req.Type is not (MediaType.Movie or MediaType.Show)) return Results.BadRequest(new ProblemInfo("Notes go on movies and shows."));
            var text = req.Text?.Trim() ?? "";
            if (text.Length > 2000) text = text[..2000];
            var row = await db.Notes.FirstOrDefaultAsync(n => n.UserId == p.UserId() && n.Type == req.Type && n.TmdbId == req.TmdbId, ct);
            if (text.Length == 0) { if (row is not null) db.Notes.Remove(row); }
            else
            {
                await t.EnsureMetaAsync(req.Type, req.TmdbId, ct);
                if (row is null) db.Notes.Add(new TitleNote { UserId = p.UserId(), Type = req.Type, TmdbId = req.TmdbId, Text = text });
                else { row.Text = text; row.UpdatedAt = DateTimeOffset.UtcNow; }
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        g.MapGet("/progress/franchises", (ClaimsPrincipal p, MoreStatsService m, CancellationToken ct) => m.FranchiseProgressAsync(p.UserId(), ct));
        g.MapGet("/recommendations", (ClaimsPrincipal p, RecommendationService r, CancellationToken ct) => r.ForYouAsync(p.UserId(), ct));

        g.MapGet("/notifications", async (ClaimsPrincipal p, AppDbContext db, ReleaseAlertService a, CancellationToken ct) =>
        {
            // Opening the page picks up today's releases straight away, rather than at the next hourly check.
            if (await db.Users.FirstOrDefaultAsync(u => u.Id == p.UserId(), ct) is { } me) await a.ScanAsync(me, ct);
            return await a.ListAsync(p.UserId(), ct);
        });
        g.MapGet("/notifications/unread", async (ClaimsPrincipal p, ReleaseAlertService a, CancellationToken ct) => new { unread = await a.UnreadAsync(p.UserId(), ct) });
        g.MapPost("/notifications/read", async (ClaimsPrincipal p, ReleaseAlertService a, CancellationToken ct) =>
        {
            await a.MarkAllReadAsync(p.UserId(), ct);
            return Results.NoContent();
        });

        // Recommending titles to friends.
        g.MapGet("/friend-recs", (ClaimsPrincipal p, FriendRecommendationService r, CancellationToken ct) => r.ListAsync(p.UserId(), ct));
        g.MapGet("/friend-recs/recipients", (ClaimsPrincipal p, FriendRecommendationService r, CancellationToken ct) => r.RecipientsAsync(p.UserId(), ct));
        g.MapGet("/friend-recs/{type}/{id:int}", (MediaType type, int id, ClaimsPrincipal p, FriendRecommendationService r, CancellationToken ct) =>
            r.ForTitleAsync(p.UserId(), type, id, ct));
        g.MapPost("/friend-recs", (RecommendRequest req, ClaimsPrincipal p, FriendRecommendationService r, CancellationToken ct) => r.SendAsync(p.UserId(), req, ct));
        g.MapPost("/friend-recs/{id:guid}/dismiss", async (Guid id, bool? undo, ClaimsPrincipal p, FriendRecommendationService r, CancellationToken ct) =>
            await r.DismissAsync(p.UserId(), id, undo != true, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/friend-recs/{id:guid}", async (Guid id, ClaimsPrincipal p, FriendRecommendationService r, CancellationToken ct) =>
            await r.UnsendAsync(p.UserId(), id, ct) ? Results.NoContent() : Results.NotFound());

        // Following actors, directors and writers.
        g.MapGet("/people/following", (ClaimsPrincipal p, PersonService people, CancellationToken ct) => people.FollowedAsync(p.UserId(), ct));
        g.MapPut("/people/{id:int}/follow", async (int id, PersonFollowRequest req, ClaimsPrincipal p, PersonService people, CancellationToken ct) =>
            await people.FollowAsync(p.UserId(), id, req.Follow, ct) ? Results.NoContent() : Results.NotFound());

        // "Previously on": a recap when you come back to a show after a while (204 when there's nothing to recap).
        g.MapGet("/shows/{id:int}/recap", async (int id, ClaimsPrincipal p, TrackingService t, CancellationToken ct) =>
            await t.RecapAsync(p.UserId(), id, ct) is { } recap ? Results.Ok(recap) : Results.NoContent());
        static AlertSettingsDto Alerts(AppUser u, ReleaseAlertService a, EmailService email) => new(u.ReleaseEmails, email.Enabled,
            $"{a.BaseUrl()}cal/{u.CalendarToken}.ics", u.MonthlyBackup,
            $"{a.BaseUrl()}feeds/{u.CalendarToken}/watchlist.rss", $"{a.BaseUrl()}feeds/{u.CalendarToken}/history.rss");
        // Push notifications for alerts (browsers and installed web apps).
        g.MapGet("/push", async (ClaimsPrincipal p, PushService push, CancellationToken ct) =>
            new PushConfigDto(push.Enabled, push.PublicKey, await push.DevicesAsync(p.UserId(), ct)));
        g.MapPost("/push/subscribe", async (PushSubscribeRequest req, ClaimsPrincipal p, PushService push, CancellationToken ct) =>
        {
            await push.SubscribeAsync(p.UserId(), req, ct);
            return Results.NoContent();
        });
        g.MapPost("/push/unsubscribe", async (PushUnsubscribeRequest req, ClaimsPrincipal p, PushService push, CancellationToken ct) =>
        {
            await push.UnsubscribeAsync(p.UserId(), req.Endpoint, ct);
            return Results.NoContent();
        });
        g.MapPost("/push/test", async (ClaimsPrincipal p, PushService push, CancellationToken ct) =>
            Results.Ok(new { sent = await push.SendAsync(p.UserId(), new PushPayload("SeenIt", "Push notifications are working on this device.", "notifications", "test"), ct) }));
        g.MapGet("/alerts", async (ClaimsPrincipal p, AppDbContext db, ReleaseAlertService a, EmailService email, CancellationToken ct) =>
            Alerts(await db.Users.AsNoTracking().FirstAsync(x => x.Id == p.UserId(), ct), a, email));
        g.MapPut("/alerts", async (AlertSettingsRequest req, ClaimsPrincipal p, HttpContext http, AppDbContext db, ReleaseAlertService a, EmailService email, CancellationToken ct) =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == p.UserId(), ct);
            if (req.Emails is { } on) u.ReleaseEmails = on;
            if (req.NewCalendarUrl) u.CalendarToken = AppUser.NewToken();
            if (req.MonthlyBackup is { } backup) u.MonthlyBackup = backup;
            await db.SaveChangesAsync(ct);
            // Turning emails on today still sends today's digest.
            if (req.Emails == true) { u.LastReleaseEmail = null; await db.SaveChangesAsync(ct); await a.ScanAsync(u, ct); }
            // Turning backups on sends this period's straight away, so you can see it works.
            if (req.MonthlyBackup == true && email.Enabled && u.Email is { Length: > 0 } to)
                await MonthlyBackupWorker.SendAsync(http.RequestServices, db, email, u, to, MonthlyBackupWorker.PeriodStart(u), ct);
            return Alerts(u, a, email);
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

        // Everyone's public lists, most saved first (or most recently updated).
        app.MapGet("/api/lists/browse", (string? q, string? sort, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            l.BrowseAsync(p.OptionalUserId(), q, sort == "recent", ct));

        var g = app.MapGroup("/api/lists").RequireAuthorization();
        g.MapGet("", (ClaimsPrincipal p, ListService l, CancellationToken ct) => l.GetMineAsync(p.UserId(), ct));
        g.MapGet("/saved", (ClaimsPrincipal p, ListService l, CancellationToken ct) => l.SavedAsync(p.UserId(), ct));
        // Feed links for one of your lists (private ones include your key; anyone with the link can read it).
        g.MapGet("/{id:guid}/feeds", async (Guid id, ClaimsPrincipal p, AppDbContext db, ReleaseAlertService a, CancellationToken ct) =>
        {
            var list = await db.Lists.AsNoTracking().Where(l => l.Id == id).Select(l => new { l.UserId, l.IsPublic }).FirstOrDefaultAsync(ct);
            if (list is null || (list.UserId != p.UserId() && !list.IsPublic)) return Results.NotFound();
            var key = list.IsPublic ? "" : "?key=" + await db.Users.Where(u => u.Id == list.UserId).Select(u => u.CalendarToken).FirstAsync(ct);
            return Results.Ok(new ListFeedsDto($"{a.BaseUrl()}feeds/lists/{id}.rss{key}", $"{a.BaseUrl()}feeds/lists/{id}.json{key}"));
        });
        g.MapPost("/{id:guid}/save", async (Guid id, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.SaveAsync(p.UserId(), id, true, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/{id:guid}/save", async (Guid id, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.SaveAsync(p.UserId(), id, false, ct) ? Results.NoContent() : Results.NotFound());
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
        g.MapPost("/{id:guid}/restore", async (Guid id, ClaimsPrincipal p, ListService l, CancellationToken ct) =>
            await l.RestoreAsync(p.UserId(), id, ct) ? Results.NoContent() : Results.NotFound());
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

        // What a profile's widgets show (only the ones this viewer may see).
        app.MapGet("/api/users/{name}/widgets", async (string name, string? preview, ClaimsPrincipal p, ProfileWidgetService w, CancellationToken ct) =>
            await w.DataAsync(name, p.OptionalUserId(), preview?.Split(',', StringSplitOptions.RemoveEmptyEntries), ct) is { } data ? Results.Ok(data) : Results.NotFound());

        // RSS / JSON Feed: lists (public ones need no key) and, with your private key, watchlist and history.
        static IResult FeedResult(FeedService.Feed? f, string format, HttpContext http) => f is null ? Results.NotFound()
            : format == "json" ? Results.Text(FeedService.JsonFeed(f, $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}{http.Request.QueryString}"), "application/feed+json; charset=utf-8")
            : Results.Text(FeedService.Rss(f), "application/rss+xml; charset=utf-8");
        app.MapGet("/feeds/lists/{id:guid}.{format:regex(^(rss|json)$)}", async (Guid id, string format, string? key, HttpContext http, FeedService feeds, CancellationToken ct) =>
            FeedResult(await feeds.ListAsync(id, key, ct), format, http));
        app.MapGet("/feeds/{key}/watchlist.{format:regex(^(rss|json)$)}", async (string key, string format, HttpContext http, FeedService feeds, CancellationToken ct) =>
            FeedResult(await feeds.WatchlistAsync(key, ct), format, http));
        app.MapGet("/feeds/{key}/history.{format:regex(^(rss|json)$)}", async (string key, string format, HttpContext http, FeedService feeds, CancellationToken ct) =>
            FeedResult(await feeds.HistoryAsync(key, ct), format, http));

        // Private calendar feed (subscribe in Google/Apple Calendar). The token in the URL is the only key.
        app.MapGet("/cal/{token}.ics", async (string token, ReleaseAlertService alerts, CancellationToken ct) =>
            await alerts.CalendarAsync(token, ct) is { } ics ? Results.Text(ics, "text/calendar; charset=utf-8") : Results.NotFound());

        // Profile pictures are public, like usernames. The URL carries a version, so it can be cached for good.
        app.MapGet("/api/users/{name}/avatar", async (string name, HttpContext http, AvatarService avatars, CancellationToken ct) =>
        {
            if (await avatars.GetAsync(name, ct) is not { } jpeg) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(jpeg, "image/jpeg");
        });

        // Who someone follows / who follows them (hidden only for private profiles you're not friends with).
        app.MapGet("/api/users/{name}/following", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.ConnectionsOfAsync(name, p.OptionalUserId(), ct) is { } c ? Results.Ok(c.Following) : Results.NotFound());
        app.MapGet("/api/users/{name}/followers", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.ConnectionsOfAsync(name, p.OptionalUserId(), ct) is { } c ? Results.Ok(c.Followers) : Results.NotFound());

        // Someone's full history, read-only: visible to whoever can see their profile.
        app.MapGet("/api/users/{name}/history", async (string name, MediaType? type, string? q, WatchSource? source, string? genre, DateTimeOffset? from, DateTimeOffset? to,
                string? sort, int? page, int? pageSize, string? tz, string? group, ClaimsPrincipal p, SocialService s, TrackingService t, CancellationToken ct) =>
        {
            if (await s.ViewableAsync(name, p.OptionalUserId(), ct) is not { } owner) return Results.NotFound();
            var history = await t.GetHistoryAsync(owner.Id, new HistoryQuery(type, q, source, genre, from, to, sort == "oldest", page ?? 1, Math.Clamp(pageSize ?? 50, 1, 5000), tz, group == "show", ShowSortOf(sort)), ct);
            return Results.Ok(owner.Id == p.OptionalUserId() ? history : history with { Items = await t.MarkSeenByAsync(p.OptionalUserId(), history.Items, ct) });
        });
        app.MapGet("/api/users/{name}/history/genres", async (string name, ClaimsPrincipal p, SocialService s, TrackingService t, CancellationToken ct) =>
            await s.ViewableAsync(name, p.OptionalUserId(), ct) is { } owner ? Results.Ok(await t.GetHistoryGenresAsync(owner.Id, ct)) : Results.NotFound());

        var g = app.MapGroup("/api/social").RequireAuthorization();
        g.MapGet("/feed", (int? take, ClaimsPrincipal p, SocialService s, CancellationToken ct) => s.FeedAsync(p.UserId(), take ?? 40, ct));
        g.MapGet("/history", (MediaType? type, string? q, WatchSource? source, string? genre, DateTimeOffset? from, DateTimeOffset? to,
                string? sort, int? page, int? pageSize, string? tz, string? group, string? user, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            s.FriendsHistoryAsync(p.UserId(), new HistoryQuery(type, q, source, genre, from, to, sort == "oldest", page ?? 1, Math.Clamp(pageSize ?? 50, 1, 5000), tz, group == "show", ShowSortOf(sort)), user, ct));
        g.MapGet("/history/genres", (ClaimsPrincipal p, SocialService s, CancellationToken ct) => s.FriendsHistoryGenresAsync(p.UserId(), ct));
        g.MapGet("/search", (string q, ClaimsPrincipal p, SocialService s, CancellationToken ct) => s.SearchAsync(p.UserId(), q, ct));
        g.MapGet("/seen/{type}/{id:int}", (MediaType type, int id, int? season, int? episode, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            s.SeenByAsync(p.UserId(), type, id, season, episode, ct));
        g.MapGet("/following", async (ClaimsPrincipal p, SocialService s, CancellationToken ct) => (await s.ConnectionsAsync(p.UserId(), ct)).Following);
        g.MapGet("/followers", async (ClaimsPrincipal p, SocialService s, CancellationToken ct) => (await s.ConnectionsAsync(p.UserId(), ct)).Followers);
        g.MapPost("/follow/{name}", async (string name, ClaimsPrincipal p, SocialService s, ActivityService a, CancellationToken ct) =>
        {
            if (await s.FollowAsync(p.UserId(), name, true, ct) is not { } result) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.Follow, detail: result.Requested ? $"{name} (requested)" : name);
            return Results.Ok(result);
        });
        g.MapGet("/requests", (ClaimsPrincipal p, SocialService s, CancellationToken ct) => s.RequestsAsync(p.UserId(), ct));
        // Your profile's widgets: save a layout, or go back to the default one.
        g.MapPut("/profile-layout", (ProfileLayoutRequest r, ClaimsPrincipal p, ProfileWidgetService w, CancellationToken ct) => w.SaveAsync(p.UserId(), r.Widgets, ct, r.Columns));
        g.MapDelete("/profile-layout", (ClaimsPrincipal p, ProfileWidgetService w, CancellationToken ct) => w.SaveAsync(p.UserId(), null, ct));
        g.MapPost("/requests/{name}", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.AnswerRequestAsync(p.UserId(), name, true, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/requests/{name}", async (string name, ClaimsPrincipal p, SocialService s, CancellationToken ct) =>
            await s.AnswerRequestAsync(p.UserId(), name, false, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/follow/{name}", async (string name, ClaimsPrincipal p, SocialService s, ActivityService a, CancellationToken ct) =>
        {
            if (await s.FollowAsync(p.UserId(), name, false, ct) is null) return Results.NotFound();
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
