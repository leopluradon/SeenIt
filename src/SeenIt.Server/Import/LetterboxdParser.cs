using System.Globalization;
using System.Text;
using SeenIt.Contracts;

namespace SeenIt.Server.Import;

/// <summary>
/// Reads a Letterboxd export (Settings → Import &amp; Export → Export your data). Letterboxd has no
/// TMDB ids in its export, so every film is a title + year to be searched for later.
/// </summary>
public static class LetterboxdParser
{
    public static ParsedImport Parse(IReadOnlyDictionary<string, string> files)
    {
        var result = new ParsedImport();
        var diaryFilms = new HashSet<(string, int?)>();

        string? Find(string name) => files.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f.Key), name, StringComparison.OrdinalIgnoreCase)
            && !f.Key.Contains("deleted/", StringComparison.OrdinalIgnoreCase)
            && !f.Key.Contains("orphaned/", StringComparison.OrdinalIgnoreCase)).Value;

        // A single uploaded CSV: work out which file it is from its header.
        if (files.Count == 1 && files.Keys.Single().EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var only = files.Values.Single();
            var header = only.Split('\n', 2)[0];
            var key = header.Contains("Watched Date") ? "diary.csv"
                : header.Contains("Rating") ? "ratings.csv"
                : header.StartsWith("Letterboxd list export") ? "lists/list.csv"
                : "watched.csv";
            files = new Dictionary<string, string> { [key] = only };
        }

        if (Find("diary.csv") is { } diary)
        {
            foreach (var row in Rows(diary))
            {
                if (Film(row) is not { } film) continue;
                var date = ParseDate(row.GetValueOrDefault("Watched Date")) ?? ParseDate(row.GetValueOrDefault("Date"));
                if (date is null) continue;
                diaryFilms.Add(Key(film));
                result.History.Add(new ImportWatch(film, null, null, date.Value));
            }
        }

        // Films logged as watched without a diary date get one play on the day they were logged.
        if (Find("watched.csv") is { } watched)
        {
            foreach (var row in Rows(watched))
            {
                if (Film(row) is not { } film || diaryFilms.Contains(Key(film))) continue;
                result.History.Add(new ImportWatch(film, null, null, ParseDate(row.GetValueOrDefault("Date")) ?? DateTimeOffset.UtcNow, Approximate: true));
            }
        }

        if (Find("ratings.csv") is { } ratings)
        {
            foreach (var row in Rows(ratings))
            {
                if (Film(row) is not { } film) continue;
                if (!double.TryParse(row.GetValueOrDefault("Rating"), NumberStyles.Float, CultureInfo.InvariantCulture, out var stars) || stars <= 0) continue;
                // Letterboxd's half-stars (0.5–5) map exactly onto 1–10.
                var value = Math.Clamp((int)Math.Round(stars * 2, MidpointRounding.AwayFromZero), 1, 10);
                result.Ratings.Add(new ImportRating(film, null, null, value, ParseDate(row.GetValueOrDefault("Date")) ?? DateTimeOffset.UtcNow));
            }
        }

        if (Find("watchlist.csv") is { } watchlist)
        {
            foreach (var row in Rows(watchlist))
                if (Film(row) is { } film)
                    result.Watchlist.Add(new ImportListItem(film, ParseDate(row.GetValueOrDefault("Date")) ?? DateTimeOffset.UtcNow));
        }

        foreach (var (name, text) in files.Where(f => f.Key.Contains("lists/", StringComparison.OrdinalIgnoreCase)
                                                      && !f.Key.Contains("deleted/", StringComparison.OrdinalIgnoreCase)))
        {
            if (ParseList(name, text) is { Items.Count: > 0 } list) result.Lists.Add(list);
        }

        return result;
    }

    /// <summary>
    /// A list file has a preamble ("Letterboxd list export v7", a header and one row describing
    /// the list), a blank line, then the films with a Position column.
    /// </summary>
    private static ImportList? ParseList(string fileName, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var itemsStart = Array.FindIndex(lines, l => l.StartsWith("Position,", StringComparison.Ordinal));
        if (itemsStart < 0) return null;

        string name = Path.GetFileNameWithoutExtension(fileName).Replace('-', ' ');
        string? description = null;
        var metaHeader = Array.FindIndex(lines, l => l.StartsWith("Date,Name,", StringComparison.Ordinal));
        if (metaHeader >= 0 && metaHeader < itemsStart)
        {
            var meta = Rows(string.Join('\n', lines[metaHeader..itemsStart])).FirstOrDefault();
            if (meta?.GetValueOrDefault("Name") is { Length: > 0 } n) name = n;
            if (meta?.GetValueOrDefault("Description") is { Length: > 0 } d) description = d;
        }

        var items = Rows(string.Join('\n', lines[itemsStart..]))
            .Select(r => Film(r) is { } f ? new ImportListItem(f, DateTimeOffset.UtcNow) : null)
            .OfType<ImportListItem>().ToList();
        return new ImportList(name, description, items);
    }

    private static ImportRef? Film(Dictionary<string, string> row)
    {
        var name = row.GetValueOrDefault("Name");
        if (string.IsNullOrWhiteSpace(name)) return null;
        int? year = int.TryParse(row.GetValueOrDefault("Year"), out var y) ? y : null;
        return new ImportRef(MediaType.Movie, null, null, name.Trim(), year);
    }

    private static (string, int?) Key(ImportRef r) => (r.Title!.ToLowerInvariant(), r.Year);

    /// <summary>Dates only; noon UTC keeps the day the same in every time zone.</summary>
    private static DateTimeOffset? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)
            : null;

    public static IEnumerable<Dictionary<string, string>> Rows(string csv)
    {
        var records = Csv.Parse(csv).ToList();
        if (records.Count == 0) yield break;
        var header = records[0];
        foreach (var rec in records.Skip(1))
        {
            if (rec.All(string.IsNullOrWhiteSpace)) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < rec.Count; i++) row[header[i].Trim()] = rec[i];
            yield return row;
        }
    }
}

/// <summary>Minimal RFC 4180 CSV reader (quoted fields, embedded commas, quotes and newlines).</summary>
public static class Csv
{
    public static IEnumerable<List<string>> Parse(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    yield return row;
                    row = [];
                    break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
