using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Server.Import;

/// <summary>Things you can do to a finished import: undo it, or match its leftover titles by hand.</summary>
public class ImportService(AppDbContext db, ImportRunner runner, CatalogService catalog, SeenIt.Server.Tmdb.TmdbClient tmdb, TrackingService tracking)
{
    public async Task<List<ImportJobDto>> RecentAsync(Guid userId, CancellationToken ct)
    {
        var jobs = await db.ImportJobs.AsNoTracking().Where(j => j.UserId == userId).OrderByDescending(j => j.CreatedAt).Take(20)
            .Select(j => new
            {
                j.Id, j.Kind, j.FileName, j.Status, j.CreatedAt, j.FinishedAt, j.Total, j.Processed, j.Imported, j.Skipped, j.Failed,
                j.Message, j.Unmatched, j.Parts, j.UndoneAt, CanUndo = j.UndoJson != null, j.PendingJson, j.ChecksJson,
            })
            .ToListAsync(ct);
        var checks = jobs.ToDictionary(j => j.Id, j => j.UndoneAt is null ? ImportCheck.Parse(j.ChecksJson) : []);
        var titles = await TitlesAsync(checks.Values.SelectMany(c => c).SelectMany(c => c.Others.Select(o => (c.Type, o.TmdbId)).Prepend((c.Type, c.TmdbId))), ct);
        return jobs.Select(j => new ImportJobDto(j.Id, j.Kind, j.FileName, j.Status, j.CreatedAt, j.FinishedAt,
            j.Total, j.Processed, j.Imported, j.Skipped, j.Failed, j.Message, j.Unmatched, j.Parts,
            j.UndoneAt, j.CanUndo && j.UndoneAt is null && j.Status == ImportStatus.Completed,
            j.UndoneAt is null
                ? PendingImport.Parse(j.PendingJson).Select(p => new PendingTitle(p.Key, p.Label, p.Type, p.Title, p.Year, p.Summary())).ToList()
                : [],
            checks[j.Id].Select(c => titles.TryGetValue((c.Type, c.TmdbId), out var t)
                ? new MatchCheck(c.Type, c.TmdbId, c.Label, t.Title, t.Year, t.Poster, c.Others.Select(o => new MatchOption(o.TmdbId, o.Title, o.Year, o.PosterPath)).ToList())
                : null).OfType<MatchCheck>().ToList())).ToList();
    }

