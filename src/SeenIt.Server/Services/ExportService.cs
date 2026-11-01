using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>Everything a user has put into SeenIt, as a zip of JSON and CSV files.</summary>
public class ExportService(AppDbContext db, TrackingService tracking, ListService lists)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<byte[]> BuildAsync(Guid userId, CancellationToken ct)
    {
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
        foreach (var summary in await lists.GetMineAsync(userId, ct))
        {
            if (await lists.GetAsync(summary.Id, userId, ct) is not { } l) continue;
            myLists.Add(new
            {
                l.Id, l.Name, l.Description, l.IsPublic,
                Items = l.Items.Select(i => new { Type = i.Media.Type.ToString().ToLowerInvariant(), i.Media.TmdbId, i.Media.Title, i.Media.Year, i.AddedAt }),
            });
        }

        var rewatches = await db.Rewatches.AsNoTracking().Where(r => r.UserId == userId)
            .Join(db.Shows, r => r.ShowTmdbId, sh => sh.TmdbId, (r, sh) => new { show = sh.Name, tmdbId = r.ShowTmdbId, number = r.Number, startedAt = r.StartedAt, completedAt = r.CompletedAt })
            .ToListAsync(ct);
        var showStatus = await db.ShowStates.AsNoTracking().Where(s => s.UserId == userId)
            .Join(db.Shows, s => s.ShowTmdbId, m => m.TmdbId, (s, m) => new { TmdbId = s.ShowTmdbId, m.Name, Status = s.Status.ToString(), s.UpdatedAt })
            .ToListAsync(ct);
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
            void AddJson(string name, object value) => Add(name, JsonSerializer.Serialize(value, Json));

            Add("README.txt", $"""
                SeenIt export for {user.UserName}, created {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC.

                history.json / history.csv  every play (watchedAt is empty when the date is unknown)
                ratings.json / ratings.csv  your ratings, 1-10
                watchlist.json              things you want to watch
                lists.json                  your lists and their items
                show-status.json            shows you've paused or dropped
                profile.json                account details, settings and who you follow

                IDs are TMDB ids (https://www.themoviedb.org). For episodes, tmdbId is the show's id.
                """);
            AddJson("profile.json", new
            {
                user.UserName, user.Email, user.DisplayName, user.CreatedAt, Visibility = user.Visibility.ToString(), user.Region,
                user.HideSpoilers, user.BlurSpoilerImages, Following = following,
            });
            AddJson("history.json", history);
            Add("history.csv", Csv(["type", "tmdb_id", "title", "season", "episode", "episode_title", "watched_at", "source"],
                history.Select(h => new object?[] { h.Type, h.TmdbId, h.Title, h.Season, h.Episode, h.EpisodeTitle, h.WatchedAt?.ToString("O"), h.Source })));
            AddJson("ratings.json", ratings);
            Add("ratings.csv", Csv(["type", "tmdb_id", "title", "season", "episode", "rating", "rated_at"],
                ratings.Select(r => new object?[] { r.Type, r.TmdbId, r.Title, r.Season, r.Episode, r.Rating, r.RatedAt.ToString("O") })));
            AddJson("watchlist.json", watchlist);
            AddJson("lists.json", myLists);
            AddJson("show-status.json", showStatus);
            AddJson("rewatches.json", rewatches);
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
