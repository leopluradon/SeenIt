namespace SeenIt.Contracts;

/// <summary>
/// One tile on a customizable profile. Order in the layout is reading order; W and H are how many grid
/// columns (of 3) and rows it spans. Text, Media and ListId configure the widgets that need it.
/// </summary>
public record ProfileWidget(string Id, string Type, int W, int H, string? Text = null, MediaRef? Media = null, Guid? ListId = null);

public record MediaRef(MediaType Type, int TmdbId);

public record ProfileLayoutRequest(IReadOnlyList<ProfileWidget> Widgets);

/// <summary>A kind of widget: what it's called, how big it starts, and which privacy part it belongs to (null: always visible).</summary>
public record WidgetKind(string Type, string Name, string Group, string Description, int W, int H, ProfilePart? Part, bool Multiple = false);

public static class ProfileWidgets
{
    public const int Columns = 3, MaxRows = 4, MaxWidgets = 30, MaxText = 600;

    public static readonly WidgetKind[] Kinds =
    [
        new("now", "Watching now", "Basics", "What's playing right now, when something is.", 3, 1, ProfilePart.WatchingNow),
        new("tiles", "Totals", "Basics", "Time watched, movies, episodes and ratings.", 3, 1, ProfilePart.Stats),
        new("recent", "Recently watched", "Basics", "The latest movies and episodes.", 3, 2, ProfilePart.History),
        new("fav-movies", "Favorite movies", "Basics", "Your four favorite movies.", 3, 2, ProfilePart.Favorites),
        new("fav-shows", "Favorite shows", "Basics", "Your four favorite shows.", 3, 2, ProfilePart.Favorites),
        new("top-rated", "Top rated", "Basics", "Your highest ratings.", 3, 2, ProfilePart.Ratings),
        new("lists", "Lists", "Basics", "Your public lists.", 3, 2, null),

        new("genres", "Top genres", "Charts", "Plays by genre.", 1, 2, ProfilePart.Stats),
        new("months", "Plays per month", "Charts", "The last 12 months.", 1, 2, ProfilePart.Stats),
        new("rating-chart", "Ratings", "Charts", "How your ratings are spread out.", 1, 2, ProfilePart.Ratings),
        new("weekdays", "Day of week", "Charts", "Plays by day of the week.", 1, 2, ProfilePart.Stats),
        new("release-gap", "How soon after release", "Charts", "How long after release you watch things.", 1, 2, ProfilePart.Stats),
        new("heatmap", "Every day this year", "Charts", "A year of watching, day by day.", 3, 1, ProfilePart.Stats),
        new("weekhour", "When you watch", "Charts", "Plays by weekday and hour.", 2, 2, ProfilePart.Stats),

        new("watching", "Shows in progress", "Shows", "Shows you're watching, with progress.", 2, 2, ProfilePart.History),
        new("finished", "Recently finished", "Shows", "Shows you've finished or caught up on.", 1, 2, ProfilePart.History),

        new("milestones", "Milestones", "Records", "Your 100th movie, 1,000th episode and more.", 2, 2, ProfilePart.Stats),
        new("year-records", "This year's records", "Records", "Longest streak, busiest day, most rewatched.", 1, 2, ProfilePart.Stats),
        new("top-people", "Top actors & directors", "Records", "Who you've watched most this year.", 2, 2, ProfilePart.Stats),

        new("bio", "About me", "Personal", "A few lines about you.", 3, 1, null, Multiple: true),
        new("spotlight", "Spotlight", "Personal", "One movie or show you want to show off.", 1, 2, null, Multiple: true),
        new("pinned-list", "Pinned list", "Personal", "One of your public lists, front and center.", 2, 2, null, Multiple: true),
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
            result.Add(new ProfileWidget(id, k.Type, Math.Clamp(w.W, 1, Columns), Math.Clamp(w.H, 1, MaxRows), text,
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
