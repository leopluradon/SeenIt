using Microsoft.Extensions.Caching.Memory;
using SeenIt.Contracts;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>
/// Search-as-you-type: prefix matches from <see cref="SuggestIndex"/> merged with TMDB's own search (which knows
/// English titles of foreign films, and catches anything the index leaves out), ranked together, with posters.
/// </summary>
public class SuggestService(SuggestIndex index, TmdbClient tmdb, IMemoryCache cache, ILogger<SuggestService> log)
{
    public const int Titles = 6, People = 3;

    // Never let a slow TMDB call hold up the list: whatever isn't back by then is left out (or shown without a poster).
    private static readonly TimeSpan TmdbBudget = TimeSpan.FromMilliseconds(600);

    public async Task<List<Suggestion>> SuggestAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        var q = SuggestIndex.Normalize(query);
        if (q.Length == 0) return [];
        var key = "suggest:" + q;
        if (cache.TryGetValue(key, out List<Suggestion>? hit) && hit is not null) return hit;

        // TMDB calls aren't canceled at the deadline, just not waited for: they finish in the background and
        // land in the TMDB cache, so the next keystroke has them.
        var deadline = DateTime.UtcNow + TmdbBudget;
        var late = false;
        async Task<T> Bounded<T>(Task<T> task, T fallback)
        {
            var left = deadline - DateTime.UtcNow;
            try { return left > TimeSpan.Zero ? await task.WaitAsync(left, ct) : task.IsCompletedSuccessfully ? task.Result : Late(fallback); }
            catch (TimeoutException) { return Late(fallback); }
        }
        T Late<T>(T fallback) { late = true; return fallback; }

        // TMDB's search matches whole words, so it's only worth asking once a word could be complete.
        var tmdbTask = q.Length >= 3 ? Bounded(SafeSearchAsync(query), []) : Task.FromResult<List<TmdbListItem>>([]);

        var candidates = new Dictionary<(string Kind, int Id), (double Score, Suggestion? Ready)>();
        foreach (var (e, score) in index.Find(query, Titles * 2, People * 2))
            candidates[(KindName(e.Kind), e.Id)] = (score, null);

        foreach (var r in await tmdbTask)
        {
            var kind = r.MediaType switch { "movie" => "movie", "tv" => "show", "person" => "person", _ => null };
            if (kind is null) continue;
            var name = (kind == "movie" ? r.Title : r.Name) ?? r.Title ?? r.Name ?? "";
            var norm = SuggestIndex.Normalize(name);
            // TMDB's result as a suggestion, scored like an index match would be (word match + popularity).
            var offset = norm.StartsWith(q, StringComparison.Ordinal) ? 0 : norm.IndexOf(" " + q, StringComparison.Ordinal) is var i and >= 0 ? i + 1 : -1;
            var pop = (float)(r.Popularity ?? 0);
            var score = offset >= 0
                ? SuggestIndex.Score(new SuggestIndex.Entry(kind == "person" ? SuggestIndex.Kind.Person : SuggestIndex.Kind.Movie, r.Id, name, pop), norm, q, offset)
                : Math.Log(1 + pop) * 0.5; // TMDB matched it some other way (the original title, a translation)
            var ready = ToSuggestion(kind, r, name);
            if (candidates.TryGetValue((kind, r.Id), out var existing))
                candidates[(kind, r.Id)] = (Math.Max(existing.Score, score), ready); // same thing: keep the English title and poster
            else
                candidates[(kind, r.Id)] = (score, ready);
        }

        var titles = candidates.Where(c => c.Key.Kind != "person").OrderByDescending(c => c.Value.Score).Take(Titles);
        var people = candidates.Where(c => c.Key.Kind == "person").OrderByDescending(c => c.Value.Score).Take(People);
        var chosen = titles.Concat(people).ToList();

        // Index-only matches need a poster, year and the English title: look them up together (cached a day).
        var filled = await Task.WhenAll(chosen.Select(async c =>
            c.Value.Ready ?? await Bounded(DetailsAsync(c.Key.Kind, c.Key.Id), null)));
        // The index matches TMDB's original titles; when the title we'd show doesn't match what was typed
        // (a foreign film under its English name), it's a less likely pick.
        var result = chosen.Zip(filled, (c, f) => (c.Value.Score, Item: f))
            .Where(x => x.Item is not null)
            .Select(x => (Score: ShownMatches(x.Item!.Title, q) ? x.Score : x.Score * 0.5, x.Item))
            .OrderBy(x => x.Item!.Kind == "person")
            .ThenByDescending(x => x.Score)
            .Select(x => x.Item!)
            .ToList();

        // Keep a complete answer for a while; a partial one (TMDB was slow) only briefly.
        cache.Set(key, result, late ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(30));
        return result;
    }

    private static bool ShownMatches(string title, string q)
    {
        var n = SuggestIndex.Normalize(title);
        return n.StartsWith(q, StringComparison.Ordinal) || n.Contains(" " + q, StringComparison.Ordinal);
    }

    private async Task<List<TmdbListItem>> SafeSearchAsync(string query)
    {
        try { return (await tmdb.SearchMultiAsync(query, 1, CancellationToken.None))?.Results ?? []; }
        catch (Exception ex) { log.LogDebug(ex, "Suggest: TMDB search failed"); return []; }
    }

    private async Task<Suggestion?> DetailsAsync(string kind, int id)
    {
        try
        {
            if (await tmdb.GetBasicAsync(kind == "show" ? "tv" : kind, id, CancellationToken.None) is not { } b) return null;
            return kind switch
            {
                "person" => new Suggestion(kind, id, b.Name ?? "", null, b.ProfilePath, Department(b.KnownForDepartment)),
                "movie" => new Suggestion(kind, id, b.Title ?? b.Name ?? "", TmdbDates.Parse(b.ReleaseDate)?.Year, b.PosterPath, "Movie"),
                _ => new Suggestion(kind, id, b.Name ?? b.Title ?? "", TmdbDates.Parse(b.FirstAirDate)?.Year, b.PosterPath, "TV"),
            };
        }
        catch (Exception ex) { log.LogDebug(ex, "Suggest: details for {Kind} {Id} failed", kind, id); return null; }
    }

    private static Suggestion ToSuggestion(string kind, TmdbListItem r, string name) => kind switch
    {
        "person" => new Suggestion(kind, r.Id, name, null, r.ProfilePath, Department(r.KnownForDepartment)),
        "movie" => new Suggestion(kind, r.Id, name, TmdbDates.Parse(r.ReleaseDate)?.Year, r.PosterPath, "Movie"),
        _ => new Suggestion(kind, r.Id, name, TmdbDates.Parse(r.FirstAirDate)?.Year, r.PosterPath, "TV"),
    };

    private static string KindName(SuggestIndex.Kind k) => k switch
    {
        SuggestIndex.Kind.Movie => "movie",
        SuggestIndex.Kind.Show => "show",
        _ => "person",
    };

    private static string? Department(string? d) => d switch
    {
        "Acting" => "Actor",
        "Directing" => "Director",
        "Writing" => "Writer",
        "Production" => "Producer",
        null or "" => null,
        _ => d,
    };
}
