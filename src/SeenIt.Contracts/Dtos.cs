namespace SeenIt.Contracts;

public enum MediaType { Movie, Show, Episode }

public enum WatchSource { Manual, Jellyfin, TraktImport, LetterboxdImport, Plex, Kodi, ImdbImport, TvTimeImport, SimklImport }

public static class WatchSources
{
    /// <summary>Media servers that scrobble plays as they happen and report "watching now".</summary>
    public static bool IsScrobbler(WatchSource s) => s is WatchSource.Jellyfin or WatchSource.Plex or WatchSource.Kodi;

    /// <summary>Whether a play's time of day is real. Letterboxd only records the day; IMDb plays are dated when rated.</summary>
    public static bool HasTimeOfDay(WatchSource s) => s is not (WatchSource.LetterboxdImport or WatchSource.ImdbImport);

    public static WatchSource ForImport(ImportKind k) => k switch
    {
        ImportKind.Trakt => WatchSource.TraktImport, ImportKind.Letterboxd => WatchSource.LetterboxdImport,
        ImportKind.Imdb => WatchSource.ImdbImport, ImportKind.TvTime => WatchSource.TvTimeImport,
        ImportKind.MediaServer => WatchSource.Jellyfin, _ => WatchSource.SimklImport,
    };

    public static string Name(ImportKind k) => Name(ForImport(k));

    public static string Name(WatchSource s) => s switch
    {
        WatchSource.Jellyfin => "Jellyfin", WatchSource.Plex => "Plex", WatchSource.Kodi => "Kodi",
        WatchSource.TraktImport => "Trakt", WatchSource.LetterboxdImport => "Letterboxd", WatchSource.ImdbImport => "IMDb",
        WatchSource.TvTimeImport => "TV Time", WatchSource.SimklImport => "Simkl",
        _ => "",
    };
}

// ---------- Auth ----------

public record RegisterRequest(string UserName, string Email, string Password, string? InviteCode = null);
public record LoginRequest(string UserNameOrEmail, string Password);
public record RefreshRequest(string RefreshToken);
public record TokenResponse(string AccessToken, string RefreshToken, long ExpiresIn);
public record ProblemInfo(string Title, Dictionary<string, string[]>? Errors = null);

public record MeDto(
    Guid Id, string UserName, string Email, string? DisplayName, DateTimeOffset CreatedAt,
    bool HideSpoilers = true, bool BlurSpoilerImages = true,
    IReadOnlyList<string>? HiddenHomeSections = null, IReadOnlyList<string>? Tabs = null,
    string? Region = null, ProfileVisibility Visibility = ProfileVisibility.Friends,
    DateOnly? TrackingSince = null, bool IsAdmin = false, string? TimeZone = null,
    IReadOnlyList<string>? HomeOrder = null, string? Theme = null, RatingScale RatingScale = RatingScale.Ten,
    TimeFormat TimeFormat = TimeFormat.DaysHours,
    string? Avatar = null,    // profile picture path, null when there's none
    UpNextSort UpNextSort = UpNextSort.LastWatched,
    UserPrefs? Prefs = null);

/// <summary>How long stretches of time read: "48d 7h", "1,159h", "48d 7h 23m" or "1,159h 23m". Under a day all read "5h 12m".</summary>
public enum TimeFormat { DaysHours, Hours, DaysHoursMinutes, HoursMinutes }

/// <summary>How ratings are given and shown. They're stored as 1–10 (to one decimal) whatever the scale.</summary>
public enum RatingScale { Ten, FiveStars, Decimal, Thumbs }

/// <summary>
/// "Liked / didn't like" ratings. They're stored as ordinary 1–10 ratings with values no other rating can have
/// (others are whole or one decimal), so everything that shows a rating can tell a thumb apart and show 👍/👎
/// to everyone, while taste match, recommendations and stats treat them as roughly 8 and 3.
/// </summary>
public static class RatingThumbs
{
    public const double Up = 8.05, Down = 3.05;
    public static bool Is(double rating) => Math.Abs(rating - Up) < 1e-6 || Math.Abs(rating - Down) < 1e-6;
    /// <summary>Liked: a 👍, or for someone who rates in thumbs, a number of 6 or more (imports, older ratings).</summary>
    public static bool Liked(double rating) => Is(rating) ? rating > 5 : rating >= 6;
    public static string Emoji(double rating) => Liked(rating) ? "👍" : "👎";
}

/// <summary>Only the fields that are set are changed.</summary>
public record PreferencesRequest(
    bool? HideSpoilers = null, bool? BlurSpoilerImages = null,
    IReadOnlyList<string>? HiddenHomeSections = null, IReadOnlyList<string>? Tabs = null,
    string? Region = null,          // "" clears it (back to the device's region)
    ProfileVisibility? Visibility = null,
    string? TrackingSince = null,   // yyyy-MM-dd; "" clears it
    string? TimeZone = null,        // IANA id, e.g. America/New_York; "" = follow the device
    IReadOnlyList<string>? HomeOrder = null,    // home section keys, top to bottom
    string? Theme = null,                       // key from UiPrefs.Themes; "" = default
    RatingScale? RatingScale = null,
    TimeFormat? TimeFormat = null,
    UpNextSort? UpNextSort = null,
    UserPrefs? Prefs = null);   // replaces all of them at once

public enum DateStyle { MonthFirst, DayFirst, Iso }
/// <summary>What tapping ✓ / Mark watched does: ask when, or record it straight away.</summary>
public enum WatchDefault { Ask, Now, ReleaseDate }
/// <summary>Whether scores (TMDB, IMDb, friends' ratings...) are blurred until tapped.</summary>
public enum ScoreSpoilers { Off, Unwatched, Always }
/// <summary>What marking something watched does to it being on your watchlist.</summary>
public enum WatchlistOnWatch { Remove, Ask, Keep }
/// <summary>The kinds of alert, each with its own delivery setting (UserPrefs.Alerts). Stored on each alert as a number:
/// add new kinds at the end.</summary>
public enum AlertKind { Episode, Movie, Person, Recommendation, RecWatched, WatchedWith, Poll }
/// <summary>How an alert reaches you: in the alerts list (and bell), as a push notification, both, or not at all.</summary>
public enum AlertDelivery { Both, Alert, Push, Off }
/// <summary>What an open page does when something of yours changes elsewhere (another tab or device, Jellyfin...).</summary>
public enum AutoRefresh { Always, Ask, Never }
public enum ListLayout { Posters, Rows }
/// <summary>How episodes are labeled: S02E05, 2x05, or Season 2, Episode 5.</summary>
public enum EpisodeStyle { Code, Cross, Long }
/// <summary>Filters and sorting saved as a quick chip on a page (UiPrefs.ViewPages), as its query string. Default: the
/// page opens with it when you go to it (not when you come back to it with Back). One default per page.</summary>
public record SavedView(string Page, string Name, string Query, bool Default = false);
public enum PosterSize { Small, Medium, Large }
public enum BackupEvery { Monthly, Weekly }
/// <summary>Which files a backup holds: JSON and CSV, or just one kind.</summary>
public enum ExportFormat { Both, Json, Csv }
/// <summary>Parts of your profile that can have their own audience (null = same as the profile).</summary>
/// <param name="Sources">Where you watched: the Jellyfin/Plex/Letterboxd tag on your plays (and filtering by it).</param>
/// <param name="WatchingApp">Which app you're watching on ("watching on Jellyfin"), separately from what.</param>
/// <summary>History also covers Progress (it's made of your plays); Stats covers month/year in review and goals.</summary>
public enum ProfilePart { History, Ratings, Stats, WatchingNow, Favorites, Sources, WatchingApp, Watchlist, Library, People }

