using System.IO.Compression;
using System.Text;
using SeenIt.Contracts;

namespace SeenIt.Server.Import;

/// <summary>A movie or show reference from an export, before it's matched to TMDB.</summary>
public record ImportRef(MediaType Type, int? TmdbId, string? ImdbId, string? Title, int? Year)
{
    public string Label => Year is { } y ? $"{Title} ({y})" : Title ?? ImdbId ?? $"#{TmdbId}";
}

public record ImportWatch(ImportRef Item, int? Season, int? Episode, DateTimeOffset WatchedAt);
public record ImportRating(ImportRef Item, int? Season, int? Episode, int Rating, DateTimeOffset RatedAt);
public record ImportListItem(ImportRef Item, DateTimeOffset AddedAt);
public record ImportList(string Name, string? Description, List<ImportListItem> Items);

public class ParsedImport
{
    public List<ImportWatch> History { get; } = [];
    public List<ImportListItem> Watchlist { get; } = [];
    public List<ImportRating> Ratings { get; } = [];
    public List<ImportList> Lists { get; } = [];

    public int Total => History.Count + Watchlist.Count + Ratings.Count + Lists.Sum(l => l.Items.Count);
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
