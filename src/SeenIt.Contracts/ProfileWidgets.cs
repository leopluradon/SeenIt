namespace SeenIt.Contracts;

/// <summary>
/// One tile on a customizable profile. Order in the layout is reading order; W is its width in twelfths of the
/// row (3 = a quarter, 6 = half, 12 = full; see ProfileWidgets.Widths) and H how many rows it spans. Text, Media and ListId configure the widgets that need it.
/// </summary>
public record ProfileWidget(string Id, string Type, int W, int H, string? Text = null, MediaRef? Media = null, Guid? ListId = null);

public record MediaRef(MediaType Type, int TmdbId);

/// <param name="Columns">The units W is in: 12 now; apps before 1.159 sent thirds (and leave this out).</param>
public record ProfileLayoutRequest(IReadOnlyList<ProfileWidget> Widgets, int Columns = 3);

/// <summary>A kind of widget: what it's called, how big it starts, and which privacy part it belongs to (null: always visible).</summary>
public record WidgetKind(string Type, string Name, string Group, string Description, int W, int H, ProfilePart? Part, bool Multiple = false);

public static class ProfileWidgets
{
    public const int Columns = 12, MaxRows = 4, MaxWidgets = 30, MaxText = 600;
    /// <summary>The widths a widget can have, in twelfths: a quarter, a third, half, two thirds, three quarters, full.</summary>
    public static readonly int[] Widths = [3, 4, 6, 8, 9, 12];

    public static int SnapWidth(int w) => Widths.MinBy(x => Math.Abs(x - w));

    public static string WidthName(int w) => w switch { 3 => "¼", 4 => "⅓", 6 => "½", 8 => "⅔", 9 => "¾", _ => "Full" };

    public static readonly WidgetKind[] Kinds =
    [
        new("now", "Watching now", "Basics", "What's playing right now, when something is.", 12, 1, ProfilePart.WatchingNow),
        new("tiles", "Totals", "Basics", "Time watched, movies, episodes and ratings.", 12, 1, ProfilePart.Stats),
        new("recent", "Recently watched", "Basics", "The latest movies and episodes.", 12, 2, ProfilePart.History),
        new("fav-movies", "Favorite movies", "Basics", "Your four favorite movies.", 12, 2, ProfilePart.Favorites),
        new("fav-shows", "Favorite shows", "Basics", "Your four favorite shows.", 12, 2, ProfilePart.Favorites),
        new("top-rated", "Top rated", "Basics", "Your highest ratings.", 12, 2, ProfilePart.Ratings),
        new("lists", "Lists", "Basics", "Your public lists.", 12, 2, null),

        new("genres", "Top genres", "Charts", "Plays by genre.", 4, 2, ProfilePart.Stats),
        new("months", "Plays per month", "Charts", "The last 12 months.", 4, 2, ProfilePart.Stats),
        new("rating-chart", "Ratings", "Charts", "How your ratings are spread out.", 4, 2, ProfilePart.Ratings),
        new("weekdays", "Day of week", "Charts", "Plays by day of the week.", 4, 2, ProfilePart.Stats),
        new("release-gap", "How soon after release", "Charts", "How long after release you watch things.", 4, 2, ProfilePart.Stats),
        new("heatmap", "Every day this year", "Charts", "A year of watching, day by day.", 12, 1, ProfilePart.Stats),
        new("weekhour", "When you watch", "Charts", "Plays by weekday and hour.", 8, 2, ProfilePart.Stats),

        new("watching", "Shows in progress", "Shows", "Shows you're watching, with progress.", 8, 2, ProfilePart.History),
        new("finished", "Recently finished", "Shows", "Shows you've finished or caught up on.", 4, 2, ProfilePart.History),

        new("milestones", "Milestones", "Records", "Your 100th movie, 1,000th episode and more.", 8, 2, ProfilePart.Stats),
        new("year-records", "This year's records", "Records", "Longest streak, busiest day, most rewatched.", 4, 2, ProfilePart.Stats),
        new("top-people", "Top actors & directors", "Records", "Who you've watched most this year.", 8, 2, ProfilePart.Stats),

        new("bio", "About me", "Personal", "A few lines about you.", 12, 1, null, Multiple: true),
        new("spotlight", "Spotlight", "Personal", "One movie or show you want to show off.", 4, 2, null, Multiple: true),
        new("pinned-list", "Pinned list", "Personal", "One of your public lists, front and center.", 8, 2, null, Multiple: true),
    ];

    public static WidgetKind? Kind(string type) => Array.Find(Kinds, k => k.Type == type);

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
            result.Add(new ProfileWidget(id, k.Type, SnapWidth(w.W), Math.Clamp(w.H, 1, MaxRows), text,
                k.Type == "spotlight" && w.Media is { Type: MediaType.Movie or MediaType.Show } m ? m : null,
                k.Type == "pinned-list" ? w.ListId : null));
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
    ProfileRecords? Records = null,
    IReadOnlyDictionary<string, MediaSummary>? Spotlights = null,   // by "movie:123" / "show:456"
    IReadOnlyDictionary<Guid, ListDetail>? Lists = null);

/// <summary>Highlights of the current year, from the year in review.</summary>
public record ProfileRecords(
    int Year, ReviewStreak LongestStreak, ReviewDay? BusiestDay, ReviewItem? MostRewatched,
    IReadOnlyList<ReviewPerson> TopActors, IReadOnlyList<ReviewPerson> TopDirectors, int Plays, long Minutes);
