using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Recent actions: what you did in the last 48 hours (the activity log), and undoing it. An action can be undone when it
/// was logged with what reversing it takes (UndoData) and hasn't been undone already; the undo is logged too.
/// </summary>
public class UndoService(AppDbContext db, TrackingService tracking, ListService lists, SocialService social, CheckInService checkIns, ActivityService activity)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(48);

    public async Task<List<RecentAction>> RecentAsync(Guid userId, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - Window;
        var rows = await db.Activity.AsNoTracking().Where(a => a.UserId == userId && a.At >= since)
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(300).ToListAsync(ct);
        var (movies, shows) = await tracking.LoadMetaAsync(rows.Where(r => r.Type is not null && r.TmdbId is not null)
            .Select(r => (r.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, r.TmdbId!.Value)).Distinct(), ct);
        return rows.Select(r =>
        {
            string? title = null, poster = null;
            if (r.TmdbId is { } id)
            {
                if (r.Type == MediaType.Movie && movies.TryGetValue(id, out var m)) (title, poster) = (m.Title, m.PosterPath);
                else if (r.Type is not null && shows.TryGetValue(id, out var s)) (title, poster) = (s.Name, s.PosterPath);
            }
            return new RecentAction(r.Id, r.At, r.Kind, title, poster, r.Type, r.TmdbId, r.SeasonNumber, r.EpisodeNumber, r.Detail,
                r.UndoJson is not null && r.UndoneAt is null, r.UndoneAt);
        }).ToList();
    }

    /// <summary>Reverses one of your recent actions. Throws (with a reason) when it can't be.</summary>
    public async Task UndoAsync(Guid userId, long id, CancellationToken ct)
    {
        var row = await db.Activity.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct) ?? throw new TrackingException("That isn't one of your actions.");
        if (row.UndoneAt is not null) throw new TrackingException("That's already been undone.");
        if (row.At < DateTimeOffset.UtcNow - Window) throw new TrackingException("Only the last 48 hours can be undone here.");
        if (row.UndoJson is null || JsonSerializer.Deserialize<UndoData>(row.UndoJson) is not { } u) throw new TrackingException("That can't be undone.");
        var type = row.Type ?? MediaType.Movie;
        var tmdbId = row.TmdbId ?? 0;

        switch (row.Kind)
        {
            case ActivityKind.Watch or ActivityKind.BulkWatch:
                await tracking.DeleteHistoryAsync(userId, u.PlayIds ?? [], ct);
                break;
            case ActivityKind.Unwatch or ActivityKind.DeletePlay:
                await tracking.RestoreAsync(userId, u.Plays ?? [], ct);
                break;
            case ActivityKind.EditPlay:
                await tracking.SetPlayDatesAsync(userId, u.Dates ?? [], ct);
                break;
            case ActivityKind.Rate or ActivityKind.Unrate:
            {
                // Straight to the rating row, as RateAsync stores it (a show's season number rates the season); the title
                // was rated before, so there's nothing to look up.
                int? season = type == MediaType.Movie ? null : row.SeasonNumber, episode = type == MediaType.Episode ? row.EpisodeNumber : null;
                var rating = await db.Ratings.FirstOrDefaultAsync(r => r.UserId == userId && r.Type == type && r.TmdbId == tmdbId
                                                                       && r.SeasonNumber == season && r.EpisodeNumber == episode, ct);
                if (u.Had && u.Rating is { } before)
                {
                    if (rating is null) db.Ratings.Add(rating = new Rating { UserId = userId, Type = type, TmdbId = tmdbId, SeasonNumber = season, EpisodeNumber = episode });
                    rating.Value = before;
                    rating.RatedAt = DateTimeOffset.UtcNow;
                }
                else if (rating is not null) db.Ratings.Remove(rating);
                await db.SaveChangesAsync(ct);
                break;
            }
            case ActivityKind.WatchlistAdd:
                if (!u.Was) await tracking.RemoveFromWatchlistAsync(userId, type, tmdbId, ct);
                break;
            case ActivityKind.WatchlistRemove:
                if (u.Was) await tracking.AddToWatchlistAsync(userId, type, tmdbId, ct);
                break;
            case ActivityKind.FavoriteAdd:
                await tracking.RemoveFavoriteAsync(userId, type, tmdbId, ct);
                break;
            case ActivityKind.FavoriteRemove:
                await tracking.AddFavoriteAsync(userId, type, tmdbId, ct);
                break;
            case ActivityKind.ShowStatus:
                await tracking.SetShowStatusAsync(userId, tmdbId, u.Status ?? ShowTrackStatus.Watching, ct);
                break;
            case ActivityKind.RewatchStart:
                await tracking.StopRewatchAsync(userId, tmdbId, ct);
                break;
            case ActivityKind.CheckIn:
                // Only if that check-in is still the one running.
                if (await checkIns.GetAsync(userId, ct) is { } now && now.TmdbId == tmdbId && now.SeasonNumber == row.SeasonNumber && now.EpisodeNumber == row.EpisodeNumber)
                    await checkIns.CancelAsync(userId, ct);
                else throw new TrackingException("That check-in has already ended.");
                break;
            case ActivityKind.ListAdd when u.ListId is { } addedTo:
                if (!await lists.RemoveItemAsync(userId, addedTo, type, tmdbId, ct)) throw new TrackingException("That list isn't there any more.");
                break;
            case ActivityKind.ListRemove when u.ListId is { } removedFrom:
                if (!await lists.AddItemAsync(userId, removedFrom, type, tmdbId, ct)) throw new TrackingException("That list isn't there any more.");
                break;
            case ActivityKind.Follow when u.User is { } followed:
                await social.FollowAsync(userId, followed, false, ct);
                break;
            case ActivityKind.Unfollow when u.User is { } unfollowed:
                await social.FollowAsync(userId, unfollowed, true, ct);
                break;
            default:
                throw new TrackingException("That can't be undone.");
        }

        row.UndoneAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(userId, ActivityKind.Undo, row.Type, row.TmdbId, row.SeasonNumber, row.EpisodeNumber,
            $"{row.Kind}{(row.Detail is { } d ? ": " + d : "")}");
    }
}
