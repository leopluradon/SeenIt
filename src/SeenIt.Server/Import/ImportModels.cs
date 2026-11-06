using System.IO.Compression;
using System.Text.Json;
using System.Text;
using SeenIt.Contracts;

namespace SeenIt.Server.Import;

/// <summary>A movie or show reference from an export, before it's matched to TMDB.</summary>
/// <param name="Url">Letterboxd's page for the film, used to find the TMDB id when the title search can't.</param>
/// <param name="TvdbId">TheTVDB id (TV Time and Simkl have them), matched through TMDB's /find.</param>
public record ImportRef(MediaType Type, int? TmdbId, string? ImdbId, string? Title, int? Year, string? Url = null, int? TvdbId = null)
{
    public string Label => Year is { } y ? $"{Title} ({y})" : Title ?? ImdbId ?? Url ?? $"#{TmdbId}";
}

/// <param name="Approximate">The date is a stand-in (when it was logged, or a "last watched" summary), not when it was seen.</param>
public record ImportWatch(ImportRef Item, int? Season, int? Episode, DateTimeOffset WatchedAt, bool Approximate = false);
public record ImportRating(ImportRef Item, int? Season, int? Episode, int Rating, DateTimeOffset RatedAt);
public record ImportListItem(ImportRef Item, DateTimeOffset AddedAt);
/// <summary>Something owned (Trakt's collection), and in what format.</summary>
public record ImportOwned(ImportRef Item, OwnedFormat Format, DateTimeOffset AddedAt);
/// <summary>"Watched up to S02E05" (int.MaxValue for "all of it"): expanded into plays once the show's episodes are known.</summary>
public record ImportProgress(ImportRef Show, int Season, int Episode, DateTimeOffset? LastWatchedAt);
/// <param name="LegacyName">What an older importer called this list, so a re-import renames it instead of duplicating it.</param>
public record ImportList(string Name, string? Description, List<ImportListItem> Items, string? LegacyName = null);

public class ParsedImport
{
    public List<ImportWatch> History { get; } = [];
    public List<ImportListItem> Watchlist { get; } = [];
    public List<ImportRating> Ratings { get; } = [];
    public List<ImportList> Lists { get; } = [];
    /// <summary>Favorite movies/shows, best first.</summary>
    public List<ImportListItem> Favorites { get; } = [];
    public List<ImportProgress> Progress { get; } = [];
    public List<ImportOwned> Library { get; } = [];

    /// <summary>Drops whatever the user chose not to import.</summary>
    public void Keep(ImportParts parts)
    {
        bool Type(ImportRef r) => r.Type == MediaType.Movie ? parts.HasFlag(ImportParts.Movies) : parts.HasFlag(ImportParts.Shows);
        if (!parts.HasFlag(ImportParts.History)) History.Clear(); else History.RemoveAll(h => !Type(h.Item));
        if (!parts.HasFlag(ImportParts.History) || !parts.HasFlag(ImportParts.Shows)) Progress.Clear();
        if (!parts.HasFlag(ImportParts.Ratings)) Ratings.Clear(); else Ratings.RemoveAll(r => !Type(r.Item));
        if (!parts.HasFlag(ImportParts.Watchlist)) Watchlist.Clear(); else Watchlist.RemoveAll(w => !Type(w.Item));
        if (!parts.HasFlag(ImportParts.Lists)) { Lists.Clear(); Favorites.Clear(); }
        Favorites.RemoveAll(f => !Type(f.Item));
        if (!parts.HasFlag(ImportParts.Library)) Library.Clear(); else Library.RemoveAll(l => !Type(l.Item));
        foreach (var l in Lists) l.Items.RemoveAll(i => !Type(i.Item));
        Lists.RemoveAll(l => l.Items.Count == 0);
    }

    public int Total => History.Count + Watchlist.Count + Ratings.Count + Lists.Sum(l => l.Items.Count) + Favorites.Count + Progress.Count + Library.Count;
}

public static class ImportFiles
{
    /// <summary>Unpacks a zip (or treats a single file as itself) into name → text.</summary>
    public static Dictionary<string, string> Read(string fileName, byte[] data)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isZip = data.Length > 4 && data[0] == 'P' && data[1] == 'K';
        if (!isZip)
        {
            files[Path.GetFileName(fileName)] = Decode(data);
            return files;
        }

        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.Length == 0) continue;
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext is not (".json" or ".csv")) continue;
            // Guard against zip bombs.
            total += entry.Length;
            if (total > 200 * 1024 * 1024) throw new InvalidDataException("The export is too large once unzipped.");
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files[entry.FullName.Replace('\\', '/')] = Decode(ms.ToArray());
        }
        return files;
    }

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');
}

/// <summary>A rating an import overwrote, so undo can put the old one back.</summary>
public record RatingBefore(Guid Id, double Value, DateTimeOffset RatedAt);

/// <summary>What an import changed in place (rather than added), for undo.</summary>
public class ImportUndo
{
    public List<RatingBefore> Ratings { get; set; } = [];
    /// <summary>Plays of yours that had "date unknown" and took the import's date.</summary>
    public List<Guid> Dated { get; set; } = [];

