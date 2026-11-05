using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SkiaSharp;

namespace SeenIt.Server.Services;

/// <summary>
/// Draws shareable images (a month/year in review, a comparison) as PNGs in the user's theme, for
/// posting elsewhere. Drawn on the server so the website and the app get the same picture.
/// </summary>
public class ShareCardService(AppDbContext db, ReviewService reviews, SocialService social, GoalService goals,
    IHttpClientFactory http, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, ILogger<ShareCardService> log)
{
    private const int W = 1080;
    private const float Pad = 72;
    private const string ImageBase = "https://image.tmdb.org/t/p/w342";

    private static readonly Lazy<(SKTypeface Regular, SKTypeface Semi, SKTypeface Bold)> Fonts = new(() =>
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        SKTypeface Load(string f) => SKTypeface.FromFile(Path.Combine(dir, f)) ?? SKTypeface.Default;
        return (Load("Inter-Regular.ttf"), Load("Inter-SemiBold.ttf"), Load("Inter-ExtraBold.ttf"));
    });

    private sealed record Palette(SKColor Bg, SKColor Surface, SKColor Accent, SKColor Text, SKColor Muted);

    private async Task<Palette> PaletteAsync(Guid userId, CancellationToken ct)
    {
        var key = await db.Users.Where(u => u.Id == userId).Select(u => u.Theme).FirstOrDefaultAsync(ct);
        var t = UiPrefs.Themes.FirstOrDefault(x => x.Key == key);
        if (t.Key is null) t = UiPrefs.Themes[0];
        return new Palette(SKColor.Parse(t.Bg), SKColor.Parse(t.Surface), SKColor.Parse(t.Accent), new SKColor(0xF2, 0xF3, 0xF5), new SKColor(0x9A, 0xA1, 0xAD));
    }

    // ---------- Month / year in review (1080×1920, story-sized) ----------

    public async Task<byte[]> ReviewAsync(Guid userId, int year, int? month, string? tz, CancellationToken ct)
    {
        var r = await reviews.BuildAsync(userId, year, month, tz, ct);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.UserName, u.DisplayName, u.FiveStarRatings }).FirstAsync(ct);
        string? Caption(ReviewItem i) => i.Rating is { } r ? $"★ {Stars(r, user.FiveStarRatings)}" : i.Type == MediaType.Show ? i.Detail : null;
        var pal = await PaletteAsync(userId, ct);
        var goalLines = month is null ? await GoalLinesAsync(userId, year, tz, ct) : [];

        var shows = r.TopShows.Take(5).ToList();
        var movies = r.TopMovies.Take(5).ToList();
        var posters = await PostersAsync(shows.Select(s => s.PosterPath).Concat(movies.Select(m => m.PosterPath)), ct);

        const int H = 1920;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        Background(c, pal, H);

        var y = Pad + 20;
        Brand(c, pal, ref y, "@" + user.UserName);

        y += month is null ? 185 : 140;
        Text(c, r.Label, Pad, y, Fonts.Value.Bold, month is null ? 170 : 104, pal.Text);
        y += 70;
        Text(c, month is null ? "Year in review" : "Month in review", Pad, y, Fonts.Value.Semi, 46, pal.Accent);

        y += 60;
        var t = r.Totals;
        var hours = t.Minutes / 60.0;
        Tiles(c, pal, ref y,
        [
            (hours >= 100 ? hours.ToString("N0") : hours.ToString("0.#"), hours == 1 ? "hour watched" : "hours watched"),
            (r.DaysActive.ToString("N0"), r.DaysActive == 1 ? "day with a play" : "days with a play"),
            (t.Movies.ToString("N0"), t.Movies == 1 ? "movie" : "movies"),
            (t.Episodes.ToString("N0"), $"episode{(t.Episodes == 1 ? "" : "s")} of {t.Shows:N0} show{(t.Shows == 1 ? "" : "s")}"),
        ]);

        if (shows.Count > 0) { y += 70; PosterRow(c, pal, ref y, "Top shows", shows.Select(s => (s.Title, Poster(posters, s.PosterPath), Caption(s))).ToList()); }
        if (movies.Count > 0) { y += 70; PosterRow(c, pal, ref y, "Top movies", movies.Select(m => (m.Title, Poster(posters, m.PosterPath), Caption(m))).ToList()); }

        // A few more lines, as many as fit above the footer.
        var facts = new List<string>();
        if (r.Genres.FirstOrDefault() is { } g) facts.Add($"Top genre: {g.Label}");
        if (r.LongestStreak.Days > 1) facts.Add($"Longest streak: {r.LongestStreak.Days} days in a row");
        if (r.TopActors.FirstOrDefault() is { } actor) facts.Add($"Most-watched actor: {actor.Name}");
        if (r.AverageRating is { } avg) facts.Add($"Average rating: {avg:0.0} / 10");
        facts.AddRange(goalLines);
        y += 80;
        foreach (var f in facts)
        {
            if (y > H - 200) break;
            Dot(c, pal, Pad + 8, y - 13);
            Text(c, Fit(f, Fonts.Value.Semi, 36, W - Pad * 2 - 40), Pad + 36, y, Fonts.Value.Semi, 36, pal.Text);
            y += 58;
        }
        if (t.Plays == 0) Text(c, "Nothing watched yet.", Pad, y, Fonts.Value.Semi, 40, pal.Muted);

        Footer(c, pal, H);
        return Encode(surface, posters);
    }

    /// <summary>A 1–10 rating as the viewer shows them: "8", or "4.5" in stars.</summary>
    private static string Stars(int r, bool five) => five ? (r / 2.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : r.ToString();

    private async Task<List<string>> GoalLinesAsync(Guid userId, int year, string? tz, CancellationToken ct)
    {
        try
        {
            return (await goals.GetAsync(userId, year, tz, ct))
                .Select(g => $"Goal: {Num(g.Current)} of {g.Target:N0} {GoalKinds.Unit(g.Kind, g.Target)}{(g.Current >= g.Target ? " ✓" : "")}")
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Goals for share card failed"); return []; }
    }

    private static string Num(double n) => n % 1 == 0 ? n.ToString("N0") : n.ToString("N1");

    // ---------- Comparison (1080×1350) ----------

    public async Task<byte[]?> CompareAsync(Guid viewerId, string a, string b, CancellationToken ct)
    {
        var cmp = await social.CompareAsync(viewerId, a, b, ct);
        if (cmp is null) return null;
        var pal = await PaletteAsync(viewerId, ct);
        var five = await db.Users.Where(u => u.Id == viewerId).Select(u => u.FiveStarRatings).FirstOrDefaultAsync(ct);

        var both = cmp.RatedBoth.Where(x => x.A >= 8 && x.B >= 8).OrderByDescending(x => x.A + x.B).Take(5).ToList();
        var rowLabel = "You both loved";
        if (both.Count < 3) { both = cmp.WatchedBoth.Take(5).ToList(); rowLabel = "You've both seen"; }
        var posters = await PostersAsync(both.Select(x => x.Media.PosterPath), ct);

        const int H = 1350;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        Background(c, pal, H);

        var y = Pad + 20;
        Brand(c, pal, ref y, "Taste match");

        y += 120;
        var names = $"@{cmp.A.UserName}  &  @{cmp.B.UserName}";
        var size = 64f;
        while (size > 36 && Measure(names, Fonts.Value.Bold, size) > W - Pad * 2) size -= 2;
        Text(c, names, Pad, y, Fonts.Value.Bold, size, pal.Text);

        y += 190;
        if (cmp.Compatibility is { } pct)
        {
            Text(c, $"{pct}%", Pad, y, Fonts.Value.Bold, 200, pal.Accent);
            Text(c, "taste match", Pad + Measure($"{pct}%", Fonts.Value.Bold, 200) + 28, y - 18, Fonts.Value.Semi, 44, pal.Muted);
        }
        else
        {
            y -= 80;
            Text(c, "Not enough shared ratings for a match yet", Pad, y, Fonts.Value.Semi, 40, pal.Muted);
        }

        y += 50;
        Tiles(c, pal, ref y,
        [
            (cmp.SharedMovies.ToString("N0"), cmp.SharedMovies == 1 ? "movie in common" : "movies in common"),
            (cmp.SharedShows.ToString("N0"), cmp.SharedShows == 1 ? "show in common" : "shows in common"),
        ]);

        if (both.Count > 0)
        {
            y += 70;
            PosterRow(c, pal, ref y, rowLabel, both.Select(x => (x.Media.Title, Poster(posters, x.Media.PosterPath),
                x.A is { } ra && x.B is { } rb ? $"★{Stars(ra, five)} · ★{Stars(rb, five)}" : null)).ToList());
        }

        Footer(c, pal, H);
        return Encode(surface, posters);
    }

    // ---------- Drawing ----------

    private static void Background(SKCanvas c, Palette pal, int h)
    {
        c.Clear(pal.Bg);
        // A soft glow of the accent color in the top corner.
        using var glow = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(W * 0.9f, 0), W * 0.95f,
                [pal.Accent.WithAlpha(70), pal.Accent.WithAlpha(0)], SKShaderTileMode.Clamp),
        };
        c.DrawRect(0, 0, W, h, glow);
    }

    private static void Brand(SKCanvas c, Palette pal, ref float y, string right)
    {
        Dot(c, pal, Pad + 14, y - 14, 14);
        Text(c, "SeenIt", Pad + 40, y, Fonts.Value.Bold, 40, pal.Text);
        var size = 34f;
        var max = W - Pad * 2 - 260;
        Text(c, Fit(right, Fonts.Value.Semi, size, max), W - Pad, y, Fonts.Value.Semi, size, pal.Muted, SKTextAlign.Right);
    }

    private static void Footer(SKCanvas c, Palette pal, int h) =>
        Text(c, "seenit.vohiyo.com", W / 2f, h - Pad + 10, Fonts.Value.Semi, 30, pal.Muted, SKTextAlign.Center);

    private static void Dot(SKCanvas c, Palette pal, float x, float y, float r = 9)
    {
        using var p = new SKPaint { IsAntialias = true, Color = pal.Accent };
        c.DrawCircle(x, y, r, p);
    }

    /// <summary>Big-number tiles, two per row.</summary>
    private static void Tiles(SKCanvas c, Palette pal, ref float y, (string Value, string Label)[] tiles)
    {
        const float gap = 24, tileH = 200;
        var tileW = (W - Pad * 2 - gap) / 2;
        using var bg = new SKPaint { IsAntialias = true, Color = pal.Surface };
        for (var i = 0; i < tiles.Length; i++)
        {
            var x = Pad + i % 2 * (tileW + gap);
            var top = y + i / 2 * (tileH + gap);
            c.DrawRoundRect(new SKRoundRect(new SKRect(x, top, x + tileW, top + tileH), 28), bg);
            var size = 92f;
            while (size > 48 && Measure(tiles[i].Value, Fonts.Value.Bold, size) > tileW - 64) size -= 4;
            Text(c, tiles[i].Value, x + 32, top + 112, Fonts.Value.Bold, size, pal.Text);
            Text(c, Fit(tiles[i].Label, Fonts.Value.Semi, 30, tileW - 64), x + 32, top + 162, Fonts.Value.Semi, 30, pal.Muted);
        }
        y += (tiles.Length + 1) / 2 * (tileH + gap) - gap;
    }

    private static void PosterRow(SKCanvas c, Palette pal, ref float y, string label, List<(string Title, SKImage? Image, string? Caption)> items)
    {
        Text(c, label.ToUpperInvariant(), Pad, y, Fonts.Value.Semi, 28, pal.Muted);
        y += 28;
        const float gap = 18;
        var pw = (W - Pad * 2 - gap * 4) / 5;
        var ph = pw * 1.5f;
        using var placeholder = new SKPaint { IsAntialias = true, Color = pal.Surface };
        for (var i = 0; i < items.Count; i++)
        {
            var x = Pad + i * (pw + gap);
            var rect = new SKRect(x, y, x + pw, y + ph);
            var rr = new SKRoundRect(rect, 16);
            c.DrawRoundRect(rr, placeholder);
            if (items[i].Image is { } img)
            {
                c.Save();
                c.ClipRoundRect(rr, antialias: true);
                c.DrawImage(img, rect, new SKSamplingOptions(SKCubicResampler.Mitchell));
                c.Restore();
            }
            else
            {
                // No poster: the title, wrapped, on the tile.
                var lines = Wrap(items[i].Title, Fonts.Value.Semi, 24, pw - 24).Take(4).ToList();
                for (var l = 0; l < lines.Count; l++) Text(c, lines[l], x + 12, y + 40 + l * 30, Fonts.Value.Semi, 24, pal.Text);
            }
            if (items[i].Caption is { } cap)
                Text(c, Fit(cap, Fonts.Value.Semi, 26, pw), x + pw / 2, y + ph + 40, Fonts.Value.Semi, 26, pal.Text, SKTextAlign.Center);
        }
        y += ph + (items.Any(i => i.Caption is not null) ? 50 : 0);
    }

    private static void Text(SKCanvas c, string text, float x, float y, SKTypeface face, float size, SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        using var font = new SKFont(face, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        c.DrawText(text, x, y, align, font, paint);
    }

    private static float Measure(string text, SKTypeface face, float size)
    {
        using var font = new SKFont(face, size);
        return font.MeasureText(text);
    }

    /// <summary>Shortens with an ellipsis to fit the width.</summary>
    private static string Fit(string text, SKTypeface face, float size, float max)
    {
        if (Measure(text, face, size) <= max) return text;
        for (var n = text.Length - 1; n > 0; n--)
            if (Measure(text[..n].TrimEnd() + "…", face, size) <= max) return text[..n].TrimEnd() + "…";
        return "…";
    }

    private static IEnumerable<string> Wrap(string text, SKTypeface face, float size, float max)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (Measure(next, face, size) <= max || line.Length == 0) line = next;
            else { yield return Fit(line, face, size, max); line = word; }
        }
        if (line.Length > 0) yield return Fit(line, face, size, max);
    }

    private static byte[] Encode(SKSurface surface, Dictionary<string, SKImage> posters)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        foreach (var p in posters.Values) p.Dispose();
        return data.ToArray();
    }

    // ---------- Posters ----------

    private static SKImage? Poster(Dictionary<string, SKImage> posters, string? path) => path is not null && posters.TryGetValue(path, out var img) ? img : null;

    private async Task<Dictionary<string, SKImage>> PostersAsync(IEnumerable<string?> paths, CancellationToken ct)
    {
        var client = http.CreateClient("tmdb-images");
        var unique = paths.OfType<string>().Distinct().ToList();
        var loaded = await Task.WhenAll(unique.Select(async p =>
        {
            try
            {
                // Posters never change at a given path, so keep the downloads for a day.
                var bytes = await Microsoft.Extensions.Caching.Memory.CacheExtensions.GetOrCreateAsync(cache, "poster:" + p, e =>
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
                    return client.GetByteArrayAsync(ImageBase + p, ct);
                });
                return (p, SKImage.FromEncodedData(bytes!));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogInformation("Poster {Path} for a share card failed: {Message}", p, ex.Message);
                return (p, (SKImage?)null);
            }
        }));
        return loaded.Where(x => x.Item2 is not null).ToDictionary(x => x.p, x => x.Item2!);
    }
}
