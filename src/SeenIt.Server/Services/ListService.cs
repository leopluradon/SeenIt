using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

public class ListService(AppDbContext db, TrackingService tracking, SmartListService smart)
{
    public async Task<List<ListSummary>> GetMineAsync(Guid userId, CancellationToken ct)
    {
        var lists = await db.Lists.Where(l => l.UserId == userId).Include(l => l.Items)
            .OrderByDescending(l => l.UpdatedAt).ToListAsync(ct);
        var (movies, shows) = await tracking.LoadMetaAsync(lists.SelectMany(l => l.Items.OrderBy(i => i.Position).Take(4)).Select(i => (i.Type, i.TmdbId)), ct);
        var result = new List<ListSummary>();
        foreach (var l in lists)
        {
            if (SmartListService.Parse(l.RulesJson) is { } rules)
            {
                var items = await smart.EvaluateAsync(userId, rules, ct);
                result.Add(new ListSummary(l.Id, l.Name, l.Description, l.IsPublic, items.Count, l.UpdatedAt, items.Take(4).Select(m => m.PosterPath).ToList(), Smart: true));
                continue;
            }
            result.Add(new ListSummary(l.Id, l.Name, l.Description, l.IsPublic, l.Items.Count, l.UpdatedAt,
                l.Items.OrderBy(i => i.Position).Take(4)
                    .Select(i => i.Type == MediaType.Movie ? movies.GetValueOrDefault(i.TmdbId)?.PosterPath : shows.GetValueOrDefault(i.TmdbId)?.PosterPath)
                    .ToList()));
        }
        return result;
    }

    /// <summary>Save or unsave someone's public list. False if it isn't a public list you can save.</summary>
    public async Task<bool> SaveAsync(Guid userId, Guid listId, bool save, CancellationToken ct)
    {
        var list = await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null || list.UserId == userId || (!list.IsPublic && save)) return false;
        var row = await db.ListLikes.FirstOrDefaultAsync(l => l.UserId == userId && l.ListId == listId, ct);
        if (save && row is null) db.ListLikes.Add(new ListLike { UserId = userId, ListId = listId });
        if (!save && row is not null) db.ListLikes.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Lists you saved that are still public, newest save first.</summary>
    public async Task<List<ListSummary>> SavedAsync(Guid userId, CancellationToken ct)
    {
        var ids = await db.ListLikes.Where(l => l.UserId == userId).OrderByDescending(l => l.CreatedAt).Select(l => l.ListId).ToListAsync(ct);
        var lists = await SummariesAsync(db.Lists.Where(l => ids.Contains(l.Id) && l.IsPublic), userId, ct);
        return lists.OrderBy(l => ids.IndexOf(l.Id)).ToList();
    }

