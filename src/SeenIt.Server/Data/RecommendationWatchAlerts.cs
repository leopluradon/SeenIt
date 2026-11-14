using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;

namespace SeenIt.Server.Data;

/// <summary>
/// "Sam watched Heat" / "Sam started Severance": tells whoever recommended a title when the person they sent it to
/// plays it. Runs after any save that adds plays (marking watched, Jellyfin and other scrobblers, check-ins, bulk
/// marks), once per recommendation, and only for plays from after it was sent (or with no date), so importing old
/// history doesn't set it off. The recommender chooses alert, push, both or off (UserPrefs.Alerts[RecWatched]).
/// Watching it also clears the recommendation from your own inbox (and its "Sam recommends…" alert), whatever
/// the sender's setting.
/// </summary>
public static class RecommendationWatchAlerts
{
    public static async Task NotifyAsync(AppDbContext db, IReadOnlyList<WatchEvent> plays, CancellationToken ct)
    {
        var byUser = plays
            .Select(p => (p.UserId, Type: p.Type == MediaType.Episode ? MediaType.Show : MediaType.Movie, p.TmdbId, p.WatchedAt))
            .Where(p => p.Type is MediaType.Movie or MediaType.Show)
            .GroupBy(p => p.UserId);
        var added = 0;
        foreach (var g in byUser)
        {
            var titles = g.Select(p => p.TmdbId).Distinct().ToList();
            var recs = await db.Recommendations
                .Where(r => r.ToId == g.Key && titles.Contains(r.TmdbId)
                            && (r.DismissedAt == null || !db.Notifications.Any(n => n.WatchedRecommendationId == r.Id)))
                .ToListAsync(ct);
            foreach (var rec in recs)
            {
                var play = g.Where(p => p.Type == rec.Type && p.TmdbId == rec.TmdbId)
                    .FirstOrDefault(p => WatchDates.IsUnknown(p.WatchedAt) || p.WatchedAt >= rec.CreatedAt);
                if (play.TmdbId == 0) continue;   // watched before it was recommended (an import of old history)

                // Watched: off your list of recommendations, and its alert counts as seen.
                if (rec.DismissedAt is null)
                {
                    rec.DismissedAt = DateTimeOffset.UtcNow;
                    foreach (var n in await db.Notifications.Where(n => n.RecommendationId == rec.Id && n.ReadAt == null).ToListAsync(ct))
                        n.ReadAt = DateTimeOffset.UtcNow;
                    added++;   // something to save
                }
                if (await db.Notifications.AnyAsync(n => n.WatchedRecommendationId == rec.Id, ct)) continue;   // sender already told

                var from = await db.Users.AsNoTracking().Where(u => u.Id == rec.FromId)
                    .Select(u => new { u.PrefsJson, u.TimeZone }).FirstOrDefaultAsync(ct);
                if (from is null || UserPrefsStore.Read(from.PrefsJson).DeliveryFor(AlertKind.RecWatched) == AlertDelivery.Off) continue;
                var who = await db.Users.AsNoTracking().Where(u => u.Id == rec.ToId)
                    .Select(u => u.DisplayName ?? u.UserName).FirstOrDefaultAsync(ct) ?? "Someone";
                var (title, poster) = rec.Type == MediaType.Movie
                    ? await db.Movies.AsNoTracking().Where(m => m.TmdbId == rec.TmdbId).Select(m => new ValueTuple<string, string?>(m.Title, m.PosterPath)).FirstOrDefaultAsync(ct)
                    : await db.Shows.AsNoTracking().Where(s => s.TmdbId == rec.TmdbId).Select(s => new ValueTuple<string, string?>(s.Name, s.PosterPath)).FirstOrDefaultAsync(ct);
                if (string.IsNullOrEmpty(title)) continue;

                var zone = Services.ReviewService.Zone(from.TimeZone);
                var headline = $"{who} {(rec.Type == MediaType.Show ? "started" : "watched")} {title}";
                var sent = TimeZoneInfo.ConvertTime(rec.CreatedAt, zone);
                db.Notifications.Add(new Notification
                {
                    UserId = rec.FromId, Type = rec.Type, TmdbId = rec.TmdbId, WatchedRecommendationId = rec.Id, Kind = AlertKind.RecWatched,
                    Title = headline.Length > 300 ? headline[..300] : headline, PosterPath = poster,
                    Detail = $"You recommended it on {sent:MMM d}",
                    Day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime),
                });
                added++;
            }
        }
        if (added == 0) return;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Two plays saved at once both found no alert yet: the other one made it. Drop ours.
            foreach (var e in db.ChangeTracker.Entries<Notification>().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
            await db.SaveChangesAsync(ct);   // the rest (clearing the recommendation) still stands
        }
    }
}
