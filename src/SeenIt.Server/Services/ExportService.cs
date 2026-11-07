using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>Everything a user has put into SeenIt, as a zip of JSON and/or CSV files.</summary>
public class ExportService(AppDbContext db, TrackingService tracking, ListService lists)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<byte[]> BuildAsync(Guid userId, CancellationToken ct, ExportFormat format = ExportFormat.Both)
    {
        var json = format != ExportFormat.Csv;
        var csv = format != ExportFormat.Json;
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);

        var events = await db.WatchEvents.AsNoTracking().Where(w => w.UserId == userId).OrderBy(w => w.WatchedAt).ToListAsync(ct);
        var history = (await tracking.ToHistoryAsync(events, ct)).Select(h => new
        {
            h.Id, Type = h.Type.ToString().ToLowerInvariant(), h.TmdbId, h.Title, Season = h.SeasonNumber, Episode = h.EpisodeNumber,
            h.EpisodeTitle, WatchedAt = WatchDates.IsUnknown(h.WatchedAt) ? (DateTimeOffset?)null : h.WatchedAt,
            Source = h.Source.ToString(), RuntimeMinutes = h.Runtime,
        }).ToList();

        var ratings = (await tracking.GetRatingsAsync(userId, null, ct)).Select(r => new
        {
            Type = r.Type.ToString().ToLowerInvariant(), r.TmdbId, r.Title, Season = r.SeasonNumber, Episode = r.EpisodeNumber, r.Rating, r.RatedAt,
        }).ToList();

        var watchlist = (await tracking.GetWatchlistAsync(userId, ct)).ToList();

        var myLists = new List<object>();
        var myListRows = new List<(string List, bool Public, string Type, int TmdbId, string Title, int? Year, DateTimeOffset AddedAt)>();
        foreach (var summary in await lists.GetMineAsync(userId, ct))
        {
            if (await lists.GetAsync(summary.Id, userId, ct) is not { } l) continue;
            myLists.Add(new
            {
                l.Id, l.Name, l.Description, l.IsPublic, Ranked = l.Ranked,
                Items = l.Items.Select(i => new { Type = i.Media.Type.ToString().ToLowerInvariant(), i.Media.TmdbId, i.Media.Title, i.Media.Year, i.AddedAt }),
            });
            myListRows.AddRange(l.Items.Select(i => (l.Name, l.IsPublic, i.Media.Type.ToString().ToLowerInvariant(), i.Media.TmdbId, i.Media.Title, i.Media.Year, i.AddedAt)));
        }

        var rewatches = await db.Rewatches.AsNoTracking().Where(r => r.UserId == userId)
            .Join(db.Shows, r => r.ShowTmdbId, sh => sh.TmdbId, (r, sh) => new { show = sh.Name, tmdbId = r.ShowTmdbId, number = r.Number, startedAt = r.StartedAt, completedAt = r.CompletedAt })
            .ToListAsync(ct);
        var showStatus = await db.ShowStates.AsNoTracking().Where(s => s.UserId == userId)
            .Join(db.Shows, s => s.ShowTmdbId, m => m.TmdbId, (s, m) => new { TmdbId = s.ShowTmdbId, m.Name, Status = s.Status.ToString(), s.UpdatedAt })
            .ToListAsync(ct);
        var favorites = (await tracking.GetFavoritesAsync(userId, ct))
            .Select(f => new { Type = f.Media.Type.ToString().ToLowerInvariant(), f.Media.TmdbId, f.Media.Title, f.Media.Year, f.AddedAt }).ToList();
        var notes = await db.Notes.AsNoTracking().Where(n => n.UserId == userId)
            .Select(n => new { Type = n.Type.ToString().ToLowerInvariant(), n.TmdbId, n.Text, n.UpdatedAt }).ToListAsync(ct);
        var library = await db.Library.AsNoTracking().Where(l => l.UserId == userId)
            .Select(l => new { Type = l.Type.ToString().ToLowerInvariant(), l.TmdbId, Format = l.Format.ToString(), l.AddedAt }).ToListAsync(ct);
        var following = await db.Follows.Where(f => f.FollowerId == userId).Join(db.Users, f => f.FolloweeId, u => u.Id, (f, u) => u.UserName).ToListAsync(ct);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            void AddJson(string name, object value) { if (json) Add(name, JsonSerializer.Serialize(value, Json)); }
            void AddCsv(string name, string[] header, IEnumerable<object?[]> rows) { if (csv) Add(name, Csv(header, rows)); }
            var kinds = format switch { ExportFormat.Json => "JSON", ExportFormat.Csv => "CSV", _ => "JSON and CSV" };

            Add("README.txt", $"""
                SeenIt export for {user.UserName}, created {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC, as {kinds}.

                history      every play (watched_at is empty when the date is unknown)
                ratings      your ratings, 1-10
                watchlist    things you want to watch
                lists        your lists and their items
                show-status  shows you've paused or dropped
                rewatches, favorites, notes, library
                profile      account details, settings and who you follow

                IDs are TMDB ids (https://www.themoviedb.org). For episodes, tmdb_id is the show's id.
                """);
            AddCsv("profile.csv", ["field", "value"], new object?[][]
            {
                ["user_name", user.UserName], ["email", user.Email], ["display_name", user.DisplayName], ["created_at", user.CreatedAt.ToString("O")],
                ["visibility", user.Visibility], ["region", user.Region], ["following", string.Join(' ', following)],
            });
            AddJson("profile.json", new
            {
                user.UserName, user.Email, user.DisplayName, user.CreatedAt, Visibility = user.Visibility.ToString(), user.Region,
                user.HideSpoilers, user.BlurSpoilerImages, Following = following,
            });
            AddJson("history.json", history);
            AddCsv("history.csv", ["type", "tmdb_id", "title", "season", "episode", "episode_title", "watched_at", "source"],
                history.Select(h => new object?[] { h.Type, h.TmdbId, h.Title, h.Season, h.Episode, h.EpisodeTitle, h.WatchedAt?.ToString("O"), h.Source }));
            AddJson("ratings.json", ratings);
            AddCsv("ratings.csv", ["type", "tmdb_id", "title", "season", "episode", "rating", "rated_at"],
                ratings.Select(r => new object?[] { r.Type, r.TmdbId, r.Title, r.Season, r.Episode, r.Rating, r.RatedAt.ToString("O") }));
            AddJson("watchlist.json", watchlist);
            AddJson("lists.json", myLists);
            AddJson("show-status.json", showStatus);
            AddJson("rewatches.json", rewatches);
            AddJson("favorites.json", favorites);
            AddJson("notes.json", notes);
            AddJson("library.json", library);
            AddCsv("watchlist.csv", ["type", "tmdb_id", "title", "year", "added_at"],
                watchlist.Select(w => new object?[] { w.Media.Type.ToString().ToLowerInvariant(), w.Media.TmdbId, w.Media.Title, w.Media.Year, w.AddedAt.ToString("O") }));
            AddCsv("lists.csv", ["list", "public", "type", "tmdb_id", "title", "year", "added_at"],
                myListRows.Select(r => new object?[] { r.List, r.Public, r.Type, r.TmdbId, r.Title, r.Year, r.AddedAt.ToString("O") }));
            AddCsv("show-status.csv", ["tmdb_id", "show", "status", "updated_at"],
                showStatus.Select(s => new object?[] { s.TmdbId, s.Name, s.Status, s.UpdatedAt.ToString("O") }));
            AddCsv("rewatches.csv", ["tmdb_id", "show", "number", "started_at", "completed_at"],
                rewatches.Select(r => new object?[] { r.tmdbId, r.show, r.number, r.startedAt.ToString("O"), r.completedAt?.ToString("O") }));
            AddCsv("favorites.csv", ["type", "tmdb_id", "title", "year", "added_at"],
                favorites.Select(f => new object?[] { f.Type, f.TmdbId, f.Title, f.Year, f.AddedAt.ToString("O") }));
            AddCsv("notes.csv", ["type", "tmdb_id", "note", "updated_at"],
                notes.Select(n => new object?[] { n.Type, n.TmdbId, n.Text, n.UpdatedAt.ToString("O") }));
            AddCsv("library.csv", ["type", "tmdb_id", "format", "added_at"],
                library.Select(l => new object?[] { l.Type, l.TmdbId, l.Format, l.AddedAt.ToString("O") }));
        }
        return ms.ToArray();
    }

    private static string Csv(string[] header, IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', header));
        foreach (var row in rows)
            sb.AppendLine(string.Join(',', row.Select(v => Cell(Convert.ToString(v, CultureInfo.InvariantCulture)))));
        return sb.ToString();

        static string Cell(string? s) =>
            s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
