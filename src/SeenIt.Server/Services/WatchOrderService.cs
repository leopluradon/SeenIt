using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>
/// Watching a show in another order than TMDB's seasons (Clone Wars chronologically, Firefly as
/// intended...), using TMDB's "episode groups". Plays are still stored by season and episode; the
/// order only changes what's next, progress, and the episode list and previous/next links.
/// </summary>
public class WatchOrderService(AppDbContext db, TmdbClient tmdb, CatalogService catalog, TrackingService tracking)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(7);

    public static string Kind(int type) => type switch
    {
        1 => "Original air date", 2 => "Absolute", 3 => "DVD", 4 => "Digital", 5 => "Story arc", 6 => "Production", 7 => "TV",
        _ => "Alternative",
    };

    public async Task<WatchOrdersDto> OptionsAsync(int showId, Guid? userId, CancellationToken ct)
    {
        var groups = (await tmdb.GetEpisodeGroupsAsync(showId, ct))?.Results ?? [];
        var options = groups.Where(g => g.EpisodeCount > 0)
            .Select(g => new WatchOrderOption(g.Id, g.Name, Kind(g.Type), string.IsNullOrWhiteSpace(g.Description) ? null : g.Description.Trim(), g.EpisodeCount, g.GroupCount))
            .ToList();
        var selected = userId is { } uid
            ? await db.ShowWatchOrders.AsNoTracking().Where(o => o.UserId == uid && o.ShowTmdbId == showId).Select(o => o.OrderId).FirstOrDefaultAsync(ct)
            : null;
        return new WatchOrdersDto(options, selected);
    }

    /// <summary>The order's parts, from the cache or TMDB. Null if TMDB doesn't have it (or it's for another show).</summary>
    public async Task<(EpisodeOrder Order, List<EpisodeOrderPart> Parts)?> LoadAsync(string orderId, int showId, CancellationToken ct)
    {
        var row = await db.EpisodeOrders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (row is not null && row.ShowTmdbId == showId && DateTimeOffset.UtcNow - row.UpdatedAt < Fresh)
            return (row, Parse(row.PartsJson));

        var group = await tmdb.GetEpisodeGroupAsync(orderId, ct);
        if (group?.Groups is null)
            return row is not null && row.ShowTmdbId == showId ? (row, Parse(row.PartsJson)) : null;   // stale beats nothing
        var parts = group.Groups.OrderBy(g => g.Order)
            .Select(g => new EpisodeOrderPart(g.Name, (g.Episodes ?? [])
                .Where(e => e.ShowId == showId || e.ShowId == 0)
                .OrderBy(e => e.Order)
                .Select(e => new EpisodeOrderSlot(e.SeasonNumber, e.EpisodeNumber)).ToList()))
            .Where(p => p.Episodes.Count > 0)
            .ToList();
        if (parts.Count == 0) return null;

        row ??= db.EpisodeOrders.Add(new EpisodeOrder { Id = orderId }).Entity;
        row.ShowTmdbId = showId;
        row.Name = group.Name.Length > 300 ? group.Name[..300] : group.Name;
        row.Description = string.IsNullOrWhiteSpace(group.Description) ? null : group.Description.Trim();
        row.Type = group.Type;
        row.PartsJson = JsonSerializer.Serialize(parts);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (row, parts);
    }

    private static List<EpisodeOrderPart> Parse(string json) => JsonSerializer.Deserialize<List<EpisodeOrderPart>>(json) ?? [];

    public async Task SetAsync(Guid userId, int showId, string? orderId, CancellationToken ct)
    {
        var current = await db.ShowWatchOrders.FirstOrDefaultAsync(o => o.UserId == userId && o.ShowTmdbId == showId, ct);
        if (string.IsNullOrEmpty(orderId))
        {
            if (current is not null) { db.ShowWatchOrders.Remove(current); await db.SaveChangesAsync(ct); }
            return;
        }
        if (await catalog.EnsureShowAsync(showId, ct) is null) throw new TrackingException("Show not found.");
        if (await LoadAsync(orderId, showId, ct) is null) throw new TrackingException("TMDB doesn't have that order for this show.");
        if (current is null) db.ShowWatchOrders.Add(new ShowWatchOrder { UserId = userId, ShowTmdbId = showId, OrderId = orderId });
        else { current.OrderId = orderId; current.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The orders a user follows for these shows, as flat episode sequences, straight from the
    /// cache (no TMDB calls; an order is cached when it's chosen). Used for progress and Up Next.
    /// </summary>
    public static async Task<Dictionary<int, (string Name, List<(int S, int E)> Sequence)>> SequencesAsync(
        AppDbContext db, Guid userId, IReadOnlyCollection<int>? showIds, CancellationToken ct)
    {
        var q = db.ShowWatchOrders.AsNoTracking().Where(o => o.UserId == userId);
        if (showIds is not null) q = q.Where(o => showIds.Contains(o.ShowTmdbId));
        var rows = await q.Join(db.EpisodeOrders, w => w.OrderId, o => o.Id, (w, o) => new { w.ShowTmdbId, o.Name, o.PartsJson })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.ShowTmdbId, r => (r.Name,
            Parse(r.PartsJson).SelectMany(p => p.Episodes).Select(e => (e.Season, e.Episode)).Distinct().ToList()));
    }

    /// <summary>The episodes the user has watched (since a rewatch started, during one).</summary>
    private async Task<HashSet<(int, int)>> WatchedAsync(Guid userId, int showId, CancellationToken ct)
    {
        var since = await db.Rewatches.AsNoTracking().Where(r => r.UserId == userId && r.ShowTmdbId == showId && r.CompletedAt == null)
            .Select(r => (DateTimeOffset?)r.StartedAt).FirstOrDefaultAsync(ct) ?? DateTimeOffset.MinValue;
        return (await db.WatchEvents.AsNoTracking()
                .Where(w => w.UserId == userId && w.Type == MediaType.Episode && w.TmdbId == showId && w.WatchedAt >= since)
                .Select(w => new { w.SeasonNumber, w.EpisodeNumber }).ToListAsync(ct))
            .Select(w => (w.SeasonNumber ?? -1, w.EpisodeNumber ?? -1)).ToHashSet();
    }

    /// <summary>The order you're following for this show, with what you've watched and what's next. Null if you follow TMDB's seasons.</summary>
    public async Task<WatchOrderDto?> DetailAsync(Guid userId, int showId, CancellationToken ct)
    {
        var orderId = await db.ShowWatchOrders.AsNoTracking().Where(o => o.UserId == userId && o.ShowTmdbId == showId)
            .Select(o => o.OrderId).FirstOrDefaultAsync(ct);
        if (orderId is null || await LoadAsync(orderId, showId, ct) is not { } loaded) return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var meta = (await db.Episodes.AsNoTracking().Where(e => e.ShowTmdbId == showId)
                .Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.Name, e.AirDate, e.Runtime }).ToListAsync(ct))
            .ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber));
        var watched = await WatchedAsync(userId, showId, ct);
        var sequence = loaded.Parts.SelectMany(p => p.Episodes).Select(e => (e.Season, e.Episode)).Distinct().ToList();
        var next = NextUp(sequence.Where(x => Aired(meta.GetValueOrDefault(x)?.AirDate, today)).ToList(), watched);

        var position = 0;
        var parts = loaded.Parts.Select(p => new WatchOrderPart(p.Name, p.Episodes.Select(e =>
        {
            var m = meta.GetValueOrDefault((e.Season, e.Episode));
            return new WatchOrderEpisode(++position, e.Season, e.Episode, m?.Name, m?.AirDate, m?.Runtime,
                watched.Contains((e.Season, e.Episode)), next == (e.Season, e.Episode));
        }).ToList())).ToList();
        var aired = sequence.Where(x => Aired(meta.GetValueOrDefault(x)?.AirDate, today)).ToList();
        return new WatchOrderDto(loaded.Order.Id, loaded.Order.Name, loaded.Order.Description,
            aired.Count, aired.Count(watched.Contains), parts);
    }

    private static bool Aired(DateOnly? d, DateOnly today) => d is { } x && x <= today;

    /// <summary>The first unwatched episode after the furthest one watched; if everything after it is done, the earliest gap.</summary>
    public static (int, int)? NextUp(List<(int S, int E)> airedInOrder, IReadOnlySet<(int, int)> watched)
    {
        var furthest = airedInOrder.FindLastIndex(x => watched.Contains(x));
        for (var i = furthest + 1; i < airedInOrder.Count; i++)
            if (!watched.Contains(airedInOrder[i])) return airedInOrder[i];
        foreach (var x in airedInOrder)
            if (!watched.Contains(x)) return x;
        return null;
    }

    /// <summary>Marks every aired, unwatched episode up to and including this one as watched (see WatchThroughRequest).</summary>
    public async Task<int> WatchThroughAsync(Guid userId, int showId, WatchThroughRequest req, CancellationToken ct)
    {
        if (await catalog.EnsureShowAsync(showId, ct) is null) throw new TrackingException("Show not found.");
        List<(int Season, int Episode)> sequence;
        if (req.InOrder)
        {
            var orderId = await db.ShowWatchOrders.AsNoTracking().Where(o => o.UserId == userId && o.ShowTmdbId == showId)
                .Select(o => o.OrderId).FirstOrDefaultAsync(ct) ?? throw new TrackingException("Pick a watch order first.");
            var loaded = await LoadAsync(orderId, showId, ct) ?? throw new TrackingException("That watch order isn't available any more.");
            sequence = loaded.Parts.SelectMany(p => p.Episodes).Select(e => (e.Season, e.Episode)).Distinct().ToList();
        }
        else
        {
            // TMDB's order: seasons then episodes. Specials only lead up to other specials.
            var specials = req.SeasonNumber == 0;
            sequence = (await db.Episodes.AsNoTracking()
                    .Where(e => e.ShowTmdbId == showId && (specials ? e.SeasonNumber == 0 : req.SeasonOnly ? e.SeasonNumber == req.SeasonNumber : e.SeasonNumber > 0))
                    .Select(e => new { e.SeasonNumber, e.EpisodeNumber }).ToListAsync(ct))
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).Select(e => (e.SeasonNumber, e.EpisodeNumber)).ToList();
        }
        var upTo = req.EpisodeNumber is { } ep
            ? sequence.IndexOf((req.SeasonNumber, ep))
            : sequence.FindLastIndex(x => x.Season == req.SeasonNumber);
        if (upTo < 0) throw new TrackingException(req.InOrder ? "That episode isn't in this order." : "That isn't an episode of this show.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var airDates = (await db.Episodes.AsNoTracking().Where(e => e.ShowTmdbId == showId)
                .Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.AirDate }).ToListAsync(ct))
            .ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber), e => e.AirDate);
        var watched = await WatchedAsync(userId, showId, ct);
        var todo = sequence.Take(upTo + 1).Where(x => !watched.Contains(x) && airDates.ContainsKey(x) && Aired(airDates[x], today)).ToList();

        // One play each; with a custom date they keep the order's sequence a minute apart.
        for (var i = 0; i < todo.Count; i++)
        {
            var (s, e) = todo[i];
            var at = req.When switch
            {
                WatchTime.Custom when req.WatchedAt is { } w => w.ToUniversalTime().AddMinutes(i - todo.Count + 1),
                WatchTime.Now => DateTimeOffset.UtcNow.AddMinutes(i - todo.Count + 1),
                _ => TrackingService.PlayTime(req.When, req.WatchedAt, airDates[(s, e)]),
            };
            db.WatchEvents.Add(new WatchEvent { UserId = userId, Type = MediaType.Episode, TmdbId = showId, SeasonNumber = s, EpisodeNumber = e, WatchedAt = at, Source = WatchSource.Manual });
        }
        if (todo.Count > 0)
        {
            await db.Watchlist.Where(w => w.UserId == userId && w.Type == MediaType.Show && w.TmdbId == showId).ExecuteDeleteAsync(ct);
            // Watching picks a paused show back up, as marking a single episode does.
            await db.ShowStates.Where(x => x.UserId == userId && x.ShowTmdbId == showId && x.Status == ShowTrackStatus.Paused).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await tracking.CompleteRewatchIfDoneAsync(userId, showId, ct);
        }
        return todo.Count;
    }
}
