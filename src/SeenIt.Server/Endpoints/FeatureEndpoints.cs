using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Server.Endpoints;

/// <summary>Goals, the Letterboxd feed sync, searching your lists, and share images.</summary>
public static class FeatureEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ----- Goals -----
        // Movie night polls: yours and ones you're asked to.
        var polls = api.MapGroup("/polls");
        polls.MapGet("", (ClaimsPrincipal p, PollService s, CancellationToken ct) => s.ListAsync(p.UserId(), ct));
        polls.MapPost("", async (PollRequest req, ClaimsPrincipal p, PollService s, CancellationToken ct) => Results.Ok(new { id = await s.CreateAsync(p.UserId(), req, ct) }));
        polls.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal p, PollService s, CancellationToken ct) =>
            await s.GetAsync(p.UserId(), id, ct) is { } poll ? Results.Ok(poll) : Results.NotFound());
        polls.MapPut("/{id:guid}/votes", async (Guid id, PollVoteRequest req, ClaimsPrincipal p, PollService s, CancellationToken ct) =>
        {
            await s.VoteAsync(p.UserId(), id, req, ct);
            return Results.NoContent();
        });
        polls.MapPost("/{id:guid}/close", async (Guid id, ClaimsPrincipal p, PollService s, CancellationToken ct) =>
        {
            await s.CloseAsync(p.UserId(), id, ct);
            return Results.NoContent();
        });
        polls.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal p, PollService s, CancellationToken ct) =>
            await s.DeleteAsync(p.UserId(), id, ct) ? Results.NoContent() : Results.NotFound());

        // Yearly challenges (every one, worked out; Goals shows the ones you've taken on, UserPrefs.Challenges).
        api.MapGet("/challenges", (int? year, string? tz, ClaimsPrincipal p, ChallengeService c, CancellationToken ct) =>
            c.GetAsync(p.UserId(), year ?? DateTime.UtcNow.Year, tz, ct));
        var goals = api.MapGroup("/goals");
        goals.MapGet("", (int? year, string? tz, ClaimsPrincipal p, GoalService g, CancellationToken ct) =>
            g.GetAsync(p.UserId(), year ?? DateTime.UtcNow.Year, tz, ct));
        goals.MapPut("", async (GoalRequest req, ClaimsPrincipal p, GoalService g, ActivityService a, CancellationToken ct) =>
        {
            await g.SetAsync(p.UserId(), req, ct);
            await a.LogAsync(p, ActivityKind.GoalSet, detail: $"{req.Year}: {req.Target} {req.Kind}");
            return Results.NoContent();
        });
        goals.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal p, GoalService g, ActivityService a, CancellationToken ct) =>
        {
            if (!await g.DeleteAsync(p.UserId(), id, ct)) return Results.NotFound();
            await a.LogAsync(p, ActivityKind.GoalDelete);
            return Results.NoContent();
        });

        // ----- Letterboxd feed sync -----
        var lb = api.MapGroup("/letterboxd-sync");
        lb.MapGet("", async (ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var uid = p.UserId();
            return LetterboxdSyncService.ToDto(await db.LetterboxdSyncs.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == uid, ct));
        });
        // Turning it on (or changing the name) checks the feed right away.
        lb.MapPut("", async (LetterboxdSyncRequest req, ClaimsPrincipal p, AppDbContext db, LetterboxdSyncService svc, CancellationToken ct) =>
        {
            var name = req.Username?.Trim().TrimStart('@') ?? "";
            if (name.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || name.Contains("letterboxd.com/", StringComparison.OrdinalIgnoreCase))
                name = name.Split("letterboxd.com/", 2)[^1].Split('/')[0];
            if (!LetterboxdSyncService.ValidUsername(name)) throw new TrackingException("That isn't a Letterboxd username.");
            await svc.FetchAsync(name, ct);   // throws a readable error if there's no such member

            var uid = p.UserId();
            var sync = await db.LetterboxdSyncs.FirstOrDefaultAsync(s => s.UserId == uid, ct);
            if (sync is null) db.LetterboxdSyncs.Add(sync = new LetterboxdSync { UserId = uid, Username = name });
            else if (!string.Equals(sync.Username, name, StringComparison.OrdinalIgnoreCase)) { sync.Username = name; sync.SeenGuids = []; }
            await db.SaveChangesAsync(ct);
            await svc.SyncAsync(sync, ct);
            return LetterboxdSyncService.ToDto(sync);
        });
        lb.MapPost("/now", async (ClaimsPrincipal p, AppDbContext db, LetterboxdSyncService svc, CancellationToken ct) =>
        {
            var uid = p.UserId();
            if (await db.LetterboxdSyncs.FirstOrDefaultAsync(s => s.UserId == uid, ct) is not { } sync) return Results.NotFound();
            if (sync.LastCheckedAt > DateTimeOffset.UtcNow.AddMinutes(-1)) return Results.Ok(LetterboxdSyncService.ToDto(sync));
            await svc.SyncAsync(sync, ct);
            return Results.Ok(LetterboxdSyncService.ToDto(sync));
        });
        lb.MapDelete("", async (ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var uid = p.UserId();
            await db.LetterboxdSyncs.Where(s => s.UserId == uid).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // ----- Search your watchlist and lists -----
        api.MapGet("/lists/search", (string? q, ClaimsPrincipal p, ListService l, CancellationToken ct) => l.SearchAsync(p.UserId(), q, ct));

        // ----- Share images -----
        var share = api.MapGroup("/share");
        share.MapGet("/review/{year:int}", async (int year, int? month, string? tz, [AsParameters] ReviewCardOptions options, ClaimsPrincipal p, ShareCardService s, CancellationToken ct) =>
            Results.File(await s.ReviewAsync(p.UserId(), year, month, tz, ct, options), "image/png",
                month is null ? $"seenit-{year}.png" : $"seenit-{year}-{month:00}.png"));
        // One day, month or year of your history (the History page's filters, From/To narrowed to that stretch), spoiler-free.
        share.MapGet("/history", async (MediaType? type, string? q, WatchSource? source, string? genre, DateTimeOffset? from, DateTimeOffset? to,
                string? tz, string? label, ClaimsPrincipal p, ShareCardService s, CancellationToken ct) =>
            Results.File(await s.HistoryAsync(p.UserId(), new HistoryQuery(type, q, source, genre, from, to, TimeZone: tz), label ?? "History", ct),
                "image/png", "seenit-history.png"));
        share.MapGet("/compare/{a}/{b}", async (string a, string b, ClaimsPrincipal p, ShareCardService s, CancellationToken ct) =>
            await s.CompareAsync(p.UserId(), a, b, ct) is { } png
                ? Results.File(png, "image/png", $"seenit-{a}-{b}.png")
                : Results.NotFound());
    }
}