/// <summary>
/// Display, tracking, home and privacy choices, kept together. Defaults reproduce how SeenIt
/// behaved before they existed.
/// </summary>
public record UserPrefs(
    string StartPage = "home",                       // home, progress, calendar, watchlist, discover
    DateStyle DateStyle = DateStyle.MonthFirst,
    bool Clock24 = false,
    bool WeekStartsMonday = false,
    IReadOnlyList<string>? Scores = null,            // other sites' scores to show, in order; null = all, usual order
    bool CountRewatches = true,                      // false: stats count each movie/episode's first play only
    IReadOnlyDictionary<string, int>? HomeCounts = null,   // items per home row, by section key
    int ComingUpDays = 14,
    WatchDefault WatchDefault = WatchDefault.Ask,
    IReadOnlyDictionary<ProfilePart, ProfileVisibility>? PartVisibility = null,
    bool PrivateNotes = true,                        // false: no private-note box on movie and show pages (notes are kept)
    IReadOnlyList<string>? HiddenScores = null,      // "source/kind" pairs turned off, e.g. "imdb/episode", "friends/season"
    ScoreSpoilers ScoreSpoilers = ScoreSpoilers.Off,
    string PosterScoreMovies = "tmdb",               // the score on movie posters: tmdb, imdb, an MDBList source, or none
    string PosterScoreShows = "tmdb",
    bool HideGuestStars = false,                     // spoilers: guest stars of episodes you haven't watched
    WatchlistOnWatch WatchlistOnWatch = WatchlistOnWatch.Remove,
    ListLayout ListLayout = ListLayout.Posters,
    bool Compact = false,                            // tighter spacing everywhere
    PosterSize PosterSize = PosterSize.Medium,
    bool DismissButtons = true,                      // the "not interested" × on posters
    bool ApproveFollowers = false,                   // new followers need your OK
    BackupEvery BackupEvery = BackupEvery.Monthly,
    ExportFormat BackupFormat = ExportFormat.Both,
    string? Accent = null,                           // key from UiPrefs.Accents; null = the theme's own
    EpisodeStyle EpisodeStyle = EpisodeStyle.Code,
    IReadOnlyDictionary<string, ListLayout>? PageLayouts = null,   // posters or rows per page (UiPrefs.LayoutPages); missing = ListLayout
    IReadOnlyDictionary<string, string>? PageSorts = null,         // last sort picked per page (UiPrefs.SortPages)
    IReadOnlyList<SavedView>? SavedViews = null,     // filters saved as quick chips on History, Watchlist and Progress
    MediaRef? ProfileBanner = null,                  // a movie or show whose backdrop sits behind your name on your profile
    string? ProfileBadge = null,                     // older single badge (a milestone label); superseded by ProfileBadges
    AutoRefresh AutoRefresh = AutoRefresh.Always,    // Ask: a "Refresh" bar instead of reloading the page under you
    bool AlertRecWatched = true,                     // superseded by Alerts[RecWatched]; still read when that isn't set
    IReadOnlyDictionary<AlertKind, AlertDelivery>? Alerts = null,   // per kind of alert; missing = Both
    IReadOnlyList<string>? ProfileBadges = null,     // badge ids shown next to your name, in order (up to MaxBadges)
    IReadOnlyList<string>? RateKinds = null,         // what "To rate" on Home offers: ScoreKinds keys; null = DefaultRateKinds
    bool HidePlaySources = false,                    // no "Letterboxd"/"Jellyfin" tag on plays in history and on title pages
    IReadOnlyList<int>? MyServices = null,           // streaming services you have (TMDB provider ids): Where to watch, Pick and the watchlist use them
    bool HideSourcesFromOthers = false,              // older switch; SourcesVisibility/WatchingAppVisibility replace it (it still reads as Only me)
    IReadOnlyList<string>? DesktopNav = null,
    // Audiences for the newer profile parts. Kept out of PartVisibility, whose keys older apps can't read.
    ProfileVisibility? SourcesVisibility = null,
    ProfileVisibility? WatchingAppVisibility = null,
    bool HideWhereToWatch = false,                   // no "Where to watch" section on movie and show pages
    bool BlurEpisodeCounts = false,                  // spoilers: blur how many episodes someone is in, for shows you're partway through
    bool BlurAllEpisodeCounts = false,               // … for every show you haven't finished, started or not (takes over from BlurEpisodeCounts)
    IReadOnlyList<string>? Challenges = null)        // yearly challenges you've taken on (Challenges.All keys), shown on Goals
{
    public static readonly UserPrefs Default = new();
    public static readonly string[] StartPages = ["home", "progress", "calendar", "watchlist", "discover"];
    public const int MaxBadges = 5;
    public static readonly (string Key, string Name)[] ScoreSources =
        [("imdb", "IMDb"), ("tomatoes", "Rotten Tomatoes"), ("popcorn", "RT audience"), ("metacritic", "Metacritic"),
         ("metacriticuser", "Metacritic users"), ("letterboxd", "Letterboxd"), ("trakt", "Trakt"), ("rogerebert", "Roger Ebert"), ("myanimelist", "MyAnimeList")];
    public const int DefaultRowCount = 12;
    /// <summary>Rows of posters scroll sideways and are narrow: 12 of the small ones didn't reach across a wide screen,
    /// leaving a gap where Up next's wider cards run on. 20 fills it whatever the poster size.</summary>
    public const int DefaultPosterRowCount = 20;
    private static readonly HashSet<string> PosterRows = ["recent", "friends", "foryou", "trending-movies", "trending-shows"];
    /// <summary>What a score can be about. A season has its own kind; everything else follows MediaType.</summary>
    public static readonly (string Key, string Name)[] ScoreKinds = [("movie", "Movies"), ("show", "Shows"), ("season", "Seasons"), ("episode", "Episodes")];
    /// <summary>Which kinds SeenIt has this source's score for. TMDB and friends cover everything; IMDb also has episodes; the rest are titles only.</summary>
    public static string[] KindsFor(string source) => source switch
    {
        "tmdb" or "friends" => ["movie", "show", "season", "episode"],
        "imdb" => ["movie", "show", "episode"],
        "trakt" => ["movie", "show", "season", "episode"],
        _ => ["movie", "show"],
    };
    /// <summary>Sources a poster's score badge can show, per kind (Letterboxd has movies only, MyAnimeList shows only).</summary>
    public static (string Key, string Name)[] PosterScoreChoices(bool movies) =>
        [("tmdb", "TMDB"), ("imdb", "IMDb"), .. ScoreSources.Where(s => s.Key != "imdb" && (movies ? s.Key != "myanimelist" : s.Key != "letterboxd")), ("none", "No score")];
    public static string Kind(MediaType type, bool season = false) => season ? "season" : type switch { MediaType.Movie => "movie", MediaType.Episode => "episode", _ => "show" };
    /// <summary>Whether to show this source's score on this kind of page. Sources left out of Scores are off everywhere.</summary>
    public bool ShowsScore(string source, string kind) =>
        (source is "tmdb" or "friends" || Scores is null || Scores.Contains(source)) && HiddenScores?.Contains($"{source}/{kind}") != true;
    /// <summary>"To rate" on Home starts with movies and shows; seasons and episodes are opt-in.</summary>
    public static readonly string[] DefaultRateKinds = ["movie", "show"];
    public bool RatesOnHome(string kind) => (RateKinds ?? DefaultRateKinds).Contains(kind);
    public int Count(string section) => HomeCounts?.TryGetValue(section, out var n) == true ? n : PosterRows.Contains(section) ? DefaultPosterRowCount : DefaultRowCount;
    public ListLayout LayoutFor(string page) => PageLayouts?.TryGetValue(page, out var l) == true ? l : ListLayout;
    public string? SortFor(string page) => PageSorts?.TryGetValue(page, out var s) == true ? s : null;
    /// <summary>Who can see this part: its own setting, or the profile's.</summary>
    public ProfileVisibility For(ProfilePart part, ProfileVisibility profile) => part switch
    {
        // The older "keep where you watched private" switch meant only you, for both.
        ProfilePart.Sources => SourcesVisibility ?? (HideSourcesFromOthers ? ProfileVisibility.Private : profile),
        ProfilePart.WatchingApp => WatchingAppVisibility ?? (HideSourcesFromOthers ? ProfileVisibility.Private : profile),
        _ => PartVisibility?.TryGetValue(part, out var v) == true ? v : profile,
    };
    /// <summary>A part's own audience, if it has one (null: same as the profile).</summary>
    public ProfileVisibility? OwnAudience(ProfilePart part) => part switch
    {
        ProfilePart.Sources => SourcesVisibility ?? (HideSourcesFromOthers ? ProfileVisibility.Private : null),
        ProfilePart.WatchingApp => WatchingAppVisibility ?? (HideSourcesFromOthers ? ProfileVisibility.Private : null),
        _ => PartVisibility?.TryGetValue(part, out var v) == true ? v : null,
    };
    /// <summary>Sets (or clears, with null) a part's own audience.</summary>
    public UserPrefs WithAudience(ProfilePart part, ProfileVisibility? who) => part switch
    {
        ProfilePart.Sources => this with { SourcesVisibility = who, HideSourcesFromOthers = false, WatchingAppVisibility = OwnAudience(ProfilePart.WatchingApp) },
        ProfilePart.WatchingApp => this with { WatchingAppVisibility = who, HideSourcesFromOthers = false, SourcesVisibility = OwnAudience(ProfilePart.Sources) },
        _ => this with
        {
            PartVisibility = (PartVisibility ?? new Dictionary<ProfilePart, ProfileVisibility>()).Where(kv => kv.Key != part)
                .Concat(who is { } w ? [new KeyValuePair<ProfilePart, ProfileVisibility>(part, w)] : [])
                .ToDictionary(kv => kv.Key, kv => kv.Value) is { Count: > 0 } d ? d : null,
        },
    };

    /// <summary>Clamps everything to allowed values.</summary>
    /// <summary>How this kind of alert reaches you.</summary>
    public AlertDelivery DeliveryFor(AlertKind kind) =>
        Alerts is not null && Alerts.TryGetValue(kind, out var d) ? d
        : kind == AlertKind.RecWatched && !AlertRecWatched ? AlertDelivery.Off   // the older on/off switch
        : AlertDelivery.Both;
    public bool ListsAlert(AlertKind kind) => DeliveryFor(kind) is AlertDelivery.Both or AlertDelivery.Alert;
    public bool PushesAlert(AlertKind kind) => DeliveryFor(kind) is AlertDelivery.Both or AlertDelivery.Push;
    public static readonly (AlertKind Kind, string Name, string Hint)[] AlertKinds =
    [
        (AlertKind.Episode, "New episodes", "Shows you watch or have on your watchlist, on the day they air."),
        (AlertKind.Movie, "Movie releases", "Movies on your watchlist, on release day."),
        (AlertKind.Person, "New work from people you follow", "Their new movies and shows."),
        (AlertKind.Recommendation, "Recommendations to you", "When a friend sends you something to watch."),
        (AlertKind.RecWatched, "Someone watched your recommendation", "\"Sam watched Heat\", or started a show you sent them."),
        (AlertKind.WatchedWith, "Logged with you", "When a friend marks something watched together with you, which adds it to your history too."),
        (AlertKind.Poll, "Movie nights", "When a friend asks you to vote on what to watch, and when their poll picks a winner."),
    ];

    public UserPrefs Clean() => this with
    {
        Alerts = Alerts?.Where(kv => Enum.IsDefined(kv.Key) && Enum.IsDefined(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value),
        ProfileBadges = ProfileBadges?.Where(b => b is { Length: > 0 and <= 60 }).Distinct().Take(MaxBadges).ToList(),
        Challenges = Challenges?.Where(k => SeenIt.Contracts.Challenges.All.Any(c => c.Key == k)).Distinct().ToList(),
        StartPage = StartPages.Contains(StartPage) ? StartPage : "home",
        Scores = Scores?.Where(s => ScoreSources.Any(x => x.Key == s)).Distinct().ToList(),
        HomeCounts = HomeCounts?.Where(kv => UiPrefs.HomeSections.Any(h => h.Key == kv.Key)).ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 4, 40)),
        ComingUpDays = ComingUpDays is 7 or 14 or 30 ? ComingUpDays : 14,
        HiddenScores = HiddenScores?.Where(h => h.Split('/') is [var src, var kind] && (src is "tmdb" or "friends" || ScoreSources.Any(x => x.Key == src))
            && ScoreKinds.Any(k => k.Key == kind)).Distinct().ToList(),
        ScoreSpoilers = Enum.IsDefined(ScoreSpoilers) ? ScoreSpoilers : ScoreSpoilers.Off,
        PosterScoreMovies = PosterScoreChoices(true).Any(c => c.Key == PosterScoreMovies) ? PosterScoreMovies : "tmdb",
        PosterScoreShows = PosterScoreChoices(false).Any(c => c.Key == PosterScoreShows) ? PosterScoreShows : "tmdb",
        PartVisibility = PartVisibility?.Where(kv => Enum.IsDefined(kv.Key) && Enum.IsDefined(kv.Value) && kv.Key is not (ProfilePart.Sources or ProfilePart.WatchingApp))
            .ToDictionary(kv => kv.Key, kv => kv.Value),
        SourcesVisibility = SourcesVisibility is { } sv && Enum.IsDefined(sv) ? sv : null,
        WatchingAppVisibility = WatchingAppVisibility is { } wv && Enum.IsDefined(wv) ? wv : null,
        Accent = UiPrefs.Accents.Any(a => a.Key == Accent) ? Accent : null,
        EpisodeStyle = Enum.IsDefined(EpisodeStyle) ? EpisodeStyle : EpisodeStyle.Code,
        PageLayouts = PageLayouts?.Where(kv => UiPrefs.LayoutPages.Contains(kv.Key) && Enum.IsDefined(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value),
        PageSorts = PageSorts?.Where(kv => UiPrefs.SortPages.Contains(kv.Key) && kv.Value is { Length: > 0 and <= 30 }).ToDictionary(kv => kv.Key, kv => kv.Value),
        SavedViews = CleanViews(SavedViews),
        ProfileBanner = ProfileBanner is { Type: MediaType.Movie or MediaType.Show } ? ProfileBanner : null,
        ProfileBadge = ProfileBadge is { Length: > 0 and <= 80 } ? ProfileBadge : null,
        RateKinds = RateKinds?.Where(k => ScoreKinds.Any(x => x.Key == k)).Distinct().ToList(),
        DesktopNav = DesktopNav?.Where(k => k != "home" && UiPrefs.Tabs.Any(t => t.Key == k)).Distinct().Take(UiPrefs.MaxDesktopNav).ToList(),
        MyServices = MyServices?.Where(id => id > 0).Distinct().Take(40).ToList(),
    };
    /// <summary>Saved views that are allowed, trimmed, and with at most one default per page (the last one marked).</summary>
    private static List<SavedView>? CleanViews(IReadOnlyList<SavedView>? views)
    {
        if (views is null) return null;
        var list = views.Where(v => UiPrefs.IsViewPage(v.Page) && !string.IsNullOrWhiteSpace(v.Name) && (v.Query?.Length ?? 0) <= 500)
            .Select(v => v with { Name = v.Name.Trim()[..Math.Min(v.Name.Trim().Length, 40)], Query = (v.Query ?? "").TrimStart('?') })
            .Take(UiPrefs.MaxSavedViews).ToList();
        var pages = new HashSet<string>();
        for (var i = list.Count - 1; i >= 0; i--)
            if (list[i].Default && !pages.Add(list[i].Page)) list[i] = list[i] with { Default = false };
        return list;
    }

    /// <summary>Whether you have this service (always true when you haven't said which you have).</summary>
    public bool HasService(int providerId) => MyServices is not { Count: > 0 } mine || mine.Contains(providerId);
}

/// <summary>Who can see your profile, history and stats. Lists have their own public/private switch.</summary>
public enum ProfileVisibility { Public, Friends, Private }

/// <summary>The customizable bits of the UI and their allowed keys.</summary>
public static class UiPrefs
{
    public static readonly (string Key, string Label)[] HomeSections =
    [
        // The default order: what you'll act on first (accounts that saved an order keep theirs).
        ("now", "Watching now"),
        ("now-cast", "Cast of what you're watching"),
        ("upnext", "Up next"),
        ("torate", "To rate"),
        ("upcoming", "Coming up"),
        ("onthisday", "On this day"),
        ("review", "Month / year in review banner"),
        ("goals", "Goals"),
        ("recent", "Recently watched"),
        ("foryou", "Picked for you"),
        ("friends", "Friends' activity"),
        ("trending-movies", "Trending movies"),
        ("trending-shows", "Trending shows"),
        // Stats widgets (the same as on profiles). Off until you turn them on.
        ("w-month", "Stats: this month so far"),
        ("w-heatmap", "Stats: every day this year"),
        ("w-genres", "Stats: top genres this year"),
        ("w-records", "Stats: this year's records"),
    ];

    /// <summary>Home sections that start switched off (new accounts, and existing ones when they were added).</summary>
    public static readonly string[] HiddenByDefault = ["w-month", "w-heatmap", "w-genres", "w-records", "now-cast"];

    /// <summary>Pages with their own posters/rows choice, sort memory and saved views.</summary>
    public static readonly string[] LayoutPages = ["watchlist", "ratings", "list"];
    public static readonly string[] SortPages = ["watchlist", "ratings", "lists-browse"];
    /// <summary>Pages with saved views. A list's own views are "list:{its id}"; "list" ones show on every list.</summary>
    public static readonly string[] ViewPages = ["history", "history-friends", "watchlist", "progress", "ratings", "recommendations", "recommendations-sent",
        "notifications", "library", "people", "not-interested", "calendar", "person", "list"];
    public static bool IsViewPage(string? page) =>
        page is not null && (ViewPages.Contains(page) || (page.StartsWith("list:", StringComparison.Ordinal) && Guid.TryParse(page[5..], out _)));
    public const int MaxSavedViews = 60;

