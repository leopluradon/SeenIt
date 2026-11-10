namespace SeenIt.Server.Data;

/// <summary>
/// The one order for lists of plays (History, Recently watched, profiles, the feed), so they always agree.
/// Plays at the same time (marked in the same minute, or a whole season at once) fall back to the order they
/// were recorded in; bulk marks record episodes in season/episode order.
/// </summary>
public static class PlayOrder
{
    public static IOrderedQueryable<WatchEvent> NewestFirst(this IQueryable<WatchEvent> q) =>
        q.OrderByDescending(w => w.WatchedAt).ThenByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id);

    public static IOrderedQueryable<WatchEvent> OldestFirst(this IQueryable<WatchEvent> q) =>
        q.OrderBy(w => w.WatchedAt).ThenBy(w => w.CreatedAt).ThenBy(w => w.Id);
}
