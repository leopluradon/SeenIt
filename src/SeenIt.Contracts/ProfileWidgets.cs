namespace SeenIt.Contracts;

/// <summary>
/// One tile on a customizable profile. Order in the layout is reading order; W is its width in twelfths of the
/// row (3 = a quarter, 6 = half, 12 = full; see ProfileWidgets.Widths) and H how many rows it spans. Text, Media and ListId configure the widgets that need it.
/// Title replaces the usual heading; Range is the period stats widgets cover (ProfileWidgets.Ranges).
/// </summary>
public record ProfileWidget(string Id, string Type, int W, int H, string? Text = null, MediaRef? Media = null, Guid? ListId = null,
    string? Title = null, string? Range = null);

public record MediaRef(MediaType Type, int TmdbId);

/// <param name="Columns">The units W is in: 12 now; apps before 1.159 sent thirds (and leave this out).</param>
public record ProfileLayoutRequest(IReadOnlyList<ProfileWidget> Widgets, int Columns = 3);

/// <summary>A kind of widget: what it's called, how big it starts, and which privacy part it belongs to (null: always visible).</summary>
public record WidgetKind(string Type, string Name, string Group, string Description, int W, int H, ProfilePart? Part, bool Multiple = false,
    string[]? Ranges = null)
{
    /// <summary>The period it covers unless you pick another: the first of its ranges.</summary>
    public string? DefaultRange => Ranges?[0];
}

public static class ProfileWidgets
{
    public const int Columns = 12, MaxRows = 4, MaxWidgets = 30, MaxText = 600;
    /// <summary>The widths a widget can have, in twelfths: a quarter, a third, half, two thirds, three quarters, full.</summary>
    public static readonly int[] Widths = [3, 4, 6, 8, 9, 12];

    public static int SnapWidth(int w) => Widths.MinBy(x => Math.Abs(x - w));

    public static string WidthName(int w) => w switch { 3 => "¼", 4 => "⅓", 6 => "½", 8 => "⅔", 9 => "¾", _ => "Full" };

    /// <summary>Periods a stats widget can cover. "all" is everything ever; the others are calendar periods.
    /// Declared before Kinds, which uses them (static fields initialize in order).</summary>
    public static readonly string[] AllRanges = ["all", "year", "month", "last-year"];
    public static readonly string[] PeriodRanges = ["year", "month", "last-year"];

    public static readonly WidgetKind[] Kinds =
    [
        new("now", "Watching now", "Basics", "What's playing right now, when something is.", 12, 1, ProfilePart.WatchingNow),
        new("tiles", "Totals", "Basics", "Time watched, movies, episodes and ratings.", 12, 1, ProfilePart.Stats, Ranges: ProfileWidgets.AllRanges),
        new("recent", "Recently watched", "Basics", "The latest movies and episodes.", 12, 2, ProfilePart.History),
        new("fav-movies", "Favorite movies", "Basics", "Your four favorite movies.", 12, 2, ProfilePart.Favorites),
        new("fav-shows", "Favorite shows", "Basics", "Your four favorite shows.", 12, 2, ProfilePart.Favorites),
        new("top-rated", "Top rated", "Basics", "Your highest ratings.", 12, 2, ProfilePart.Ratings),
        new("lists", "Lists", "Basics", "Your public lists.", 12, 2, null),

        new("genres", "Top genres", "Charts", "Plays by genre.", 4, 2, ProfilePart.Stats, Ranges: ProfileWidgets.AllRanges),
        new("months", "Plays per month", "Charts", "The last 12 months.", 4, 2, ProfilePart.Stats),
        new("rating-chart", "Ratings", "Charts", "How your ratings are spread out.", 4, 2, ProfilePart.Ratings, Ranges: ProfileWidgets.AllRanges),
        new("weekdays", "Day of week", "Charts", "Plays by day of the week.", 4, 2, ProfilePart.Stats, Ranges: ProfileWidgets.AllRanges),
        new("release-gap", "How soon after release", "Charts", "How long after release you watch things.", 4, 2, ProfilePart.Stats),
        new("heatmap", "Every day this year", "Charts", "A year of watching, day by day.", 12, 1, ProfilePart.Stats),
        new("weekhour", "When you watch", "Charts", "Plays by weekday and hour.", 8, 2, ProfilePart.Stats),

        new("watching", "Shows in progress", "Shows", "Shows you're watching, with progress.", 8, 2, ProfilePart.History),
        new("finished", "Recently finished", "Shows", "Shows you've finished or caught up on.", 4, 2, ProfilePart.History),

        new("milestones", "Milestones", "Records", "Your 100th movie, 1,000th episode and more.", 8, 2, ProfilePart.Stats),
        new("year-records", "Records", "Records", "Longest streak, busiest day, most rewatched.", 4, 2, ProfilePart.Stats, Ranges: ProfileWidgets.PeriodRanges),
        new("top-people", "Top actors & directors", "Records", "Who you've watched most.", 8, 2, ProfilePart.Stats, Ranges: ProfileWidgets.PeriodRanges),

        new("bio", "About me", "Personal", "A few lines about you.", 12, 1, null, Multiple: true),
        new("spotlight", "Spotlight", "Personal", "One movie or show you want to show off.", 4, 2, null, Multiple: true),
        new("pinned-list", "Pinned list", "Personal", "One of your public lists, front and center.", 8, 2, null, Multiple: true),
    ];