    /// <summary>Accent colors that can replace the theme's own. Hover is a lighter shade; OnAccent is text on it.</summary>
    public static readonly (string Key, string Name, string Hex, string Hover, string Rgb, string Text, string OnAccent)[] Accents =
    [
        ("red", "Red", "#ec3f4f", "#f25b69", "236, 63, 79", "#ffb3ba", "#fff"),
        ("orange", "Orange", "#f27323", "#fb8a45", "242, 115, 35", "#fed7aa", "#fff"),
        ("gold", "Gold", "#e2b024", "#efc24a", "226, 176, 36", "#fde68a", "#1a1405"),
        ("green", "Green", "#34b27b", "#46c98f", "52, 178, 123", "#b9f0d4", "#fff"),
        ("teal", "Teal", "#22b3b0", "#3ccbc7", "34, 179, 176", "#b3f0ee", "#06201f"),
        ("blue", "Blue", "#4f8cff", "#6ea0ff", "79, 140, 255", "#bcd3ff", "#fff"),
        ("purple", "Purple", "#a660f5", "#bb85fa", "166, 96, 245", "#e1c8ff", "#fff"),
        ("pink", "Pink", "#e8559c", "#f074b0", "232, 85, 156", "#fbc6df", "#fff"),
    ];

    /// <summary>CSS custom properties for an accent (applied to the page, or to a profile for its visitors).</summary>
    public static string? AccentStyle(string? key) => Accents.FirstOrDefault(a => a.Key == key) is { Key: not null } a
        ? $"--accent:{a.Hex};--accent-hover:{a.Hover};--accent-rgb:{a.Rgb};--accent-soft:rgba({a.Rgb}, .15);--accent-text:{a.Text};--on-accent:{a.OnAccent};"
        : null;

    public static readonly (string Key, string Label, string Icon, string Href)[] Tabs =
    [
        ("home", "Home", "home", ""),
        ("discover", "Discover", "compass", "discover"),
        ("progress", "Progress", "progress", "progress"),
        ("calendar", "Calendar", "calendar", "calendar"),
        ("history", "History", "history", "history"),
        ("watchlist", "Watchlist", "bookmark", "watchlist"),
        ("lists", "Lists", "list", "lists"),
        ("stats", "Stats", "chart", "stats"),
        ("ratings", "Ratings", "star", "ratings"),
        ("friends", "Friends", "users", "friends"),
        ("search", "Search", "search", "search"),
        // Everything else on the More page, so it can be a tab (app) or a link at the top (website) too.
        ("notifications", "Alerts", "bell", "notifications"),
        ("for-you", "For you", "eye", "for-you"),
        ("pick", "Pick for me", "shuffle", "pick"),
        ("favorites", "Favorites", "heart", "favorites"),
        ("library", "Library", "download", "library"),
        ("review-month", "This month", "clock", ReviewMonthHref),
        ("review-year", "This year", "chart", ReviewYearHref),
        ("goals", "Goals", "target", "goals"),
        ("completionist", "Completionist", "check", "completionist"),
        ("recommendations", "Recommendations", "send", "recommendations"),
        ("people", "People", "user-plus", "people"),
        ("movie-night", "Movie night", "users", "movie-night"),
        ("duplicates", "Duplicates", "copy", "duplicates"),
        ("recent", "Recent actions", "repeat", "recent"),
        ("settings", "Settings", "settings", "settings"),
    ];
    /// <summary>The Tabs (all but Home) by category, in order: the website's More menu, like the app's More page.</summary>
    public static readonly (string Title, string[] Keys)[] TabGroups =
    [
        ("Watching", ["progress", "calendar", "watchlist", "history", "notifications"]),
        ("Find something", ["search", "discover", "for-you", "pick"]),
        ("Your collection", ["lists", "ratings", "favorites", "library"]),
        ("Stats", ["stats", "review-month", "review-year", "goals", "completionist"]),
        ("Friends", ["friends", "recommendations", "people", "movie-night"]),
        ("Settings", ["settings", "duplicates", "recent"]),
    ];
    /// <summary>Stand-ins for the current month's and year's review pages; the UI fills in the dates (UiLayout.Href).</summary>
    public const string ReviewMonthHref = "review/{month}", ReviewYearHref = "review/{year}";
    /// <summary>The website's links at the top, after Home, when you haven't picked your own (what it always had).</summary>
    public static readonly string[] DefaultDesktopNav = ["discover", "progress", "calendar", "watchlist", "history", "lists", "stats", "friends"];
    public const int MaxDesktopNav = 14;

    /// <summary>
    /// Home sections top to bottom: the saved order, then any sections it doesn't mention (added
    /// since it was saved) slotted in after the section they follow by default.
    /// </summary>
    public static List<string> HomeOrder(IReadOnlyList<string>? saved)
    {
        var order = (saved ?? []).Where(k => HomeSections.Any(s => s.Key == k)).Distinct().ToList();
        for (var i = 0; i < HomeSections.Length; i++)
        {
            var key = HomeSections[i].Key;
            if (order.Contains(key)) continue;
            var after = i == 0 ? -1 : order.IndexOf(HomeSections[i - 1].Key);
            order.Insert(after + 1, key);
        }
        return order;
    }

    /// <summary>Color themes, all dark. Colors are for the picker's swatches and the phone's system bars; the CSS has the rest.</summary>
    public static readonly (string Key, string Name, string Bg, string Surface, string Accent)[] Themes =
    [
        ("seenit", "SeenIt", "#0e1014", "#1e222b", "#ec3f4f"),
        ("midnight", "Midnight", "#0b1020", "#18213f", "#4f8cff"),
        ("black", "True black", "#000000", "#141417", "#ec3f4f"),
        ("forest", "Forest", "#0c120f", "#19231e", "#34b27b"),
        ("dusk", "Dusk", "#110e1a", "#201b31", "#a660f5"),
        ("ember", "Ember", "#14100c", "#251d17", "#f27323"),
        ("nord", "Nord", "#1b1f27", "#2a303d", "#88c0d0"),
    ];

    public static (string Key, string Name, string Bg, string Surface, string Accent) Theme(string? key) =>
        Themes.FirstOrDefault(t => t.Key == key) is { Key: not null } t ? t : Themes[0];

    public const int MaxTabs = 4; // plus the fixed "More" tab
    public static readonly string[] DefaultTabs = ["home", "discover", "progress", "calendar"];
}
public record UpdateProfileRequest(string? DisplayName);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record ChangeEmailRequest(string NewEmail, string Password);
public record ChangeUserNameRequest(string NewUserName, string Password);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);
/// <summary>What this server can do, so clients can hide features it isn't set up for.</summary>
public record ServerFeatures(bool Email, bool OpenRegistration = true, string? Version = null);
public record InviteDto(string Code, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? UsedBy, DateTimeOffset? UsedAt);
/// <summary>Public check of an invite link before signing up.</summary>
public record InviteCheck(bool Valid, string? InvitedBy);
public record DeleteAccountRequest(string Password);
/// <summary>Wipes the chosen data but keeps the account, its settings and friends.</summary>
/// <param name="Before">Only what's dated before this day (in your time zone), not the day itself; null = everything.</param>
public record ClearDataRequest(string Password, ImportParts Parts, DateOnly? Before = null);
public record ClearDataResult(int Plays, int Ratings, int Watchlist, int Lists);

// ---------- Catalog (TMDB) ----------

/// <summary>A poster card: enough to render a grid tile for a movie or show.</summary>
public record MediaSummary(
    MediaType Type,
    int TmdbId,
    string Title,
    int? Year,
    string? PosterPath,
    string? BackdropPath,
    double? VoteAverage,
    string? Overview,
    bool? Watched = null,       // the viewer has seen it (a movie) or started it (a show); filled on browse, search and "you might also like"
    bool? OnWatchlist = null);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int TotalPages, int TotalResults);

/// <summary>
/// A title's score on another site. Value is as that site shows it (7.8, 94, 3.9); Max is its scale
/// (10, 100, 5); Percent marks %-style scores (Rotten Tomatoes).
/// </summary>
public record ExternalScore(string Source, string Name, double Value, double Max, bool Percent, int? Votes, string? Url);

/// <summary>
/// What a search for an id or link found: an IMDb id/URL (tt…, nm…), a TMDB URL, or a bare number
/// (tried as a TMDB movie and show id). Go is the page to open when it's one unambiguous thing.
/// </summary>
public record LookupResult(string Label, IReadOnlyList<MediaSummary> Titles, IReadOnlyList<PersonHit> People, string? Go);

/// <summary>A person from search, with what they're best known for.</summary>
/// <summary>A search-as-you-type suggestion. Kind is "movie", "show" or "person"; Image is a poster or profile path.</summary>
public record Suggestion(string Kind, int Id, string Title, int? Year, string? Image, string? Sub);

public record PersonHit(int Id, string Name, string? ProfilePath, string? Department, IReadOnlyList<string> KnownFor);

/// <summary>
/// What counts toward finishing someone's filmography (person pages and Completionist): released movies, leaving out
/// obscure ones (fewer than MinVotes votes on TMDB) unless you've seen them, so a long tail of shorts and bit parts
/// doesn't make it impossible.
/// </summary>
public static class Filmography
{
    public const int MinVotes = 20;
    public static bool Counts(DateOnly? released, int votes, bool seen, DateOnly today) =>
        released is { } d && d <= today && (votes >= MinVotes || seen);
}

/// <summary>Someone whose movies you're working through: how many of them in one role you've seen, and a few you haven't.</summary>
public record CompletionDto(int PersonId, string Name, string? ProfilePath, string Role, int Seen, int Total,
    IReadOnlyList<MediaSummary> Unseen, DateTimeOffset? CompletedAt);

/// <summary>Two people and what they both have a credit in (acting or behind the camera), newest first.</summary>
public record PersonPair(PersonHit A, PersonHit B, IReadOnlyList<PairCredit> Credits);
/// <summary>
/// A title both worked on, with each one's part ("as Bakta", "Director"). Watched: you've seen it (a show: seen
/// both of them in it); PartlySeen: a show you've watched some of, but not with both of them.
/// </summary>
public record PairCredit(MediaType Type, int TmdbId, string Title, DateOnly? Date, string? PosterPath,
    string? RoleA, string? RoleB, int? EpisodesA, int? EpisodesB, bool Watched, bool PartlySeen,
    bool Appearance = false);   // talk shows, documentaries, "Self" for either of them: listed apart, it's not working together

public record CastMember(int Id, string Name, string? Character, string? ProfilePath, int? EpisodeCount = null);
public record CrewMember(int Id, string Name, string Job, string? ProfilePath);
public record Video(string Key, string Name, string Site, string Type);

/// <summary>What the signed-in user has done with a title. All empty when signed out.</summary>
public record UserMediaState(
    bool InWatchlist,
    double? Rating,
    int Plays,
    DateTimeOffset? LastWatchedAt,
    IReadOnlyList<Guid> ListIds,
    IReadOnlyList<PlayDto>? History = null,    // every play, newest first (movies only)
    bool IsFavorite = false,
    bool NotInterested = false,
    OwnedFormat? Owned = null,    // in your library, and how (the first copy; Copies has them all)
    string? Note = null,          // your private note
    IReadOnlyList<OwnedCopy>? Copies = null);

public record MovieDetail(
    int TmdbId,
    string Title,
    string? Tagline,
    string? Overview,
    DateOnly? ReleaseDate,
    int? Runtime,
    IReadOnlyList<string> Genres,
    string? PosterPath,
    string? BackdropPath,
    double? VoteAverage,
    int? VoteCount,
    string? ImdbId,
    string? Status,
    IReadOnlyList<CastMember> Cast,
    IReadOnlyList<CrewMember> Crew,
    IReadOnlyList<Video> Videos,
    IReadOnlyList<MediaSummary> Recommendations,
    UserMediaState? User,
    CollectionDto? Collection = null,
    ReleaseStatus? Release = null);   // in theaters / coming to theaters, for the viewer's country

/// <summary>Where a movie is in its release, for one country: TMDB's theatrical and digital dates there.</summary>
/// <param name="Kind">InTheaters or ComingToTheaters.</param>
public record ReleaseStatus(ReleaseKind Kind, DateOnly Theatrical, DateOnly? Digital, string Country);
public enum ReleaseKind { InTheaters, ComingToTheaters }

