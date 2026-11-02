using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Import;

/// <summary>
/// Matches parsed export rows to TMDB and writes them. Safe to re-run: plays you already have
/// (see ImportHistoryAsync), existing watchlist entries and list items are skipped, and ratings are upserted.
/// </summary>
public class ImportRunner(AppDbContext db, TmdbClient tmdb, CatalogService catalog, ILogger<ImportRunner> log)
{
    private readonly Dictionary<ImportRef, int?> _resolved = [];
    private readonly Dictionary<(MediaType, int), bool> _metaOk = [];

    public async Task RunAsync(ImportJob job, CancellationToken ct)
    {
        var files = ImportFiles.Read(job.FileName, job.Payload ?? []);
        var parsed = job.Kind == ImportKind.Trakt ? TraktParser.Parse(files) : LetterboxdParser.Parse(files);
        var found = parsed.Total;
        parsed.Keep(job.Parts);
        if (found > 0 && parsed.Total == 0)
            throw new InvalidDataException("Nothing in that file matched what you chose to import.");
        if (parsed.Total == 0)
            throw new InvalidDataException(job.Kind == ImportKind.Trakt
                ? "No Trakt history, ratings, watchlist or lists were found in that file."
                : "No Letterboxd diary, watched, ratings, watchlist or list files were found in that file.");

        job.Total = parsed.Total;
        job.Processed = job.Imported = job.Skipped = job.Failed = 0;
        job.Unmatched = [];
        await db.SaveChangesAsync(ct);

        await ImportHistoryAsync(job, parsed.History, ct);
        await ImportWatchlistAsync(job, parsed.Watchlist, ct);
        await ImportRatingsAsync(job, parsed.Ratings, ct);
        foreach (var list in parsed.Lists) await ImportListAsync(job, list, ct);

        job.Message = $"Imported {job.Imported:N0}, skipped {job.Skipped:N0} already present, {job.Failed:N0} couldn't be matched.";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds plays, skipping ones you already have. Each existing play can stand in for one imported
    /// play only, so a title watched three times keeps three plays, and re-running the same export
    /// adds nothing. An imported play matches an existing one of the same movie/episode within a day
    /// either side (other sources record the same viewing at a slightly different time, or as a bare
    /// date); a play whose date is only a stand-in matches any existing play of it. A play you
    /// marked "date unknown" takes the imported date.
    /// </summary>
    private async Task ImportHistoryAsync(ImportJob job, List<ImportWatch> history, CancellationToken ct)
    {
        var existing = (await db.WatchEvents.Where(w => w.UserId == job.UserId)
                .Select(w => new { w.Id, w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt }).ToListAsync(ct))
            .GroupBy(w => (w.Type, w.TmdbId, w.SeasonNumber, w.EpisodeNumber))
            .ToDictionary(g => g.Key, g => g.Select(w => new ExistingPlay(w.Id, w.WatchedAt)).ToList());
        var source = job.Kind == ImportKind.Trakt ? WatchSource.TraktImport : WatchSource.LetterboxdImport;

        // Real dates first, so a stand-in date can't take the existing play a real one would have matched.
        foreach (var w in history.OrderBy(h => h.Approximate).ThenBy(h => h.WatchedAt))
        {
            if (await ResolveAsync(job, w.Item, ct) is { } tmdbId)
            {
                var type = w.Item.Type == MediaType.Movie ? MediaType.Movie : MediaType.Episode;
                var key = (type, tmdbId, w.Season, w.Episode);
                if (!existing.TryGetValue(key, out var plays)) existing[key] = plays = [];

                var match = MatchPlay(plays, w);
                if (match is null)
                {
                    db.WatchEvents.Add(new WatchEvent
                    {
                        UserId = job.UserId, Type = type, TmdbId = tmdbId, SeasonNumber = w.Season, EpisodeNumber = w.Episode,
                        WatchedAt = w.WatchedAt, Source = source,
                    });
                    plays.Add(new ExistingPlay(null, w.WatchedAt) { Used = true });
                    job.Imported++;
                }
                else if (match.Id is { } id && WatchDates.IsUnknown(match.At) && !w.Approximate)
                {
                    await db.WatchEvents.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(e => e.WatchedAt, w.WatchedAt), ct);
                    job.Imported++;
                }
                else job.Skipped++;
            }
            await StepAsync(job, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    public sealed class ExistingPlay(Guid? id, DateTimeOffset at)
    {
        public Guid? Id { get; } = id;
        public DateTimeOffset At { get; } = at;
        public bool Used { get; set; }
    }

    public static readonly TimeSpan SamePlayWindow = TimeSpan.FromDays(1);

    /// <summary>The existing play this imported one duplicates, now marked used; null if it's new.</summary>
    public static ExistingPlay? MatchPlay(List<ExistingPlay> plays, ImportWatch w)
    {
        var free = plays.Where(p => !p.Used).ToList();
        var match = w.Approximate
            ? free.FirstOrDefault()
            : free.Where(p => !WatchDates.IsUnknown(p.At) && (p.At - w.WatchedAt).Duration() <= SamePlayWindow)
                  .MinBy(p => (p.At - w.WatchedAt).Duration())
              ?? free.FirstOrDefault(p => WatchDates.IsUnknown(p.At));
        if (match is not null) match.Used = true;
        return match;
    }

    private async Task ImportWatchlistAsync(ImportJob job, List<ImportListItem> items, CancellationToken ct)
    {
        var existing = (await db.Watchlist.Where(w => w.UserId == job.UserId).Select(w => new { w.Type, w.TmdbId }).ToListAsync(ct))
            .Select(w => (w.Type, w.TmdbId)).ToHashSet();
        var watchedMovies = (await db.WatchEvents.Where(w => w.UserId == job.UserId && w.Type == MediaType.Movie)
            .Select(w => w.TmdbId).Distinct().ToListAsync(ct)).ToHashSet();

        foreach (var item in items)
        {
            if (await ResolveAsync(job, item.Item, ct) is { } id)
            {
                // Don't re-add movies the history says you've already seen.
                if ((item.Item.Type == MediaType.Movie && watchedMovies.Contains(id)) || !existing.Add((item.Item.Type, id)))
                    job.Skipped++;
                else
                {
                    db.Watchlist.Add(new WatchlistItem { UserId = job.UserId, Type = item.Item.Type, TmdbId = id, AddedAt = item.AddedAt });
                    job.Imported++;
                }
            }
            await StepAsync(job, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ImportRatingsAsync(ImportJob job, List<ImportRating> ratings, CancellationToken ct)
    {
        var existing = await db.Ratings.Where(r => r.UserId == job.UserId).ToListAsync(ct);
        var byKey = existing.ToDictionary(r => (r.Type, r.TmdbId, r.SeasonNumber, r.EpisodeNumber));

        foreach (var r in ratings)
        {
            if (await ResolveAsync(job, r.Item, ct) is { } id)
            {
                var type = r.Season is not null ? MediaType.Episode : r.Item.Type;
                var key = (type, id, r.Season, r.Episode);
                if (byKey.TryGetValue(key, out var row))
                {
                    if (row.Value == r.Rating) job.Skipped++;
                    else { row.Value = r.Rating; row.RatedAt = r.RatedAt; job.Imported++; }
                }
                else
                {
                    row = new Rating { UserId = job.UserId, Type = type, TmdbId = id, SeasonNumber = r.Season, EpisodeNumber = r.Episode, Value = r.Rating, RatedAt = r.RatedAt };
                    db.Ratings.Add(row);
                    byKey[key] = row;
                    job.Imported++;
                }
            }
            await StepAsync(job, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ImportListAsync(ImportJob job, ImportList import, CancellationToken ct)
    {
        var name = import.Name.Length > 100 ? import.Name[..100] : import.Name;
        var list = await db.Lists.Include(l => l.Items).FirstOrDefaultAsync(l => l.UserId == job.UserId && l.Name == name, ct);
        if (list is null)
        {
            list = new UserList { UserId = job.UserId, Name = name, Description = import.Description };
            db.Lists.Add(list);
        }
        var position = list.Items.Count == 0 ? 0 : list.Items.Max(i => i.Position) + 1;

        foreach (var item in import.Items)
        {
            if (await ResolveAsync(job, item.Item, ct) is { } id)
            {
                if (list.Items.Any(i => i.Type == item.Item.Type && i.TmdbId == id)) job.Skipped++;
                else
                {
                    var row = new UserListItem { ListId = list.Id, Type = item.Item.Type, TmdbId = id, AddedAt = item.AddedAt, Position = position++ };
                    // Add through the DbSet: a client-set Guid key found only via the navigation
                    // would be treated as an existing row and UPDATEd instead of inserted.
                    db.ListItems.Add(row);
                    if (!list.Items.Contains(row)) list.Items.Add(row);
                    job.Imported++;
                }
            }
            await StepAsync(job, ct);
        }
        list.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task StepAsync(ImportJob job, CancellationToken ct)
    {
        job.Processed++;
        if (job.Processed % 100 == 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>Returns the TMDB id (movie, or show for episodes) and makes sure its metadata is cached.</summary>
    private async Task<int?> ResolveAsync(ImportJob job, ImportRef r, CancellationToken ct)
    {
        if (!_resolved.TryGetValue(r, out var id))
        {
            try
            {
                id = await MatchAsync(r, ct);
                if (id is { } found && !await EnsureMetaAsync(r.Type, found, ct)) id = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Matching {Item} failed", r.Label);
                id = null;
            }
            _resolved[r] = id;
            if (id is null && job.Unmatched.Count < 200 && !job.Unmatched.Contains(r.Label))
                job.Unmatched.Add(r.Label);
        }
        if (id is null) job.Failed++;
        return id;
    }

    private async Task<int?> MatchAsync(ImportRef r, CancellationToken ct)
    {
        if (r.TmdbId is { } id) return id;

        if (!string.IsNullOrEmpty(r.ImdbId))
        {
            var found = await tmdb.FindAsync(r.ImdbId, "imdb_id", ct);
            var hit = r.Type == MediaType.Movie ? found?.MovieResults?.FirstOrDefault() : found?.TvResults?.FirstOrDefault();
            if (hit is not null) return hit.Id;
        }

        if (string.IsNullOrWhiteSpace(r.Title)) return null;

        if (r.Type == MediaType.Movie)
        {
            // Release years differ between sources surprisingly often (300 is 2006 on Letterboxd, 2007
            // on TMDB), so look for an exact title in the neighbouring years before settling for
            // TMDB's popularity-ordered first result, which can be a different film entirely.
            TmdbPage<TmdbListItem>? sameYear = null;
            foreach (var year in r.Year is { } y ? new int?[] { y, y + 1, y - 1 } : [null])
            {
                var res = await tmdb.SearchMovieAsync(r.Title, year, ct);
                sameYear ??= res;
                if (Exact(res?.Results, r.Title, r.Year) is { } m) return m.Id;
            }
            if (r.Year is not null && Exact((await tmdb.SearchMovieAsync(r.Title, null, ct))?.Results, r.Title, r.Year, maxYearGap: 2) is { } any)
                return any.Id;
            return Loose(sameYear?.Results, r.Title)?.Id;
        }

        var shows = await tmdb.SearchShowAsync(r.Title, r.Year, ct) is { Results.Count: > 0 } withYear
            ? withYear : await tmdb.SearchShowAsync(r.Title, null, ct);
        return (Exact(shows?.Results, r.Title, r.Year, maxYearGap: 99) ?? Loose(shows?.Results, r.Title))?.Id;
    }

    /// <summary>A result whose title (or original title) matches, nearest in year first.</summary>
    public static TmdbListItem? Exact(List<TmdbListItem>? results, string title, int? year, int maxYearGap = 1)
    {
        var want = Normalize(title);
        return (results ?? [])
            .Where(x => Normalize(x.Title ?? x.Name) == want || Normalize(x.OriginalTitle ?? x.OriginalName) == want)
            .Select(x => (x, Gap: year is { } y && YearOf(x) is { } ry ? Math.Abs(ry - y) : 0))
            .Where(x => x.Gap <= maxYearGap)
            .OrderBy(x => x.Gap)   // stable: keeps TMDB's popularity order between equals
            .Select(x => x.x).FirstOrDefault();
    }

    /// <summary>No exact title: the top result, but only if one title contains the other.</summary>
    public static TmdbListItem? Loose(List<TmdbListItem>? results, string title)
    {
        var want = Normalize(title);
        return (results ?? []).FirstOrDefault(x => Normalize(x.Title ?? x.Name) is { Length: > 0 } t
            && (t.StartsWith(want) || want.StartsWith(t)));
    }

    private static int? YearOf(TmdbListItem x) =>
        (x.ReleaseDate ?? x.FirstAirDate) is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null;

    /// <summary>Case, accents, punctuation and "&amp;"/"and" don't matter: "Spider-Man: No Way Home" = "spider man no way home".</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.Replace("&", " and ").Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private async Task<bool> EnsureMetaAsync(MediaType type, int id, CancellationToken ct)
    {
        var key = (type, id);
        if (_metaOk.TryGetValue(key, out var ok)) return ok;
        ok = type == MediaType.Movie
            ? await catalog.EnsureMovieAsync(id, ct) is not null
            : await catalog.EnsureShowAsync(id, ct) is not null;
        return _metaOk[key] = ok;
    }

}
