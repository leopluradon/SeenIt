using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

public class MdbListOptions
{
    /// <summary>Free key from mdblist.com (Preferences → API). Without it only IMDb's rating is shown.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>
/// What other sites score a movie or show. IMDb comes from IMDb's daily ratings file (no key);
/// Rotten Tomatoes, Metacritic, Letterboxd, Trakt and others come from MDBList when a key is set.
/// </summary>
public class ScoreService(AppDbContext db, TmdbClient tmdb, CatalogService catalog, IHttpClientFactory http,
    Microsoft.Extensions.Options.IOptions<MdbListOptions> options, ILogger<ScoreService> log)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>MDBList source → name, scale, whether it's a percentage. Order is display order.</summary>
    private static readonly (string Source, string Name, double Max, bool Percent)[] Sources =
    [
        ("tomatoes", "Rotten Tomatoes", 100, true),
        ("popcorn", "RT audience", 100, true),
        ("metacritic", "Metacritic", 100, false),
        ("metacriticuser", "Metacritic users", 10, false),
        ("letterboxd", "Letterboxd", 5, false),
        ("trakt", "Trakt", 100, true),
        ("rogerebert", "Roger Ebert", 4, false),
        ("myanimelist", "MyAnimeList", 10, false),
    ];

    public bool HasMdbList => !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public async Task<List<ExternalScore>> GetAsync(MediaType type, int tmdbId, CancellationToken ct)
    {
        if (type is not (MediaType.Movie or MediaType.Show)) return [];
        var scores = new List<ExternalScore>();

        var imdbId = type == MediaType.Movie
            ? (await catalog.EnsureMovieAsync(tmdbId, ct))?.ImdbId
            : (await tmdb.GetExternalIdsAsync("tv", tmdbId, ct))?.ImdbId;
        if (!string.IsNullOrEmpty(imdbId)
            && await db.ImdbRatings.AsNoTracking().FirstOrDefaultAsync(r => r.ImdbId == imdbId, ct) is { } imdb)
            scores.Add(new ExternalScore("imdb", "IMDb", imdb.Rating, 10, false, imdb.Votes, $"https://www.imdb.com/title/{imdbId}/"));

        if (HasMdbList) scores.AddRange(await MdbListAsync(type, tmdbId, imdbId, ct));
        return scores;
    }

    private async Task<List<ExternalScore>> MdbListAsync(MediaType type, int tmdbId, string? imdbId, CancellationToken ct)
    {
        var cached = await db.ExternalScores.FirstOrDefaultAsync(x => x.Type == type && x.TmdbId == tmdbId, ct);
        if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < Fresh)
            return JsonSerializer.Deserialize<List<ExternalScore>>(cached.Json, Json) ?? [];

        List<ExternalScore>? fresh = null;
        try { fresh = await FetchMdbListAsync(type, tmdbId, imdbId, ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("MDBList lookup for {Type} {Id} failed: {Message}", type, tmdbId, ex.Message);
        }
        if (fresh is null) return cached is null ? [] : JsonSerializer.Deserialize<List<ExternalScore>>(cached.Json, Json) ?? [];   // stale beats nothing

        cached ??= db.ExternalScores.Add(new ExternalScoreCache { Type = type, TmdbId = tmdbId }).Entity;
        cached.Json = JsonSerializer.Serialize(fresh, Json);
        cached.FetchedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // two page loads raced; either copy is fine
        return fresh;
    }