    private async Task<Dictionary<(MediaType, int), (string Title, int? Year, string? Poster)>> TitlesAsync(IEnumerable<(MediaType Type, int Id)> refs, CancellationToken ct)
    {
        var list = refs.ToList();
        var movieIds = list.Where(r => r.Type == MediaType.Movie).Select(r => r.Id).Distinct().ToList();
        var showIds = list.Where(r => r.Type != MediaType.Movie).Select(r => r.Id).Distinct().ToList();
        var result = new Dictionary<(MediaType, int), (string, int?, string?)>();
        if (movieIds.Count > 0)
            foreach (var m in await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId)).Select(m => new { m.TmdbId, m.Title, m.ReleaseDate, m.PosterPath }).ToListAsync(ct))
                result[(MediaType.Movie, m.TmdbId)] = (m.Title, m.ReleaseDate?.Year, m.PosterPath);
        if (showIds.Count > 0)
            foreach (var x in await db.Shows.AsNoTracking().Where(x => showIds.Contains(x.TmdbId)).Select(x => new { x.TmdbId, x.Name, x.FirstAirDate, x.PosterPath }).ToListAsync(ct))
                result[(MediaType.Show, x.TmdbId)] = (x.Name, x.FirstAirDate?.Year, x.PosterPath);
        return result;
    }

    /// <summary>"That's right" (keeps it) or "it was this one" (moves your plays, ratings and list entries over).</summary>
    public async Task<RematchResult?> ResolveCheckAsync(Guid userId, Guid jobId, ResolveCheckRequest req, CancellationToken ct)
    {
        var job = await db.ImportJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct)
                  ?? throw new TrackingException("That import is gone.");
        var checks = ImportCheck.Parse(job.ChecksJson);
        var check = checks.FirstOrDefault(c => c.Type == req.Type && c.TmdbId == req.TmdbId)
                    ?? throw new TrackingException("That's already been sorted.");
        RematchResult? moved = null;
        if (req.ToTmdbId is { } to && to != req.TmdbId)
        {
            if (check.Others.All(o => o.TmdbId != to)) throw new TrackingException("That isn't one of the choices.");
            moved = await tracking.RematchAsync(userId, req.Type, req.TmdbId, to, ct);
        }
        checks.Remove(check);
        job.ChecksJson = System.Text.Json.JsonSerializer.Serialize(checks);
        await db.SaveChangesAsync(ct);
        return moved;
    }

    /// <summary>
    /// Imports from before match checks existed: looks again at the titles matched by name (Letterboxd and
    /// TV Time) and lists the ones TMDB has namesakes for. Old imports didn't tag their plays, so those are
    /// found by source and the time the import ran.
    /// </summary>
    public async Task RecheckAsync(ImportJob job, CancellationToken ct)
    {
        var checks = new List<ImportCheck>();
        if (job.Kind is ImportKind.Letterboxd or ImportKind.TvTime && job.Status == ImportStatus.Completed && job.UndoneAt is null)
        {
            Guid? tag = job.Id;
            var source = WatchSources.ForImport(job.Kind);
            var from = job.StartedAt ?? job.CreatedAt;
            var to = (job.FinishedAt ?? job.CreatedAt).AddMinutes(1);
            var refs = (await db.WatchEvents.AsNoTracking()
                    .Where(w => w.UserId == job.UserId && (w.ImportJobId == tag || (w.ImportJobId == null && w.Source == source && w.CreatedAt >= from && w.CreatedAt <= to)))
                    .Select(w => new { w.Type, w.TmdbId }).Distinct().ToListAsync(ct))
                .Select(w => (Type: w.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, w.TmdbId)).ToList();
            refs.AddRange((await db.Ratings.AsNoTracking().Where(r => r.UserId == job.UserId && r.ImportJobId == tag).Select(r => new { r.Type, r.TmdbId }).Distinct().ToListAsync(ct))
                .Select(r => (r.Type == MediaType.Movie ? MediaType.Movie : MediaType.Show, r.TmdbId)));
            refs.AddRange((await db.Watchlist.AsNoTracking().Where(r => r.UserId == job.UserId && r.ImportJobId == tag).Select(r => new { r.Type, r.TmdbId }).ToListAsync(ct))
                .Select(r => (r.Type, r.TmdbId)));
            refs.AddRange((await db.ListItems.AsNoTracking().Where(r => r.ImportJobId == tag).Select(r => new { r.Type, r.TmdbId }).ToListAsync(ct))
                .Select(r => (r.Type, r.TmdbId)));
            var titles = await TitlesAsync(refs.Distinct(), ct);
            foreach (var ((type, id), t) in titles)
            {
                var movie = type == MediaType.Movie;
                var found = movie
                    ? (await tmdb.SearchMovieAsync(t.Title, null, ct))?.Results.Concat((t.Year is { } y ? await tmdb.SearchMovieAsync(t.Title, y, ct) : null)?.Results ?? []).ToList()
                    : (await tmdb.SearchShowAsync(t.Title, null, ct))?.Results;
                var matches = ImportRunner.Matches(found, t.Title, t.Year, maxYearGap: movie ? 1 : 99).DistinctBy(o => o.Id).ToList();
                // The current pick has to be among them to compare with (one matched through Letterboxd's page may not be).
                if (matches.FirstOrDefault(o => o.Id == id) is { } current && ImportRunner.Doubtful(current, matches, t.Year))
                    checks.Add(new ImportCheck(type, id, t.Year is { } yr ? $"{t.Title} ({yr})" : t.Title, ImportRunner.Alternatives(current, matches, t.Year)));
            }
        }
        job.ChecksJson = System.Text.Json.JsonSerializer.Serialize(checks);
        await db.SaveChangesAsync(ct);
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
        var library = await db.Library.Where(l => l.UserId == userId && l.ImportJobId == id).ExecuteDeleteAsync(ct);
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
        return new UndoImportResult(plays, ratings, watchlist, listItems, favorites, lists, library);
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
