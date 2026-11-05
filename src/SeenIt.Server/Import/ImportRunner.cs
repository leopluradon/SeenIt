using System.Globalization;
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
public class ImportRunner(AppDbContext db, TmdbClient tmdb, CatalogService catalog, IHttpClientFactory http, ILogger<ImportRunner> log)
{
    private readonly Dictionary<ImportRef, int?> _resolved = [];
    private readonly Dictionary<(MediaType, int), bool> _metaOk = [];
    private readonly ImportUndo _undo = new();

    /// <summary>What rows get tagged with, so the import can be undone. The Letterboxd feed sync runs with an empty id: nothing to undo.</summary>
    private static Guid? Tag(ImportJob job) => job.Id == Guid.Empty ? null : job.Id;

    public async Task RunAsync(ImportJob job, CancellationToken ct)
    {
        var files = ImportFiles.Read(job.FileName, job.Payload ?? []);
        var parsed = job.Kind switch
        {
            ImportKind.Trakt => TraktParser.Parse(files),
            ImportKind.Letterboxd => LetterboxdParser.Parse(files),
            ImportKind.Imdb => ImdbParser.Parse(files),
            ImportKind.TvTime => TvTimeParser.Parse(files),
            _ => SimklParser.Parse(files),
        };
        await RunAsync(job, parsed, ct);
    }

