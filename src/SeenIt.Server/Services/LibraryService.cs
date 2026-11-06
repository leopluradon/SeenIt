using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>What you own: added by hand, or by Plex/Jellyfin when something lands on your server.</summary>
public class LibraryService(AppDbContext db, TrackingService tracking)
{
    public async Task<List<LibraryEntry>> ListAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.Library.AsNoTracking().Where(l => l.UserId == userId).OrderByDescending(l => l.AddedAt).ToListAsync(ct);
        var (movies, shows) = await tracking.LoadMetaAsync(rows.Select(r => (r.Type, r.TmdbId)), ct);
        var watchedMovies = (await db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Movie).Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet();
        var watchedShows = (await db.WatchEvents.Where(w => w.UserId == userId && w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet();
        return rows.Select(r => r.Type == MediaType.Movie
                ? movies.TryGetValue(r.TmdbId, out var m) ? new LibraryEntry(CatalogService.ToSummary(m), r.Format, r.AddedAt, r.Source, watchedMovies.Contains(r.TmdbId)) : null
                : shows.TryGetValue(r.TmdbId, out var s) ? new LibraryEntry(CatalogService.ToSummary(s), r.Format, r.AddedAt, r.Source, watchedShows.Contains(r.TmdbId)) : null)
            .OfType<LibraryEntry>().ToList();
    }

    /// <summary>Adds it, or changes its format. Returns false if it was already there in that format.</summary>
    public async Task<bool> AddAsync(Guid userId, MediaType type, int tmdbId, OwnedFormat format, WatchSource source, CancellationToken ct)
    {
        if (type is not (MediaType.Movie or MediaType.Show)) throw new TrackingException("Only movies and shows go in the library.");
        await tracking.EnsureMetaAsync(type, tmdbId, ct);
        var row = await db.Library.FirstOrDefaultAsync(l => l.UserId == userId && l.Type == type && l.TmdbId == tmdbId, ct);
        if (row is not null)
        {
            // A server adding a copy you already listed by hand doesn't change what you said you own.
            if (row.Format == format || (source != WatchSource.Manual && row.Source == WatchSource.Manual)) return false;
            row.Format = format;
            row.Source = source;
        }
        else db.Library.Add(new LibraryItem { UserId = userId, Type = type, TmdbId = tmdbId, Format = format, Source = source });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }   // the same item arrived twice at once
        return true;
    }

    public async Task<bool> RemoveAsync(Guid userId, MediaType type, int tmdbId, CancellationToken ct) =>
        await db.Library.Where(l => l.UserId == userId && l.Type == type && l.TmdbId == tmdbId).ExecuteDeleteAsync(ct) > 0;
}