public static class ReleaseStatuses
{
    /// <summary>How long a film counts as in cinemas after it opens. TMDB has no end date, and films keep playing
    /// for weeks after they're out on digital (Other Mommy: cinemas Oct 9, digital Nov 10, still showing).</summary>
    public const int TheatricalRunDays = 90;

    /// <summary>
    /// From one country's release dates (TMDB types: 2 limited and 3 wide theatrical, 4 digital): coming to
    /// theaters when the first theatrical date is ahead; in theaters for the usual run after it opens, whether or
    /// not it's on digital yet (Digital says when). Null otherwise: long released, or straight to streaming.
    /// </summary>
    public static ReleaseStatus? For(IEnumerable<(int Type, DateOnly Date)> dates, string country, DateOnly today)
    {
        var list = dates.ToList();
        if (list.Where(d => d.Type is 2 or 3).Select(d => (DateOnly?)d.Date).Min() is not { } theatrical) return null;
        var digital = list.Where(d => d.Type == 4).Select(d => (DateOnly?)d.Date).Min();
        if (theatrical > today) return new(ReleaseKind.ComingToTheaters, theatrical, digital, country);
        if (today.DayNumber - theatrical.DayNumber > TheatricalRunDays) return null;
        return new(ReleaseKind.InTheaters, theatrical, digital, country);
    }
}

/// <summary>A TMDB movie collection (franchise), with which parts the user has seen.</summary>
public record CollectionDto(int Id, string Name, string? PosterPath, string? BackdropPath, IReadOnlyList<CollectionPart> Parts)
{
    public int Released => Parts.Count(p => p.Released);
    public int Seen => Parts.Count(p => p.Watched);
}
public record CollectionPart(MediaSummary Media, DateOnly? ReleaseDate, bool Released, bool Watched);

public record SeasonSummary(int SeasonNumber, string Name, int EpisodeCount, DateOnly? AirDate, string? PosterPath, int Watched,
    DateTimeOffset? CompletedAt = null);   // when the last unwatched episode was first watched

public record ShowDetail(
    int TmdbId,
    string Name,
    string? Tagline,
    string? Overview,
    DateOnly? FirstAirDate,
    DateOnly? LastAirDate,
    string? Status,
    IReadOnlyList<string> Networks,
    IReadOnlyList<string> Genres,
    int? EpisodeRuntime,
    string? PosterPath,
    string? BackdropPath,
    double? VoteAverage,
    int? VoteCount,
    int NumberOfSeasons,
    int NumberOfEpisodes,
    IReadOnlyList<SeasonSummary> Seasons,
    IReadOnlyList<CastMember> Cast,
    IReadOnlyList<Video> Videos,
    IReadOnlyList<MediaSummary> Recommendations,
    EpisodeRef? NextEpisodeToAir,
    UserMediaState? User,
    ShowProgressDto? Progress,
    long TotalRuntime = 0,        // minutes, every cached regular-season episode
    int AiredEpisodes = 0,
    long WatchedMinutes = 0,
    long RemainingMinutes = 0,    // aired episodes you haven't watched
    ShowTrackStatus TrackStatus = ShowTrackStatus.Watching,
    IReadOnlyList<CrewMember>? Crew = null,    // created by, and who directed, wrote and scored the most episodes
    DateTimeOffset? FirstWatchedAt = null,     // your first and latest dated episode plays (rewatches included)
    DateTimeOffset? LastWatchedAt = null);

/// <summary>A milestone episode, shown as a badge: S01E01, a season's first or last, or the very last.</summary>
public enum EpisodeMark { None, SeriesPremiere, SeasonPremiere, SeasonFinale, SeriesFinale }

public static class EpisodeMarks
{
    /// <param name="type">TMDB's episode type ("finale" marks a season's last).</param>
    /// <param name="ended">The show has ended or been canceled.</param>
    /// <param name="lastSeason">The show's highest season number.</param>
    /// <param name="lastEpisode">The highest episode number in this episode's season.</param>
    public static EpisodeMark Of(int season, int episode, string? type, bool ended, int lastSeason, int lastEpisode)
    {
        if (season <= 0) return EpisodeMark.None;   // specials
        var last = season == lastSeason && episode == lastEpisode;
        // An ended show's very last episode is its finale even before TMDB's type is known.
        if (ended && last && (type is "finale" or null)) return EpisodeMark.SeriesFinale;
        if (type == "finale") return EpisodeMark.SeasonFinale;
        if (episode == 1) return season == 1 ? EpisodeMark.SeriesPremiere : EpisodeMark.SeasonPremiere;
        return EpisodeMark.None;
    }

    public static string? Label(EpisodeMark m) => m switch
    {
        EpisodeMark.SeriesPremiere => "Series premiere",
        EpisodeMark.SeasonPremiere => "Season premiere",
        EpisodeMark.SeasonFinale => "Season finale",
        EpisodeMark.SeriesFinale => "Series finale",
        _ => null,
    };
}

public record EpisodeRef(int SeasonNumber, int EpisodeNumber, string? Name, DateOnly? AirDate, bool Watched = false,
    DateTimeOffset? AirsAt = null, string? Network = null,   // exact time and channel, from TVmaze, when known
    EpisodeMark Mark = EpisodeMark.None);

public record EpisodeDto(
    int TmdbId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Name,
    string? Overview,
    DateOnly? AirDate,
    int? Runtime,
    string? StillPath,
    double? VoteAverage,
    int Plays,
    double? Rating,
    bool Rewatched = false,    // played since the current rewatch started
    EpisodeMark Mark = EpisodeMark.None);

public record SeasonDetail(
    int ShowTmdbId,
    string ShowName,
    int SeasonNumber,
    string Name,
    string? Overview,
    string? PosterPath,
    IReadOnlyList<EpisodeDto> Episodes,
    IReadOnlyList<CastMember>? Cast = null,
    int? Rewatch = null,       // number of the rewatch in progress (2 = second time through)
    DateTimeOffset? CompletedAt = null,
    double? Rating = null,        // your rating of the season itself
    int? PreviousSeason = null, int? NextSeason = null,   // neighbors in season order, see below
    double? VoteAverage = null);  // TMDB's score for the season
// PreviousSeason/NextSeason: neighbors in season order (specials sit outside, except Specials → Season 1)

// ---------- Tracking ----------

/// <summary>When a play happened. Custom uses WatchedAt; ReleaseDate uses each item's own release/air date.</summary>
public enum WatchTime { Now, ReleaseDate, Custom, Unknown }

/// <summary>What the "when did you watch it?" dialog returns.</summary>
/// <summary>Through: also mark everything before it (when the dialog offered that).</summary>
/// SeasonOnly: "up to here" stays within the episode's season.
/// With: friends' usernames who watched it too (their play is logged as well).
public record WatchChoice(WatchTime When, DateTimeOffset? At = null, bool Through = false, bool SeasonOnly = false, IReadOnlyList<string>? With = null);

public record WatchRequest(
    MediaType Type,
    int TmdbId,                 // movie id, or show id for episodes/seasons/shows
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    DateTimeOffset? WatchedAt = null,
    WatchTime? When = null,     // null: Custom if WatchedAt is set, otherwise Now
    IReadOnlyList<string>? With = null);   // friends (usernames) who watched it with you: it goes in their history too

/// <summary>Marks every aired episode of a show (or one season) watched.</summary>
public record BulkWatchRequest(int ShowTmdbId, int? SeasonNumber = null, DateTimeOffset? WatchedAt = null, bool OnlyUnwatched = true, WatchTime? When = null);

/// <summary>What marking something watched did, so it can be undone: the plays it added, and whether that took the title off the watchlist.</summary>
public record WatchedResult(int Added, IReadOnlyList<Guid> PlayIds, bool LeftWatchlist = false,
    IReadOnlyList<Guid>? WithPlayIds = null,     // plays logged for friends who watched with you (undone through api/watch/with/undo)
    IReadOnlyList<string>? WithNames = null,     // their names, for the toast; anyone left out isn't a friend
    ShowProgressDto? Progress = null);           // an episode or show: where you are in it now, so a card can update without reloading everything

/// <summary>Plays recorded with "unknown date" are stored at the Unix epoch.</summary>
public static class WatchDates
{
    public static readonly DateTimeOffset Unknown = DateTimeOffset.UnixEpoch;
    public static bool IsUnknown(DateTimeOffset d) => d.UtcTicks == Unknown.UtcTicks;
}

public record PlayDto(Guid Id, DateTimeOffset WatchedAt, WatchSource Source);

public record EpisodeDetail(
    int ShowTmdbId,
    string ShowName,
    string? ShowPosterPath,
    string? ShowBackdropPath,
    int SeasonNumber,
    int EpisodeNumber,
    int TmdbId,
    string? Name,
    string? Overview,
    DateOnly? AirDate,
    int? Runtime,
    string? StillPath,
    double? VoteAverage,
    int? VoteCount,
    IReadOnlyList<CastMember> GuestStars,
    IReadOnlyList<CrewMember> Crew,
    EpisodeRef? Previous,
    EpisodeRef? Next,
    double? Rating,
    IReadOnlyList<PlayDto>? Plays,    // null when signed out
    IReadOnlyList<SeasonSummary>? Seasons = null,
    IReadOnlyList<CastMember>? Cast = null,    // regulars in this episode; guests are in GuestStars
    EpisodeMark Mark = EpisodeMark.None);

// ---------- People ----------

public record PersonCreditDto(
    MediaType Type,
    int TmdbId,
    string Title,
    DateOnly? Date,
    string? Role,              // character, or job for crew
    int? EpisodeCount,
    string? PosterPath,
    int VoteCount,
    bool Watched,
    bool Appearance = false,    // talk shows, news, "Self" roles: listed separately
    IReadOnlyList<CrewJob>? Jobs = null,    // crew only: each job with its TMDB department, to split directing/writing/other
    bool PartlySeen = false,                // a show you've watched some of, but not their episode(s): Watched is false
    bool Watching = false,                  // a show you've started and haven't seen every aired episode of: with the
                                            // BlurEpisodeCounts setting on, its episode count is blurred (it gives away who stays)
    bool InLibrary = false);                // you own it (Library), in any format

public record CrewJob(string Job, string? Department);

/// <summary>A title the viewer has watched this person in.</summary>
public record WatchedWith(
    MediaType Type,
    int TmdbId,
    string Title,
    string? PosterPath,
    string? Character,
    int Plays,                  // movie plays, or episode plays
    long Minutes,
    bool GuestRole,             // true: Episodes lists exactly the episodes they're in
    IReadOnlyList<EpisodeRef> Episodes,
    string? Jobs = null);       // what they did behind the camera on it ("Director, Screenplay")

public record PersonDetail(
    int Id,
    string Name,
    string? Biography,
    DateOnly? Birthday,
    DateOnly? Deathday,
    string? PlaceOfBirth,
    string? ProfilePath,
    string? KnownFor,
    string? ImdbId,
    IReadOnlyList<PersonCreditDto> Cast,
    IReadOnlyList<PersonCreditDto> Crew,
    IReadOnlyList<WatchedWith>? Watched,   // null when signed out
    int WatchedMovies,
    int WatchedEpisodes,
    int WatchedShows,
    long WatchedMinutes,
    bool Following = false,      // you follow this person (their new work goes on your calendar)
    double? AvgRating = null,    // your average rating of their movies and shows
    int Rated = 0,
    string? WikidataId = null,   // e.g. Q23547: Wikidata redirects that to their Wikipedia article
    IReadOnlyList<int>? PendingShows = null);   // quick load: shows of theirs you've watched some of, whose episodes are still being checked (PersonSeen fills them in)

/// <summary>
/// The slow part of a person's page, loaded after the rest: what you've seen them in. Checking which of a show's
/// episodes they're in takes a TMDB lookup per show, so the page shows first and this fills in.
/// </summary>
public record PersonSeen(IReadOnlyList<WatchedWith> Watched, int WatchedMovies, int WatchedEpisodes, int WatchedShows, long WatchedMinutes,
    IReadOnlyList<int> SeenShows, IReadOnlyList<int> PartlyShows);

/// <summary>Someone you follow on the People page, with what they have coming.</summary>
public record FollowedPerson(int Id, string Name, string? ProfilePath, string? Department, DateTimeOffset Since, IReadOnlyList<UpcomingWork> Upcoming);
/// <summary>Date is null when it's announced without one.</summary>
public record UpcomingWork(MediaType Type, int TmdbId, string Title, string? PosterPath, DateOnly? Date, string? Role);
public record PersonFollowRequest(bool Follow);

// ---------- Recap ("previously on") ----------