    /// <summary>Imports already-parsed rows (the Letterboxd feed sync builds them itself).</summary>
    /// <param name="resolving">Adding titles matched by hand to a finished import: keep its counts and pending list.</param>
    public async Task RunAsync(ImportJob job, ParsedImport parsed, CancellationToken ct, bool resolving = false)
    {
        var found = parsed.Total;
        parsed.Keep(job.Parts);
        if (found > 0 && parsed.Total == 0)
            throw new InvalidDataException("Nothing in that file matched what you chose to import.");
        if (parsed.Total == 0)
            throw new InvalidDataException(job.Kind switch
            {
                ImportKind.Trakt => "No Trakt history, ratings, watchlist or lists were found in that file.",
                ImportKind.Letterboxd => "No Letterboxd diary, watched, ratings, watchlist or list files were found in that file.",
                ImportKind.Imdb => "That doesn't look like an IMDb ratings, watchlist or list export (a CSV with a Const column).",
                ImportKind.TvTime => "No seen_episode CSV was found. Upload TV Time's whole export zip.",
                _ => "No movies or shows were found in that Simkl backup.",
            });

        if (!resolving)
        {
            job.Total = parsed.Total;
            job.Processed = job.Imported = job.Skipped = job.Failed = 0;
            job.Unmatched = [];
        }
        await db.SaveChangesAsync(ct);

        if (parsed.Progress.Count > 0) await ExpandProgressAsync(job, parsed, ct);
        await ImportHistoryAsync(job, parsed.History, ct);
        await ImportWatchlistAsync(job, parsed.Watchlist, ct);
        await ImportRatingsAsync(job, parsed.Ratings, ct);
        foreach (var list in parsed.Lists) await ImportListAsync(job, list, ct);
        if (parsed.Favorites.Count > 0) await ImportFavoritesAsync(job, parsed.Favorites, ct);

        if (Tag(job) is not null)
        {
            job.UndoJson = ImportUndo.Merge(job.UndoJson, _undo);
            if (!resolving) job.PendingJson = PendingImport.Serialize(PendingImport.From(parsed, _resolved));
        }
        if (resolving) { await db.SaveChangesAsync(ct); return; }

        // Failed counts rows; people think in titles ("Chernobyl" in the diary twice is one title).
        var unmatched = job.Unmatched.Count < 200 ? job.Unmatched.Count : job.Failed;
        job.Message = $"Imported {job.Imported:N0}, skipped {job.Skipped:N0} already present"
                      + (unmatched == 0 ? "." : $", {unmatched:N0} title{(unmatched == 1 ? "" : "s")} couldn't be matched.");
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
        var source = WatchSources.ForImport(job.Kind);

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
                        WatchedAt = w.WatchedAt, Source = source, ImportJobId = Tag(job),
                    });
                    plays.Add(new ExistingPlay(null, w.WatchedAt) { Used = true });
                    job.Imported++;
                }
                else if (match.Id is { } id && WatchDates.IsUnknown(match.At) && !w.Approximate)
                {
                    await db.WatchEvents.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(e => e.WatchedAt, w.WatchedAt), ct);
                    _undo.Dated.Add(id);   // undo puts its "date unknown" back
                    job.Imported++;
                }
                else job.Skipped++;
            }
            await StepAsync(job, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Turns "watched up to S02E05" into a play of every aired episode up to it. Only the last one
    /// gets the export's date; the rest are "date unknown" rather than all piling onto one day.
    /// </summary>
    private async Task ExpandProgressAsync(ImportJob job, ParsedImport parsed, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var p in parsed.Progress)
        {
            if (await ResolveAsync(job, p.Show, ct) is { } id)
            {
                var episodes = (await db.Episodes.AsNoTracking()
                        .Where(e => e.ShowTmdbId == id && e.SeasonNumber > 0 && e.AirDate != null && e.AirDate <= today)
                        .Select(e => new { e.SeasonNumber, e.EpisodeNumber }).ToListAsync(ct))
                    .Where(e => (e.SeasonNumber, e.EpisodeNumber).CompareTo((p.Season, p.Episode)) <= 0)
                    .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
                var known = p.Show with { TmdbId = id };
                _resolved[known] = id;
                for (var i = 0; i < episodes.Count; i++)
                {
                    var last = i == episodes.Count - 1 && p.LastWatchedAt is not null;
                    parsed.History.Add(new ImportWatch(known, episodes[i].SeasonNumber, episodes[i].EpisodeNumber,
                        last ? p.LastWatchedAt!.Value : WatchDates.Unknown, Approximate: !last));
                }
                job.Total += episodes.Count;
            }
            await StepAsync(job, ct);
        }
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
                    db.Watchlist.Add(new WatchlistItem { UserId = job.UserId, Type = item.Item.Type, TmdbId = id, AddedAt = item.AddedAt, ImportJobId = Tag(job) });
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
                    else
                    {
                        if (Tag(job) is not null && row.ImportJobId != Tag(job)) _undo.Ratings.Add(new RatingBefore(row.Id, row.Value, row.RatedAt));
                        row.Value = r.Rating; row.RatedAt = r.RatedAt; job.Imported++;
                    }
                }
                else
                {
                    row = new Rating { UserId = job.UserId, Type = type, TmdbId = id, SeasonNumber = r.Season, EpisodeNumber = r.Episode, Value = r.Rating, RatedAt = r.RatedAt, ImportJobId = Tag(job) };
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
        if (list is null && import.LegacyName is { } legacy && legacy != name)
        {
            // Imported before under the name an older importer gave it: rename rather than duplicate.
            var old = legacy.Length > 100 ? legacy[..100] : legacy;
            list = await db.Lists.Include(l => l.Items).FirstOrDefaultAsync(l => l.UserId == job.UserId && l.Name == old, ct);
            if (list is not null) { list.Name = name; list.Description ??= import.Description; }
        }
        if (list is null)
        {
            list = new UserList { UserId = job.UserId, Name = name, Description = import.Description, ImportJobId = Tag(job) };
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
                    var row = new UserListItem { ListId = list.Id, Type = item.Item.Type, TmdbId = id, AddedAt = item.AddedAt, Position = position++, ImportJobId = Tag(job) };
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

    /// <summary>Adds favorites in the export's order, after any the user already has.</summary>
    private async Task ImportFavoritesAsync(ImportJob job, List<ImportListItem> favorites, CancellationToken ct)
    {
        var existing = (await db.Favorites.Where(f => f.UserId == job.UserId).Select(f => new { f.Type, f.TmdbId }).ToListAsync(ct))
            .Select(f => (f.Type, f.TmdbId)).ToHashSet();
        var rank = await db.Favorites.Where(f => f.UserId == job.UserId).MaxAsync(f => (int?)f.Rank, ct) ?? 0;
        var imported = new HashSet<(MediaType, int)>();
        foreach (var item in favorites)
        {
            if (await ResolveAsync(job, item.Item, ct) is { } id)
            {
                imported.Add((item.Item.Type, id));
                if (!existing.Add((item.Item.Type, id))) job.Skipped++;
                else
                {
                    db.Favorites.Add(new Favorite { UserId = job.UserId, Type = item.Item.Type, TmdbId = id, Rank = ++rank, AddedAt = item.AddedAt, ImportJobId = Tag(job) });
                    job.Imported++;
                }
            }
            await StepAsync(job, ct);
        }
        await db.SaveChangesAsync(ct);

        // Older imports turned Trakt's favorites into an ordinary list called "Favorites". Now that
        // they're real favorites, drop that list if it holds nothing else.
        if (job.Kind == ImportKind.Trakt && imported.Count > 0
            && await db.Lists.Include(l => l.Items).FirstOrDefaultAsync(l => l.UserId == job.UserId && l.Name == "Favorites", ct) is { } stale
            && stale.Items.Count > 0 && stale.Items.All(i => imported.Contains((i.Type, i.TmdbId))))
        {
            db.Lists.Remove(stale);
            await db.SaveChangesAsync(ct);
        }
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

        foreach (var (external, source) in new[] { (r.ImdbId, "imdb_id"), (r.TvdbId?.ToString(CultureInfo.InvariantCulture), "tvdb_id") })
        {
            if (string.IsNullOrEmpty(external)) continue;
            var found = await tmdb.FindAsync(external, source, ct);
            var hit = r.Type == MediaType.Movie ? found?.MovieResults?.FirstOrDefault() : found?.TvResults?.FirstOrDefault();
            if (hit is not null) return hit.Id;
        }

        // Letterboxd's profile favorites are just links.
        if (string.IsNullOrWhiteSpace(r.Title)) return r.Type == MediaType.Movie ? await LetterboxdTmdbIdAsync(r.Url, ct) : null;

        if (r.Type == MediaType.Movie)
        {
            // Only an exact title counts. TMDB's first search result is often a different film
            // ("300" gave "My Poetic Works 300 Yen"), and a wrong match is worse than an unmatched
            // one, which at least gets listed. Release years differ between sources surprisingly
            // often (300 is 2006 on Letterboxd, 2007 on TMDB), so neighboring years are tried too.
            foreach (var year in r.Year is { } y ? new int?[] { y, y + 1, y - 1 } : [null])
                if (Exact((await tmdb.SearchMovieAsync(r.Title, year, ct))?.Results, r.Title, r.Year) is { } m) return m.Id;
            if (r.Year is not null && Exact((await tmdb.SearchMovieAsync(r.Title, null, ct))?.Results, r.Title, r.Year, maxYearGap: 2) is { } any)
                return any.Id;
            // Retitled on TMDB ("Glass Onion" is "Glass Onion: A Knives Out Mystery"): Letterboxd's
            // own page for the film names its TMDB id.
            return await LetterboxdTmdbIdAsync(r.Url, ct);
        }

        var shows = await tmdb.SearchShowAsync(r.Title, r.Year, ct) is { Results.Count: > 0 } withYear
            ? withYear : await tmdb.SearchShowAsync(r.Title, null, ct);
        if (Exact(shows?.Results, r.Title, r.Year, maxYearGap: 99) is { } show) return show.Id;

        // TV Time and others tell same-named shows apart with a suffix TMDB doesn't use:
        // "The Office (US)", "Doctor Who (2005)", "MasterChef (IT)".
        var suffix = System.Text.RegularExpressions.Regex.Match(r.Title, @"^(.+?)\s*\(([^()]+)\)$");
        if (!suffix.Success) return null;
        var bare = suffix.Groups[1].Value;
        int? suffixYear = int.TryParse(suffix.Groups[2].Value, out var sy) && sy is > 1900 and < 2200 ? sy : r.Year;
        var retry = await tmdb.SearchShowAsync(bare, suffixYear, ct) is { Results.Count: > 0 } r2 ? r2 : await tmdb.SearchShowAsync(bare, null, ct);
        return Exact(retry?.Results, bare, suffixYear, maxYearGap: suffixYear is null ? 99 : 1)?.Id;
    }

    /// <summary>
    /// Reads the TMDB id from a Letterboxd film page (boxd.it short links redirect there). Null for
    /// anything that isn't a TMDB movie — Letterboxd also lists TV miniseries, which have no single
    /// movie to attach a play to — or if Letterboxd can't be reached.
    /// </summary>
    private async Task<int?> LetterboxdTmdbIdAsync(string? url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host is not ("boxd.it" or "letterboxd.com" or "www.letterboxd.com"))
            return null;
        try
        {
            var html = await http.CreateClient("letterboxd").GetStringAsync(uri, ct);
            return LetterboxdPageTmdbId(html);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogInformation("Letterboxd lookup failed for {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    public static int? LetterboxdPageTmdbId(string html)
    {
        var type = System.Text.RegularExpressions.Regex.Match(html, @"data-tmdb-type=""(\w+)""");
        var id = System.Text.RegularExpressions.Regex.Match(html, @"data-tmdb-id=""(\d+)""");
        if (type.Success && type.Groups[1].Value == "movie" && id.Success) return int.Parse(id.Groups[1].Value);
        var link = System.Text.RegularExpressions.Regex.Match(html, @"themoviedb\.org/movie/(\d+)");
        return link.Success && !(type.Success && type.Groups[1].Value != "movie") ? int.Parse(link.Groups[1].Value) : null;
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

    private static int? YearOf(TmdbListItem x) =>
        (x.ReleaseDate ?? x.FirstAirDate) is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null;

    /// <summary>
    /// Case, accents, punctuation, "&amp;"/"and" and a leading article don't matter:
    /// "Spider-Man: No Way Home" = "spider man no way home", "The School of Rock" = "School of Rock".
    /// </summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"^(the|a|an)\s+(?=\S)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