    public static WidgetKind? Kind(string type) => Array.Find(Kinds, k => k.Type == type);

    public static string RangeName(string? range, DateOnly today) => range switch
    {
        "year" => today.Year.ToString(),
        "month" => today.ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
        "last-year" => (today.Year - 1).ToString(),
        _ => "All time",
    };

    /// <summary>The calendar period a range means: (year, month or null for the whole year); null for all time.</summary>
    public static (int Year, int? Month)? Period(string? range, DateOnly today) => range switch
    {
        "year" => (today.Year, null),
        "month" => (today.Year, today.Month),
        "last-year" => (today.Year - 1, null),
        _ => null,
    };

    /// <summary>How profiles looked before they were customizable.</summary>
    public static IReadOnlyList<ProfileWidget> Default { get; } =
        new[] { "now", "tiles", "recent", "fav-movies", "fav-shows", "top-rated", "lists" }
            .Select(t => Kind(t)!).Select(k => new ProfileWidget(k.Type, k.Type, k.W, k.H)).ToList();

    /// <summary>Drops unknown types and duplicates of single-use widgets, clamps sizes and text.</summary>
    public static List<ProfileWidget> Clean(IEnumerable<ProfileWidget>? widgets)
    {
        var seen = new HashSet<string>();
        var ids = new HashSet<string>();
        var result = new List<ProfileWidget>();
        foreach (var w in widgets ?? [])
        {
            if (Kind(w.Type) is not { } k || (!k.Multiple && !seen.Add(k.Type))) continue;
            var id = string.IsNullOrWhiteSpace(w.Id) || w.Id.Length > 40 || !ids.Add(w.Id) ? Guid.NewGuid().ToString("N")[..10] : w.Id;
            ids.Add(id);
            var text = w.Text?.Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, MaxText)] : null;
            var title = w.Title?.Trim() is { Length: > 0 } h ? h[..Math.Min(h.Length, 60)] : null;
            // Spotlights and pinned lists used to keep their heading in Text.
            if (k.Type is "spotlight" or "pinned-list" && text is not null) { title ??= text.Length <= 60 ? text : null; text = null; }
            if (k.Type != "bio") text = null;
            var range = k.Ranges?.Contains(w.Range) == true ? w.Range : k.DefaultRange;
            result.Add(new ProfileWidget(id, k.Type, SnapWidth(w.W), Math.Clamp(w.H, 1, MaxRows), text,
                k.Type == "spotlight" && w.Media is { Type: MediaType.Movie or MediaType.Show } m ? m : null,
                k.Type == "pinned-list" ? w.ListId : null, title, range));
            if (result.Count == MaxWidgets) break;
        }
        return result;
    }
}

/// <summary>What the widgets on a profile show. Only what the layout uses (and the viewer may see) is filled in.</summary>
public record ProfileWidgetData(
    StatsDto? Stats = null,
    ReleaseGapStats? ReleaseGap = null,
    IReadOnlyList<HeatmapDay>? Heatmap = null,
    IReadOnlyList<IReadOnlyList<int>>? WeekHour = null,
    IReadOnlyList<Milestone>? Milestones = null,
    IReadOnlyList<ShowProgressDto>? Watching = null,
    IReadOnlyList<ShowProgressDto>? Finished = null,
    ProfileRecords? Records = null,                                   // this year (apps before 1.159; now in Periods)
    IReadOnlyDictionary<string, MediaSummary>? Spotlights = null,   // by "movie:123" / "show:456"
    IReadOnlyDictionary<Guid, ListDetail>? Lists = null,
    IReadOnlyDictionary<string, ProfilePeriod>? Periods = null);       // by range: "year", "month", "last-year"

/// <summary>A calendar period's stats, for widgets set to a year or month instead of all time.</summary>
public record ProfilePeriod(
    string Label, PeriodTotals Totals, int Ratings,
    IReadOnlyList<CountBucket> Genres, IReadOnlyList<CountBucket> Weekdays, IReadOnlyList<CountBucket> RatingDistribution, double? AverageRating,
    ReviewStreak LongestStreak, ReviewDay? BusiestDay, ReviewItem? MostRewatched,
    IReadOnlyList<ReviewPerson> TopActors, IReadOnlyList<ReviewPerson> TopDirectors);

/// <summary>Highlights of the current year, from the year in review.</summary>
public record ProfileRecords(
    int Year, ReviewStreak LongestStreak, ReviewDay? BusiestDay, ReviewItem? MostRewatched,
    IReadOnlyList<ReviewPerson> TopActors, IReadOnlyList<ReviewPerson> TopDirectors, int Plays, long Minutes);
