using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>Following, profiles, the friends feed and taste comparisons.</summary>
public class SocialService(AppDbContext db, TrackingService tracking, ListService lists, CheckInService checkIns, UserDataCache cache)
{
    public Task<AppUser?> FindAsync(string userName) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == userName.Trim().ToUpperInvariant());

    /// <summary>Public: anyone. Friends: people who follow each other. Private: only you.</summary>
    public static bool CanView(AppUser owner, Guid? viewer, bool mutual) =>
        owner.Id == viewer || owner.Visibility == ProfileVisibility.Public || (owner.Visibility == ProfileVisibility.Friends && mutual);

    private async Task<(bool Following, bool FollowsYou)> RelationAsync(Guid? viewer, Guid them, CancellationToken ct)
    {
        if (viewer is not { } me || me == them) return (false, false);
        var rows = await db.Follows.Where(f => (f.FollowerId == me && f.FolloweeId == them) || (f.FollowerId == them && f.FolloweeId == me))
            .Select(f => f.FollowerId).ToListAsync(ct);
        return (rows.Contains(me), rows.Contains(them));
    }

    private async Task<List<UserCard>> CardsAsync(Guid me, IEnumerable<AppUser> users, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToList();
        var following = (await db.Follows.Where(f => f.FollowerId == me && ids.Contains(f.FolloweeId)).Select(f => f.FolloweeId).ToListAsync(ct)).ToHashSet();
        var followers = (await db.Follows.Where(f => f.FolloweeId == me && ids.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        return users.Select(u => new UserCard(u.UserName!, u.DisplayName, following.Contains(u.Id), followers.Contains(u.Id))).ToList();
    }

    public async Task<ProfileStats> StatsAsync(Guid userId, CancellationToken ct)
    {
        // Totals only (time zone doesn't matter), shared with the Stats page's cached copy.
        var s = await cache.GetAsync(userId, "stats:totals", () => tracking.GetStatsAsync(userId, null, ct), ct);
        return new ProfileStats(s.MoviesWatched, s.ShowsWatched, s.EpisodesWatched, s.MinutesMovies + s.MinutesEpisodes, s.Ratings);
    }

    public async Task<ProfileDto?> GetProfileAsync(string userName, Guid? viewer, CancellationToken ct)
    {
        var owner = await FindAsync(userName);
        if (owner is null) return null;
        var (following, followsYou) = await RelationAsync(viewer, owner.Id, ct);
        var isMe = owner.Id == viewer;
        var canView = CanView(owner, viewer, following && followsYou);
        var card = new UserCard(owner.UserName!, owner.DisplayName, following, followsYou);
        var followers = await db.Follows.CountAsync(f => f.FolloweeId == owner.Id, ct);
        var followingCount = await db.Follows.CountAsync(f => f.FollowerId == owner.Id, ct);

        // Public lists are shareable by link anyway, so they show even on a private profile.
        var publicLists = (await lists.GetMineAsync(owner.Id, ct)).Where(l => isMe || l.IsPublic).ToList();
        if (!canView)
            return new ProfileDto(card, owner.CreatedAt, isMe, owner.Visibility, false, followers, followingCount, null, null, [], [], publicLists);

        var recentRows = await db.WatchEvents.AsNoTracking().Where(w => w.UserId == owner.Id && w.WatchedAt > WatchDates.Unknown)
            .OrderByDescending(w => w.WatchedAt).Take(24).ToListAsync(ct);
        var topRated = await tracking.TopRatedAsync(owner.Id, 18, ct);

        return new ProfileDto(card, owner.CreatedAt, isMe, owner.Visibility, true, followers, followingCount,
            await checkIns.GetAsync(owner.Id, ct), await StatsAsync(owner.Id, ct),
            await tracking.ToHistoryAsync(recentRows, ct), topRated, publicLists,
            await tracking.GetFavoritesAsync(owner.Id, ct));
    }

    /// <summary>The account behind a profile, if the viewer may see its history (same rule as the profile).</summary>
    public async Task<AppUser?> ViewableAsync(string userName, Guid? viewer, CancellationToken ct)
    {
        var owner = await FindAsync(userName);
        if (owner is null) return null;
        var (following, followsYou) = await RelationAsync(viewer, owner.Id, ct);
        return CanView(owner, viewer, following && followsYou) ? owner : null;
    }

    public async Task<bool> FollowAsync(Guid me, string userName, bool follow, CancellationToken ct)
    {
        var them = await FindAsync(userName);
        if (them is null) return false;
        if (them.Id == me) throw new TrackingException("You can't follow yourself.");
        var existing = await db.Follows.FirstOrDefaultAsync(f => f.FollowerId == me && f.FolloweeId == them.Id, ct);
        if (follow && existing is null) db.Follows.Add(new Follow { FollowerId = me, FolloweeId = them.Id });
        if (!follow && existing is not null) db.Follows.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<UserCard>> SearchAsync(Guid me, string q, CancellationToken ct)
    {
        q = q.Trim();
        if (q.Length < 2) return [];
        var pattern = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var users = await db.Users.AsNoTracking()
            .Where(u => u.Id != me && (EF.Functions.ILike(u.UserName!, pattern) || EF.Functions.ILike(u.DisplayName ?? "", pattern)))
            .OrderBy(u => u.UserName!.Length).Take(20).ToListAsync(ct);
        return await CardsAsync(me, users, ct);
    }

    public async Task<(List<UserCard> Following, List<UserCard> Followers)> ConnectionsAsync(Guid me, CancellationToken ct)
    {
        var following = await db.Follows.Where(f => f.FollowerId == me).OrderByDescending(f => f.CreatedAt)
            .Join(db.Users, f => f.FolloweeId, u => u.Id, (f, u) => u).AsNoTracking().ToListAsync(ct);
        var followers = await db.Follows.Where(f => f.FolloweeId == me).OrderByDescending(f => f.CreatedAt)
            .Join(db.Users, f => f.FollowerId, u => u.Id, (f, u) => u).AsNoTracking().ToListAsync(ct);
        return (await CardsAsync(me, following, ct), await CardsAsync(me, followers, ct));
    }

    /// <summary>People you follow whose profiles you're allowed to see.</summary>
    private async Task<List<AppUser>> VisibleFolloweesAsync(Guid me, CancellationToken ct)
    {
        var followees = await db.Follows.Where(f => f.FollowerId == me)
            .Join(db.Users, f => f.FolloweeId, u => u.Id, (f, u) => u).AsNoTracking().ToListAsync(ct);
        var ids = followees.Select(u => u.Id).ToList();
        var followBack = (await db.Follows.Where(f => f.FolloweeId == me && ids.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        return followees.Where(u => CanView(u, me, followBack.Contains(u.Id))).ToList();
    }

    public async Task<FeedDto> FeedAsync(Guid me, int take, CancellationToken ct)
    {
        var people = await VisibleFolloweesAsync(me, ct);
        if (people.Count == 0) return new FeedDto([], []);
        var ids = people.Select(u => u.Id).ToList();
        var cards = (await CardsAsync(me, people, ct)).ToDictionary(c => c.UserName);
        var byId = people.ToDictionary(u => u.Id, u => cards[u.UserName!]);

        var live = (await db.CheckIns.AsNoTracking().Where(c => ids.Contains(c.UserId)).ToListAsync(ct)).Where(CheckInService.IsLive).ToList();
        var liveDtos = await checkIns.ToDtosAsync(live, ct);
        var watching = live.Zip(liveDtos, (c, d) => new FriendWatching(byId[c.UserId], d)).ToList();

        var rows = await db.WatchEvents.AsNoTracking()
            .Where(w => ids.Contains(w.UserId) && w.WatchedAt > WatchDates.Unknown && w.WatchedAt <= DateTimeOffset.UtcNow.AddDays(1))
            .OrderByDescending(w => w.WatchedAt).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        var entries = await tracking.ToHistoryAsync(rows, ct);
        return new FeedDto(watching, rows.Zip(entries, (w, e) => new ActivityItem(byId[w.UserId], e)).ToList());
    }

    /// <summary>Whether the viewer may see this person's profile (and so compare against it).</summary>
    private async Task<(bool Ok, UserCard Card)> VisibleAsync(AppUser user, Guid viewer, CancellationToken ct)
    {
        var (following, followsYou) = await RelationAsync(viewer, user.Id, ct);
        return (CanView(user, viewer, following && followsYou), new UserCard(user.UserName!, user.DisplayName, following, followsYou));
    }

    /// <summary>Compares two people. The viewer can be either of them or anyone who can see both profiles.</summary>
    public async Task<CompareDto?> CompareAsync(Guid viewer, string nameA, string nameB, CancellationToken ct)
    {
        var a = await FindAsync(nameA);
        var b = await FindAsync(nameB);
        if (a is null || b is null || a.Id == b.Id) return null;
        var (okA, cardA) = await VisibleAsync(a, viewer, ct);
        var (okB, cardB) = await VisibleAsync(b, viewer, ct);
        if (!okA || !okB) return null;

        async Task<(Dictionary<int, DateTimeOffset> Movies, Dictionary<int, DateTimeOffset> Shows)> Watched(Guid id)
        {
            var w = await db.WatchEvents.Where(x => x.UserId == id && x.Type != MediaType.Show)
                .GroupBy(x => new { x.Type, x.TmdbId }).Select(g => new { g.Key.Type, g.Key.TmdbId, Last = g.Max(x => x.WatchedAt) }).ToListAsync(ct);
            return (w.Where(x => x.Type == MediaType.Movie).ToDictionary(x => x.TmdbId, x => x.Last),
                    w.Where(x => x.Type == MediaType.Episode).ToDictionary(x => x.TmdbId, x => x.Last));
        }
        var aw = await Watched(a.Id);
        var bw = await Watched(b.Id);
        bool Seen((Dictionary<int, DateTimeOffset> Movies, Dictionary<int, DateTimeOffset> Shows) w, (MediaType Type, int Id) k) =>
            k.Type == MediaType.Movie ? w.Movies.ContainsKey(k.Id) : w.Shows.ContainsKey(k.Id);

        var watchedBoth = aw.Movies.Keys.Intersect(bw.Movies.Keys).Select(id => (Key: (MediaType.Movie, id), Last: aw.Movies[id]))
            .Concat(aw.Shows.Keys.Intersect(bw.Shows.Keys).Select(id => (Key: (MediaType.Show, id), Last: aw.Shows[id])))
            .OrderByDescending(x => x.Last).Select(x => x.Key).ToList();

        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => (r.UserId == a.Id || r.UserId == b.Id) && r.Type != MediaType.Episode && r.SeasonNumber == null).ToListAsync(ct);
        var ar = ratings.Where(r => r.UserId == a.Id).ToDictionary(r => (r.Type, r.TmdbId), r => r.Value);
        var br = ratings.Where(r => r.UserId == b.Id).ToDictionary(r => (r.Type, r.TmdbId), r => r.Value);
        var shared = ar.Keys.Intersect(br.Keys).ToList();
        int? compat = shared.Count >= 3
            ? (int)Math.Round(100 * (1 - shared.Average(k => Math.Abs(ar[k] - br[k])) / 9.0))
            : null;
        var bLoved = br.Where(r => r.Value >= 8 && !Seen(aw, r.Key)).OrderByDescending(r => r.Value).Select(r => r.Key).ToList();
        var aLoved = ar.Where(r => r.Value >= 8 && !Seen(bw, r.Key)).OrderByDescending(r => r.Value).Select(r => r.Key).ToList();

        var (movies, shows) = await tracking.LoadMetaAsync(watchedBoth.Concat(shared).Concat(bLoved).Concat(aLoved).Distinct(), ct);
        List<CompareItem> Items(IEnumerable<(MediaType Type, int Id)> keys) => keys
            .Select(k => (k.Type == MediaType.Movie
                    ? movies.TryGetValue(k.Id, out var m) ? CatalogService.ToSummary(m) : null
                    : shows.TryGetValue(k.Id, out var s) ? CatalogService.ToSummary(s) : null) is { } media
                ? new CompareItem(media, ar.TryGetValue(k, out var x) ? x : null, br.TryGetValue(k, out var y) ? y : null)
                : null)
            .OfType<CompareItem>().ToList();

        var ratedBoth = Items(shared).OrderBy(i => Math.Abs(i.A!.Value - i.B!.Value)).ThenByDescending(i => i.A + i.B).ToList();
        return new CompareDto(cardA, cardB, await StatsAsync(a.Id, ct), await StatsAsync(b.Id, ct), compat,
            aw.Movies.Keys.Intersect(bw.Movies.Keys).Count(), aw.Shows.Keys.Intersect(bw.Shows.Keys).Count(),
            ratedBoth, Items(watchedBoth), Items(bLoved), Items(aLoved));
    }
}
