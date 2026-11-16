using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Keeps expensive per-user results (stats, reviews) until the user's data changes. Rather than
/// hooking every write path, each read takes a cheap fingerprint of the user's plays, ratings, show
/// statuses, rewatches and settings (counts, sums and latest times, one indexed query); any add, delete,
/// re-date, re-rate, pause/drop/resume or rewatch changes it. Entries also expire after a while, so metadata refreshes show up.
/// </summary>
public class UserDataCache(IMemoryCache cache, AppDbContext db)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private sealed record Fingerprint(long Plays, double PlaySum, DateTimeOffset? LastAdded, long Ratings, double RatingSum, DateTimeOffset? LastRated,
        long States, long StateSum, DateTimeOffset? LastState, long Rewatches, DateTimeOffset? LastRewatch);

    public async Task<string> FingerprintAsync(Guid userId, CancellationToken ct)
    {
        var fp = await db.Database.SqlQuery<Fingerprint>($"""
            SELECT
              (SELECT count(*) FROM "WatchEvents" WHERE "UserId" = {userId}) AS "Plays",
              (SELECT coalesce(sum(extract(epoch FROM "WatchedAt")), 0)::float8 FROM "WatchEvents" WHERE "UserId" = {userId}) AS "PlaySum",
              (SELECT max("CreatedAt") FROM "WatchEvents" WHERE "UserId" = {userId}) AS "LastAdded",
              (SELECT count(*) FROM "Ratings" WHERE "UserId" = {userId}) AS "Ratings",
              (SELECT coalesce(sum("Value"), 0)::float8 FROM "Ratings" WHERE "UserId" = {userId}) AS "RatingSum",
              (SELECT max("RatedAt") FROM "Ratings" WHERE "UserId" = {userId}) AS "LastRated",
              -- Paused/dropped shows count differently in stats ("finishing shows"); resuming deletes the row.
              (SELECT count(*) FROM "ShowStates" WHERE "UserId" = {userId}) AS "States",
              (SELECT coalesce(sum("Status" * "ShowTmdbId"), 0)::bigint FROM "ShowStates" WHERE "UserId" = {userId}) AS "StateSum",
              (SELECT max("UpdatedAt") FROM "ShowStates" WHERE "UserId" = {userId}) AS "LastState",
              (SELECT count(*) FROM "Rewatches" WHERE "UserId" = {userId}) AS "Rewatches",
              (SELECT max(coalesce("CompletedAt", "StartedAt")) FROM "Rewatches" WHERE "UserId" = {userId}) AS "LastRewatch"
            """).FirstAsync(ct);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.TimeZone, u.TrackingSince, u.PrefsJson }).FirstOrDefaultAsync(ct);
        // Preferences that change what stats count are part of the key too.
        var firstOnly = !UserPrefsStore.Read(user?.PrefsJson).CountRewatches;
        return $"{fp.Plays}|{fp.PlaySum:R}|{fp.LastAdded?.UtcTicks}|{fp.Ratings}|{fp.RatingSum:R}|{fp.LastRated?.UtcTicks}"
             + $"|{fp.States}|{fp.StateSum}|{fp.LastState?.UtcTicks}|{fp.Rewatches}|{fp.LastRewatch?.UtcTicks}|{user?.TimeZone}|{user?.TrackingSince}|{firstOnly}";
    }

    /// <param name="key">What's being cached and its parameters, e.g. "review:2026:10:America/New_York".</param>
    public async Task<T> GetAsync<T>(Guid userId, string key, Func<Task<T>> build, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await build();   // the in-memory test database can't run the fingerprint SQL
        var cacheKey = $"user:{userId}:{key}:{await FingerprintAsync(userId, ct)}";
        if (cache.TryGetValue(cacheKey, out T? hit) && hit is not null) return hit;
        var value = await build();
        cache.Set(cacheKey, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 });
        return value;
    }
}
