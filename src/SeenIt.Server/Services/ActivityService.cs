using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Endpoints;

namespace SeenIt.Server.Services;

/// <summary>
/// Writes the admin activity log. Logging must never break the action it describes, so failures
/// are only logged. Uses its own context: the caller's may hold unsaved or failed changes.
/// </summary>
public class ActivityService(IDbContextFactory<AppDbContext> factory, ILogger<ActivityService> log)
{
    public async Task LogAsync(Guid userId, ActivityKind kind, MediaType? type = null, int? tmdbId = null,
        int? season = null, int? episode = null, string? detail = null, WatchSource source = WatchSource.Manual,
        string? userName = null)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            userName ??= await db.Users.Where(u => u.Id == userId).Select(u => u.UserName).FirstOrDefaultAsync() ?? "?";
            db.Activity.Add(new ActivityEntry
            {
                UserId = userId, UserName = userName, Kind = kind, Source = source,
                Type = type, TmdbId = tmdbId, SeasonNumber = season, EpisodeNumber = episode,
                Detail = detail is { Length: > 500 } ? detail[..500] : detail,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't write activity {Kind} for {User}", kind, userId);
        }
    }

    public Task LogAsync(ClaimsPrincipal p, ActivityKind kind, MediaType? type = null, int? tmdbId = null,
        int? season = null, int? episode = null, string? detail = null) =>
        LogAsync(p.UserId(), kind, type, tmdbId, season, episode, detail, WatchSource.Manual, p.Identity?.Name);

    /// <summary>"custom date 2 Mar 2019", "release date", … for a watch request.</summary>
    public static string? When(WatchTime? when, DateTimeOffset? at) => (when, at) switch
    {
        (WatchTime.ReleaseDate, _) => "at release date",
        (WatchTime.Unknown, _) => "date unknown",
        (WatchTime.Custom or null, { } d) => $"on {d:yyyy-MM-dd HH:mm zzz}",
        _ => null,
    };
}