    /// <summary>Everyone's public lists with something in them: most saved first ("popular") or most recently changed.</summary>
    public async Task<List<ListSummary>> BrowseAsync(Guid? viewerId, string? query, bool recent, CancellationToken ct)
    {
        var q = db.Lists.Where(l => l.IsPublic && l.Items.Count > 0);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = db.Database.IsRelational()
                ? q.Where(l => EF.Functions.ILike(l.Name, pattern) || EF.Functions.ILike(l.Description ?? "", pattern))
                : q.Where(l => l.Name.ToLower().Contains(query.Trim().ToLower()));
        }
        q = recent
            ? q.OrderByDescending(l => l.UpdatedAt)
            : q.OrderByDescending(l => db.ListLikes.Count(x => x.ListId == l.Id)).ThenByDescending(l => l.UpdatedAt);
        return await SummariesAsync(q.Take(60), viewerId, ct, keepOrder: true);
    }

    private async Task<List<ListSummary>> SummariesAsync(IQueryable<UserList> query, Guid? viewerId, CancellationToken ct, bool keepOrder = false)
    {
        var lists = await query.Include(l => l.Items).Include(l => l.User).AsNoTracking().ToListAsync(ct);
        var ids = lists.Select(l => l.Id).ToList();
        var saves = await db.ListLikes.Where(x => ids.Contains(x.ListId)).GroupBy(x => x.ListId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var (movies, shows) = await tracking.LoadMetaAsync(lists.SelectMany(l => l.Items.OrderBy(i => i.Position).Take(4)).Select(i => (i.Type, i.TmdbId)), ct);
        return lists.Select(l => new ListSummary(l.Id, l.Name, l.Description, l.IsPublic, l.Items.Count, l.UpdatedAt,
            l.Items.OrderBy(i => i.Position).Take(4)
                .Select(i => i.Type == MediaType.Movie ? movies.GetValueOrDefault(i.TmdbId)?.PosterPath : shows.GetValueOrDefault(i.TmdbId)?.PosterPath).ToList(),
            l.UserId == viewerId ? null : l.User?.UserName, saves.GetValueOrDefault(l.Id))).ToList();
    }

    /// <summary>
    /// Titles on your watchlist or any of your lists whose name contains the query (ignoring case,
    /// accents, punctuation and a leading "The"), with where each one is.
    /// </summary>
    public async Task<List<ListSearchHit>> SearchAsync(Guid userId, string? query, CancellationToken ct)
    {
        var q = Import.ImportRunner.Normalize(query);
        if (q.Length == 0) return [];

        var watchlist = await db.Watchlist.AsNoTracking().Where(w => w.UserId == userId).Select(w => new { w.Type, w.TmdbId }).ToListAsync(ct);
        var inLists = await db.ListItems.AsNoTracking()
            .Join(db.Lists.Where(l => l.UserId == userId), i => i.ListId, l => l.Id, (i, l) => new { i.Type, i.TmdbId, l.Id, l.Name, l.UpdatedAt })
            .ToListAsync(ct);
        var keys = watchlist.Select(w => (w.Type, w.TmdbId)).Concat(inLists.Select(i => (i.Type, i.TmdbId))).Distinct().ToList();
        if (keys.Count == 0) return [];

        var (movies, shows) = await tracking.LoadMetaAsync(keys, ct);
        var onWatchlist = watchlist.Select(w => (w.Type, w.TmdbId)).ToHashSet();
        var listsByTitle = inLists.ToLookup(i => (i.Type, i.TmdbId));
        var hits = new List<(ListSearchHit Hit, bool Prefix)>();
        foreach (var key in keys)
        {
            MediaSummary? media = key.Type == MediaType.Movie
                ? movies.TryGetValue(key.TmdbId, out var m) ? CatalogService.ToSummary(m) : null
                : shows.TryGetValue(key.TmdbId, out var sh) ? CatalogService.ToSummary(sh) : null;
            if (media is null) continue;
            var title = Import.ImportRunner.Normalize(media.Title);
            if (!title.Contains(q, StringComparison.Ordinal)) continue;
            var lists = listsByTitle[key].OrderByDescending(l => l.UpdatedAt).Select(l => new ListRef(l.Id, l.Name)).ToList();
            hits.Add((new ListSearchHit(media, onWatchlist.Contains(key), lists), title.StartsWith(q, StringComparison.Ordinal)));
        }
        // Titles that start with the query first, then alphabetical.
        return hits.OrderByDescending(h => h.Prefix).ThenBy(h => h.Hit.Media.Title, StringComparer.OrdinalIgnoreCase)
            .Take(100).Select(h => h.Hit).ToList();
    }

    /// <summary>Owner sees any of their lists; anyone (signed in or not) can see a public list.</summary>
    public async Task<ListDetail?> GetAsync(Guid listId, Guid? viewerId, CancellationToken ct)
    {
        var list = await db.Lists.Include(l => l.Items).Include(l => l.User).FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is null || (!list.IsPublic && list.UserId != viewerId)) return null;
        var saves = await db.ListLikes.CountAsync(l => l.ListId == list.Id, ct);
        var saved = viewerId is { } v && await db.ListLikes.AnyAsync(l => l.ListId == list.Id && l.UserId == v, ct);
        if (SmartListService.Parse(list.RulesJson) is { } rules)
        {
            // Worked out from the owner's library (or TMDB) each time; the ids just need to be stable.
            var found = await smart.EvaluateAsync(list.UserId, rules, ct);
            var entries = found.Select((m, i) => new ListItemDto(SmartItemId(list.Id, m), m, list.UpdatedAt, i + 1)).ToList();
            return new ListDetail(list.Id, list.Name, list.Description, list.IsPublic, list.User?.UserName ?? "", list.UserId == viewerId, entries, false, saves, saved, rules);
        }
        var (movies, shows) = await tracking.LoadMetaAsync(list.Items.Select(i => (i.Type, i.TmdbId)), ct);
        var items = list.Items.OrderBy(i => i.Position).Select(i =>
        {
            MediaSummary? media = i.Type == MediaType.Movie
                ? movies.TryGetValue(i.TmdbId, out var m) ? CatalogService.ToSummary(m) : null
                : shows.TryGetValue(i.TmdbId, out var s) ? CatalogService.ToSummary(s) : null;
            return media is null ? null : new ListItemDto(i.Id, media, i.AddedAt, i.Position, i.Note);
        }).OfType<ListItemDto>().ToList();
        return new ListDetail(list.Id, list.Name, list.Description, list.IsPublic, list.User?.UserName ?? "", list.UserId == viewerId, items, list.Ranked, saves, saved);
    }

    public async Task<UserList> CreateAsync(Guid userId, CreateListRequest req, CancellationToken ct)
    {
        var list = new UserList
        {
            UserId = userId, Name = Validate(req.Name), Description = Trim(req.Description, 2000), IsPublic = req.IsPublic,
            Ranked = req.Rules is null && req.Ranked, RulesJson = req.Rules is { } r ? SmartListService.Serialize(r) : null,
        };
        db.Lists.Add(list);
        await db.SaveChangesAsync(ct);
        return list;
    }

    public async Task<bool> UpdateAsync(Guid userId, Guid listId, CreateListRequest req, CancellationToken ct)
    {
        var list = await db.Lists.FirstOrDefaultAsync(l => l.Id == listId && l.UserId == userId, ct);
        if (list is null) return false;
        list.Name = Validate(req.Name);
        list.Description = Trim(req.Description, 2000);
        list.IsPublic = req.IsPublic;
        // A smart list stays smart (its rules change); an ordinary list stays ordinary.
        if (list.RulesJson is not null && req.Rules is { } r) list.RulesJson = SmartListService.Serialize(r);
        list.Ranked = list.RulesJson is null && req.Ranked;
        list.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static Guid SmartItemId(Guid listId, MediaSummary m)
    {
        var bytes = listId.ToByteArray();
        var key = BitConverter.GetBytes(((long)m.Type << 32) | (uint)m.TmdbId);
        for (var i = 0; i < key.Length; i++) bytes[8 + i] ^= key[i];
        return new Guid(bytes);
    }

    /// <summary>Hides the list for a day so it can be restored (Undo); a worker then removes it for good.</summary>
    public async Task<bool> DeleteAsync(Guid userId, Guid listId, CancellationToken ct)
    {
        var list = await db.Lists.FirstOrDefaultAsync(l => l.Id == listId && l.UserId == userId, ct);
        if (list is null) return false;
        list.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RestoreAsync(Guid userId, Guid listId, CancellationToken ct)
    {
        var list = await db.Lists.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.Id == listId && l.UserId == userId && l.DeletedAt != null, ct);
        if (list is null) return false;
        list.DeletedAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static Task<int> PurgeDeletedAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        return db.Lists.IgnoreQueryFilters().Where(l => l.DeletedAt != null && l.DeletedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task<bool> AddItemAsync(Guid userId, Guid listId, MediaType type, int tmdbId, CancellationToken ct, DateTimeOffset? addedAt = null)
    {
        if (type is not (MediaType.Movie or MediaType.Show)) throw new TrackingException("Lists hold movies and shows.");
        var list = await db.Lists.Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == listId && l.UserId == userId, ct);
        if (list is null) return false;
        if (list.RulesJson is not null) throw new TrackingException("A smart list fills itself in from its rules; edit the rules instead.");
        if (list.Items.Any(i => i.Type == type && i.TmdbId == tmdbId)) return true;
        await tracking.EnsureMetaAsync(type, tmdbId, ct);
        db.ListItems.Add(new UserListItem
        {
            ListId = list.Id, Type = type, TmdbId = tmdbId, AddedAt = addedAt ?? DateTimeOffset.UtcNow,
            Position = list.Items.Count == 0 ? 0 : list.Items.Max(i => i.Position) + 1,
        });
        list.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveItemAsync(Guid userId, Guid listId, MediaType type, int tmdbId, CancellationToken ct)
    {
        var list = await db.Lists.Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == listId && l.UserId == userId, ct);
        var item = list?.Items.FirstOrDefault(i => i.Type == type && i.TmdbId == tmdbId);
        if (list is null || item is null) return false;
        db.ListItems.Remove(item);
        list.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Puts the list in the given order. Items missing from it keep their relative order at the end.</summary>
    public async Task<bool> ReorderAsync(Guid userId, Guid listId, IReadOnlyList<Guid> itemIds, CancellationToken ct)
    {
        var list = await db.Lists.Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == listId && l.UserId == userId, ct);
        if (list is null) return false;
        if (list.RulesJson is not null) throw new TrackingException("A smart list fills itself in from its rules; edit the rules instead.");
        var rank = itemIds.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
        var ordered = list.Items.OrderBy(i => rank.TryGetValue(i.Id, out var r) ? r : int.MaxValue).ThenBy(i => i.Position).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Position = i;
        list.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetNoteAsync(Guid userId, Guid listId, Guid itemId, string? note, CancellationToken ct)
    {
        var item = await db.ListItems.FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId
            && db.Lists.Any(l => l.Id == listId && l.UserId == userId), ct);
        if (item is null) return false;
        item.Note = Trim(note, 500);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Validate(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new TrackingException("A list needs a name.");
        return name.Length > 100 ? name[..100] : name;
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
}
