using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Import;

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

        // Movies cached before metadata gained cast/directors/etc. (or long ago) get refetched.
        List<int> movies;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var used = db.WatchEvents.Where(w => w.Type == MediaType.Movie).Select(w => w.TmdbId)
                .Union(db.Watchlist.Where(w => w.Type == MediaType.Movie).Select(w => w.TmdbId))
                .Union(db.Ratings.Where(r => r.Type == MediaType.Movie).Select(r => r.TmdbId));
            movies = await db.Movies.AsNoTracking()
                .Where(m => used.Contains(m.TmdbId) && m.MetaVersion < MetaVersions.Current)
                .Select(m => m.TmdbId).Take(500).ToListAsync(ct);
        }
        foreach (var id in movies)
        {
            await using var scope = scopes.CreateAsyncScope();
            try { await scope.ServiceProvider.GetRequiredService<CatalogService>().EnsureMovieAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Refreshing movie {Id} failed", id); }
        }
        if (movies.Count > 0) log.LogInformation("Backfilled metadata for {Count} movies", movies.Count);
    }
}
