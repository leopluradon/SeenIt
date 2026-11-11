using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Customizable profiles: the owner's widget layout, and the data its widgets show. Each widget belongs to a
/// privacy part (Settings → Privacy); viewers who can't see that part don't get the widget or its data.
/// </summary>
public class ProfileWidgetService(AppDbContext db, SocialService social, TrackingService tracking, MoreStatsService more,
    ReviewService reviews, ListService lists, CatalogService catalog, UserDataCache cache)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<ProfileWidget> Layout(AppUser owner)
    {
        if (owner.ProfileLayoutJson is not { } json) return [.. ProfileWidgets.Default];
        try { return ProfileWidgets.Clean(JsonSerializer.Deserialize<List<ProfileWidget>>(json, Json)); }
        catch (JsonException) { return [.. ProfileWidgets.Default]; }
    }

    /// <summary>The widgets this viewer may see. Lists are public anyway; personal widgets follow the profile's own setting.</summary>
    public static List<ProfileWidget> Visible(AppUser owner, Guid? viewer, bool mutual) =>
        Layout(owner).Where(w => CanSee(owner, viewer, mutual, w.Type)).ToList();

    private static bool CanSee(AppUser owner, Guid? viewer, bool mutual, string type) =>
        ProfileWidgets.Kind(type) is { } k && (k.Type == "lists"
            || (k.Part is { } part ? SocialService.CanSee(owner, viewer, mutual, part) : SocialService.CanView(owner, viewer, mutual)));

    /// <param name="columns">The units the widths are in: 12, or 3 from apps older than 1.159.</param>
    public async Task<List<ProfileWidget>> SaveAsync(Guid userId, IReadOnlyList<ProfileWidget>? widgets, CancellationToken ct, int columns = ProfileWidgets.Columns)
    {
        if (widgets is not null && columns == 3) widgets = widgets.Select(w => w with { W = w.W * 4 }).ToList();
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (widgets is null) { user.ProfileLayoutJson = null; await db.SaveChangesAsync(ct); return [.. ProfileWidgets.Default]; }
        var clean = ProfileWidgets.Clean(widgets);
        // Spotlights show a poster and title: make sure the catalog has them.
        foreach (var m in clean.Select(w => w.Media).OfType<MediaRef>().Distinct())
            _ = m.Type == MediaType.Movie ? await catalog.EnsureMovieAsync(m.TmdbId, ct) is not null : await catalog.EnsureShowAsync(m.TmdbId, ct) is not null;
        user.ProfileLayoutJson = JsonSerializer.Serialize(clean, Json);
        await db.SaveChangesAsync(ct);
        return clean;
    }

    /// <param name="preview">The owner, while editing (or on Home): data for these widgets instead of the saved layout's,
    /// each "type" or "type@range" (e.g. "genres@month").</param>
    public async Task<ProfileWidgetData?> DataAsync(string userName, Guid? viewer, IReadOnlyCollection<string>? preview, CancellationToken ct)
    {
        if (await social.FindAsync(userName) is not { } owner) return null;
        var (following, followsYou) = await social.RelationAsync(viewer, owner.Id, ct);
        var mutual = following && followsYou;
        var wanted = preview is not null && owner.Id == viewer
            ? preview.Select(p => p.Split('@', 2)).Where(p => ProfileWidgets.Kind(p[0]) is not null)
                .Select(p => (Type: p[0], Range: p.Length > 1 ? p[1] : ProfileWidgets.Kind(p[0])!.DefaultRange)).ToList()
            : Visible(owner, viewer, mutual).Select(w => (w.Type, w.Range)).ToList();
        var types = wanted.Select(w => w.Type).ToHashSet();
        // Calendar periods some widget is set to (all-time ones use the plain stats).
        var ranges = wanted.Where(w => ProfileWidgets.Kind(w.Type)?.Ranges is not null && w.Range is not null and not "all")
            .Select(w => w.Range!).ToHashSet();
        var widgets = Layout(owner);
        bool Has(params string[] t) => t.Any(types.Contains);
        var id = owner.Id;

        bool seeStats = SocialService.CanSee(owner, viewer, mutual, ProfilePart.Stats);
        bool AllTime(string type) => wanted.Any(w => w.Type == type && (w.Range ?? "all") == "all");

        StatsDto? stats = null;
        if (Has("months") || new[] { "genres", "weekdays", "rating-chart" }.Any(AllTime))
        {
            stats = await cache.GetAsync(id, "stats:", () => tracking.GetStatsAsync(id, null, ct), ct);
            // Ratings can be shared without the rest of the stats: then only the rating spread goes out.
            if (!seeStats)
                stats = new StatsDto(0, 0, 0, 0, 0, 0, 0, stats.Ratings, stats.AverageRating, [], [], stats.RatingDistribution, []);
        }

        MoreStatsDto? m = Has("release-gap", "heatmap", "weekhour", "milestones")
            ? await cache.GetAsync(id, "more:", () => more.GetAsync(id, null, ct), ct) : null;

        List<ShowProgressDto>? progress = Has("watching", "finished") ? await tracking.GetProgressAsync(id, ct) : null;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ReviewService.Zone(owner.TimeZone)).DateTime);
        Dictionary<string, ProfilePeriod>? periods = null;
        if (ranges.Count > 0)
        {
            periods = [];
            foreach (var range in ranges)
            {
                if (ProfileWidgets.Period(range, today) is not { } p) continue;
                var r = await cache.GetAsync(id, $"review:{p.Year}:{p.Month}:", () => reviews.BuildAsync(id, p.Year, p.Month, null, ct), ct);
                var period = new ProfilePeriod(ProfileWidgets.RangeName(range, today), r.Totals, r.Totals.Ratings,
                    r.Genres.Take(10).ToList(), r.Weekdays, r.RatingDistribution, r.AverageRating,
                    r.LongestStreak, r.BusiestDay, r.MostRewatched, r.TopActors.Take(6).ToList(), r.TopDirectors.Take(6).ToList());
                // Ratings can be shared without the rest of the stats: then only the rating spread goes out.
                if (!seeStats)
                    period = period with { Totals = new PeriodTotals(0, 0, 0, 0, 0, 0, 0, r.Totals.Ratings), Genres = [], Weekdays = [],
                        LongestStreak = new ReviewStreak(0, null, null), BusiestDay = null, MostRewatched = null, TopActors = [], TopDirectors = [] };
                periods[range] = period;
            }
        }

        ProfileRecords? records = null;
        if (preview is null && Has("year-records", "top-people"))
        {
            var year = DateTime.UtcNow.Year;
            var r = await cache.GetAsync(id, $"review:{year}:", () => reviews.BuildAsync(id, year, null, null, ct), ct);
            records = new ProfileRecords(year, r.LongestStreak, r.BusiestDay, r.MostRewatched,
                r.TopActors.Take(6).ToList(), r.TopDirectors.Take(6).ToList(), r.Totals.Plays, r.Totals.Minutes);
        }

        Dictionary<string, MediaSummary>? spotlights = null;
        if (Has("spotlight"))
        {
            var refs = widgets.Where(w => w.Type == "spotlight").Select(w => w.Media).OfType<MediaRef>().Distinct().ToList();
            var (movies, shows) = await tracking.LoadMetaAsync(refs.Select(r => (r.Type, r.TmdbId)), ct);
            spotlights = [];
            foreach (var r in refs)
            {
                MediaSummary? s = r.Type == MediaType.Movie
                    ? movies.TryGetValue(r.TmdbId, out var mm) ? CatalogService.ToSummary(mm) : null
                    : shows.TryGetValue(r.TmdbId, out var sm) ? CatalogService.ToSummary(sm) : null;
                if (s is not null) spotlights[Key(r)] = s;
            }
        }

        Dictionary<Guid, ListDetail>? pinned = null;
        if (Has("pinned-list"))
        {
            pinned = [];
            foreach (var listId in widgets.Where(w => w.Type == "pinned-list").Select(w => w.ListId).OfType<Guid>().Distinct())
                // Only the owner's own lists, and only ones this viewer could open anyway.
                if (await lists.GetAsync(listId, viewer, ct) is { } l && string.Equals(l.OwnerUserName, owner.UserName, StringComparison.OrdinalIgnoreCase))
                    pinned[listId] = l with { Items = l.Items.Take(12).ToList() };
        }

        return new ProfileWidgetData(
            stats,
            Has("release-gap") ? m?.ReleaseGap : null,
            Has("heatmap") ? m?.Heatmap : null,
            Has("weekhour") ? m?.WeekHour : null,
            Has("milestones") ? m?.Milestones : null,
            Has("watching") ? progress?.Where(p => !p.Completed && p.Watched > 0 && p.Status == ShowTrackStatus.Watching)
                .OrderByDescending(p => p.LastWatchedAt).Take(8).ToList() : null,
            Has("finished") ? progress?.Where(p => p.Completed).OrderByDescending(p => p.LastWatchedAt).Take(8).ToList() : null,
            records, spotlights, pinned, periods);
    }

    public static string Key(MediaRef r) => $"{(r.Type == MediaType.Movie ? "movie" : "show")}:{r.TmdbId}";
}
