using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Tmdb;

namespace SeenIt.Server.Services;

/// <summary>
/// Link previews (Discord, Slack, iMessage, WhatsApp, Telegram, X...): those fetch a page and read its Open Graph tags
/// without running any script, so the server writes the right ones into index.html for each link: a movie's title, year,
/// genres and backdrop, a show's, a person's, a public profile or list. Episodes leave out their title and synopsis
/// (spoilers), and nothing shows that a signed-out visitor couldn't see.
/// </summary>
public partial class EmbedService(AppDbContext db, CatalogService catalog, TmdbClient tmdb, IConfiguration config, ILogger<EmbedService> log)
{
    public sealed record Embed(string Title, string Description, string? Image, bool LargeImage, string Type = "website");

    private const string Img = "https://image.tmdb.org/t/p/";
    private string Site => (config["SeenIt:PublicUrl"] is { Length: > 0 } u ? u : "https://seenit.vohiyo.com/").TrimEnd('/');
    private Embed Default => new("SeenIt", "Track the movies and TV you watch, see what's next in every show, and share it with friends.",
        $"{Site}/icon-512.png", false);

    /// <summary>The preview for a page path ("movie/949"). Never throws: a failed lookup gives the plain SeenIt card.</summary>
    public async Task<Embed> ForPathAsync(string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));   // a preview is never worth a slow page
        try { return await LookupAsync(path.Trim('/'), cts.Token) ?? Default; }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogInformation(ex, "No link preview for {Path}", path);
            return Default;
        }
    }

    private async Task<Embed?> LookupAsync(string path, CancellationToken ct)
    {
        if (MovieRoute().Match(path) is { Success: true } mm && int.TryParse(mm.Groups[1].Value, out var movieId))
        {
            var m = await db.Movies.AsNoTracking().FirstOrDefaultAsync(x => x.TmdbId == movieId, ct) ?? await catalog.EnsureMovieAsync(movieId, ct);
            if (m is null) return null;
            var facts = Join(m.ReleaseDate?.Year.ToString(), Genres(m.Genres), Runtime(m.Runtime), Score(m.VoteAverage));
            return new($"{m.Title}{(m.ReleaseDate is { } d ? $" ({d.Year})" : "")}", Lines(facts, m.Overview),
                Image(m.BackdropPath, "w1280") ?? Image(m.PosterPath, "w500"), m.BackdropPath is not null, "video.movie");
        }
        if (ShowRoute().Match(path) is { Success: true } sm && int.TryParse(sm.Groups[1].Value, out var showId))
        {
            var s = await db.Shows.AsNoTracking().FirstOrDefaultAsync(x => x.TmdbId == showId, ct) ?? await catalog.EnsureShowAsync(showId, ct);
            if (s is null) return null;
            var seasons = await db.Episodes.Where(e => e.ShowTmdbId == showId && e.SeasonNumber > 0).Select(e => e.SeasonNumber).Distinct().CountAsync(ct);
            var facts = Join(s.FirstAirDate?.Year.ToString(), s.Networks.FirstOrDefault(), Genres(s.Genres),
                seasons > 0 ? $"{seasons} season{(seasons == 1 ? "" : "s")}" : null, Score(s.VoteAverage));
            var image = Image(s.BackdropPath, "w1280") ?? Image(s.PosterPath, "w500");
            var season = sm.Groups[2].Success && int.TryParse(sm.Groups[2].Value, out var sn) ? sn : (int?)null;
            var episode = sm.Groups[3].Success && int.TryParse(sm.Groups[3].Value, out var en) ? en : (int?)null;
            // An episode or season: which one, and the show; never the episode's title or synopsis (spoilers).
            if (season is { } sNo && episode is { } eNo)
                return new($"{s.Name} · {(sNo == 0 ? $"Special {eNo}" : $"S{sNo:00}E{eNo:00}")}", facts, image, s.BackdropPath is not null, "video.episode");
            if (season is { } only)
                return new($"{s.Name} · {(only == 0 ? "Specials" : $"Season {only}")}", facts, image, s.BackdropPath is not null, "video.tv_show");
            return new(s.Name, Lines(facts, s.Overview), image, s.BackdropPath is not null, "video.tv_show");
        }
        if (PersonRoute().Match(path) is { Success: true } pm && int.TryParse(pm.Groups[1].Value, out var personId))
        {
            var p = await tmdb.GetPersonAsync(personId, ct);
            if (p is null) return null;
            var known = p.KnownForDepartment is { Length: > 0 } k ? (k == "Acting" ? "Actor" : $"Known for {k.ToLowerInvariant()}") : null;
            return new(p.Name, Lines(known, p.Biography), Image(p.ProfilePath, "w500"), false, "profile");
        }
        if (ProfileRoute().Match(path) is { Success: true } um)
        {
            var name = Uri.UnescapeDataString(um.Groups[1].Value);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUserName == name.ToUpperInvariant(), ct);
            // Only what anyone signed out could see: a private or friends-only profile gets the plain card.
            if (user is null || !SocialService.CanView(user, null, false)) return null;
            string? summary = null;
            if (SocialService.CanSee(user, null, false, ProfilePart.History))
            {
                var movies = await db.WatchEvents.Where(w => w.UserId == user.Id && w.Type == MediaType.Movie).Select(w => w.TmdbId).Distinct().CountAsync(ct);
                var shows = await db.WatchEvents.Where(w => w.UserId == user.Id && w.Type == MediaType.Episode).Select(w => w.TmdbId).Distinct().CountAsync(ct);
                summary = $"{movies:N0} movie{(movies == 1 ? "" : "s")} and {shows:N0} show{(shows == 1 ? "" : "s")} watched";
            }
            var shown = user.DisplayName is { Length: > 0 } dn ? $"{dn} (@{user.UserName})" : $"@{user.UserName}";
            return new($"{shown} on SeenIt", summary ?? "Their movies and shows on SeenIt.",
                $"{Site}/api/users/{Uri.EscapeDataString(user.UserName!)}/avatar", false, "profile");
        }
        if (ListRoute().Match(path) is { Success: true } lm && Guid.TryParse(lm.Groups[1].Value, out var listId))
        {
            var list = await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listId && l.IsPublic, ct);
            if (list is null) return null;   // private lists: plain card
            var owner = await db.Users.AsNoTracking().Where(u => u.Id == list.UserId).Select(u => u.DisplayName ?? u.UserName).FirstOrDefaultAsync(ct);
            var items = await db.ListItems.AsNoTracking().Where(i => i.ListId == listId).OrderBy(i => i.Position).Select(i => new { i.Type, i.TmdbId }).ToListAsync(ct);
            string? image = null;
            foreach (var i in items.Take(5))
            {
                var art = i.Type == MediaType.Movie
                    ? await db.Movies.Where(m => m.TmdbId == i.TmdbId).Select(m => m.BackdropPath ?? m.PosterPath).FirstOrDefaultAsync(ct)
                    : await db.Shows.Where(s => s.TmdbId == i.TmdbId).Select(s => s.BackdropPath ?? s.PosterPath).FirstOrDefaultAsync(ct);
                if ((image = Image(art, "w1280")) is not null) break;
            }
            return new(list.Name, Lines($"A list of {items.Count} title{(items.Count == 1 ? "" : "s")}{(owner is null ? "" : $" by {owner}")}", list.Description),
                image, image is not null);
        }
        return null;
    }

    /// <summary>index.html with this page's preview tags (and title) in its head.</summary>
    public string Render(string html, Embed e, string url, bool discord)
    {
        // Only what breaks HTML: titles keep their accents, ★ and · as they are.
        static string A(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        var tags = new StringBuilder();
        void Tag(string attr, string key, string? value) { if (value is { Length: > 0 }) tags.Append($"    <meta {attr}=\"{key}\" content=\"{A(value)}\" />\n"); }
        Tag("property", "og:site_name", "SeenIt");
        Tag("property", "og:type", e.Type);
        Tag("property", "og:title", e.Title);
        Tag("property", "og:description", e.Description);
        Tag("property", "og:url", url);
        Tag("property", "og:image", e.Image);
        Tag("name", "twitter:card", e.LargeImage ? "summary_large_image" : "summary");
        Tag("name", "twitter:title", e.Title);
        Tag("name", "twitter:description", e.Description);
        Tag("name", "twitter:image", e.Image);

        var title = e.Title == "SeenIt" ? "SeenIt" : $"{e.Title} · SeenIt";
        html = html.Replace("<title>SeenIt</title>", $"<title>{A(title)}</title>")
            .Replace("<meta name=\"description\" content=\"SeenIt: track the movies and TV you watch.\" />",
                $"<meta name=\"description\" content=\"{A(e.Description)}\" />");
        // Discord colors the embed's edge with theme-color; browsers use it for their bars, so only for Discord.
        if (discord) html = html.Replace("<meta name=\"theme-color\" content=\"#0e1014\" />", "<meta name=\"theme-color\" content=\"#ec3f4f\" />");
        return html.Replace("</head>", tags + "</head>");
    }

    private static string? Image(string? path, string size) => path is { Length: > 0 } p ? Img + size + p : null;
    private static string? Genres(List<string> g) => g.Count > 0 ? string.Join(", ", g.Take(3)) : null;
    private static string? Runtime(int? m) => m is > 0 and var v ? (v >= 60 ? $"{v / 60}h {v % 60}m" : $"{v}m") : null;
    private static string? Score(double? s) => s is > 0 and var v ? $"★ {v:0.0}" : null;
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    /// <summary>A facts line, then up to ~300 characters of text, cut at a word.</summary>
    private static string Lines(string? first, string? text)
    {
        text = text?.Trim().ReplaceLineEndings(" ");
        if (text is { Length: > 300 }) text = text[..text.LastIndexOf(' ', 297)] + "…";
        return string.Join("\n", new[] { first, text }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    [GeneratedRegex(@"^movie/(\d+)(?:[/?#]|$)")] private static partial Regex MovieRoute();
    [GeneratedRegex(@"^show/(\d+)(?:/season/(\d+)(?:/episode/(\d+))?)?(?:[/?#]|$)")] private static partial Regex ShowRoute();
    [GeneratedRegex(@"^person/(\d+)(?:[/?#]|$)")] private static partial Regex PersonRoute();
    [GeneratedRegex(@"^u/([^/?#]+)(?:[/?#]|$)")] private static partial Regex ProfileRoute();
    [GeneratedRegex(@"^lists/([0-9a-fA-F-]{36})(?:[/?#]|$)")] private static partial Regex ListRoute();
}
