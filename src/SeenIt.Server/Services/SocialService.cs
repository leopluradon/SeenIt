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

    /// <summary>One part of a profile (history, ratings…): its own audience if the owner set one, else the profile's.</summary>
    public static bool CanSee(AppUser owner, Guid? viewer, bool mutual, ProfilePart part)
    {
        if (owner.Id == viewer) return true;
        var who = owner.Prefs().For(part, owner.Visibility);
        return who == ProfileVisibility.Public || (who == ProfileVisibility.Friends && mutual);
    }

    public async Task<(bool Following, bool FollowsYou)> RelationAsync(Guid? viewer, Guid them, CancellationToken ct)
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
        var requested = (await db.FollowRequests.Where(r => r.RequesterId == me && ids.Contains(r.TargetId)).Select(r => r.TargetId).ToListAsync(ct)).ToHashSet();
        return users.Select(u => new UserCard(u.UserName!, u.DisplayName, following.Contains(u.Id), followers.Contains(u.Id), AvatarService.Path(u.UserName!, u.AvatarVersion),
            requested.Contains(u.Id))).ToList();
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
        var mutual = following && followsYou;
        bool See(ProfilePart part) => CanSee(owner, viewer, mutual, part);
        var canView = Enum.GetValues<ProfilePart>().Any(See);
        var requested = viewer is { } vid && !following && await db.FollowRequests.AnyAsync(r => r.RequesterId == vid && r.TargetId == owner.Id, ct);
        var card = new UserCard(owner.UserName!, owner.DisplayName, following, followsYou, AvatarService.Path(owner.UserName!, owner.AvatarVersion), requested);
        var followers = await db.Follows.CountAsync(f => f.FolloweeId == owner.Id, ct);
        var followingCount = await db.Follows.CountAsync(f => f.FollowerId == owner.Id, ct);

        // Public lists are shareable by link anyway, so they show even on a private profile.
        var publicLists = (await lists.GetMineAsync(owner.Id, ct)).Where(l => isMe || l.IsPublic).ToList();
        var layout = ProfileWidgetService.Visible(owner, viewer, mutual);
        if (!canView)
            return new ProfileDto(card, owner.CreatedAt, isMe, owner.Visibility, false, followers, followingCount, null, null, [], [], publicLists, Layout: layout);

        // Each part has its own audience (Settings → Privacy); what this viewer may not see is left out.
        var recent = See(ProfilePart.History)
            ? await tracking.ToHistoryAsync(await db.WatchEvents.AsNoTracking().Where(w => w.UserId == owner.Id && w.WatchedAt > WatchDates.Unknown)
                .NewestFirst().Take(24).ToListAsync(ct), ct)
            : [];
        var topRated = See(ProfilePart.Ratings) ? await tracking.TopRatedAsync(owner.Id, 18, ct) : [];

        return new ProfileDto(card, owner.CreatedAt, isMe, owner.Visibility, true, followers, followingCount,
            See(ProfilePart.WatchingNow) ? await checkIns.GetAsync(owner.Id, ct) : null,
            See(ProfilePart.Stats) ? await StatsAsync(owner.Id, ct) : null,
            recent, topRated, publicLists,
            See(ProfilePart.Favorites) ? await tracking.GetFavoritesAsync(owner.Id, ct) : null, layout);
    }

    /// <summary>
    /// Who someone follows and who follows them. Open unless the profile is private and you're not
    /// friends (following each other). Cards show your own relation to each person.
    /// </summary>
    public async Task<(List<UserCard> Following, List<UserCard> Followers)?> ConnectionsOfAsync(string userName, Guid? viewer, CancellationToken ct)
    {
        var owner = await FindAsync(userName);
        if (owner is null) return null;
        var (following, followsYou) = await RelationAsync(viewer, owner.Id, ct);
        if (owner.Id != viewer && owner.Visibility == ProfileVisibility.Private && !(following && followsYou)) return null;
        var theyFollow = await db.Follows.Where(f => f.FollowerId == owner.Id).OrderByDescending(f => f.CreatedAt)
            .Join(db.Users, f => f.FolloweeId, u => u.Id, (f, u) => u).AsNoTracking().ToListAsync(ct);
        var followThem = await db.Follows.Where(f => f.FolloweeId == owner.Id).OrderByDescending(f => f.CreatedAt)
            .Join(db.Users, f => f.FollowerId, u => u.Id, (f, u) => u).AsNoTracking().ToListAsync(ct);
        var me = viewer ?? Guid.Empty;   // signed out: nobody is "following" or "follows you"
        return (await CardsAsync(me, theyFollow, ct), await CardsAsync(me, followThem, ct));
    }

    /// <summary>The account behind a profile, if the viewer may see its history (same rule as the profile).</summary>
    public async Task<AppUser?> ViewableAsync(string userName, Guid? viewer, CancellationToken ct)
    {
        var owner = await FindAsync(userName);
        if (owner is null) return null;
        var (following, followsYou) = await RelationAsync(viewer, owner.Id, ct);
        return CanSee(owner, viewer, following && followsYou, ProfilePart.History) ? owner : null;
    }

    /// <summary>
    /// Follows or unfollows. Someone who approves followers gets a request instead (Requested = true);
    /// unfollowing also withdraws a pending request. Null when there's no such member.
    /// </summary>
    public async Task<FollowResult?> FollowAsync(Guid me, string userName, bool follow, CancellationToken ct)
    {
        var them = await FindAsync(userName);
        if (them is null) return null;
        if (them.Id == me) throw new TrackingException("You can't follow yourself.");
        var existing = await db.Follows.FirstOrDefaultAsync(f => f.FollowerId == me && f.FolloweeId == them.Id, ct);
        var request = await db.FollowRequests.FirstOrDefaultAsync(r => r.RequesterId == me && r.TargetId == them.Id, ct);
        var requested = false;
        if (follow && existing is null)
        {
            if (them.Prefs().ApproveFollowers)
            {
                if (request is null) db.FollowRequests.Add(new FollowRequest { RequesterId = me, TargetId = them.Id });
                requested = true;
            }
            else db.Follows.Add(new Follow { FollowerId = me, FolloweeId = them.Id });
        }
        if (!follow)
        {
            if (existing is not null) db.Follows.Remove(existing);
            if (request is not null) db.FollowRequests.Remove(request);
        }
        await db.SaveChangesAsync(ct);
        return new FollowResult(requested);
    }

    /// <summary>People waiting for you to approve their follow, newest first.</summary>
    public async Task<List<UserCard>> RequestsAsync(Guid me, CancellationToken ct)
    {
        var users = await db.FollowRequests.Where(r => r.TargetId == me).OrderByDescending(r => r.CreatedAt)
            .Join(db.Users, r => r.RequesterId, u => u.Id, (r, u) => u).AsNoTracking().ToListAsync(ct);
        return await CardsAsync(me, users, ct);
    }

    /// <summary>Approves (they now follow you) or declines a follow request. False when there's no such request.</summary>
    public async Task<bool> AnswerRequestAsync(Guid me, string userName, bool approve, CancellationToken ct)
    {
        var them = await FindAsync(userName);
        if (them is null) return false;
        var request = await db.FollowRequests.FirstOrDefaultAsync(r => r.RequesterId == them.Id && r.TargetId == me, ct);
        if (request is null) return false;
        db.FollowRequests.Remove(request);
        if (approve && !await db.Follows.AnyAsync(f => f.FollowerId == them.Id && f.FolloweeId == me, ct))
            db.Follows.Add(new Follow { FollowerId = them.Id, FolloweeId = me });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Turning approval off lets everyone who was waiting in.</summary>
    public async Task ApproveAllAsync(Guid me, CancellationToken ct)
    {
        var waiting = await db.FollowRequests.Where(r => r.TargetId == me).ToListAsync(ct);
        if (waiting.Count == 0) return;
        var already = (await db.Follows.Where(f => f.FolloweeId == me).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        db.Follows.AddRange(waiting.Where(r => !already.Contains(r.RequesterId)).Select(r => new Follow { FollowerId = r.RequesterId, FolloweeId = me }));
        db.FollowRequests.RemoveRange(waiting);
        await db.SaveChangesAsync(ct);
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

    /// <summary>
    /// Who you follow, or who follows you, has seen or rated this (a movie, show, season or episode),
    /// limited to people whose profiles you can see.
    /// </summary>
    public async Task<List<FriendSeen>> SeenByAsync(Guid me, MediaType type, int tmdbId, int? season, int? episode, CancellationToken ct)
    {
        var following = await db.Follows.Where(f => f.FollowerId == me).Select(f => f.FolloweeId).ToListAsync(ct);
        var followers = await db.Follows.Where(f => f.FolloweeId == me).Select(f => f.FollowerId).ToListAsync(ct);
        var ids = following.Union(followers).ToList();
        if (ids.Count == 0) return [];
        var everyone = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        bool Mutual(AppUser u) => following.Contains(u.Id) && followers.Contains(u.Id);
        // Plays need their history to be visible to you, ratings their ratings.
        var historyOk = everyone.Where(u => CanSee(u, me, Mutual(u), ProfilePart.History)).Select(u => u.Id).ToList();
        var ratingsOk = everyone.Where(u => CanSee(u, me, Mutual(u), ProfilePart.Ratings)).Select(u => u.Id).ToList();
        var people = everyone.Where(u => historyOk.Contains(u.Id) || ratingsOk.Contains(u.Id)).ToDictionary(u => u.Id);
        if (people.Count == 0) return [];
        var visible = historyOk;

        // Plays: movies and episodes count every play; shows and seasons count distinct episodes.
        var plays = db.WatchEvents.AsNoTracking().Where(w => visible.Contains(w.UserId) && w.TmdbId == tmdbId);
        plays = type switch
        {
            MediaType.Movie => plays.Where(w => w.Type == MediaType.Movie),
            MediaType.Episode => plays.Where(w => w.Type == MediaType.Episode && w.SeasonNumber == season && w.EpisodeNumber == episode),
            _ when season is { } sn => plays.Where(w => w.Type == MediaType.Episode && w.SeasonNumber == sn),
            _ => plays.Where(w => w.Type == MediaType.Episode),
        };
        var rows = await plays.Select(w => new { w.UserId, w.SeasonNumber, w.EpisodeNumber, w.WatchedAt }).ToListAsync(ct);
        var distinctEpisodes = type == MediaType.Show;
        var watched = rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => (
            Count: distinctEpisodes ? g.Select(r => (r.SeasonNumber, r.EpisodeNumber)).Distinct().Count() : g.Count(),
            Last: g.Where(r => !WatchDates.IsUnknown(r.WatchedAt)).Select(r => (DateTimeOffset?)r.WatchedAt).Max()));

        var ratingType = type == MediaType.Episode ? MediaType.Episode : type;
        int? rSeason = type == MediaType.Movie ? null : season, rEpisode = type == MediaType.Episode ? episode : null;
        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => ratingsOk.Contains(r.UserId) && r.Type == ratingType && r.TmdbId == tmdbId && r.SeasonNumber == rSeason && r.EpisodeNumber == rEpisode)
            .ToDictionaryAsync(r => r.UserId, r => r.Value, ct);

        var cards = (await CardsAsync(me, people.Values.Where(u => watched.ContainsKey(u.Id) || ratings.ContainsKey(u.Id)), ct))
            .ToDictionary(c => c.UserName);
        return people.Values.Where(u => cards.ContainsKey(u.UserName!))
            .Select(u => new FriendSeen(cards[u.UserName!], watched.TryGetValue(u.Id, out var w) ? w.Count : 0, w.Last, ratings.TryGetValue(u.Id, out var r) ? r : null))
            .OrderByDescending(f => f.LastWatched ?? DateTimeOffset.MinValue).ThenBy(f => f.User.Name)
            .ToList();
    }

    /// <summary>People you follow whose profiles you're allowed to see.</summary>
    private async Task<List<AppUser>> VisibleFolloweesAsync(Guid me, CancellationToken ct)
    {
        var followees = await db.Follows.Where(f => f.FollowerId == me)
            .Join(db.Users, f => f.FolloweeId, u => u.Id, (f, u) => u).AsNoTracking().ToListAsync(ct);
        var ids = followees.Select(u => u.Id).ToList();
        var followBack = (await db.Follows.Where(f => f.FolloweeId == me && ids.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        return followees.Where(u => CanSee(u, me, followBack.Contains(u.Id), ProfilePart.History) || CanSee(u, me, followBack.Contains(u.Id), ProfilePart.WatchingNow)).ToList();
    }

    public async Task<FeedDto> FeedAsync(Guid me, int take, CancellationToken ct)
    {
        var people = await VisibleFolloweesAsync(me, ct);
        if (people.Count == 0) return new FeedDto([], []);
        var ids = people.Select(u => u.Id).ToList();
        var cards = (await CardsAsync(me, people, ct)).ToDictionary(c => c.UserName);
        var byId = people.ToDictionary(u => u.Id, u => cards[u.UserName!]);

        var mutualIds = (await db.Follows.Where(f => f.FolloweeId == me && ids.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        var watchingIds = people.Where(u => CanSee(u, me, mutualIds.Contains(u.Id), ProfilePart.WatchingNow)).Select(u => u.Id).ToList();
        var historyIds = people.Where(u => CanSee(u, me, mutualIds.Contains(u.Id), ProfilePart.History)).Select(u => u.Id).ToList();
        var live = (await db.CheckIns.AsNoTracking().Where(c => watchingIds.Contains(c.UserId)).ToListAsync(ct)).Where(CheckInService.IsLive).ToList();
        var liveDtos = await checkIns.ToDtosAsync(live, ct);
        var watching = live.Zip(liveDtos, (c, d) => new FriendWatching(byId[c.UserId], d)).ToList();

        var rows = await db.WatchEvents.AsNoTracking()
            .Where(w => historyIds.Contains(w.UserId) && w.WatchedAt > WatchDates.Unknown && w.WatchedAt <= DateTimeOffset.UtcNow.AddDays(1))
            .NewestFirst().Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        var entries = await tracking.ToHistoryAsync(rows, ct);
        return new FeedDto(watching, rows.Zip(entries, (w, e) => new ActivityItem(byId[w.UserId], e)).ToList());
    }

    /// <summary>The people you follow whose history you may see.</summary>
    private async Task<List<AppUser>> HistoryFolloweesAsync(Guid me, CancellationToken ct)
    {
        var people = await VisibleFolloweesAsync(me, ct);
        var ids = people.Select(u => u.Id).ToList();
        var mutual = (await db.Follows.Where(f => f.FolloweeId == me && ids.Contains(f.FollowerId)).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        return people.Where(u => CanSee(u, me, mutual.Contains(u.Id), ProfilePart.History)).OrderBy(u => u.DisplayName ?? u.UserName).ToList();
    }

    /// <summary>
    /// Friends' history: every play of everyone you follow whose history you can see, with the same
    /// filters as your own history; "user" narrows it to one of them.
    /// </summary>
    public async Task<HistoryPage> FriendsHistoryAsync(Guid me, HistoryQuery f, string? user, CancellationToken ct)
    {
        var people = await HistoryFolloweesAsync(me, ct);
        var cards = await CardsAsync(me, people, ct);
        var names = people.ToDictionary(u => u.Id, u => u.UserName!);
        var ids = people.Where(u => string.IsNullOrEmpty(user) || string.Equals(u.UserName, user, StringComparison.OrdinalIgnoreCase)).Select(u => u.Id).ToList();
        if (ids.Count == 0) return new HistoryPage([], 1, 0, 0, 0, [], cards);
        return await tracking.GetHistoryAsync(ids, f, names, ct) with { People = cards };
    }

    public async Task<List<string>> FriendsHistoryGenresAsync(Guid me, CancellationToken ct)
    {
        var ids = (await HistoryFolloweesAsync(me, ct)).Select(u => u.Id).ToList();
        return ids.Count == 0 ? [] : await tracking.GetHistoryGenresAsync(ids, ct);
    }

    /// <summary>Whether the viewer may see this person's profile (and so compare against it).</summary>
    private async Task<(bool Ok, UserCard Card)> VisibleAsync(AppUser user, Guid viewer, CancellationToken ct)
    {
        var (following, followsYou) = await RelationAsync(viewer, user.Id, ct);
        return (CanSee(user, viewer, following && followsYou, ProfilePart.History), new UserCard(user.UserName!, user.DisplayName, following, followsYou, AvatarService.Path(user.UserName!, user.AvatarVersion)));
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

        // Someone whose ratings aren't visible to the viewer compares without them.
        async Task<bool> RatingsVisible(AppUser u)
        {
            var (f, fy) = await RelationAsync(viewer, u.Id, ct);
            return CanSee(u, viewer, f && fy, ProfilePart.Ratings);
        }
        var rateIds = new List<Guid>();
        if (await RatingsVisible(a)) rateIds.Add(a.Id);
        if (await RatingsVisible(b)) rateIds.Add(b.Id);
        var ratings = await db.Ratings.AsNoTracking()
            .Where(r => rateIds.Contains(r.UserId) && r.Type != MediaType.Episode && r.SeasonNumber == null).ToListAsync(ct);
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
