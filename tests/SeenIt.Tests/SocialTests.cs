using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class SocialTests
{
    private static readonly AppUser Me = new() { UserName = "me", NormalizedUserName = "ME" };

    [Theory]
    [InlineData(ProfileVisibility.Public, false, true)]
    [InlineData(ProfileVisibility.Friends, false, false)]
    [InlineData(ProfileVisibility.Friends, true, true)]
    [InlineData(ProfileVisibility.Private, true, false)]
    public void Visibility_rules(ProfileVisibility v, bool mutual, bool visible)
    {
        var owner = new AppUser { UserName = "them", Visibility = v };
        Assert.Equal(visible, SocialService.CanView(owner, Me.Id, mutual));
        Assert.True(SocialService.CanView(owner, owner.Id, false)); // always yourself
    }

    [Fact]
    public async Task Feed_only_includes_people_whose_profiles_you_can_see()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var open = new AppUser { UserName = "open", NormalizedUserName = "OPEN", Visibility = ProfileVisibility.Public };
        var friend = new AppUser { UserName = "friend", NormalizedUserName = "FRIEND", Visibility = ProfileVisibility.Friends };
        var oneWay = new AppUser { UserName = "oneway", NormalizedUserName = "ONEWAY", Visibility = ProfileVisibility.Friends };
        var secret = new AppUser { UserName = "secret", NormalizedUserName = "SECRET", Visibility = ProfileVisibility.Private };
        db.Users.AddRange(Me, open, friend, oneWay, secret);
        db.Movies.Add(new MovieMeta { TmdbId = 1, Title = "Heat", Runtime = 170 });
        foreach (var u in new[] { open, friend, oneWay, secret })
        {
            db.Follows.Add(new Follow { FollowerId = Me.Id, FolloweeId = u.Id });
            db.WatchEvents.Add(new WatchEvent { UserId = u.Id, Type = MediaType.Movie, TmdbId = 1, WatchedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        }
        db.Follows.Add(new Follow { FollowerId = friend.Id, FolloweeId = Me.Id }); // mutual
        db.CheckIns.Add(new CheckIn { UserId = friend.Id, Type = MediaType.Movie, TmdbId = 1, EndsAt = DateTimeOffset.UtcNow.AddHours(1), Source = WatchSource.Manual });
        db.CheckIns.Add(new CheckIn { UserId = open.Id, Type = MediaType.Movie, TmdbId = 1, EndsAt = DateTimeOffset.UtcNow.AddHours(-1), Source = WatchSource.Manual }); // finished
        await db.SaveChangesAsync();

        var tracking = new TrackingService(db, null!);
        var social = new SocialService(db, tracking, null!, new CheckInService(db, null!, tracking));
        var feed = await social.FeedAsync(Me.Id, 50, default);

        Assert.Equal(["friend", "open"], feed.Activity.Select(a => a.User.UserName).Order());
        Assert.Equal("friend", Assert.Single(feed.WatchingNow).User.UserName);
        Assert.Equal("Heat", feed.WatchingNow[0].Item.Title);
    }

    [Fact]
    public async Task Paused_and_dropped_shows_leave_up_next()
    {
        var user = Guid.NewGuid();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (var id in new[] { 1, 2, 3 })
        {
            db.Shows.Add(new ShowMeta { TmdbId = id, Name = $"Show {id}" });
            db.Episodes.Add(new EpisodeMeta { ShowTmdbId = id, SeasonNumber = 1, EpisodeNumber = 1, AirDate = new DateOnly(2020, 1, 1) });
            db.Episodes.Add(new EpisodeMeta { ShowTmdbId = id, SeasonNumber = 1, EpisodeNumber = 2, AirDate = new DateOnly(2020, 1, 8) });
            db.WatchEvents.Add(new WatchEvent { UserId = user, Type = MediaType.Episode, TmdbId = id, SeasonNumber = 1, EpisodeNumber = 1, WatchedAt = DateTimeOffset.UtcNow.AddDays(-id) });
        }
        db.ShowStates.Add(new ShowState { UserId = user, ShowTmdbId = 2, Status = ShowTrackStatus.Paused });
        db.ShowStates.Add(new ShowState { UserId = user, ShowTmdbId = 3, Status = ShowTrackStatus.Dropped });
        await db.SaveChangesAsync();

        var t = new TrackingService(db, null!);
        var progress = await t.GetProgressAsync(user, default);
        Assert.Equal(ShowTrackStatus.Paused, progress.Single(p => p.ShowTmdbId == 2).Status);
        Assert.Equal(ShowTrackStatus.Dropped, progress.Single(p => p.ShowTmdbId == 3).Status);
        Assert.Equal([1], (await t.GetDashboardAsync(user, default)).UpNext.Select(p => p.ShowTmdbId));
    }
}
