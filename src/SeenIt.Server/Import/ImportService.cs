using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Server.Import;

/// <summary>Things you can do to a finished import: undo it, or match its leftover titles by hand.</summary>
public class ImportService(AppDbContext db, ImportRunner runner, CatalogService catalog)
{
    public async Task<List<ImportJobDto>> RecentAsync(Guid userId, CancellationToken ct)
    {
        var jobs = await db.ImportJobs.AsNoTracking().Where(j => j.UserId == userId).OrderByDescending(j => j.CreatedAt).Take(20)
            .Select(j => new
            {
                j.Id, j.Kind, j.FileName, j.Status, j.CreatedAt, j.FinishedAt, j.Total, j.Processed, j.Imported, j.Skipped, j.Failed,
                j.Message, j.Unmatched, j.Parts, j.UndoneAt, CanUndo = j.UndoJson != null, j.PendingJson,
            })
            .ToListAsync(ct);
        return jobs.Select(j => new ImportJobDto(j.Id, j.Kind, j.FileName, j.Status, j.CreatedAt, j.FinishedAt,
            j.Total, j.Processed, j.Imported, j.Skipped, j.Failed, j.Message, j.Unmatched, j.Parts,
            j.UndoneAt, j.CanUndo && j.UndoneAt is null && j.Status == ImportStatus.Completed,
            j.UndoneAt is null
                ? PendingImport.Parse(j.PendingJson).Select(p => new PendingTitle(p.Key, p.Label, p.Type, p.Title, p.Year, p.Summary())).ToList()
                : [])).ToList();
    }

    /// <summary>
    /// Removes everything the import added (plays, ratings, watchlist and list entries, favorites,
    /// and lists it created that are now empty) and puts back what it changed: ratings it
    /// overwrote, and plays it gave a date to.
    /// </summary>
    public async Task<UndoImportResult?> UndoAsync(Guid userId, Guid jobId, CancellationToken ct)
    {
        var job = await db.ImportJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        if (job is null) return null;
        if (job.UndoneAt is not null) throw new TrackingException("That import has already been undone.");
        if (job.Status != ImportStatus.Completed) throw new TrackingException("Only finished imports can be undone.");
        if (job.UndoJson is null) throw new TrackingException("That import is from before undo existed, so SeenIt can't tell what it added.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Guid? id = job.Id;
        var plays = await db.WatchEvents.Where(w => w.UserId == userId && w.ImportJobId == id).ExecuteDeleteAsync(ct);
        var ratings = await db.Ratings.Where(r => r.UserId == userId && r.ImportJobId == id).ExecuteDeleteAsync(ct);
        var watchlist = await db.Watchlist.Where(w => w.UserId == userId && w.ImportJobId == id).ExecuteDeleteAsync(ct);
        var favorites = await db.Favorites.Where(f => f.UserId == userId && f.ImportJobId == id).ExecuteDeleteAsync(ct);
        var mine = db.Lists.Where(l => l.UserId == userId).Select(l => l.Id);
        var listItems = await db.ListItems.Where(i => i.ImportJobId == id && mine.Contains(i.ListId)).ExecuteDeleteAsync(ct);
        var lists = await db.Lists.Where(l => l.UserId == userId && l.ImportJobId == id && !l.Items.Any()).ExecuteDeleteAsync(ct);

        var undo = ImportUndo.Parse(job.UndoJson);
        foreach (var r in undo.Ratings)
            ratings += await db.Ratings.Where(x => x.Id == r.Id && x.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, r.Value).SetProperty(x => x.RatedAt, r.RatedAt), ct);
        if (undo.Dated.Count > 0)
            plays += await db.WatchEvents.Where(w => w.UserId == userId && undo.Dated.Contains(w.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.WatchedAt, WatchDates.Unknown), ct);

        job.UndoneAt = DateTimeOffset.UtcNow;
        job.PendingJson = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new UndoImportResult(plays, ratings, watchlist, listItems, favorites, lists);
    }

    /// <summary>Brings in an unmatched title's rows as the title the user picked.</summary>
    public async Task<ResolveImportResult?> ResolveAsync(Guid userId, Guid jobId, ResolveImportRequest req, CancellationToken ct)
    {
        var job = await db.ImportJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        if (job is null) return null;
        if (job.UndoneAt is not null) throw new TrackingException("That import has been undone.");
        var pending = PendingImport.Parse(job.PendingJson);
        var item = pending.FirstOrDefault(p => p.Key == req.Key) ?? throw new TrackingException("That title has already been matched.");
        var found = item.Type == MediaType.Movie
            ? await catalog.EnsureMovieAsync(req.TmdbId, ct) is not null
            : await catalog.EnsureShowAsync(req.TmdbId, ct) is not null;
        if (!found) throw new TrackingException("That title wasn't found on TMDB.");

        // Same id as the import, so undoing the import undoes these too.
        var run = new ImportJob { Id = job.Id, UserId = userId, Kind = job.Kind, Parts = ImportParts.All, UndoJson = job.UndoJson };
        await runner.RunAsync(run, item.ToParsed(req.TmdbId), ct, resolving: true);

        pending.Remove(item);
        job.PendingJson = PendingImport.Serialize(pending);
        job.UndoJson = run.UndoJson;
        job.Imported += run.Imported;
        job.Skipped += run.Skipped;
        job.Unmatched = [.. job.Unmatched.Where(u => u != item.Label)];
        await db.SaveChangesAsync(ct);
        return new ResolveImportResult(run.Imported, run.Skipped);
    }
}
