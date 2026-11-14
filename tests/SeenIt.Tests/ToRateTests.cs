using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class ToRateTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const int Heat = 949, Ended = 1396, Airing = 95396;

    /// <summary>Heat watched yesterday; an ended two-season show finished today; a show still airing season 2.</summary>
    private static async Task<AppDbContext> Setup(params string[] kinds)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Users.Add(new AppUser { Id = Me, UserName = "me", PrefsJson = UserPrefsStore.Write(UserPrefs.Default with { RateKinds = kinds }) });
        db.Movies.Add(new MovieMeta { TmdbId = Heat, Title = "Heat", ReleaseDate = new DateOnly(1995, 12, 15) });
        db.Shows.Add(new ShowMeta { TmdbId = Ended, Name = "Breaking Bad", Status = "Ended" });
        db.Shows.Add(new ShowMeta { TmdbId = Airing, Name = "Severance", Status = "Returning Series" });
        var past = DateOnly.FromDateTime(Now.UtcDateTime).AddYears(-2);
        foreach (var (show, season, eps, airs) in new[] { (Ended, 1, 2, past), (Ended, 2, 2, past), (Airing, 1, 2, past), (Airing, 2, 2, DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-7)) })
            for (var e = 1; e <= eps; e++)
                db.Episodes.Add(new EpisodeMeta { ShowTmdbId = show, SeasonNumber = season, EpisodeNumber = e, Name = $"Ep {e}",
                    AirDate = show == Airing && season == 2 && e == 2 ? DateOnly.FromDateTime(Now.UtcDateTime).AddDays(7) : airs });
        db.Episodes.Add(new EpisodeMeta { ShowTmdbId = Ended, SeasonNumber = 0, EpisodeNumber = 1, AirDate = past });   // a special: doesn't count

        db.WatchEvents.Add(Play(MediaType.Movie, Heat, Now.AddDays(-1)));
        // Breaking Bad season 1 from an import years ago, season 2 this week.
        db.WatchEvents.Add(Play(MediaType.Episode, Ended, Now.AddYears(-1), 1, 1, import: true));
        db.WatchEvents.Add(Play(MediaType.Episode, Ended, Now.AddYears(-1), 1, 2, import: true));
        db.WatchEvents.Add(Play(MediaType.Episode, Ended, Now.AddDays(-2), 2, 1));
        db.WatchEvents.Add(Play(MediaType.Episode, Ended, Now.AddHours(-1), 2, 2));
        // Severance: season 1 this month, season 2's first episode; its second hasn't aired.
        db.WatchEvents.Add(Play(MediaType.Episode, Airing, Now.AddDays(-10), 1, 1));
        db.WatchEvents.Add(Play(MediaType.Episode, Airing, Now.AddDays(-9), 1, 2));
        db.WatchEvents.Add(Play(MediaType.Episode, Airing, Now.AddDays(-3), 2, 1));
        await db.SaveChangesAsync();
        return db;
    }

    private static WatchEvent Play(MediaType t, int id, DateTimeOffset at, int? s = null, int? e = null, bool import = false) =>
        new() { UserId = Me, Type = t, TmdbId = id, SeasonNumber = s, EpisodeNumber = e, WatchedAt = at, ImportJobId = import ? Guid.NewGuid() : null };

    private static async Task<List<string>> Keys(AppDbContext db) =>
        (await new ToRateService(db).GetAsync(Me, 40, default))
            .Select(i => i.Kind switch { "season" => $"season {i.Title} {i.SeasonNumber}", "episode" => $"episode {i.Title} {i.SeasonNumber}x{i.EpisodeNumber}", _ => $"{i.Kind} {i.Title}" }).ToList();

    [Fact]
    public async Task Shows_and_movies_only_by_default_newest_first()
    {
        var db = await Setup("movie", "show");
        // Breaking Bad ended and every episode is seen (season 1 through the import); Severance is still airing.
        Assert.Equal(["show Breaking Bad", "movie Heat"], await Keys(db));
        Assert.Equal(new[] { "movie", "show" }, UserPrefs.DefaultRateKinds);
    }

    [Fact]
    public async Task Seasons_count_once_every_aired_episode_is_seen_and_none_are_still_to_come()
    {
        var db = await Setup("season");
        // Breaking Bad season 2 (finished this week) and Severance season 1. Not BB season 1: no recent play.
        // Not Severance season 2: its second episode hasn't aired.
        Assert.Equal(["season Breaking Bad 2", "season Severance 1"], await Keys(db));
    }

    [Fact]
    public async Task Episodes_leave_out_imports()
    {
        var db = await Setup("episode");
        Assert.Equal(["episode Breaking Bad 2x2", "episode Breaking Bad 2x1", "episode Severance 2x1", "episode Severance 1x2", "episode Severance 1x1"], await Keys(db));
    }

    [Fact]
    public async Task Rated_and_skipped_items_drop_out_and_unskipping_brings_them_back()
    {
        var db = await Setup("movie", "show", "season");
        db.Ratings.Add(new Rating { UserId = Me, Type = MediaType.Show, TmdbId = Airing, SeasonNumber = 1, Value = 8 });
        await db.SaveChangesAsync();
        var service = new ToRateService(db);
        await service.SkipAsync(Me, new ToRateSkip(MediaType.Movie, Heat), default);
        await service.SkipAsync(Me, new ToRateSkip(MediaType.Movie, Heat), default);   // twice is fine
        Assert.Equal(["show Breaking Bad", "season Breaking Bad 2"], await Keys(db));

        await service.UnskipAsync(Me, new ToRateSkip(MediaType.Movie, Heat), default);
        Assert.Contains("movie Heat", await Keys(db));
    }

    [Fact]
    public async Task Nothing_picked_means_nothing_offered()
    {
        var db = await Setup();
        Assert.Empty(await Keys(db));
    }

    [Fact]
    public void Unknown_kinds_are_dropped_when_saved()
    {
        var p = (UserPrefs.Default with { RateKinds = ["show", "bogus", "show", "episode"] }).Clean();
        Assert.Equal(["show", "episode"], p.RateKinds);
        Assert.True(UserPrefs.Default.RatesOnHome("movie"));
        Assert.False(UserPrefs.Default.RatesOnHome("episode"));
    }
}