/// <summary>Coming back to a show after a long break: where you stopped, and the last episodes you saw.</summary>
public record RecapDto(DateTimeOffset LastWatchedAt, IReadOnlyList<RecapEpisode> Episodes, EpisodeRef Next);
public record RecapEpisode(int SeasonNumber, int EpisodeNumber, string? Name, string? Overview, string? StillPath, DateTimeOffset WatchedAt);

// ---------- Recommendations between friends ----------

/// <summary>
/// Received: Who sent it, and Watched/Rating are yours. Sent: Who got it, and Watched/Rating are theirs
/// (null when their history or ratings aren't visible to you).
/// </summary>
public record RecommendationDto(Guid Id, MediaSummary Media, UserCard Who, string? Note, DateTimeOffset At, bool Dismissed,
    bool? Watched, DateTimeOffset? WatchedAt, double? Rating, bool OnWatchlist,
    int? Weight = null);   // how strongly it's recommended, 1–10, if the sender said
public record RecommendationsDto(IReadOnlyList<RecommendationDto> Received, IReadOnlyList<RecommendationDto> Sent);
// ---------- Movie night polls ----------

/// <summary>Start a poll: 2–8 titles, friends to ask (people who follow you), and optionally a name and when it closes.</summary>
public record PollRequest(string? Title, IReadOnlyList<MediaRef> Options, IReadOnlyList<string> Invite, DateTimeOffset? ClosesAt = null);
/// <summary>Your votes: every option you'd watch (any number; none takes your votes back).</summary>
public record PollVoteRequest(IReadOnlyList<Guid> OptionIds);
/// <param name="Voters">Usernames of who'd watch it.</param>
public record PollOptionDto(Guid Id, MediaSummary Media, IReadOnlyList<string> Voters, bool Mine);
/// <param name="WinnerId">Once closed: the option with the most votes (ties go to the earlier option).</param>
public record PollDto(Guid Id, string? Title, UserCard Owner, bool IsOwner, DateTimeOffset CreatedAt, DateTimeOffset? ClosesAt, bool Closed,
    IReadOnlyList<PollOptionDto> Options, IReadOnlyList<UserCard> Members, IReadOnlyList<string> Voted, Guid? WinnerId);
/// <summary>A poll in your list: yours or one you're asked to.</summary>
public record PollSummary(Guid Id, string? Title, string OwnerName, bool IsOwner, DateTimeOffset CreatedAt, DateTimeOffset? ClosesAt, bool Closed,
    IReadOnlyList<string?> Posters, int Members, int Voted, bool YouVoted, string? Winner);

/// <summary>Someone you've already recommended a title to, and when (the latest time).</summary>
public record RecommendedBefore(string UserName, DateTimeOffset At);

public record RecommendRequest(MediaType Type, int TmdbId, IReadOnlyList<string> To, string? Note,
    int? Weight = null);   // optional strength, 1–10
public record RecommendResult(int Sent, IReadOnlyList<string> Skipped);   // Skipped: names you can't send to

public record HistoryEntry(
    Guid Id,
    MediaType Type,
    int TmdbId,
    string Title,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string? PosterPath,
    DateTimeOffset WatchedAt,
    WatchSource Source,
    int? Runtime = null,
    int? Episodes = null,      // Shows view: episodes watched that day (one row per show per day)
    string? Range = null,      // Shows view: "S04E01–S04E05", or one episode's number
    string? Who = null,        // Friends' history: whose play this is (a UserName from HistoryPage.People)
    bool? YouWatched = null,   // Someone else's play: whether the viewer has seen this episode too (null on your own)
    int? EpisodesAired = null,   // One-per-show view: episodes out so far (Episodes is then how many of them were seen)
    EpisodeMark Mark = EpisodeMark.None);

/// <summary>History filters. Type = Show groups episodes into one row per show per day. From is inclusive, To exclusive; both are absolute instants computed by the client in its own time zone.</summary>
public record HistoryQuery(
    MediaType? Type = null,
    string? Q = null,
    WatchSource? Source = null,
    string? Genre = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    bool Oldest = false,
    int Page = 1,
    int PageSize = 50,
    string? TimeZone = null,    // IANA id; decides where one day ends and the next begins
    bool PerShow = false,       // with Type = Show: one row per show for the whole matched period, not per day
    string? ShowSort = null,    // one-per-show order: null (last watched), "title" or "episodes"; Oldest = first watched
    HistoryUnit Unit = HistoryUnit.Day);   // what the page is grouped by: totals per day, month or year (and the Shows view's rows)

/// <summary>How History is grouped. Totals (HistoryDay) are then per month or year, dated the 1st of it.</summary>
public enum HistoryUnit { Day, Month, Year }

/// <summary>Totals for one day (null = unknown date) across every matching play, not just the visible page. Grouped by
/// month or year (HistoryQuery.Unit), Date is the first day of it.</summary>
public record HistoryDay(DateOnly? Date, int Plays, long Minutes);

/// <summary>People: on friends' history, everyone whose history you can see (for the filter and each row's name).</summary>
public record HistoryPage(IReadOnlyList<HistoryEntry> Items, int Page, int TotalPages, int TotalResults, long TotalMinutes, IReadOnlyList<HistoryDay>? Days = null,
    IReadOnlyList<UserCard>? People = null);

public record WatchlistEntry(MediaSummary Media, DateTimeOffset AddedAt, DateOnly? ReleaseDate);
public record WatchlistRequest(MediaType Type, int TmdbId);

public record RatingRequest(MediaType Type, int TmdbId, double Rating, int? SeasonNumber = null, int? EpisodeNumber = null);
/// <summary>
/// Something you watched lately and haven't rated, for "To rate" on Home: a movie or episode, or a season or
/// (ended) show you've just finished. A season is Type Show with SeasonNumber set, as in RatingRequest.
/// </summary>
public record ToRateItem(string Kind, MediaType Type, int TmdbId, int? SeasonNumber, int? EpisodeNumber,
    string Title, string? Detail, string? PosterPath, DateTimeOffset FinishedAt,
    WatchSource Source = WatchSource.Manual);   // where the last play came from: Jellyfin & co. get a "Rate it?" prompt
/// <summary>"Not this one" on a To rate card (or undoing that).</summary>
public record ToRateSkip(MediaType Type, int TmdbId, int? SeasonNumber = null, int? EpisodeNumber = null);
public record RatingEntry(MediaType Type, int TmdbId, string Title, int? SeasonNumber, int? EpisodeNumber, string? PosterPath, double Rating, DateTimeOffset RatedAt);

public record ShowProgressDto(
    int ShowTmdbId,
    string Name,
    string? PosterPath,
    string? BackdropPath,
    int Aired,
    int Watched,
    DateTimeOffset? LastWatchedAt,
    EpisodeRef? NextEpisode,
    bool Completed,
    ShowTrackStatus Status = ShowTrackStatus.Watching,
    int? Rewatch = null,       // rewatch in progress: Watched/NextEpisode count only plays since it started
    int RewatchesCompleted = 0,
    string? WatchOrder = null,  // name of the alternative order being followed ("Chronological"), if any
    DateOnly? LatestAired = null,    // newest episode that has aired (for "newest episode" sorting)
    string? ShowStatus = null,       // TMDB's status (Returning Series, Ended, Canceled…), filled in for caught-up shows
    EpisodeRef? Returning = null);   // caught up: the next episode TMDB has a date for, if any

/// <summary>How Up Next and Progress are ordered.</summary>
public enum UpNextSort { LastWatched, NewestEpisode, FewestLeft }
public static class UpNextSorts
{
    public static IEnumerable<ShowProgressDto> Apply(IEnumerable<ShowProgressDto> items, UpNextSort sort) => sort switch
    {
        UpNextSort.NewestEpisode => items.OrderByDescending(p => p.LatestAired ?? DateOnly.MinValue).ThenByDescending(p => p.LastWatchedAt),
        UpNextSort.FewestLeft => items.OrderBy(p => p.NextEpisode is null ? int.MaxValue : p.Aired - p.Watched).ThenByDescending(p => p.LastWatchedAt),
        _ => items.OrderByDescending(p => p.LastWatchedAt),
    };
    /// <summary>
    /// Stopped partway and not touched in six months: kept out of Up next (Progress lists it separately) until you
    /// watch another episode. A show you'd caught up on whose new season has since aired doesn't count.
    /// </summary>
    public static bool Stalled(ShowProgressDto p, DateTimeOffset now) =>
        p is { NextEpisode: { } next, Status: ShowTrackStatus.Watching, Rewatch: null, LastWatchedAt: { } last }
        && now - last > TimeSpan.FromDays(183)
        && (next.AirDate is not { } aired || aired <= DateOnly.FromDateTime(last.UtcDateTime));

    public static string Label(UpNextSort s) => s switch { UpNextSort.NewestEpisode => "Newest episode", UpNextSort.FewestLeft => "Fewest left", _ => "Last watched" };
}

/// <summary>Paused and dropped shows are kept out of Up Next and the widget until you resume them.</summary>
public enum ShowTrackStatus { Watching, Paused, Dropped }

/// <summary>Release alerts for a show: Auto (while you're watching it or have it on your watchlist; not paused or
/// dropped), always On, or Off. On and Off decide the calendar too.</summary>
public enum ShowAlerts { Auto, On, Off }
/// <summary>The choice, and whether alerts come with it right now.</summary>
public record ShowAlertState(ShowAlerts Setting, bool Active);

/// <summary>
/// Where you stand with one movie, show or episode, for the right-click / long-press menu. Plays and Rating are the
/// episode's own when one is asked for; the show fields (Watched/Aired/NextEpisode/ShowStatus/Rewatch) are for shows.
/// </summary>
public record ItemStatus(
    int Plays,
    DateTimeOffset? LastWatched,
    double? Rating,
    bool OnWatchlist,
    bool Favorite,
    bool NotInterested,
    IReadOnlyList<Guid> Lists,
    DateOnly? ReleaseDate,
    int Watched = 0,
    int Aired = 0,
    EpisodeRef? NextEpisode = null,
    ShowTrackStatus? ShowStatus = null,
    int? Rewatch = null,
    bool Completed = false,
    OwnedFormat? Owned = null,    // in your library, and how (the first copy)
    IReadOnlyList<OwnedCopy>? Copies = null);
public record ShowStatusRequest(ShowTrackStatus Status);

public record NotInterestedEntry(MediaSummary Media, DateTimeOffset AddedAt);

/// <summary>How you own something in your library.</summary>
public enum OwnedFormat { Digital, BluRay, Uhd, Dvd, MediaServer, Other }
public static class OwnedFormats
{
    /// <summary>
    /// What you own of a title in a few words: "DVD · Media server", or for a show "Blu-ray (S1–3) · Media server".
    /// A format you have the whole show in doesn't list its seasons.
    /// </summary>
    public static string Describe(IEnumerable<OwnedCopy> copies) => string.Join(" · ", copies
        .GroupBy(c => c.Format).OrderBy(g => g.Key)
        .Select(g => g.Any(c => c.SeasonNumber is null) ? Label(g.Key) : $"{Label(g.Key)} ({Seasons(g.Select(c => c.SeasonNumber!.Value))})"));

    /// <summary>Season numbers as ranges: 1, 2, 3, 5 → "S1–3, S5"; specials are "Specials".</summary>
    public static string Seasons(IEnumerable<int> seasons)
    {
        var list = seasons.Distinct().Order().ToList();
        var parts = new List<string>();
        foreach (var n in list.Where(n => n > 0))
        {
            if (parts.Count > 0 && parts[^1] is var last && RangeEnd(last) == n - 1)
                parts[^1] = $"{last.Split('–')[0]}–{n}";
            else parts.Add($"S{n}");
        }
        if (list.Contains(0)) parts.Add("Specials");
        return string.Join(", ", parts);

        static int RangeEnd(string part) => int.Parse(part.Contains('–') ? part[(part.IndexOf('–') + 1)..] : part[1..]);
    }

    public static string Label(OwnedFormat f) => f switch
    {
        OwnedFormat.BluRay => "Blu-ray", OwnedFormat.Uhd => "4K Blu-ray", OwnedFormat.Dvd => "DVD",
        OwnedFormat.MediaServer => "Media server", OwnedFormat.Digital => "Digital", _ => "Other",
    };
}
/// <summary>One thing you own of a title: a format, and for a show either the whole show (SeasonNumber null) or one season.</summary>
public record OwnedCopy(OwnedFormat Format, int? SeasonNumber, WatchSource Source, DateTimeOffset AddedAt);
/// <summary>A title in your library. Format/Source are the first copy's (older apps read only those); Copies has every one.</summary>
public record LibraryEntry(MediaSummary Media, OwnedFormat Format, DateTimeOffset AddedAt, WatchSource Source, bool Watched,
    IReadOnlyList<OwnedCopy>? Copies = null);