    public static ImportUndo Parse(string? json) =>
        string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<ImportUndo>(json) ?? new();

    public static string Merge(string? existing, ImportUndo more)
    {
        var all = Parse(existing);
        // The first value seen is the one from before the import.
        foreach (var r in more.Ratings) if (all.Ratings.All(x => x.Id != r.Id)) all.Ratings.Add(r);
        all.Dated = [.. all.Dated.Union(more.Dated)];
        return JsonSerializer.Serialize(all);
    }
}

public record PendingPlay(int? Season, int? Episode, DateTimeOffset WatchedAt, bool Approximate);
public record PendingRating(int? Season, int? Episode, int Value, DateTimeOffset RatedAt);
public record PendingListEntry(string List, string? Description, DateTimeOffset AddedAt);

/// <summary>
/// An unmatched title from an import with everything it would have brought in, kept so the user
/// can pick the right title later (the uploaded file itself isn't kept).
/// </summary>
public record PendingImport(
    string Key, string Label, MediaType Type, string? Title, int? Year,
    List<PendingPlay> Plays, List<PendingRating> Ratings, DateTimeOffset? Watchlist,
    List<PendingListEntry> Lists, DateTimeOffset? Favorite, ImportProgress? Progress)
{
    private const int Max = 500;

    public static List<PendingImport> From(ParsedImport parsed, IReadOnlyDictionary<ImportRef, int?> resolved)
    {
        var result = new List<PendingImport>();
        foreach (var r in resolved.Where(x => x.Value is null).Select(x => x.Key).DistinctBy(r => (r.Type, r.Label)).Take(Max))
        {
            bool Same(ImportRef x) => x.Type == r.Type && x.Label == r.Label;
            var plays = parsed.History.Where(h => Same(h.Item)).Select(h => new PendingPlay(h.Season, h.Episode, h.WatchedAt, h.Approximate)).ToList();
            var ratings = parsed.Ratings.Where(x => Same(x.Item)).Select(x => new PendingRating(x.Season, x.Episode, x.Rating, x.RatedAt)).ToList();
            var lists = parsed.Lists.SelectMany(l => l.Items.Where(i => Same(i.Item)).Select(i => new PendingListEntry(l.Name, l.Description, i.AddedAt))).ToList();
            var item = new PendingImport($"p{result.Count}", r.Label, r.Type, r.Title, r.Year, plays, ratings,
                parsed.Watchlist.FirstOrDefault(w => Same(w.Item))?.AddedAt, lists,
                parsed.Favorites.FirstOrDefault(f => Same(f.Item))?.AddedAt, parsed.Progress.FirstOrDefault(p => Same(p.Show)));
            if (plays.Count + ratings.Count + lists.Count > 0 || item.Watchlist is not null || item.Favorite is not null || item.Progress is not null)
                result.Add(item);
        }
        return result;
    }

    public static string? Serialize(List<PendingImport> items) => items.Count == 0 ? null : JsonSerializer.Serialize(items);
    public static List<PendingImport> Parse(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<PendingImport>>(json) ?? [];

    /// <summary>These rows again, now pointing at the title the user picked.</summary>
    public ParsedImport ToParsed(int tmdbId)
    {
        var r = new ImportRef(Type, tmdbId, null, Title, Year);
        var parsed = new ParsedImport();
        parsed.History.AddRange(Plays.Select(p => new ImportWatch(r, p.Season, p.Episode, p.WatchedAt, p.Approximate)));
        parsed.Ratings.AddRange(Ratings.Select(x => new ImportRating(r, x.Season, x.Episode, x.Value, x.RatedAt)));
        if (Watchlist is { } w) parsed.Watchlist.Add(new ImportListItem(r, w));
        foreach (var g in Lists.GroupBy(l => l.List))
            parsed.Lists.Add(new ImportList(g.Key, g.First().Description, [.. g.Select(l => new ImportListItem(r, l.AddedAt))]));
        if (Favorite is { } f) parsed.Favorites.Add(new ImportListItem(r, f));
        if (Progress is { } p) parsed.Progress.Add(p with { Show = r });
        return parsed;
    }

    public string Summary()
    {
        var parts = new List<string>();
        var plays = Plays.Count;
        if (plays > 0) parts.Add(Type == MediaType.Show ? $"{plays} episode{(plays == 1 ? "" : "s")}" : $"{plays} play{(plays == 1 ? "" : "s")}");
        if (Progress is not null) parts.Add("progress");
        if (Ratings.FirstOrDefault(x => x.Season is null) is { } rating) parts.Add($"rated {rating.Value}");
        if (Watchlist is not null) parts.Add("watchlist");
        if (Favorite is not null) parts.Add("favorite");
        if (Lists.Count > 0) parts.Add(Lists.Count == 1 ? $"list “{Lists[0].List}”" : $"{Lists.Count} lists");
        return string.Join(" · ", parts);
    }
}
