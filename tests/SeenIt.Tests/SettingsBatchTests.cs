using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class SettingsBatchTests
{
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AppUser User(string name, UserPrefs? prefs = null) => new()
    {
        Id = Guid.NewGuid(), UserName = name, NormalizedUserName = name.ToUpperInvariant(), Email = $"{name}@example.invalid",
        PrefsJson = prefs is null ? null : UserPrefsStore.Write(prefs),
    };

    [Fact]
    public async Task Approving_followers_turns_follows_into_requests()
    {
        await using var db = Db();
        var picky = User("picky", new UserPrefs(ApproveFollowers: true));
        var open = User("open");
        var fan = User("fan");
        db.Users.AddRange(picky, open, fan);
        await db.SaveChangesAsync();
        var social = new SocialService(db, null!, null!, null!, null!, null!);

        Assert.Equal(new FollowResult(true), await social.FollowAsync(fan.Id, "picky", true, default));
        Assert.Equal(new FollowResult(false), await social.FollowAsync(fan.Id, "open", true, default));
        Assert.False(await db.Follows.AnyAsync(f => f.FolloweeId == picky.Id));
        Assert.True(await db.Follows.AnyAsync(f => f.FolloweeId == open.Id));
        Assert.Equal(["fan"], (await social.RequestsAsync(picky.Id, default)).Select(u => u.UserName));

        // Cancelling withdraws the request; asking again and approving makes it a follow.
        await social.FollowAsync(fan.Id, "picky", false, default);
        Assert.Empty(await social.RequestsAsync(picky.Id, default));
        await social.FollowAsync(fan.Id, "picky", true, default);
        Assert.True(await social.AnswerRequestAsync(picky.Id, "fan", approve: true, default));
        Assert.True(await db.Follows.AnyAsync(f => f.FollowerId == fan.Id && f.FolloweeId == picky.Id));
        Assert.False(await social.AnswerRequestAsync(picky.Id, "fan", approve: true, default));   // nothing left to answer
    }

    [Fact]
    public async Task Turning_approval_off_lets_everyone_waiting_in()
    {
        await using var db = Db();
        var picky = User("picky", new UserPrefs(ApproveFollowers: true));
        var a = User("a"); var b = User("b");
        db.Users.AddRange(picky, a, b);
        await db.SaveChangesAsync();
        var social = new SocialService(db, null!, null!, null!, null!, null!);
        await social.FollowAsync(a.Id, "picky", true, default);
        await social.FollowAsync(b.Id, "picky", true, default);
        await social.ApproveAllAsync(picky.Id, default);
        Assert.Equal(2, await db.Follows.CountAsync(f => f.FolloweeId == picky.Id));
        Assert.Empty(db.FollowRequests);
    }

    [Fact]
    public void Weekly_backups_start_on_monday()
    {
        var u = User("w", new UserPrefs(BackupEvery: BackupEvery.Weekly));
        var start = MonthlyBackupWorker.PeriodStart(u);
        Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
        Assert.Equal(1, MonthlyBackupWorker.PeriodStart(User("m")).Day);
    }

    [Fact]
    public void Clean_keeps_poster_choices_valid()
    {
        var p = new UserPrefs(PosterScoreMovies: "myanimelist", PosterScoreShows: "letterboxd").Clean();
        Assert.Equal("tmdb", p.PosterScoreMovies);   // MyAnimeList has no movies
        Assert.Equal("tmdb", p.PosterScoreShows);    // Letterboxd has no shows
        Assert.Equal("imdb", new UserPrefs(PosterScoreShows: "imdb").Clean().PosterScoreShows);
    }
}
