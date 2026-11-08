using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SeenIt.Server.Services;

/// <summary>
/// Search-as-you-type over TMDB's popular titles and people, in memory. TMDB's search API only matches whole
/// words ("seve" doesn't find Severance), so this keeps its own prefix index, built from TMDB's daily export
/// files (every movie, show and person with a popularity score) and refreshed daily.
/// Any word of a title can be the start of a match: "dark kni" finds The Dark Knight.
/// </summary>
public class SuggestIndex(IHttpClientFactory http, IConfiguration config, ILogger<SuggestIndex> log)
{
    public enum Kind : byte { Movie, Show, Person }

    /// <summary>One indexed title or person: its TMDB id, the name TMDB exports (the original title), and popularity.</summary>
    public readonly record struct Entry(Kind Kind, int Id, string Name, float Popularity);

    // What's worth suggesting: popularity at or above these (TMDB's scale; ~1 is a title few people look up).
    public const float MinMovie = 1f, MinShow = 1f, MinPerson = 1.5f;

    private sealed record Data(Entry[] Entries, string[] Norm, long[] Keys, DateTime BuiltAt);
    private volatile Data? data;

    public bool Ready => data is not null;
    public int Count => data?.Entries.Length ?? 0;
    public DateTime? BuiltAt => data?.BuiltAt;

    private string CacheFile => Path.Combine(config["SeenIt:CacheDir"] is { Length: > 0 } d ? d : Path.GetTempPath(), "suggest-index.tsv.gz");

