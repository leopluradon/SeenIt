using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class PollTests
{
    private static readonly Guid Owner = Guid.NewGuid(), Fan = Guid.NewGuid(), Other = Guid.NewGuid(), Stranger = Guid.NewGuid();
    private const int Heat = 949, Alien = 348, Severance = 95396;

    private static async Task<(PollService, AppDbContext)> Setup()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var (id, name) in new[] { (Owner, "owner"), (Fan, "fan"), (Other, "other"), (Stranger, "stranger") })
            db.Users.Add(new AppUser { Id = id, UserName = name, NormalizedUserName = name.ToUpperInvariant() });
        db.Follows.Add(new Follow { FollowerId = Fan, FolloweeId = Owner });
        db.Follows.Add(new Follow { FollowerId = Other, FolloweeId = Owner });
        db.Movies.Add(new MovieMeta { TmdbId = Heat, Title = "Heat" });
        db.Movies.Add(new MovieMeta { TmdbId = Alien, Title = "Alien" });
        db.Shows.Add(new ShowMeta { TmdbId = Severance, Name = "Severance" });
        await db.SaveChangesAsync();
        return (new PollService(db, new TrackingService(db, null!), new LiveHub()), db);
    }

    private static PollRequest Req(params string[] invite) =>
        new("Friday", [new(MediaType.Movie, Heat), new(MediaType.Movie, Alien), new(MediaType.Show, Severance)], invite);

    [Fact]
    public async Task Only_followers_are_asked_and_alerted()
    {
        var (polls, db) = await Setup();
        var id = await polls.CreateAsync(Owner, Req("fan", "stranger"), default);
        var poll = await polls.GetAsync(Owner, id, default);
        Assert.Equal(["owner", "fan"], poll!.Members.Select(m => m.UserName));
        Assert.Null(await polls.GetAsync(Stranger, id, default));
        Assert.Equal([Fan], await db.Notifications.Where(n => n.PollId == id).Select(n => n.UserId).ToListAsync());
        await Assert.ThrowsAsync<TrackingException>(() => polls.CreateAsync(Owner, Req("stranger"), default));
        await Assert.ThrowsAsync<TrackingException>(() => polls.CreateAsync(Owner, new PollRequest(null, [new(MediaType.Movie, Heat)], ["fan"]), default));
    }

    [Fact]
    public async Task Most_votes_wins_and_ties_go_to_the_earlier_option()
    {
        var (polls, db) = await Setup();
        var id = await polls.CreateAsync(Owner, Req("fan", "other"), default);
        var opts = (await polls.GetAsync(Owner, id, default))!.Options.Select(o => o.Id).ToList();
        await polls.VoteAsync(Fan, id, new PollVoteRequest([opts[1], opts[2]]), default);
        await polls.VoteAsync(Other, id, new PollVoteRequest([opts[2]]), default);
        await polls.VoteAsync(Owner, id, new PollVoteRequest([opts[1]]), default);
        await polls.VoteAsync(Owner, id, new PollVoteRequest([opts[0], opts[1]]), default);   // changing your mind replaces your votes
        // Alien 2, Severance 2: the tie goes to Alien, added first.
        await Assert.ThrowsAsync<TrackingException>(() => polls.CloseAsync(Fan, id, default));
        await polls.CloseAsync(Owner, id, default);

        var poll = (await polls.GetAsync(Fan, id, default))!;
        Assert.True(poll.Closed);
        Assert.Equal(opts[1], poll.WinnerId);
        Assert.Equal(["fan", "owner"], poll.Options[1].Voters.Order());
        Assert.Equal(3, poll.Voted.Count);
        Assert.Contains(await db.Notifications.Where(n => n.PollId == id && n.UserId == Other).Select(n => n.Title).ToListAsync(), t => t.Contains("Alien it is"));
        Assert.False(await db.Notifications.AnyAsync(n => n.PollId == id && n.UserId == Owner));
        await Assert.ThrowsAsync<TrackingException>(() => polls.VoteAsync(Fan, id, new PollVoteRequest([opts[0]]), default));
    }

    [Fact]
    public async Task A_poll_past_its_closing_time_closes_itself()
    {
        var (polls, db) = await Setup();
        var id = await polls.CreateAsync(Owner, Req("fan") with { ClosesAt = DateTimeOffset.UtcNow.AddHours(1) }, default);
        var opts = (await polls.GetAsync(Fan, id, default))!.Options.Select(o => o.Id).ToList();
        await polls.VoteAsync(Fan, id, new PollVoteRequest([opts[2]]), default);
        var p = await db.Polls.SingleAsync(x => x.Id == id);
        p.ClosesAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var list = await polls.ListAsync(Fan, default);
        Assert.True(list.Single().Closed);
        Assert.Equal("Severance", list.Single().Winner);
        Assert.True(list.Single().YouVoted);
    }
}
