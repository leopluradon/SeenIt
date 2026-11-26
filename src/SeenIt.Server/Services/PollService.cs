using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Movie night polls: a few titles, friends who follow you vote on every one they'd watch (approval voting), and the one
/// with most votes wins when the owner closes it or its closing time passes (ties go to the earlier option). Members are
/// alerted when asked and when it picks a winner, and open polls update live as votes come in.
/// </summary>
public class PollService(AppDbContext db, TrackingService tracking, LiveHub live)
{
    public const int MinOptions = 2, MaxOptions = 8;

    public async Task<Guid> CreateAsync(Guid me, PollRequest req, CancellationToken ct)
    {
        var options = req.Options.Where(o => o.Type is MediaType.Movie or MediaType.Show).DistinctBy(o => (o.Type, o.TmdbId)).ToList();
        if (options.Count is < MinOptions or > MaxOptions) throw new TrackingException($"Pick {MinOptions} to {MaxOptions} titles.");
        if (req.ClosesAt is { } closes && closes <= DateTimeOffset.UtcNow) throw new TrackingException("The closing time has to be in the future.");
        foreach (var o in options)
            if (!(o.Type == MediaType.Movie ? await db.Movies.AnyAsync(m => m.TmdbId == o.TmdbId, ct) : await db.Shows.AnyAsync(m => m.TmdbId == o.TmdbId, ct)))
                await tracking.EnsureMetaAsync(o.Type, o.TmdbId, ct);

        // Like recommendations: only people who follow you can be asked.
        var names = req.Invite.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().ToUpperInvariant()).Distinct().Take(30).ToList();
        var invited = await db.Follows.Where(f => f.FolloweeId == me).Join(db.Users, f => f.FollowerId, u => u.Id, (f, u) => u)
            .Where(u => names.Contains(u.NormalizedUserName!)).ToListAsync(ct);
        if (invited.Count == 0) throw new TrackingException("Ask at least one person who follows you.");

        var poll = new Poll { OwnerId = me, Title = req.Title?.Trim() is { Length: > 0 } t ? (t.Length > 100 ? t[..100] : t) : null, ClosesAt = req.ClosesAt?.ToUniversalTime() };
        poll.Options.AddRange(options.Select((o, i) => new PollOption { Type = o.Type, TmdbId = o.TmdbId, Position = i }));
        poll.Members.Add(new PollMember { UserId = me });
        poll.Members.AddRange(invited.Select(u => new PollMember { UserId = u.Id }));
        db.Polls.Add(poll);