    /// <summary>Null when MDBList couldn't answer (try again later); empty when it has nothing.</summary>
    private async Task<List<ExternalScore>?> FetchMdbListAsync(MediaType type, int tmdbId, string? imdbId, CancellationToken ct)
    {
        var kind = type == MediaType.Movie ? "movie" : "show";
        using var resp = await http.CreateClient("mdblist").GetAsync(
            $"https://api.mdblist.com/tmdb/{kind}/{tmdbId}?apikey={Uri.EscapeDataString(options.Value.ApiKey!)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("MDBList answered {Status} for {Type} {Id}", (int)resp.StatusCode, type, tmdbId);
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        return ParseMdbList(doc.RootElement, type, tmdbId, imdbId);
    }

    public static List<ExternalScore> ParseMdbList(JsonElement root, MediaType type, int tmdbId, string? imdbId)
    {
        var list = new List<ExternalScore>();
        if (!root.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Array) return list;
        var byName = new Dictionary<string, JsonElement>();
        foreach (var r in ratings.EnumerateArray())
            if (r.TryGetProperty("source", out var src) && src.GetString() is { } name) byName[name] = r;

        var kind = type == MediaType.Movie ? "movie" : "show";
        foreach (var (source, label, max, percent) in Sources)
        {
            if (!byName.TryGetValue(source, out var r)) continue;
            if (!r.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) continue;
            var value = v.GetDouble();
            if (value <= 0) continue;
            int? votes = r.TryGetProperty("votes", out var vo) && vo.ValueKind == JsonValueKind.Number && vo.TryGetInt32(out var n) && n > 0 ? n : null;
            var url = r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            url = url switch
            {
                { } x when x.StartsWith("http", StringComparison.OrdinalIgnoreCase) => x,
                { } x when source is "tomatoes" or "popcorn" && x.StartsWith('/') => "https://www.rottentomatoes.com" + x,
                { } x when source is "metacritic" or "metacriticuser" && x.StartsWith('/') => "https://www.metacritic.com" + x,
                _ => source switch
                {
                    "letterboxd" when type == MediaType.Movie => $"https://letterboxd.com/tmdb/{tmdbId}/",
                    "trakt" => $"https://trakt.tv/search/tmdb/{tmdbId}?id_type={kind}",
                    _ => null,
                },
            };
            list.Add(new ExternalScore(source, label, value, max, percent, votes, url));
        }
        return list;
    }
}

/// <summary>Loads IMDb's public ratings file (title.ratings.tsv.gz, ~1.5M titles) once a day.</summary>
public class ImdbRatingsWorker(IServiceScopeFactory scopes, IHttpClientFactory http, ILogger<ImdbRatingsWorker> log) : BackgroundService
{
    private const string StateKey = "imdb-ratings-loaded";
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var state = await db.ServerState.FirstOrDefaultAsync(s => s.Key == StateKey, stoppingToken);
                var last = state is not null && DateTimeOffset.TryParse(state.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTimeOffset.MinValue;
                if (DateTimeOffset.UtcNow - last >= Every)
                {
                    var n = await LoadAsync(db, stoppingToken);
                    state ??= db.ServerState.Add(new ServerState { Key = StateKey }).Entity;
                    state.Value = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    await db.SaveChangesAsync(stoppingToken);
                    log.LogInformation("Loaded {Count:N0} IMDb ratings", n);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { log.LogWarning(ex, "Loading IMDb ratings failed; trying again in an hour"); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task<int> LoadAsync(AppDbContext db, CancellationToken ct)
    {
        using var resp = await http.CreateClient("imdb").GetAsync("https://datasets.imdbws.com/title.ratings.tsv.gz", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var gz = new GZipStream(await resp.Content.ReadAsStreamAsync(ct), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""CREATE TEMP TABLE imdb_load (id varchar(16), rating double precision, votes integer) ON COMMIT DROP""", conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        var count = 0;
        await using (var copy = await conn.BeginBinaryImportAsync("COPY imdb_load (id, rating, votes) FROM STDIN (FORMAT BINARY)", ct))
        {
            await reader.ReadLineAsync(ct);   // header: tconst averageRating numVotes
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length < 3 || parts[0].Length > 16
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
                    || !int.TryParse(parts[2], out var votes)) continue;
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(parts[0], NpgsqlTypes.NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(rating, NpgsqlTypes.NpgsqlDbType.Double, ct);
                await copy.WriteAsync(votes, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                count++;
            }
            await copy.CompleteAsync(ct);
        }
        if (count < 100_000) throw new InvalidOperationException($"IMDb's ratings file only had {count} rows; keeping the old ones.");
        await using (var cmd = new NpgsqlCommand("""
            TRUNCATE "ImdbRatings";
            INSERT INTO "ImdbRatings" ("ImdbId", "Rating", "Votes") SELECT DISTINCT ON (id) id, rating, votes FROM imdb_load;
            """, conn, tx) { CommandTimeout = 300 })
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return count;
    }
}
