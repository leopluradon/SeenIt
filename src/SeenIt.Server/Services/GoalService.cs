using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>Yearly goals and how far along they are, counted the way the year in review counts.</summary>
public class GoalService(AppDbContext db, TrackingService tracking, ReviewService reviews)
{
    public async Task<List<GoalDto>> GetAsync(Guid userId, int year, string? tzId, CancellationToken ct)
    {
        var goals = await db.Goals.AsNoTracking().Where(g => g.UserId == userId && g.Year == year).OrderBy(g => g.Kind).ToListAsync(ct);
        if (goals.Count == 0) return [];

        var tz = ReviewService.Zone(await tracking.ZoneIdAsync(userId, tzId, ct));
        var progress = await ProgressAsync(userId, year, tz, goals.Select(g => g.Kind).ToHashSet(), ct);

        var now = ReviewService.Local(DateTimeOffset.UtcNow, tz);
        var start = new DateTime(year, 1, 1);
        var fraction = Math.Clamp((now - start).TotalDays / (start.AddYears(1) - start).TotalDays, 0, 1);
        return goals.Select(g => new GoalDto(g.Id, g.Year, g.Kind, g.Target, progress.GetValueOrDefault(g.Kind),
            Math.Round(g.Target * fraction, 1), fraction >= 1)).ToList();
    }

    /// <summary>Progress toward every requested kind of goal in the year.</summary>
    public async Task<Dictionary<GoalKind, double>> ProgressAsync(Guid userId, int year, TimeZoneInfo tz, IReadOnlySet<GoalKind> kinds, CancellationToken ct)
    {
        var since = await tracking.TrackingSinceAsync(userId, ct);
        var all = await reviews.LoadPlaysAsync(userId, tz, null, ct);
        var start = new DateTime(year, 1, 1);
        var end = start.AddYears(1);
        bool InYear(DateTime local) => local >= start && local < end && (since is null || DateOnly.FromDateTime(local) >= since);
        var plays = all.Where(p => InYear(p.Local)).ToList();

        var result = new Dictionary<GoalKind, double>();
        if (kinds.Contains(GoalKind.Movies)) result[GoalKind.Movies] = plays.Count(p => p.Type == MediaType.Movie);
        if (kinds.Contains(GoalKind.Episodes)) result[GoalKind.Episodes] = plays.Count(p => p.Type == MediaType.Episode);
        if (kinds.Contains(GoalKind.NewMovies))
        {
            // First time ever, against the whole history (including plays from before tracking started).
            var firstEver = all.Where(p => p.Type == MediaType.Movie).GroupBy(p => p.TmdbId).ToDictionary(g => g.Key, g => g.Min(p => p.At));
            result[GoalKind.NewMovies] = plays.Where(p => p.Type == MediaType.Movie && p.At == firstEver[p.TmdbId]).Select(p => p.TmdbId).Distinct().Count();
        }
        if (kinds.Contains(GoalKind.Hours))
        {
            var minutes = await reviews.RuntimesAsync(plays, ct);
            result[GoalKind.Hours] = Math.Round(plays.Sum(p => minutes(p)) / 60.0, 1);
        }
        if (kinds.Contains(GoalKind.ShowsFinished))
        {
            var shows = plays.Where(p => p.Type == MediaType.Episode).Select(p => p.TmdbId).Distinct().ToList();
            var progress = shows.Count == 0 ? [] : await new ProgressCalculator(db).ForUserAsync(userId, shows, ct);
            result[GoalKind.ShowsFinished] = progress.Count(p => p.Completed && p.LastWatchedAt is { } at && InYear(ReviewService.Local(at, tz)));
        }
        return result;
    }

    public async Task SetAsync(Guid userId, GoalRequest req, CancellationToken ct)
    {
        if (req.Year is < 2000 or > 2200) throw new TrackingException("Pick a year.");
        if (!Enum.IsDefined(req.Kind)) throw new TrackingException("Pick a kind of goal.");
        if (req.Target is < 1 or > 100_000) throw new TrackingException("A goal needs a target between 1 and 100,000.");
        var goal = await db.Goals.FirstOrDefaultAsync(g => g.UserId == userId && g.Year == req.Year && g.Kind == req.Kind, ct);
        if (goal is null) db.Goals.Add(new Goal { UserId = userId, Year = req.Year, Kind = req.Kind, Target = req.Target });
        else goal.Target = req.Target;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct) =>
        await db.Goals.Where(g => g.UserId == userId && g.Id == id).ExecuteDeleteAsync(ct) > 0;
}
