using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>
/// Completionist: the directors, actors and writers whose movies you've seen most, how many of each one's movies you've
/// seen (Filmography.Counts decides what counts), and which you're closest to finishing. The results are kept
/// (PersonCompletion) for the Completionist badge.
/// </summary>
public class CompletionService(AppDbContext db, TmdbClient tmdb, ILogger<CompletionService> log)
{
    private const int PerRole = 25;
    private static readonly HashSet<int> AppearanceGenres = [10767, 10763];   // talk, news

    public async Task<List<CompletionDto>> GetAsync(Guid userId, CancellationToken ct)
    {
        var seen = (await db.WatchEvents.AsNoTracking().Where(w => w.UserId == userId && w.Type == MediaType.Movie)
            .Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet();
        if (seen.Count == 0) return [];

        // Candidates from what you've seen: directors, and the top-billed few of each cast.
        var seenIds = seen.ToList();
        var metas = await db.Movies.AsNoTracking().Where(m => seenIds.Contains(m.TmdbId)).Select(m => new { m.Directors, m.Cast }).ToListAsync(ct);
        var directors = metas.SelectMany(m => m.Directors).GroupBy(p => p.Id).Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).Take(PerRole).Select(g => g.Key);
        var actors = metas.SelectMany(m => m.Cast.Take(5)).GroupBy(p => p.Id).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).Take(PerRole).Select(g => g.Key);
        var people = directors.Concat(actors).Distinct().ToList();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var results = new List<CompletionDto>();
        using var gate = new SemaphoreSlim(6);
        var fetched = await Task.WhenAll(people.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try { return await tmdb.GetPersonAsync(id, ct); }
            catch (HttpRequestException ex) { log.LogInformation(ex, "Person {Id} for Completionist failed", id); return null; }
            finally { gate.Release(); }
        }));
        foreach (var p in fetched.OfType<TmdbPerson>())
        {
            var cast = (p.CombinedCredits?.Cast ?? []).Where(c => c.MediaType == "movie" && !Appearance(c));
            var crew = (p.CombinedCredits?.Crew ?? []).Where(c => c.MediaType == "movie");
            foreach (var (role, credits) in new[]
                     {
                         ("Directing", crew.Where(c => c.Job is "Director" or "Co-Director")),
                         ("Acting", cast),
                         ("Writing", crew.Where(c => c.Department == "Writing")),
                     })
            {
                var movies = credits.GroupBy(c => c.Id).Select(g => g.First())
                    .Where(c => Filmography.Counts(TmdbDates.Parse(c.ReleaseDate), c.VoteCount ?? 0, seen.Contains(c.Id), today)).ToList();
                var mine = movies.Count(c => seen.Contains(c.Id));
                // Only roles they're really known for here: a few movies, and some of them seen.
                if (movies.Count < 3 || mine < 2) continue;
                var unseen = movies.Where(c => !seen.Contains(c.Id)).OrderByDescending(c => c.VoteCount ?? 0).Take(4)
                    .Select(c => new MediaSummary(MediaType.Movie, c.Id, c.Title ?? "", TmdbDates.Parse(c.ReleaseDate)?.Year, c.PosterPath, null, null, null)).ToList();
                results.Add(new CompletionDto(p.Id, p.Name, p.ProfilePath, role, mine, movies.Count, unseen, null));
            }
        }

        // Kept for the badge; a role's first complete look is its CompletedAt.
        var stored = await db.PersonCompletions.Where(c => c.UserId == userId).ToDictionaryAsync(c => (c.PersonId, c.Role), ct);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (!stored.TryGetValue((r.PersonId, r.Role), out var row))
                db.PersonCompletions.Add(row = new PersonCompletion { UserId = userId, PersonId = r.PersonId, Role = r.Role });
            (row.Name, row.Seen, row.Total, row.UpdatedAt) = (r.Name, r.Seen, r.Total, now);
            if (r.Seen >= r.Total) row.CompletedAt ??= now;
            results[i] = r with { CompletedAt = row.CompletedAt };
        }
        await db.SaveChangesAsync(ct);

        // Closest to done first: fewest left, then the biggest share seen.
        return results.OrderBy(r => r.Total - r.Seen).ThenByDescending(r => (double)r.Seen / r.Total).ThenBy(r => r.Name).ToList();
    }

    private static bool Appearance(TmdbPersonCredit c) =>
        (c.GenreIds ?? []).Any(AppearanceGenres.Contains)
        || c.Character is { } ch && (ch.Contains("Self", StringComparison.OrdinalIgnoreCase) || ch is "Himself" or "Herself");
}

/// <summary>
/// Works out everyone's Completionist progress a couple of minutes after start and then daily, so the badge keeps up
/// without anyone opening the page (person lookups are cached, and a few people per user at most).
/// </summary>
public class CompletionWorker(IServiceScopeFactory scopes, ILogger<CompletionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<Guid> ids;
                await using (var scope = scopes.CreateAsyncScope())
                    ids = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WatchEvents
                        .Where(w => w.Type == MediaType.Movie).Select(w => w.UserId).Distinct().ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    try { await scope.ServiceProvider.GetRequiredService<CompletionService>().GetAsync(id, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Completionist for {User} failed", id); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogError(ex, "Completionist loop failed"); }
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }
}