        var owner = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me, ct);
        var first = await MediaAsync(options.Take(1), ct);
        var headline = Trim($"{owner.DisplayName ?? owner.UserName} asked you to vote on {(poll.Title is { } title ? $"“{title}”" : "movie night")}", 300);
        foreach (var u in invited)
            db.Notifications.Add(Alert(u, poll, headline, $"{options.Count} to pick from", first.Values.FirstOrDefault()));
        await db.SaveChangesAsync(ct);
        foreach (var u in invited) live.Notify(u.Id);
        return poll.Id;
    }

    public async Task<List<PollSummary>> ListAsync(Guid me, CancellationToken ct)
    {
        var ids = await db.PollMembers.Where(m => m.UserId == me).Select(m => m.PollId).ToListAsync(ct);
        var polls = await db.Polls.Include(p => p.Options).Include(p => p.Members).Include(p => p.Votes)
            .Where(p => ids.Contains(p.Id)).OrderByDescending(p => p.CreatedAt).Take(50).ToListAsync(ct);
        foreach (var p in polls) await CloseIfDueAsync(p, ct);
        var owners = await db.Users.AsNoTracking().Where(u => polls.Select(p => p.OwnerId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var media = await MediaAsync(polls.SelectMany(p => p.Options).Select(o => new MediaRef(o.Type, o.TmdbId)).Distinct(), ct);
        return polls.Select(p =>
        {
            var winner = Winner(p);
            return new PollSummary(p.Id, p.Title, owners.TryGetValue(p.OwnerId, out var o) ? o.DisplayName ?? o.UserName! : "?", p.OwnerId == me,
                p.CreatedAt, p.ClosesAt, p.ClosedAt is not null,
                p.Options.OrderBy(x => x.Position).Select(x => media.GetValueOrDefault((x.Type, x.TmdbId))?.PosterPath).ToList(),
                p.Members.Count, p.Votes.Select(v => v.UserId).Distinct().Count(), p.Votes.Any(v => v.UserId == me),
                p.ClosedAt is not null && winner is not null ? media.GetValueOrDefault((winner.Type, winner.TmdbId))?.Title : null);
        }).ToList();
    }

    /// <summary>A poll you're in (null otherwise).</summary>
    public async Task<PollDto?> GetAsync(Guid me, Guid id, CancellationToken ct)
    {
        var p = await LoadAsync(id, ct);
        if (p is null || p.Members.All(m => m.UserId != me)) return null;
        await CloseIfDueAsync(p, ct);
        var people = await db.Users.AsNoTracking().Where(u => p.Members.Select(m => m.UserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var following = (await db.Follows.Where(f => f.FollowerId == me).Select(f => f.FolloweeId).ToListAsync(ct)).ToHashSet();
        var followers = (await db.Follows.Where(f => f.FolloweeId == me).Select(f => f.FollowerId).ToListAsync(ct)).ToHashSet();
        UserCard Card(Guid uid) => people.TryGetValue(uid, out var u)
            ? new UserCard(u.UserName!, u.DisplayName, following.Contains(uid), followers.Contains(uid), AvatarService.Path(u.UserName!, u.AvatarVersion))
            : new UserCard("?", null, false, false);
        var media = await MediaAsync(p.Options.Select(o => new MediaRef(o.Type, o.TmdbId)), ct);
        var marked = await UserMarksFor(me, media.Values.ToList(), ct);
        var options = p.Options.OrderBy(o => o.Position).Where(o => marked.ContainsKey((o.Type, o.TmdbId))).Select(o => new PollOptionDto(o.Id, marked[(o.Type, o.TmdbId)],
            p.Votes.Where(v => v.OptionId == o.Id).Select(v => people.GetValueOrDefault(v.UserId)?.UserName).OfType<string>().ToList(),
            p.Votes.Any(v => v.OptionId == o.Id && v.UserId == me))).ToList();
        return new PollDto(p.Id, p.Title, Card(p.OwnerId), p.OwnerId == me, p.CreatedAt, p.ClosesAt, p.ClosedAt is not null, options,
            p.Members.Select(m => Card(m.UserId)).ToList(),
            p.Votes.Select(v => v.UserId).Distinct().Select(v => people.GetValueOrDefault(v)?.UserName).OfType<string>().ToList(),
            p.ClosedAt is not null ? Winner(p)?.Id : null);
    }

    public async Task VoteAsync(Guid me, Guid id, PollVoteRequest req, CancellationToken ct)
    {
        var p = await LoadAsync(id, ct);
        if (p is null || p.Members.All(m => m.UserId != me)) throw new TrackingException("That poll isn't one you're in.");
        await CloseIfDueAsync(p, ct);
        if (p.ClosedAt is not null) throw new TrackingException("Voting's closed.");
        var picked = req.OptionIds.Where(o => p.Options.Any(x => x.Id == o)).Distinct().ToList();
        db.PollVotes.RemoveRange(p.Votes.Where(v => v.UserId == me));
        db.PollVotes.AddRange(picked.Select(o => new PollVote { PollId = id, UserId = me, OptionId = o }));
        await db.SaveChangesAsync(ct);
        foreach (var m in p.Members) live.Notify(m.UserId);
    }

    public async Task CloseAsync(Guid me, Guid id, CancellationToken ct)
    {
        var p = await LoadAsync(id, ct);
        if (p is null || p.OwnerId != me) throw new TrackingException("Only whoever started the poll can close it.");
        if (p.ClosedAt is null) await CloseNowAsync(p, DateTimeOffset.UtcNow, ct);
    }

    public async Task<bool> DeleteAsync(Guid me, Guid id, CancellationToken ct)
    {
        var p = await db.Polls.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == me, ct);
        if (p is null) return false;
        await db.Notifications.Where(n => n.PollId == id).ExecuteDeleteAsync(ct);
        db.Polls.Remove(p);
        await db.SaveChangesAsync(ct);
        foreach (var m in p.Members) live.Notify(m.UserId);
        return true;
    }

    private Task<Poll?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Polls.Include(p => p.Options).Include(p => p.Members).Include(p => p.Votes).FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>Most votes; a tie goes to the option added first. Null with no votes at all.</summary>
    private static PollOption? Winner(Poll p) => p.Votes.Count == 0 ? null
        : p.Options.OrderByDescending(o => p.Votes.Count(v => v.OptionId == o.Id)).ThenBy(o => o.Position).First();

    private async Task CloseIfDueAsync(Poll p, CancellationToken ct)
    {
        if (p.ClosedAt is null && p.ClosesAt is { } at && at <= DateTimeOffset.UtcNow) await CloseNowAsync(p, at, ct);
    }

    /// <summary>Closes it and tells everyone in it (but whoever closed it) what won.</summary>
    private async Task CloseNowAsync(Poll p, DateTimeOffset at, CancellationToken ct)
    {
        p.ClosedAt = at;
        var winner = Winner(p);
        if (winner is not null)
        {
            var media = await MediaAsync([new MediaRef(winner.Type, winner.TmdbId)], ct);
            var m = media.Values.FirstOrDefault();
            var members = await db.Users.AsNoTracking().Where(u => p.Members.Select(x => x.UserId).Contains(u.Id) && u.Id != p.OwnerId).ToListAsync(ct);
            var votes = p.Votes.Count(v => v.OptionId == winner.Id);
            foreach (var u in members)
                db.Notifications.Add(Alert(u, p, Trim($"Movie night: {m?.Title ?? "the winner"} it is", 300), $"{votes} vote{(votes == 1 ? "" : "s")}{(p.Title is { } t ? $" · {t}" : "")}", m));
        }
        await db.SaveChangesAsync(ct);
        foreach (var x in p.Members) live.Notify(x.UserId);
    }

    private static Notification Alert(AppUser to, Poll p, string title, string detail, MediaSummary? m) => new()
    {
        UserId = to.Id, PollId = p.Id, Kind = AlertKind.Poll, Title = title, Detail = Trim(detail, 400),
        Type = m?.Type == MediaType.Show ? MediaType.Show : MediaType.Movie, TmdbId = m?.TmdbId ?? 0, PosterPath = m?.PosterPath,
        Day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ReviewService.Zone(to.TimeZone)).DateTime),
    };

    private async Task<Dictionary<(MediaType, int), MediaSummary>> MediaAsync(IEnumerable<MediaRef> refs, CancellationToken ct)
    {
        var list = refs.ToList();
        var (movies, shows) = await tracking.LoadMetaAsync(list.Select(r => (r.Type, r.TmdbId)), ct);
        var result = new Dictionary<(MediaType, int), MediaSummary>();
        foreach (var r in list)
        {
            if (r.Type == MediaType.Movie && movies.TryGetValue(r.TmdbId, out var m)) result[(r.Type, r.TmdbId)] = CatalogService.ToSummary(m);
            else if (r.Type == MediaType.Show && shows.TryGetValue(r.TmdbId, out var s)) result[(r.Type, r.TmdbId)] = CatalogService.ToSummary(s);
        }
        return result;
    }

    /// <summary>Marks what the viewer has seen or has on their watchlist (like search results).</summary>
    private async Task<Dictionary<(MediaType, int), MediaSummary>> UserMarksFor(Guid me, List<MediaSummary> items, CancellationToken ct) =>
        (await Endpoints.UserMarks.ApplyAsync(db, me, items, ct)).ToDictionary(m => (m.Type, m.TmdbId));

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
