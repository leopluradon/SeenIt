using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class EmbedTests
{
    private static async Task<(EmbedService Embeds, AppDbContext Db)> Setup()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Movies.Add(new MovieMeta { TmdbId = 949, Title = "Heat", ReleaseDate = new DateOnly(1995, 12, 15), Runtime = 170, Genres = ["Crime", "Drama"],
            VoteAverage = 7.9, Overview = "Obsessive master thief Neil McCauley leads a crew.", BackdropPath = "/heat-bd.jpg", PosterPath = "/heat.jpg" });
        db.Shows.Add(new ShowMeta { TmdbId = 95396, Name = "Severance", FirstAirDate = new DateOnly(2022, 2, 18), Networks = ["Apple TV+"], BackdropPath = "/sev.jpg" });
        db.Episodes.Add(new EpisodeMeta { ShowTmdbId = 95396, SeasonNumber = 2, EpisodeNumber = 5, Name = "Trojan's Horse", Overview = "A big twist." });
        var pub = new AppUser { Id = Guid.NewGuid(), UserName = "sam", NormalizedUserName = "SAM", DisplayName = "Sam", Visibility = ProfileVisibility.Public };
        var priv = new AppUser { Id = Guid.NewGuid(), UserName = "alex", NormalizedUserName = "ALEX", Visibility = ProfileVisibility.Friends };
        db.Users.AddRange(pub, priv);
        db.WatchEvents.Add(new WatchEvent { UserId = pub.Id, Type = MediaType.Movie, TmdbId = 949, WatchedAt = DateTimeOffset.UtcNow });
        var hidden = new UserList { UserId = pub.Id, Name = "Secret", IsPublic = false };
        var shared = new UserList { UserId = pub.Id, Name = "Heists", IsPublic = true };
        db.Lists.AddRange(hidden, shared);
        db.ListItems.Add(new UserListItem { ListId = shared.Id, Type = MediaType.Movie, TmdbId = 949 });
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SeenIt:PublicUrl"] = "https://seenit.example/" }).Build();
        return (new EmbedService(db, null!, null!, config, NullLogger<EmbedService>.Instance), db);
    }

    [Fact]
    public async Task Movies_shows_and_episodes_get_their_own_preview_without_episode_spoilers()
    {
        var (e, db) = await Setup();
        var movie = await e.ForPathAsync("/movie/949", default);
        Assert.Equal("Heat (1995)", movie.Title);
        Assert.StartsWith("1995 · Crime, Drama · 2h 50m · ★ 7.9\nObsessive", movie.Description);
        Assert.Equal("https://image.tmdb.org/t/p/w1280/heat-bd.jpg", movie.Image);
        Assert.True(movie.LargeImage);

        var ep = await e.ForPathAsync("/show/95396/season/2/episode/5", default);
        Assert.Equal("Severance · S02E05", ep.Title);
        Assert.DoesNotContain("Trojan", ep.Title + ep.Description);
        Assert.DoesNotContain("twist", ep.Description);
        Assert.Equal("Severance · Season 2", (await e.ForPathAsync("/show/95396/season/2", default)).Title);
    }

    [Fact]
    public async Task Private_profiles_and_lists_get_the_plain_card()
    {
        var (e, db) = await Setup();
        var sam = await e.ForPathAsync("/u/sam", default);
        Assert.Equal("Sam (@sam) on SeenIt", sam.Title);
        Assert.Equal("1 movie and 0 shows watched", sam.Description);
        Assert.Equal("SeenIt", (await e.ForPathAsync("/u/alex", default)).Title);       // friends-only
        Assert.Equal("SeenIt", (await e.ForPathAsync("/u/nobody", default)).Title);

        var lists = await db.Lists.ToListAsync();
        Assert.Equal("SeenIt", (await e.ForPathAsync($"/lists/{lists.Single(l => !l.IsPublic).Id}", default)).Title);
        var shared = await e.ForPathAsync($"/lists/{lists.Single(l => l.IsPublic).Id}", default);
        Assert.Equal("Heists", shared.Title);
        Assert.Equal("A list of 1 title by Sam", shared.Description);
        Assert.Equal("SeenIt", (await e.ForPathAsync("/history", default)).Title);
    }

    [Fact]
    public async Task Tags_go_in_the_head_escaped_and_discord_gets_the_accent_color()
    {
        var (e, _) = await Setup();
        const string html = "<html><head><meta name=\"theme-color\" content=\"#0e1014\" /><title>SeenIt</title></head><body></body></html>";
        var out1 = e.Render(html, new EmbedService.Embed("Tom & Jerry \"Live\"", "a<b", "https://img/x.jpg", true), "https://seenit.example/movie/1", discord: true);
        Assert.Contains("<meta property=\"og:title\" content=\"Tom &amp; Jerry &quot;Live&quot;\" />", out1);
        Assert.Contains("<meta name=\"twitter:card\" content=\"summary_large_image\" />", out1);
        Assert.Contains("<title>Tom &amp; Jerry &quot;Live&quot; · SeenIt</title>", out1);
        Assert.Contains("#ec3f4f", out1);
        Assert.True(out1.IndexOf("og:title") < out1.IndexOf("</head>"));
        Assert.Contains("#0e1014", e.Render(html, new EmbedService.Embed("X", "y", null, false), "u", discord: false));
    }
}