public record LibraryRequest(MediaType Type, int TmdbId, OwnedFormat Format, int? SeasonNumber = null);
/// <summary>An empty Text removes the note.</summary>
public record NoteRequest(MediaType Type, int TmdbId, string? Text);
/// <summary>A recommendation and the title of yours that led to it ("Because you liked …").</summary>
public record ForYouItem(MediaSummary Media, string Because, MediaType BecauseType, int BecauseTmdbId);
public record ForYouGroup(MediaSummary Seed, IReadOnlyList<MediaSummary> Items);
/// <summary>Top: best overall. Groups: "Because you liked X" for a few of your favorites. Empty until you've rated or watched a few things.</summary>
public record ForYouDto(IReadOnlyList<ForYouItem> Top, IReadOnlyList<ForYouGroup> Groups);

public record NotificationDto(Guid Id, MediaType Type, int TmdbId, int? SeasonNumber, int? EpisodeNumber, string Title, string? Detail,
    string? PosterPath, DateOnly Day, bool Read, bool Recommendation = false,   // Recommendation: Title is "Sam recommends Heat", Detail their note
    bool RecommendationWatched = false,    // "Sam watched Heat": someone watched a title you recommended them
    Guid? PollId = null,                   // a movie night poll you're in (opens polls/{id})
    EpisodeMark Mark = EpisodeMark.None);
public record NotificationsDto(IReadOnlyList<NotificationDto> Items, int Unread);
/// <summary>Push notifications: whether the server can send them, and the key a browser subscribes with.</summary>
public record PushConfigDto(bool Enabled, string? PublicKey, IReadOnlyList<PushDeviceDto> Devices);
public record PushDeviceDto(string Endpoint, string? Device, DateTimeOffset Since, DateTimeOffset? LastSentAt);
public record PushSubscribeRequest(string Endpoint, string P256dh, string Auth, string? Device = null);
public record PushUnsubscribeRequest(string Endpoint);
/// <summary>The Android app on one phone (InstallId: a GUID it made up on first run).</summary>
public record AppPushRegisterRequest(string InstallId, string? Device = null);
/// <summary>What the service worker shows. Url is relative to the site.</summary>
public record PushPayload(string Title, string Body, string Url, string? Tag = null);
/// <summary>Release alerts and the private calendar feed. CalendarUrl is secret: anyone with it sees your calendar.</summary>
public record AlertSettingsDto(bool Emails, bool EmailAvailable, string CalendarUrl, bool MonthlyBackup = false, string? WatchlistFeed = null, string? HistoryFeed = null);
public record AlertSettingsRequest(bool? Emails = null, bool NewCalendarUrl = false, bool? MonthlyBackup = null);
/// <summary>A list's RSS and JSON Feed links (private lists carry the owner's secret key).</summary>
public record ListFeedsDto(string Rss, string Json);

public record CalendarEntry(
    DateOnly Date,
    MediaType Type,
    int TmdbId,
    string Title,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string? PosterPath,
    bool Watched,
    bool Started = true,             // you've watched some of this show (false: only on your watchlist)
    DateTimeOffset? AirsAt = null,   // exact air time, from TVmaze, when known
    string? Network = null,
    string? Via = null,              // a person you follow made it ("Christopher Nolan"); Type is Movie or Show
    EpisodeMark Mark = EpisodeMark.None);

public record StatsDto(
    int MoviesWatched,
    int MoviePlays,
    int ShowsWatched,
    int EpisodesWatched,
    int EpisodePlays,
    long MinutesMovies,
    long MinutesEpisodes,
    int Ratings,
    double? AverageRating,
    IReadOnlyList<CountBucket> Genres,
    IReadOnlyList<CountBucket> PlaysByMonth,
    IReadOnlyList<CountBucket> RatingDistribution,
    IReadOnlyList<CountBucket> PlaysByWeekday);

public record CountBucket(string Label, int Count);

public record DashboardDto(
    IReadOnlyList<ShowProgressDto> UpNext,
    IReadOnlyList<CalendarEntry> Upcoming,
    IReadOnlyList<HistoryEntry> RecentHistory);

// ---------- Lists ----------

public record ListSummary(Guid Id, string Name, string? Description, bool IsPublic, int ItemCount, DateTimeOffset UpdatedAt, IReadOnlyList<string?> Posters,
    string? Owner = null, int Saves = 0, bool Smart = false,   // Owner: set when it's someone else's list
    bool FriendsCanSee = false, bool Shared = false);           // Shared: someone else's list you can add to, or yours that others can
public record ListDetail(Guid Id, string Name, string? Description, bool IsPublic, string OwnerUserName, bool IsOwner, IReadOnlyList<ListItemDto> Items,
    bool Ranked = false, int Saves = 0, bool Saved = false, SmartRules? Rules = null,
    bool FriendsCanSee = false,
    bool CanEdit = false,                              // the owner or a collaborator: can add, remove, reorder and annotate
    IReadOnlyList<UserCard>? Collaborators = null);

/// <summary>Where a smart list looks: what you've watched, your watchlist, or everything on TMDB.</summary>
public enum SmartSource { History, Watchlist, Tmdb, Library }
public enum SmartSort { Popular, Score, Newest, Oldest, RecentlyWatched, Title }
/// <summary>
/// A list that fills itself in. Genres match any of them. Watched: true = only ones you've seen,
/// false = only ones you haven't, null = either. MinMyRating looks at your own ratings.
/// </summary>
public record SmartRules(SmartSource Source, MediaType? Type = null, IReadOnlyList<string>? Genres = null,
    int? YearFrom = null, int? YearTo = null, double? MinScore = null, double? MinMyRating = null,
    string? Language = null, bool? Watched = null, SmartSort Sort = SmartSort.Popular, int Limit = 50);
public record ListItemDto(Guid Id, MediaSummary Media, DateTimeOffset AddedAt, int Position, string? Note = null);
/// <param name="Collaborators">Usernames (friends only) who can add to the list; null leaves them as they are.</param>
public record CreateListRequest(string Name, string? Description, bool IsPublic, bool Ranked = false, SmartRules? Rules = null,
    bool FriendsCanSee = false, IReadOnlyList<string>? Collaborators = null);
/// <summary>Every item id of the list, in the new order.</summary>
public record ListOrderRequest(IReadOnlyList<Guid> ItemIds);
public record ListNoteRequest(string? Note);
public record ListItemRequest(MediaType Type, int TmdbId);

// ---------- Integrations ----------

public record JellyfinSettingsDto(string WebhookUrlPath, int WatchedThresholdPercent, IReadOnlyList<JellyfinEventDto> RecentEvents,
    string PlexUrlPath = "", string? PlexUserName = null);
public record PlexUserRequest(string? UserName);
public record JellyfinEventDto(DateTimeOffset ReceivedAt, string? ItemName, string Outcome);
public record JellyfinThresholdRequest(int WatchedThresholdPercent);

/// <summary>MediaServer: a list of what's on your Jellyfin (its Reports plugin's CSV), for your library.</summary>
public enum ImportKind { Trakt, Letterboxd, Imdb, TvTime, Simkl, MediaServer }

/// <summary>What an export holds, read before importing (after the parts and "only since" choices). Nothing is matched or saved yet.</summary>
public record ImportPreview(int Plays, int Ratings, int Watchlist, int ListItems, int Lists, int Favorites, int Library, int ShowsWithProgress, DateOnly? From, DateOnly? To)
{
    public int Total => Plays + Ratings + Watchlist + ListItems + Favorites + Library + ShowsWithProgress;
}
public enum ImportStatus { Queued, Running, Completed, Failed }

/// <summary>Kinds of entries in the admin activity log.</summary>
public enum ActivityKind
{
    Watch, BulkWatch, Unwatch, DeletePlay,
    Rate, Unrate,
    WatchlistAdd, WatchlistRemove,
    ShowStatus, RewatchStart, RewatchStop,
    CheckIn, CheckInCancel,
    ListCreate, ListEdit, ListDelete, ListAdd, ListRemove,
    Import, ClearData,
    SignUp, AccountDeleted, PasswordChanged, PasswordReset,
    Follow, Unfollow,
    Rematch, RestorePlays,
    FavoriteAdd, FavoriteRemove,
    EditPlay,
    GoalSet, GoalDelete, LetterboxdSync,
    ImportUndo, ImportResolve, ListReorder, ListNote, WatchOrder,
    ProfilePicture,
    EmailChanged, UserNameChanged,
    Undo,   // something undone from Recent actions (Detail says what)
}

/// <summary>
/// What reversing an action needs, kept with it in the activity log: the plays it added (PlayIds) or took away
/// (Plays), the dates plays had (Dates), a rating or show status from before (Rating, Status; Had: there was a
/// rating), the list (ListId), whether the title was already on the watchlist (Was), the person followed (User).
/// </summary>
public record UndoData(IReadOnlyList<Guid>? PlayIds = null, IReadOnlyList<RestorePlay>? Plays = null, IReadOnlyList<PlayDate>? Dates = null,
    double? Rating = null, bool Had = false, ShowTrackStatus? Status = null, Guid? ListId = null, bool Was = false, string? User = null);

/// <summary>One of your actions on the Recent actions page. Title/PosterPath: the movie or show it was about.</summary>
public record RecentAction(long Id, DateTimeOffset At, ActivityKind Kind, string? Title, string? PosterPath, MediaType? Type, int? TmdbId,
    int? SeasonNumber, int? EpisodeNumber, string? Detail, bool CanUndo, DateTimeOffset? UndoneAt);

/// <summary>What an import brings in. Movies/Shows pick the kind of title; the rest pick the kind of data.</summary>
[Flags]
public enum ImportParts
{
    None = 0,
    Movies = 1, Shows = 2,
    History = 4, Ratings = 8, Watchlist = 16, Lists = 32,   // Lists includes favorites
    Library = 64,                                           // what you own (Trakt's collection)
    All = Movies | Shows | History | Ratings | Watchlist | Lists | Library,
}

public record ImportJobDto(
    Guid Id,
    ImportKind Kind,
    string FileName,
    ImportStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    int Total,
    int Processed,
    int Imported,
    int Skipped,
    int Failed,
    string? Message,
    IReadOnlyList<string> Unmatched,
    ImportParts Parts = ImportParts.All,
    DateTimeOffset? UndoneAt = null,
    bool CanUndo = false,                          // imports from before undo existed can't be undone
    IReadOnlyList<PendingTitle>? Pending = null,   // unmatched titles that can still be matched by hand
    IReadOnlyList<MatchCheck>? Checks = null,      // titles matched by name where TMDB has others of that name
    DateOnly? Since = null);                       // only what's dated on or after this day was brought in

/// <summary>
/// An import title matched by name when TMDB has more than one of that name (Under the Skin: Glazer's
/// 2014 film and a 2013 one). SeenIt picked one; this asks you to confirm it or switch.
/// </summary>
public record MatchCheck(MediaType Type, int TmdbId, string Label, string Title, int? Year, string? PosterPath, IReadOnlyList<MatchOption> Others);
public record MatchOption(int TmdbId, string Title, int? Year, string? PosterPath);
/// <summary>Keep the pick (ToTmdbId null) or move everything to another title.</summary>
public record ResolveCheckRequest(MediaType Type, int TmdbId, int? ToTmdbId);

/// <summary>An unmatched import title, with what it would bring in.</summary>
public record PendingTitle(string Key, string Label, MediaType Type, string? Title, int? Year, string Summary);
public record ResolveImportRequest(string Key, int TmdbId);
public record ResolveImportResult(int Imported, int Skipped);
public record UndoImportResult(int Plays, int Ratings, int Watchlist, int ListItems, int Favorites, int Lists, int Library = 0);

/// <summary>Several plays at once: one date for all (kept in their order, a minute apart), each one's release date, or unknown.</summary>
public record BulkEditPlaysRequest(IReadOnlyList<Guid> Ids, WatchTime When, DateTimeOffset? WatchedAt = null);
public record PlayDate(Guid Id, DateTimeOffset WatchedAt);

// ---------- Month / year in review ----------

public record PeriodTotals(
    int Plays,
    int MoviePlays,
    int Movies,          // distinct
    int EpisodePlays,
    int Episodes,        // distinct
    int Shows,           // distinct
    long Minutes,
    int Ratings);