    /// <summary>Loads the saved copy if it's recent, otherwise downloads TMDB's exports. Safe to call again to refresh.</summary>
    public async Task LoadAsync(bool forceDownload, CancellationToken ct)
    {
        var file = CacheFile;
        if (!forceDownload && File.Exists(file) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-2))
        {
            try
            {
                data = Build(await ReadCacheAsync(file, ct), File.GetLastWriteTimeUtc(file));
                log.LogInformation("Suggest index: {Count} entries from {File}", data.Entries.Length, file);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Suggest index: couldn't read {File}; downloading", file); }
        }
        var entries = await DownloadAsync(ct);
        data = Build(entries, DateTime.UtcNow);
        log.LogInformation("Suggest index: {Count} entries from TMDB exports", entries.Count);
        try { await WriteCacheAsync(file, entries, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Suggest index: couldn't save {File}", file); }
    }

    /// <summary>Replaces the index with these entries (tests, or a hand-built index).</summary>
    public void Use(IEnumerable<Entry> entries) => data = Build(entries.ToList(), DateTime.UtcNow);

    /// <summary>The best matches for what's been typed so far, most likely first.</summary>
    public List<(Entry Entry, double Score)> Find(string query, int titles, int people)
    {
        var d = data;
        var q = Normalize(query);
        if (d is null || q.Length == 0) return [];
        var lo = LowerBound(d, q);
        var best = new List<(Entry, double)>();
        var bestTitles = new PriorityQueue<int, double>();
        var bestPeople = new PriorityQueue<int, double>();
        var seen = new HashSet<int>();
        for (var i = lo; i < d.Keys.Length; i++)
        {
            var (entry, offset) = Unpack(d.Keys[i]);
            if (!d.Norm[entry].AsSpan(offset).StartsWith(q, StringComparison.Ordinal)) break;
            if (!seen.Add(entry)) continue; // matched at two words of the same title
            var e = d.Entries[entry];
            var score = Score(e, d.Norm[entry], q, offset);
            var (heap, max) = e.Kind == Kind.Person ? (bestPeople, people) : (bestTitles, titles);
            heap.Enqueue(entry, score);
            if (heap.Count > max) heap.Dequeue();
        }
        foreach (var heap in new[] { bestTitles, bestPeople })
            while (heap.TryDequeue(out var entry, out var score)) best.Add((d.Entries[entry], score));
        return best.OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>
    /// How good a match is: popularity, scaled up for an exact name, the name starting with what's typed, or a
    /// whole word typed. So "dune" puts Dune above a more popular title that merely has a word starting "dune".
    /// </summary>
    public static double Score(Entry e, string norm, string q, int offset)
    {
        // Popularity does most of the work; where the match is only nudges it, so a well-known title matched
        // on a later word (Le Fabuleux Destin d'Amélie Poulain for "amel") still beats obscure ones starting with it.
        var boost = norm == q ? 4.0
            : offset == 0 ? 1.6
            : offset == 4 && norm.StartsWith("the ", StringComparison.Ordinal) ? 1.5   // "dark kn" → The Dark Knight
            : 1.0;
        var wholeWord = norm.Length == offset + q.Length || norm[offset + q.Length] == ' ';
        if (wholeWord) boost *= 1.3;
        // People's popularity runs on a different scale from titles'; pull it into line.
        var pop = e.Kind == Kind.Person ? e.Popularity * 0.6 : e.Popularity;
        return Math.Log(1 + pop) * boost;
    }

    /// <summary>Lowercase, accents off, punctuation to spaces: "Amélie" → "amelie", "WALL·E" → "wall e".</summary>
    public static string Normalize(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); space = false; }
            else if (c is '\'' or '’')
            {
                // "d'Amélie" → "d amelie" (a one-letter word before it), but "Ocean's" → "oceans".
                var oneLetter = sb.Length >= 1 && (sb.Length == 1 || sb[^2] == ' ');
                if (oneLetter && !space) { sb.Append(' '); space = true; }
            }
            else if (!space) { sb.Append(' '); space = true; }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static long Pack(int entry, int offset) => ((long)entry << 16) | (uint)offset;
    private static (int Entry, int Offset) Unpack(long key) => ((int)(key >> 16), (int)(key & 0xFFFF));

    private static int LowerBound(Data d, string q)
    {
        int lo = 0, hi = d.Keys.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            var (entry, offset) = Unpack(d.Keys[mid]);
            if (d.Norm[entry].AsSpan(offset).CompareTo(q, StringComparison.Ordinal) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Every word start of every name becomes a key, sorted, so a prefix is one binary search away.</summary>
    private static Data Build(List<Entry> entries, DateTime builtAt)
    {
        var arr = entries.ToArray();
        var norm = new string[arr.Length];
        var keys = new List<long>(arr.Length * 3);
        for (var i = 0; i < arr.Length; i++)
        {
            var n = Normalize(arr[i].Name);
            if (n.Length > 200) n = n[..200];
            norm[i] = n;
            if (n.Length == 0) continue;
            for (var o = 0; o < n.Length; o++)
                if (o == 0 || n[o - 1] == ' ') keys.Add(Pack(i, o));
        }
        var k = keys.ToArray();
        Array.Sort(k, (a, b) =>
        {
            var (ea, oa) = Unpack(a);
            var (eb, ob) = Unpack(b);
            return norm[ea].AsSpan(oa).CompareTo(norm[eb].AsSpan(ob), StringComparison.Ordinal);
        });
        return new Data(arr, norm, k, builtAt);
    }

    // ---------- TMDB exports ----------

    private async Task<List<Entry>> DownloadAsync(CancellationToken ct)
    {
        var client = http.CreateClient(nameof(SuggestIndex));
        client.Timeout = TimeSpan.FromMinutes(10);
        // Each day's files appear in the morning (UTC); yesterday's are always there.
        var day = DateTime.UtcNow.AddDays(-1);
        var entries = new List<Entry>(400_000);
        foreach (var (file, kind, nameKey, min) in new[]
                 {
                     ("movie_ids", Kind.Movie, "original_title", MinMovie),
                     ("tv_series_ids", Kind.Show, "original_name", MinShow),
                     ("person_ids", Kind.Person, "name", MinPerson),
                 })
        {
            Exception? last = null;
            for (var back = 0; back < 3; back++)
            {
                var url = $"http://files.tmdb.org/p/exports/{file}_{day.AddDays(-back):MM_dd_yyyy}.json.gz";
                try
                {
                    var before = entries.Count;
                    using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    await using var gz = new GZipStream(await resp.Content.ReadAsStreamAsync(ct), CompressionMode.Decompress);
                    using var reader = new StreamReader(gz, Encoding.UTF8);
                    while (await reader.ReadLineAsync(ct) is { } line)
                        if (ParseLine(line, kind, nameKey, min) is { } e) entries.Add(e);
                    log.LogInformation("Suggest index: {Count} from {Url}", entries.Count - before, url);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
            }
            if (last is not null) throw new InvalidOperationException($"Couldn't download TMDB's {file} export", last);
        }
        return entries;
    }

    public static Entry? ParseLine(string line, Kind kind, string nameKey, float min)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
        int id = 0;
        string? name = null;
        float pop = 0;
        bool skip = false;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            var prop = reader.GetString();
            reader.Read();
            switch (prop)
            {
                case "id": id = reader.GetInt32(); break;
                case "popularity": pop = (float)reader.GetDouble(); break;
                case "adult" or "video": skip |= reader.TokenType == JsonTokenType.True; break;
                default: if (prop == nameKey && reader.TokenType == JsonTokenType.String) name = reader.GetString(); break;
            }
        }
        return skip || id == 0 || pop < min || string.IsNullOrWhiteSpace(name) ? null : new Entry(kind, id, name, pop);
    }

    // ---------- Saved copy (so restarts and deploys don't download ~110MB again) ----------

    private static async Task WriteCacheAsync(string file, List<Entry> entries, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        await using (var fs = File.Create(tmp))
        await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        await using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            foreach (var e in entries)
                await w.WriteLineAsync($"{(int)e.Kind}\t{e.Id}\t{e.Popularity.ToString(CultureInfo.InvariantCulture)}\t{e.Name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')}".AsMemory(), ct);
        }
        File.Move(tmp, file, overwrite: true);
    }

    private static async Task<List<Entry>> ReadCacheAsync(string file, CancellationToken ct)
    {
        var entries = new List<Entry>(400_000);
        await using var fs = File.OpenRead(file);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        while (await r.ReadLineAsync(ct) is { } line)
        {
            var p = line.Split('\t', 4);
            if (p.Length == 4) entries.Add(new Entry((Kind)int.Parse(p[0]), int.Parse(p[1]), p[3], float.Parse(p[2], CultureInfo.InvariantCulture)));
        }
        return entries;
    }
}

/// <summary>Loads the suggest index at startup (from the saved copy when fresh) and refreshes it daily.</summary>
public class SuggestIndexWorker(SuggestIndex index, ILogger<SuggestIndexWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var force = false;
        while (!ct.IsCancellationRequested)
        {
            try { await index.LoadAsync(force, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Suggest index: refresh failed; will retry"); }
            // Retry sooner if there's nothing loaded yet.
            try { await Task.Delay(index.Ready ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(15), ct); }
            catch (OperationCanceledException) { return; }
            force = index.Ready;
        }
    }
}
