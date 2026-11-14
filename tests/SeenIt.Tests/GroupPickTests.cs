using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class GroupPickTests
{
    private static readonly Guid Me = Guid.NewGuid(), Sam = Guid.NewGuid(), Alex = Guid.NewGuid();

    [Fact]
    public async Task Picks_from_everyones_watchlists_skipping_what_anyone_has_seen_and_weighting_shared_ones()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var past = new DateOnly(2020, 1, 1);
        foreach (var (id, title) in new[] { (1, "Heat"), (2, "Ronin"), (3, "Collateral"), (4, "Thief") })
            db.Movies.Add(new MovieMeta { TmdbId = id, Title = title, ReleaseDate = past, Runtime = 120 });
        db.Movies.Add(new MovieMeta { TmdbId = 5, Title = "Unreleased", ReleaseDate = new DateOnly(2099, 1, 1) });
        db.Shows.Add(new ShowMeta { TmdbId = 10, Name = "Severance", FirstAirDate = past });
        db.Shows.Add(new ShowMeta { TmdbId = 11, Name = "Andor", FirstAirDate = past });
        void Want(Guid u, MediaType t, int id) => db.Watchlist.Add(new WatchlistItem { UserId = u, Type = t, TmdbId = id });
        Want(Me, MediaType.Movie, 1); Want(Sam, MediaType.Movie, 1); Want(Alex, MediaType.Movie, 1);   // everyone
        Want(Sam, MediaType.Movie, 2); Want(Me, MediaType.Movie, 2);                                   // two of us
        Want(Alex, MediaType.Movie, 3);                                                                // Alex only
        Want(Sam, MediaType.Movie, 4);                                                                 // Sam's, but I've seen it
        Want(Me, MediaType.Movie, 5);                                                                  // not out yet
        Want(Sam, MediaType.Show, 10);                                                                 // Alex started it
        Want(Me, MediaType.Show, 11);
        Want(Guid.NewGuid(), MediaType.Movie, 2);                                                      // a stranger's list doesn't count
        db.WatchEvents.Add(new WatchEvent { UserId = Me, Type = MediaType.Movie, TmdbId = 4, WatchedAt = DateTimeOffset.UtcNow });
        db.WatchEvents.Add(new WatchEvent { UserId = Alex, Type = MediaType.Episode, TmdbId = 10, SeasonNumber = 1, EpisodeNumber = 1, WatchedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var picks = new PickService(db, null!, NullLogger<PickService>.Instance);
        var c = (await picks.GroupCandidatesAsync(Me, [(Sam, "Sam"), (Alex, "Alex")], PickKind.Any, DateOnly.FromDateTime(DateTime.UtcNow), default))
            .ToDictionary(x => x.Media.Title);

        Assert.Equal(["Andor", "Collateral", "Heat", "Ronin"], c.Keys.Order());
        Assert.Equal(("On all your watchlists", 3), (c["Heat"].Reason, c["Heat"].Weight));
        Assert.Equal(("On Sam's and your watchlists", 2), (c["Ronin"].Reason, c["Ronin"].Weight));
        Assert.Equal("On Alex's watchlist", c["Collateral"].Reason);
        Assert.Equal("On your watchlist", c["Andor"].Reason);

        var moviesOnly = await picks.GroupCandidatesAsync(Me, [(Sam, "Sam")], PickKind.Movie, DateOnly.FromDateTime(DateTime.UtcNow), default);
        Assert.DoesNotContain(moviesOnly, x => x.Type == MediaType.Show);
        Assert.DoesNotContain(moviesOnly, x => x.Media.Title == "Collateral");   // Alex isn't watching this time
    }
}