/// <summary>
/// What goes on a month/year in review image. Lists are comma-separated keys, in the order they're drawn
/// (ReviewCardChoices names them). Settable properties so they bind from the image's query string.
/// </summary>
public class ReviewCardOptions
{
    public string Tiles { get; set; } = "hours,days,movies,episodes";   // up to 4 big numbers
    public int Shows { get; set; } = 5;                                  // top shows' posters, 0–5
    public int Movies { get; set; } = 5;                                 // top movies' posters, 0–5
    public bool Ratings { get; set; } = true;                            // your rating under each poster
    public string Facts { get; set; } = "genre,streak,actor,rating,goals";
    public bool Name { get; set; } = true;                               // @username at the top
    public string? Theme { get; set; }                                   // a theme key; null = yours
    public string? Accent { get; set; }                                  // an accent key; null = the theme's
    public ReviewNew New { get; set; }                                   // everything, first watches, or titles new to you
    public string? Type { get; set; }                                    // "movie" or "tv" for just those (MediaFilter)

    public IReadOnlyList<string> TileKeys() => Keys(Tiles, ReviewCardChoices.Tiles).Take(4).ToList();
    public IReadOnlyList<string> FactKeys() => Keys(Facts, ReviewCardChoices.Facts).ToList();
    private static IEnumerable<string> Keys(string? list, (string Key, string Label)[] allowed) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => allowed.Any(a => a.Key == k)).Distinct();
}

/// <summary>Stats and reviews for just movies or just TV ("?type=movie" / "?type=tv"); null is everything.</summary>
public static class MediaFilter
{
    public static MediaType? Parse(string? type) => type?.ToLowerInvariant() switch
    {
        "movie" or "movies" => MediaType.Movie,
        "tv" or "show" or "shows" => MediaType.Show,
        _ => null,
    };
    public static string? Key(MediaType? kind) => kind switch { MediaType.Movie => "movie", MediaType.Show => "tv", _ => null };
    /// <summary>Whether a play or rating of this type counts: TV takes shows, seasons and episodes alike.</summary>
    public static bool Keeps(MediaType? kind, MediaType type) => kind is null || (kind == MediaType.Movie ? type == MediaType.Movie : type != MediaType.Movie);
}

public static class ReviewCardChoices
{
    public static readonly (string Key, string Label)[] Tiles =
    [
        ("hours", "Hours watched"), ("days", "Days with a play"), ("movies", "Movies"), ("episodes", "Episodes"), ("shows", "Shows"),
        ("plays", "Plays"), ("ratings", "Ratings given"), ("streak", "Longest streak"), ("new", "New to you"),
        ("started", "Shows started"), ("finished", "Shows finished"),
    ];
    public static readonly (string Key, string Label)[] Facts =
    [
        ("genre", "Top genre"), ("streak", "Longest streak"), ("actor", "Most-watched actor"), ("director", "Most-watched director or creator"),
        ("rating", "Average rating"), ("goals", "Goals (year)"), ("busiest", "Busiest day"), ("weekday", "Favorite weekday"),
        ("network", "Top network"), ("finished", "Shows finished"), ("new", "Titles new to you"),
    ];
}

public record ReviewItem(
    MediaType Type,
    int TmdbId,
    string Title,
    string? PosterPath,
    int Plays,
    long Minutes,
    double? Rating,
    string? Detail,
    bool New = false);   // your first-ever play of it is in this period

public record ReviewPerson(int Id, string Name, string? ProfilePath, int Titles, int Plays);

public record ReviewDay(DateOnly Date, int Plays, long Minutes);

public record ReviewStreak(int Days, DateOnly? Start, DateOnly? End);

public record ReviewDto(
    int Year,
    int? Month,
    string Label,
    string TimeZone,
    PeriodTotals Totals,
    PeriodTotals Previous,
    string PreviousLabel,
    int DaysActive,
    int DaysInPeriod,
    ReviewStreak LongestStreak,
    ReviewDay? BusiestDay,
    string? FavoriteWeekday,
    int? PeakHour,
    double AveragePlaysPerActiveDay,
    IReadOnlyList<CountBucket> Timeline,        // per day (month) or per month (year)
    IReadOnlyList<CountBucket> Weekdays,
    IReadOnlyList<CountBucket> Hours,
    IReadOnlyList<ReviewItem> TopShows,
    IReadOnlyList<ReviewItem> TopMovies,
    IReadOnlyList<ReviewPerson> TopActors,
    IReadOnlyList<ReviewPerson> TopDirectors,
    IReadOnlyList<CountBucket> Genres,
    IReadOnlyList<CountBucket> Networks,
    IReadOnlyList<CountBucket> Languages,
    IReadOnlyList<CountBucket> Countries,
    IReadOnlyList<CountBucket> Decades,
    IReadOnlyList<CountBucket> Sources,
    double? AverageRating,
    IReadOnlyList<CountBucket> RatingDistribution,
    IReadOnlyList<ReviewItem> HighestRated,
    int ShowsStarted,
    int ShowsCompleted,
    int FirstTimeWatches,
    int Rewatches,
    ReviewItem? LongestMovie,
    ReviewItem? OldestMovie,
    ReviewItem? NewestMovie,
    HistoryEntry? FirstPlay,
    HistoryEntry? LastPlay,
    ReviewItem? MostRewatched,
    DateOnly? TrackingSince = null,    // reviews only count from here (see Settings → Stats)
    IReadOnlyList<ReviewItem>? AllShows = null,     // every show / movie in the period, same order as the top lists
    IReadOnlyList<ReviewItem>? AllMovies = null,
    ReviewNew New = ReviewNew.All);                 // what it was built from (see ReviewNew)

/// <summary>
/// What a review counts. Firsts: movies and episodes watched for the first time in the period, so a show
/// you started earlier still counts for its new episodes, and rewatches don't. Titles: only movies and
/// shows whose very first play is in the period.
/// </summary>
public enum ReviewNew { All, Firsts, Titles }

public record ReviewMonth(int Month, int Plays, long Minutes);
public record ReviewYear(int Year, int Plays, long Minutes, IReadOnlyList<ReviewMonth> Months);

// ---------- Where to watch ----------

public record ProviderDto(int Id, string Name, string? LogoPath);
public record WatchProvidersDto(
    string Region, string? Link,
    IReadOnlyList<ProviderDto> Stream, IReadOnlyList<ProviderDto> Free, IReadOnlyList<ProviderDto> Ads,
    IReadOnlyList<ProviderDto> Rent, IReadOnlyList<ProviderDto> Buy)
{
    public bool IsEmpty => Stream.Count + Free.Count + Ads.Count + Rent.Count + Buy.Count == 0;
}
public record RegionDto(string Code, string Name);

// ---------- Check-ins ("watching now") ----------

public record CheckInRequest(MediaType Type, int TmdbId, int? SeasonNumber = null, int? EpisodeNumber = null);

/// <summary>Type is Movie or Episode. Manual check-ins are recorded as a play when EndsAt passes; Jellyfin ones on stop.</summary>
public record NowWatchingDto(
    MediaType Type, int TmdbId, string Title, int? SeasonNumber, int? EpisodeNumber, string? EpisodeTitle,
    string? PosterPath, string? BackdropPath, DateTimeOffset StartedAt, DateTimeOffset EndsAt, WatchSource Source,
    bool? YouWatched = null,   // Someone else's check-in: whether the viewer has seen this episode (null on your own)
    DateTimeOffset? PausedAt = null);   // the player is paused (Jellyfin/Plex/Emby): progress and time left hold here

// ---------- Social ----------

/// <summary>
/// Someone you follow (or who follows you) who has watched or rated a title. Plays: for an episode or
/// movie, how many times; for a show or season, how many different episodes (out of the aired count).
/// </summary>
public record FriendSeen(UserCard User, int Plays, DateTimeOffset? LastWatched, double? Rating);

/// <summary>Avatar: the profile picture's path on the server (versioned), or null for the initial.</summary>
public record UserCard(string UserName, string? DisplayName, bool Following, bool FollowsYou, string? Avatar = null, bool Requested = false)   // Requested: you asked to follow and they haven't answered
{
    public string Name => DisplayName ?? UserName;
}

public record ProfileStats(int Movies, int Shows, int Episodes, long Minutes, int Ratings);

public record ProfileDto(
    UserCard User, DateTimeOffset JoinedAt, bool IsMe, ProfileVisibility Visibility, bool CanView,
    int Followers, int Following,
    NowWatchingDto? NowWatching, ProfileStats? Stats,
    IReadOnlyList<HistoryEntry> Recent, IReadOnlyList<RatingEntry> TopRated, IReadOnlyList<ListSummary> Lists,
    IReadOnlyList<FavoriteEntry>? Favorites = null,
    IReadOnlyList<ProfileWidget>? Layout = null,    // the widgets this viewer may see, in order
    string? Banner = null,                          // backdrop image path behind the name
    string? Accent = null,                          // the owner's accent color key
    string? Badge = null,                           // the first pinned badge's label (for app versions before Badges)
    IReadOnlyList<BadgeDto>? Badges = null,         // badges pinned next to the name, in the owner's order
    IReadOnlyList<ProfilePart>? Pages = null);      // which of their shared pages (u/{name}/progress, …) this viewer may open

/// <summary>Badge tiers, lowest first; Special is for one-off badges with no ladder.</summary>
public enum BadgeTier { Bronze, Silver, Gold, Platinum, Diamond, Emerald, Ruby, Sapphire, Obsidian, Mythic, Special }
/// <summary>
/// A badge family ("movies", "genre:Horror") at the tier reached. Locked (not Earned) badges carry the first tier's
/// name and the progress toward it, for the picker. Progress/Goal: toward the next tier (null at the top).
/// </summary>
public record BadgeDto(string Id, string Label, string Icon, BadgeTier Tier, string Description, bool Earned,
    long? Progress = null, long? Goal = null, string? Next = null);

/// <summary>A movie or show the user marked as a favorite, in their order.</summary>
public record FavoriteEntry(MediaSummary Media, DateTimeOffset AddedAt);
/// <summary>Four favorite movies and four favorite shows.</summary>
public static class FavoriteLimits { public const int PerType = 4; }

public record FavoriteRequest(MediaType Type, int TmdbId);

public record ActivityItem(UserCard User, HistoryEntry Entry);
public record FriendWatching(UserCard User, NowWatchingDto Item);
/// <summary>Answer to a follow: Requested when they approve followers first.</summary>
public record FollowResult(bool Requested);
/// <summary>Poster badges: one source's score for many titles at once.</summary>
public record BadgeRequest(string Source, IReadOnlyList<BadgeTitle> Titles);
public record BadgeTitle(MediaType Type, int TmdbId);
public record BadgeScore(MediaType Type, int TmdbId, double Value, double Max, bool Percent);
public record FeedDto(IReadOnlyList<FriendWatching> WatchingNow, IReadOnlyList<ActivityItem> Activity);

/// <summary>Two people side by side (/compare/{a}/{b}). Every list is complete; the page pages them.</summary>
public record CompareDto(
    UserCard A, UserCard B, ProfileStats AStats, ProfileStats BStats,
    int? Compatibility,            // 0–100 from titles both rated; null when there are too few
    int SharedMovies, int SharedShows,
    IReadOnlyList<CompareItem> RatedBoth,     // closest agreement first
    IReadOnlyList<CompareItem> WatchedBoth,   // most recently watched by A first
    IReadOnlyList<CompareItem> BLovedANot,    // rated 8+ by B, not watched by A
    IReadOnlyList<CompareItem> ALovedBNot,
    IReadOnlyList<CompareItem>? AOnly = null,   // watched by A, not by B, most recently watched first
    IReadOnlyList<CompareItem>? BOnly = null);  // watched by B, not by A
/// <summary>A title in a comparison, with each person's rating if they rated it.</summary>
public record CompareItem(MediaSummary Media, double? A, double? B, IReadOnlyList<string>? Genres = null);   // Genres: for Compare's filters

// ---------- Pick for me ----------

public enum PickSource { Watchlist, Paused, Both }
public enum PickKind { Any, Movie, Show }
public record PickDto(
    MediaSummary Media,
    string Reason,                         // "On your watchlist" / "Paused show"
    int? Minutes,                          // movie runtime or episode length
    EpisodeRef? NextEpisode,
    IReadOnlyList<ProviderDto> Streaming,  // flatrate/free/ads in the user's region
    int Candidates,                        // how many titles matched the filters
    int? FitEpisodes = null,               // with a time budget: how many episodes fit, starting at NextEpisode
    int? FitMinutes = null,                // how long that (or the movie) takes
    EpisodeRef? FitThrough = null);        // the last episode that fits

// ---------- On this day ----------

public record OnThisDayItem(int YearsAgo, DateOnly Date, MediaType Type, int TmdbId, string Title, string? PosterPath, string Text);

