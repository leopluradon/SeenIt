using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Import;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>Runs queued imports one at a time. Jobs live in the database, so a restart resumes them.</summary>
public class ImportWorker(IServiceScopeFactory scopes, ILogger<ImportWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Imports are idempotent, so anything interrupted mid-run just starts over.
            await db.ImportJobs.Where(j => j.Status == ImportStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ImportStatus.Queued), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunNextAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Import worker loop failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task<bool> RunNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ImportJobs.Where(j => j.Status == ImportStatus.Queued).OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is null) return false;

        job.Status = ImportStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Starting {Kind} import {Id}", job.Kind, job.Id);

        try
        {
            await scope.ServiceProvider.GetRequiredService<ImportRunner>().RunAsync(job, ct);
            job.Status = ImportStatus.Completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Import {Id} failed", job.Id);
            job.Status = ImportStatus.Failed;
            job.Message = ex is InvalidDataException or System.Text.Json.JsonException or System.IO.InvalidDataException
                ? ex.Message
                : "The import stopped unexpectedly: " + ex.Message;
        }

        var (status, message, finished) = (job.Status, job.Message, DateTimeOffset.UtcNow);
        if (status == ImportStatus.Failed)
        {
            // Whatever broke may still be pending in the change tracker; start clean so the
            // failure itself can be recorded (otherwise the job would sit at "Running" forever).
            db.ChangeTracker.Clear();
            job = await db.ImportJobs.FirstAsync(j => j.Id == job.Id, CancellationToken.None);
            job.Status = status;
            job.Message = message;
        }
        job.FinishedAt = finished;
        job.Payload = null; // Don't keep people's exports around once processed.
        await db.SaveChangesAsync(CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ActivityService>().LogAsync(job.UserId, ActivityKind.Import,
            detail: $"{job.Kind} {job.FileName} {status.ToString().ToLowerInvariant()}: {message}",
            source: WatchSources.ForImport(job.Kind));
        return true;
    }
}

/// <summary>
/// Keeps episode lists fresh for shows people actually follow, so progress and the calendar
/// pick up new episodes without anyone opening the show page.
/// </summary>
public class ShowRefreshWorker(IServiceScopeFactory scopes, ILogger<ShowRefreshWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshStaleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Show refresh pass failed"); }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    private async Task RefreshStaleAsync(CancellationToken ct)
    {
        List<ShowMeta> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var followed = db.WatchEvents.Where(w => w.Type == MediaType.Episode).Select(w => w.TmdbId)
                .Union(db.Watchlist.Where(w => w.Type == MediaType.Show).Select(w => w.TmdbId));
            candidates = await db.Shows.AsNoTracking().Where(s => followed.Contains(s.TmdbId)).ToListAsync(ct);
        }

        var stale = candidates
            .Where(s => s.EpisodesUpdatedAt is null || DateTimeOffset.UtcNow - s.EpisodesUpdatedAt > CatalogService.ShowTtl(s) || s.MetaVersion < MetaVersions.Current)
            .OrderBy(s => s.EpisodesUpdatedAt ?? DateTimeOffset.MinValue)
            .Take(200)
            .ToList();

        foreach (var s in stale)
        {
            await using var scope = scopes.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogService>();
            try { await catalog.EnsureShowAsync(s.TmdbId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Refreshing show {Id} failed", s.TmdbId); }
        }
        if (stale.Count > 0) log.LogInformation("Refreshed {Count} shows", stale.Count);

        // Movies people have watched, rated, listed or favorited: refetch ones whose details are
        // out of date (CatalogService.MovieTtl) or from an older metadata format, oldest first. Movies
        // only refresh on their own when someone opens them, so without this, posters, runtimes and
        // collections shown in lists, history and stats would never change.
        List<int> movies;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var used = db.WatchEvents.Where(w => w.Type == MediaType.Movie).Select(w => w.TmdbId)
                .Union(db.Watchlist.Where(w => w.Type == MediaType.Movie).Select(w => w.TmdbId))
                .Union(db.Ratings.Where(r => r.Type == MediaType.Movie).Select(r => r.TmdbId))
                .Union(db.ListItems.Where(i => i.Type == MediaType.Movie).Select(i => i.TmdbId))
                .Union(db.Favorites.Where(f => f.Type == MediaType.Movie).Select(f => f.TmdbId));
            var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
            var movieCandidates = await db.Movies.AsNoTracking()
                .Where(m => used.Contains(m.TmdbId) && (m.MetaVersion < MetaVersions.Current || m.UpdatedAt < weekAgo))
                .Select(m => new MovieMeta { TmdbId = m.TmdbId, ReleaseDate = m.ReleaseDate, UpdatedAt = m.UpdatedAt, MetaVersion = m.MetaVersion })
                .ToListAsync(ct);
            // A few hundred per pass (every 30 minutes) keeps well inside TMDB's rate limits.
            movies = movieCandidates
                .Where(m => m.MetaVersion < MetaVersions.Current || DateTimeOffset.UtcNow - m.UpdatedAt > CatalogService.MovieTtl(m))
                .OrderBy(m => m.UpdatedAt).Take(300).Select(m => m.TmdbId).ToList();
        }
        foreach (var id in movies)
        {
            await using var scope = scopes.CreateAsyncScope();
            try { await scope.ServiceProvider.GetRequiredService<CatalogService>().EnsureMovieAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Refreshing movie {Id} failed", id); }
        }
        if (movies.Count > 0) log.LogInformation("Refreshed {Count} movies", movies.Count);

        // Collections (franchises) of those movies, for franchise progress on the stats page.
        List<int> collections;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var monthAgo = DateTimeOffset.UtcNow.AddDays(-30);
            var wanted = db.Movies.Where(m => m.CollectionId != null).Select(m => m.CollectionId!.Value).Distinct();
            collections = await wanted.Where(id => !db.Collections.Any(c => c.Id == id && c.UpdatedAt > monthAgo)).Take(100).ToListAsync(ct);
        }
        foreach (var id in collections)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            try
            {
                var c = await scope.ServiceProvider.GetRequiredService<TmdbClient>().GetCollectionAsync(id, ct);
                var row = await db.Collections.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (row is null) db.Collections.Add(row = new CollectionMeta { Id = id });
                row.Name = c?.Name ?? row.Name;
                row.PosterPath = c?.PosterPath ?? row.PosterPath;
                if (c?.Parts is { } parts)
                    row.Parts = parts.Select(p => new CollectionPartMeta { TmdbId = p.Id, Title = p.Title ?? p.Name ?? "", ReleaseDate = TmdbDates.Parse(p.ReleaseDate), PosterPath = p.PosterPath }).ToList();
                row.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Refreshing collection {Id} failed", id); }
        }
        if (collections.Count > 0) log.LogInformation("Refreshed {Count} collections", collections.Count);
    }
}

/// <summary>Turns finished manual check-ins into plays, once a minute.</summary>
public class CheckInWorker(IServiceScopeFactory scopes, ILogger<CheckInWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var done = await scope.ServiceProvider.GetRequiredService<CheckInService>().CompleteDueAsync(stoppingToken);
                if (done > 0) log.LogInformation("Recorded {Count} finished check-ins", done);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Check-in pass failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
