using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// "Recommend to a friend": send a movie or show to people who follow you, with a short note. It lands under
/// their bell and on their Recommendations page; you see whether they watched it and what they thought.
/// </summary>
public class FriendRecommendationService(AppDbContext db, TrackingService tracking)
{
    public const int NoteMax = 500;
    private const int DailyLimit = 100;

    /// <summary>Who you can send to: people who follow you (they asked to hear from you). Friends who follow each other first.</summary>
    public async Task<List<UserCard>> RecipientsAsync(Guid me, CancellationToken ct)
    {
        var followers = await db.Follows.Where(f => f.FolloweeId == me)
            .Join(db.Users, f => f.FollowerId, u => u.Id, (f, u) => u).AsNoTracking().ToListAsync(ct);
        var following = await FollowingAsync(me, followers.Select(u => u.Id), ct);
        return followers.Select(u => Card(u, following.Contains(u.Id), true))
            .OrderByDescending(c => c.Following).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<RecommendResult> SendAsync(Guid me, RecommendRequest req, CancellationToken ct)
    {
        if (req.Type is not (MediaType.Movie or MediaType.Show)) throw new TrackingException("Only movies and shows can be recommended.");
        var wanted = req.To.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        if (wanted.Count == 0) throw new TrackingException("Pick at least one person.");
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        if (await db.Recommendations.CountAsync(r => r.FromId == me && r.CreatedAt > since, ct) + wanted.Count > DailyLimit)
            throw new TrackingException($"That's more than {DailyLimit} recommendations in a day. Try again tomorrow.");

        await tracking.EnsureMetaAsync(req.Type, req.TmdbId, ct);
        var (title, poster) = req.Type == MediaType.Movie
            ? await db.Movies.Where(m => m.TmdbId == req.TmdbId).Select(m => new ValueTuple<string, string?>(m.Title, m.PosterPath)).FirstAsync(ct)
            : await db.Shows.Where(s => s.TmdbId == req.TmdbId).Select(s => new ValueTuple<string, string?>(s.Name, s.PosterPath)).FirstAsync(ct);
        var sender = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me, ct);
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : Trim(req.Note.Trim(), NoteMax);

        var normalized = wanted.Select(w => w.ToUpperInvariant()).ToList();
        var targets = await db.Follows.Where(f => f.FolloweeId == me)
            .Join(db.Users, f => f.FollowerId, u => u.Id, (f, u) => u)
            .Where(u => normalized.Contains(u.NormalizedUserName!)).ToListAsync(ct);
        var skipped = wanted.Where(w => !targets.Any(t => string.Equals(t.UserName, w, StringComparison.OrdinalIgnoreCase))).ToList();

        var headline = Trim($"{sender.DisplayName ?? sender.UserName} recommends {title}", 300);
        foreach (var to in targets)
        {
            // Sending the same title again refreshes the note and brings it back to the top, rather than piling up.
            var rec = await db.Recommendations.FirstOrDefaultAsync(r => r.FromId == me && r.ToId == to.Id && r.Type == req.Type && r.TmdbId == req.TmdbId, ct);
            if (rec is null)
            {
                rec = new Recommendation { FromId = me, ToId = to.Id, Type = req.Type, TmdbId = req.TmdbId };
                db.Recommendations.Add(rec);
            }
            rec.Note = note;
            rec.CreatedAt = DateTimeOffset.UtcNow;
            rec.DismissedAt = null;

            await db.Notifications.Where(x => x.RecommendationId == rec.Id).ExecuteDeleteAsync(ct);
            db.Notifications.Add(new Notification
            {
                // The recipient's own calendar day, so the alert files under "Today" for them rather than the UTC date.
                UserId = to.Id, Type = req.Type, TmdbId = req.TmdbId, Title = headline, PosterPath = poster,
                Day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ReviewService.Zone(to.TimeZone)).DateTime),
                Detail = note is null ? null : Trim(note, 400), RecommendationId = rec.Id,
            });
        }
        await db.SaveChangesAsync(ct);
        return new RecommendResult(targets.Count, skipped);
    }

    public async Task<RecommendationsDto> ListAsync(Guid me, CancellationToken ct)
    {
        var received = await db.Recommendations.AsNoTracking().Where(r => r.ToId == me).OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var sent = await db.Recommendations.AsNoTracking().Where(r => r.FromId == me).OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        // Opening the page reads their alerts under the bell (release alerts stay as they are).
        await db.Notifications.Where(n => n.UserId == me && n.RecommendationId != null && n.ReadAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
        return new RecommendationsDto(await ReceivedAsync(me, received, ct), await SentAsync(me, sent, ct));
    }

    /// <summary>Who recommended this title to you (still in your inbox), for its page.</summary>
    public async Task<List<RecommendationDto>> ForTitleAsync(Guid me, MediaType type, int tmdbId, CancellationToken ct)
    {
        var rows = await db.Recommendations.AsNoTracking()
            .Where(r => r.ToId == me && r.Type == type && r.TmdbId == tmdbId && r.DismissedAt == null)
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        return rows.Count == 0 ? [] : await ReceivedAsync(me, rows, ct);
    }

    public async Task<bool> DismissAsync(Guid me, Guid id, bool dismissed, CancellationToken ct)
    {
        var n = await db.Recommendations.Where(r => r.Id == id && r.ToId == me)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DismissedAt, dismissed ? DateTimeOffset.UtcNow : null), ct);
        if (dismissed) await db.Notifications.Where(x => x.RecommendationId == id && x.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, DateTimeOffset.UtcNow), ct);
        return n > 0;
    }

    /// <summary>Takes back one you sent (it disappears from their inbox and bell).</summary>
    public async Task<bool> UnsendAsync(Guid me, Guid id, CancellationToken ct) =>
        await db.Recommendations.Where(r => r.Id == id && r.FromId == me).ExecuteDeleteAsync(ct) > 0;

    // ---------- Building the rows ----------

    private async Task<List<RecommendationDto>> ReceivedAsync(Guid me, List<Recommendation> rows, CancellationToken ct)
    {
        var senders = await UsersAsync(rows.Select(r => r.FromId), ct);
        var following = await FollowingAsync(me, senders.Keys, ct);
        var media = await MediaAsync(rows, ct);
        var plays = await PlaysAsync([me], rows, ct);
        var ratings = await RatingsAsync([me], rows, ct);
        var watchlist = (await db.Watchlist.Where(w => w.UserId == me).Select(w => new { w.Type, w.TmdbId }).ToListAsync(ct))
            .Select(w => (w.Type, w.TmdbId)).ToHashSet();
        return rows.Where(r => senders.ContainsKey(r.FromId) && media.ContainsKey((r.Type, r.TmdbId))).Select(r =>
        {
            var played = plays.TryGetValue((me, r.Type, r.TmdbId), out var at);
            return new RecommendationDto(r.Id, media[(r.Type, r.TmdbId)], Card(senders[r.FromId], following.Contains(r.FromId), true), r.Note, r.CreatedAt,
                r.DismissedAt is not null, played, played ? at : null, ratings.TryGetValue((me, r.Type, r.TmdbId), out var v) ? v : null,
                watchlist.Contains((r.Type, r.TmdbId)));
        }).ToList();
    }

    private async Task<List<RecommendationDto>> SentAsync(Guid me, List<Recommendation> rows, CancellationToken ct)
    {
        var recipients = await UsersAsync(rows.Select(r => r.ToId), ct);
        var following = await FollowingAsync(me, recipients.Keys, ct);
        var followers = (await db.Follows.Where(f => f.FolloweeId == me && recipients.Keys.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        bool Mutual(Guid id) => following.Contains(id) && followers.Contains(id);
        // Whether they watched or rated it only shows when their history or ratings are visible to you.
        var historyOk = recipients.Values.Where(u => SocialService.CanSee(u, me, Mutual(u.Id), ProfilePart.History)).Select(u => u.Id).ToList();
        var ratingsOk = recipients.Values.Where(u => SocialService.CanSee(u, me, Mutual(u.Id), ProfilePart.Ratings)).Select(u => u.Id).ToList();
        var media = await MediaAsync(rows, ct);
        var plays = await PlaysAsync(historyOk, rows, ct);
        var ratings = await RatingsAsync(ratingsOk, rows, ct);
        return rows.Where(r => recipients.ContainsKey(r.ToId) && media.ContainsKey((r.Type, r.TmdbId))).Select(r =>
        {
            var canSee = historyOk.Contains(r.ToId);
            var played = plays.TryGetValue((r.ToId, r.Type, r.TmdbId), out var at);
            return new RecommendationDto(r.Id, media[(r.Type, r.TmdbId)], Card(recipients[r.ToId], following.Contains(r.ToId), followers.Contains(r.ToId)),
                r.Note, r.CreatedAt, r.DismissedAt is not null,
                canSee ? played : null, played ? at : null, ratings.TryGetValue((r.ToId, r.Type, r.TmdbId), out var v) ? v : null, false);
        }).ToList();
    }

    private Task<Dictionary<Guid, AppUser>> UsersAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
    }

    private async Task<HashSet<Guid>> FollowingAsync(Guid me, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        return (await db.Follows.Where(f => f.FollowerId == me && list.Contains(f.FolloweeId)).Select(f => f.FolloweeId).ToListAsync(ct)).ToHashSet();
    }

    private async Task<Dictionary<(MediaType, int), MediaSummary>> MediaAsync(List<Recommendation> rows, CancellationToken ct)
    {
        var movieIds = rows.Where(r => r.Type == MediaType.Movie).Select(r => r.TmdbId).Distinct().ToList();
        var showIds = rows.Where(r => r.Type == MediaType.Show).Select(r => r.TmdbId).Distinct().ToList();
        var result = new Dictionary<(MediaType, int), MediaSummary>();
        foreach (var m in await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId)).ToListAsync(ct)) result[(MediaType.Movie, m.TmdbId)] = CatalogService.ToSummary(m);
        foreach (var s in await db.Shows.AsNoTracking().Where(s => showIds.Contains(s.TmdbId)).ToListAsync(ct)) result[(MediaType.Show, s.TmdbId)] = CatalogService.ToSummary(s);
        return result;
    }

    /// <summary>Latest play per person and title (any episode counts for a show). Unknown dates count as watched.</summary>
    private async Task<Dictionary<(Guid, MediaType, int), DateTimeOffset?>> PlaysAsync(List<Guid> users, List<Recommendation> rows, CancellationToken ct)
    {
        if (users.Count == 0) return [];
        var movieIds = rows.Where(r => r.Type == MediaType.Movie).Select(r => r.TmdbId).Distinct().ToList();
        var showIds = rows.Where(r => r.Type == MediaType.Show).Select(r => r.TmdbId).Distinct().ToList();
        var plays = await db.WatchEvents.AsNoTracking()
            .Where(w => users.Contains(w.UserId) && ((w.Type == MediaType.Movie && movieIds.Contains(w.TmdbId)) || (w.Type == MediaType.Episode && showIds.Contains(w.TmdbId))))
            .GroupBy(w => new { w.UserId, w.Type, w.TmdbId }).Select(g => new { g.Key.UserId, g.Key.Type, g.Key.TmdbId, Last = g.Max(w => w.WatchedAt) })
            .ToListAsync(ct);
        return plays.ToDictionary(p => (p.UserId, p.Type == MediaType.Episode ? MediaType.Show : MediaType.Movie, p.TmdbId),
            p => WatchDates.IsUnknown(p.Last) ? (DateTimeOffset?)null : p.Last);
    }

    private async Task<Dictionary<(Guid, MediaType, int), double>> RatingsAsync(List<Guid> users, List<Recommendation> rows, CancellationToken ct)
    {
        if (users.Count == 0) return [];
        var ids = rows.Select(r => r.TmdbId).Distinct().ToList();
        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => users.Contains(r.UserId) && (r.Type == MediaType.Movie || r.Type == MediaType.Show) && r.SeasonNumber == null && r.EpisodeNumber == null && ids.Contains(r.TmdbId))
            .Select(r => new { r.UserId, r.Type, r.TmdbId, r.Value }).ToListAsync(ct);
        return ratings.ToDictionary(r => (r.UserId, r.Type, r.TmdbId), r => r.Value);
    }

    private static UserCard Card(AppUser u, bool following, bool followsYou) =>
        new(u.UserName!, u.DisplayName, following, followsYou, AvatarService.Path(u.UserName!, u.AvatarVersion));

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