// ---------- Admin ----------

public record AdminDeployment(string Version, string Commit, string? Subject, DateTimeOffset? BuiltAt, DateTimeOffset StartedAt);
public record AdminLogEntry(DateTimeOffset At, string Level, string Category, string Message);

public record AdminOverview(
    string Version, string Commit, string? Subject, DateTimeOffset? BuiltAt, DateTimeOffset StartedAt,
    string Runtime, string Os, long MemoryMb, string DatabaseSize,
    IReadOnlyList<AdminDeployment> Deployments,
    IReadOnlyList<CountBucket> Counts,          // users, plays, ratings, … across everyone
    IReadOnlyList<AdminSetting> Config,
    int ActiveToday, int ActiveWeek, int ActionsToday,
    IReadOnlyList<CountBucket> ActivityByDay,   // last 30 days
    int RecentProblems,
    IReadOnlyList<AdminSetting>? Health = null,  // backups, off-site copy, alerts
    IReadOnlyList<AdminAlert>? Alerts = null,
    IReadOnlyList<AdminPackage>? OutdatedPackages = null);
public record AdminPackage(string Project, string Package, string Current, string Latest);
public record AdminAlert(DateTimeOffset At, string Subject, string Body, bool Emailed);

public record AdminSetting(string Name, string Value, bool Ok);

public record AdminUser(
    Guid Id, string UserName, string? DisplayName, string Email, bool EmailConfirmed,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? LastActivityAt,
    int MoviePlays, int EpisodePlays, int Ratings, int Watchlist, int Lists,
    int Following, int Followers, string? InvitedBy, ProfileVisibility Visibility,
    bool JellyfinActive, bool LockedOut, bool IsAdmin, DateOnly? TrackingSince);

public record AdminActivity(
    long Id, DateTimeOffset At, string UserName, ActivityKind Kind, WatchSource Source,
    MediaType? Type, int? TmdbId, int? SeasonNumber, int? EpisodeNumber, string? Title, string? Detail);
public record AdminActivityPage(IReadOnlyList<AdminActivity> Items, int Page, int TotalPages, int Total);

public record AdminImport(
    Guid Id, string UserName, ImportKind Kind, ImportParts Parts, string FileName, ImportStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int Total, int Processed, int Imported, int Skipped, int Failed, string? Message, IReadOnlyList<string> Unmatched);

public record AdminJellyfinEvent(DateTimeOffset At, string UserName, string? Item, string? Outcome);
public record AdminInvite(string Code, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? UsedBy, DateTimeOffset? UsedAt);
public record AdminResetLink(string Url, DateTimeOffset ExpiresAt);

// ---------- Fixing data ----------

/// <summary>A play as it was, so a deletion can be undone exactly (date and source included).</summary>
public record RestorePlay(MediaType Type, int TmdbId, int? SeasonNumber, int? EpisodeNumber, DateTimeOffset WatchedAt, WatchSource Source);
public record RemovedPlays(int Removed, IReadOnlyList<RestorePlay> Plays);
/// <summary>Changes when a play happened: a date, the release/air date, or "unknown".</summary>
public record EditPlayRequest(WatchTime When, DateTimeOffset? WatchedAt = null);

/// <summary>Moves everything of yours from one TMDB title to another (a wrong match). Type is Movie or Show.</summary>
public record RematchRequest(MediaType Type, int FromTmdbId, int ToTmdbId);
public record RematchResult(int Plays, int Ratings, int Watchlist, int ListEntries);

/// <summary>Plays of the same movie/episode within a day of each other: probably logged twice.</summary>
public record DuplicateGroup(MediaType Type, int TmdbId, int? SeasonNumber, int? EpisodeNumber, string Title, string? PosterPath,
    IReadOnlyList<DuplicatePlay> Plays);
public record DuplicatePlay(Guid Id, DateTimeOffset WatchedAt, WatchSource Source);

public record TimeZoneOption(string Id, string Label);

/// <summary>The newest Android app on this server, for "update available" in the app.</summary>
public record AdminEndpointTiming(string Route, long Count, double AvgMs, double P95Ms, double MaxMs);

// ---------- More stats (Stats page, below the totals) ----------

public record MoreStatsDto(
    DateOnly? TrackingSince,
    ReleaseGapStats ReleaseGap,
    CompletionStats Completion,
    IReadOnlyList<HeatmapDay> Heatmap,            // last 365 days (from the tracking date), minutes per day
    IReadOnlyList<IReadOnlyList<int>> WeekHour,   // [weekday 0=Sun][hour] plays
    RuntimeStats Runtime,
    EraStats Era,
    IReadOnlyList<CountBucket> Studios,
    FranchiseStats Franchises,
    BacklogStats Backlog,
    RatingsVsTmdb RatingsVsTmdb,
    FriendsBoard Friends,
    IReadOnlyList<Milestone> Milestones);

/// <summary>How long after release you watch things (first watch of each movie/episode).</summary>
public record ReleaseGapStats(int Count, int? MedianDays, int DayOnePercent, int NewReleasePercent, IReadOnlyList<CountBucket> Buckets);

/// <summary>Shows you've started: finished, still going, paused, dropped or stalled (no play in 6 months).</summary>
public record CompletionStats(
    int Finished, int Watching, int Caught, int Paused, int Dropped, int Stalled,
    int PilotOnly, double? MedianStopEpisode,
    IReadOnlyList<CompletionRow> ByGenre, IReadOnlyList<CompletionRow> ByNetwork);
public record CompletionRow(string Label, int Started, int Finished, int Abandoned);

public record HeatmapDay(DateOnly Date, int Minutes, int Plays);

public record RuntimeStats(int Movies, int? AverageMinutes, int ShortPercent, int EpicPercent, IReadOnlyList<CountBucket> AverageByYear);

/// <summary>Average release year of the movies you watched, overall and per month.</summary>
public record EraStats(int? AverageYear, IReadOnlyList<CountBucket> ByMonth);

public record FranchiseStats(int Started, int Completed, IReadOnlyList<FranchiseProgress> Items);
public record FranchiseProgress(int Id, string Name, string? PosterPath, int Seen, int Released, MediaSummary? Next,
    DateTimeOffset? LastWatchedAt = null, int Upcoming = 0);   // Upcoming: announced parts not out yet

public record BacklogStats(int Shows, int Episodes, long Minutes, int MinutesPerDay, DateOnly? CaughtUpBy);

public record RatingsVsTmdb(int Count, double? AverageDifference, IReadOnlyList<HotTake> Higher, IReadOnlyList<HotTake> Lower);
public record HotTake(MediaSummary Media, double You, double Tmdb);

public record FriendsBoard(string Period, IReadOnlyList<LeaderRow> Rows, MediaSummary? TopTitle, int TopTitleFriends);
public record LeaderRow(UserCard User, long Minutes, int Plays, bool IsMe);

public record Milestone(string Label, DateTimeOffset? At, MediaSummary? Item, bool BeforeTracking);

// ---------- Goals ----------

/// <summary>ShowsStartedAndFinished: ended shows whose first episode and last you both watched in the year (not caught up on a running one).</summary>
public enum GoalKind { Movies, NewMovies, Episodes, Hours, ShowsFinished, ShowsStartedAndFinished }

public static class GoalKinds
{
    public static string Label(GoalKind k) => k switch
    {
        GoalKind.Movies => "Movies",
        GoalKind.NewMovies => "New-to-me movies",
        GoalKind.Episodes => "Episodes",
        GoalKind.Hours => "Hours watched",
        GoalKind.ShowsStartedAndFinished => "Shows started & finished",
        _ => "Shows finished",
    };

    /// <summary>"movies", "hours"… after a number.</summary>
    public static string Unit(GoalKind k, double n) => k switch
    {
        GoalKind.Movies => n == 1 ? "movie" : "movies",
        GoalKind.NewMovies => n == 1 ? "new movie" : "new movies",
        GoalKind.Episodes => n == 1 ? "episode" : "episodes",
        GoalKind.Hours => n == 1 ? "hour" : "hours",
        GoalKind.ShowsStartedAndFinished => n == 1 ? "show started & finished" : "shows started & finished",
        _ => n == 1 ? "show finished" : "shows finished",
    };

    public static string Hint(GoalKind k) => k switch
    {
        GoalKind.Movies => "Every movie play counts, rewatches too.",
        GoalKind.NewMovies => "Only movies you'd never seen before.",
        GoalKind.Episodes => "Every episode play counts.",
        GoalKind.Hours => "Movies and episodes, by runtime.",
        GoalKind.ShowsStartedAndFinished => "Shows that have ended, watched from your first episode to your last within the year. Being caught up on one still running doesn't count, nor does one you started in an earlier year.",
        _ => "Shows you caught up on or finished this year.",
    };
}

/// <param name="Expected">Where you'd be by today on an even pace (the target once the year is over).</param>
/// <summary>A yearly challenge: Target slots to fill from what you watch in the year.</summary>
public record ChallengeDef(string Key, string Title, string Description, int Target);

public static class Challenges
{
    public static readonly ChallengeDef[] All =
    [
        new("decades", "Every decade", "A movie from each decade, the 1920s to the 2020s", 11),
        new("countries", "Around the world", "Movies made in 10 different countries", 10),
        new("languages", "Lost in translation", "Movies in 5 languages other than English", 5),
        new("silent", "Before sound", "5 movies from before 1930", 5),
        new("classics", "Golden age", "10 movies from before 1970", 10),
        new("epics", "Epics", "3 movies over three hours long", 3),
        new("genres", "Genre hopper", "Movies from 12 different genres", 12),
        new("docs", "True stories", "6 documentaries", 6),
        new("animation", "Drawn in", "5 animated movies", 5),
        new("fresh", "Fresh off the screen", "12 movies in the year they came out", 12),
        new("longrunner", "In it for the long haul", "Episodes of a show that ran 10 seasons or more", 1),
        new("worldtv", "World TV", "Shows from 5 different countries", 5),
    ];
}

/// <summary>A challenge's progress: one slot per thing that counts (a decade, a country…), with the title that filled it.</summary>
public record ChallengeDto(string Key, string Title, string Description, int Target, int Count, IReadOnlyList<ChallengeSlot> Slots);
/// <param name="Label">What the slot is ("1940s", "Japan"), or the title itself for a plain count.</param>
/// <param name="Item">What filled it; null for a slot still open (decades show every one).</param>
public record ChallengeSlot(string Label, MediaSummary? Item);

public record GoalDto(Guid Id, int Year, GoalKind Kind, int Target, double Current, double Expected, bool YearOver);
public record GoalRequest(int Year, GoalKind Kind, int Target);

// ---------- Letterboxd feed sync ----------

public record LetterboxdSyncDto(string? Username, DateTimeOffset? LastCheckedAt, DateTimeOffset? LastAddedAt, string? LastResult, bool LastFailed);
public record LetterboxdSyncRequest(string Username);

// ---------- Searching lists ----------

/// <summary>A title found in your watchlist and/or lists.</summary>
public record ListSearchHit(MediaSummary Media, bool OnWatchlist, IReadOnlyList<ListRef> Lists);
public record ListRef(Guid Id, string Name);

// ---------- Alternative watch orders ----------

/// <param name="Kind">TMDB's kind of order: "Original air date", "DVD", "Story arc"...</param>
public record WatchOrderOption(string Id, string Name, string Kind, string? Description, int Episodes, int Parts);
/// <param name="SelectedId">The order you're following; null for TMDB's seasons.</param>
public record WatchOrdersDto(IReadOnlyList<WatchOrderOption> Options, string? SelectedId);
public record WatchOrderDto(string Id, string Name, string? Description, int Aired, int Watched, IReadOnlyList<WatchOrderPart> Parts);
public record WatchOrderPart(string Name, IReadOnlyList<WatchOrderEpisode> Episodes);
public record WatchOrderEpisode(int Position, int SeasonNumber, int EpisodeNumber, string? Name, DateOnly? AirDate, int? Runtime, bool Watched, bool IsNext);
public record SetWatchOrderRequest(string? OrderId);
/// <summary>
/// Mark everything up to and including this episode (or, without an episode, the end of this season)
/// as watched: in the watch order you follow when InOrder is set, otherwise in TMDB's season order
/// (specials only up to a special).
/// </summary>
/// <summary>What's left to watch up to an episode: aired episodes not yet seen (it included), and their total runtime.</summary>
public record CatchUp(int Episodes, long Minutes);

public record WatchThroughRequest(int SeasonNumber, int? EpisodeNumber, WatchTime When, DateTimeOffset? WatchedAt = null, bool InOrder = false,
    bool SeasonOnly = false);   // TMDB order only: start from the season's first episode, not the show's
