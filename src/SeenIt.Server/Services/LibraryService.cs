using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// What you own: added by hand, by Plex/Jellyfin when something lands on your server, or from an import. A title
/// can be owned in several formats, and a show either whole or season by season (one LibraryItem each).
/// </summary>
public class LibraryService(AppDbContext db, TrackingService tracking)
{
    public async Task<List<LibraryEntry>> ListAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.Library.AsNoTracking().Where(l => l.UserId == userId).ToListAsync(ct);
        var (movies, shows) = await tracking.LoadMetaAsync(rows.Select(r => (r.Type, r.TmdbId)).Distinct(), ct);
        var watchedMovies = (await db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Movie).Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet();
        var watchedShows = (await db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet();
        return rows.GroupBy(r => (r.Type, r.TmdbId))
            .Select(g =>
            {
                var copies = Copies(g);
                var first = copies[0];
                var media = g.Key.Type == MediaType.Movie
                    ? movies.TryGetValue(g.Key.TmdbId, out var m) ? CatalogService.ToSummary(m) : null
                    : shows.TryGetValue(g.Key.TmdbId, out var s) ? CatalogService.ToSummary(s) : null;
                var watched = g.Key.Type == MediaType.Movie ? watchedMovies.Contains(g.Key.TmdbId) : watchedShows.Contains(g.Key.TmdbId);
                return media is null ? null : new LibraryEntry(media, first.Format, copies.Min(c => c.AddedAt), first.Source, watched, copies);
            })
            .OfType<LibraryEntry>().OrderByDescending(e => e.Copies!.Max(c => c.AddedAt)).ToList();
    }

    /// <summary>A title's copies in a steady order: by format, the whole show before its seasons.</summary>
    public static List<OwnedCopy> Copies(IEnumerable<LibraryItem> rows) => rows
        .OrderBy(r => r.Format).ThenBy(r => r.SeasonNumber ?? -1)
        .Select(r => new OwnedCopy(r.Format, r.SeasonNumber, r.Source, r.AddedAt)).ToList();

    /// <summary>
    /// Adds one copy: a format, and for a show the whole show (season null) or a season. Returns false if it was
    /// already there. A server adding a season replaces a vaguer "whole show" it had added (now it's known which).
    /// </summary>
    public async Task<bool> AddAsync(Guid userId, MediaType type, int tmdbId, OwnedFormat format, WatchSource source, CancellationToken ct, int? season = null)
    {
        if (type is not (MediaType.Movie or MediaType.Show)) throw new TrackingException("Only movies and shows go in the library.");
        if (type == MediaType.Movie) season = null;
        if (season is < 0) throw new TrackingException("That isn't a season.");
        await tracking.EnsureMetaAsync(type, tmdbId, ct);
        var mine = db.Library.Where(l => l.UserId == userId && l.Type == type && l.TmdbId == tmdbId && l.Format == format);
        if (await mine.AnyAsync(l => l.SeasonNumber == season, ct)) return false;
        if (season is not null && source != WatchSource.Manual)
            db.Library.RemoveRange(await mine.Where(l => l.SeasonNumber == null && l.Source == source).ToListAsync(ct));
        db.Library.Add(new LibraryItem { UserId = userId, Type = type, TmdbId = tmdbId, SeasonNumber = season, Format = format, Source = source });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }   // the same copy arrived twice at once
        return true;
    }

    /// <summary>
    /// Takes copies out: with no format, everything of the title; with a format, that format (the whole show and
    /// every season of it), or just one season when that's given too.
    /// </summary>
    public async Task<bool> RemoveAsync(Guid userId, MediaType type, int tmdbId, CancellationToken ct, OwnedFormat? format = null, int? season = null)
    {
        var q = db.Library.Where(l => l.UserId == userId && l.Type == type && l.TmdbId == tmdbId);
        if (format is { } f) q = q.Where(l => l.Format == f);
        if (season is { } s) q = q.Where(l => l.SeasonNumber == s);
        var rows = await q.ToListAsync(ct);
        db.Library.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count > 0;
    }
}
