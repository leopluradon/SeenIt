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
        ImportKind.Imdb => WatchSource.ImdbImport, ImportKind.TvTime => WatchSource.TvTimeImport, _ => WatchSource.SimklImport,
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

/// <summary>How long stretches of time read: "48d 7h" or "1,159h". Under a day both read "5h 12m".</summary>
public enum TimeFormat { DaysHours, Hours }

/// <summary>How ratings are given and shown. They're stored as 1–10 (to one decimal) whatever the scale.</summary>
public enum RatingScale { Ten, FiveStars, Decimal }

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
public enum ListLayout { Posters, Rows }
/// <summary>How episodes are labeled: S02E05, 2x05, or Season 2, Episode 5.</summary>
public enum EpisodeStyle { Code, Cross, Long }
/// <summary>A filter saved as a quick chip on a page: History, Watchlist or Progress, plus its query string.</summary>
public record SavedView(string Page, string Name, string Query);
public enum PosterSize { Small, Medium, Large }
public enum BackupEvery { Monthly, Weekly }
/// <summary>Which files a backup holds: JSON and CSV, or just one kind.</summary>
public enum ExportFormat { Both, Json, Csv }
/// <summary>Parts of your profile that can have their own audience (null = same as the profile).</summary>
public enum ProfilePart { History, Ratings, Stats, WatchingNow, Favorites }

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
    string? ProfileBadge = null)                     // a milestone shown next to your name (its label)
{
    public static readonly UserPrefs Default = new();
    public static readonly string[] StartPages = ["home", "progress", "calendar", "watchlist", "discover"];
    public static readonly (string Key, string Name)[] ScoreSources =
        [("imdb", "IMDb"), ("tomatoes", "Rotten Tomatoes"), ("popcorn", "RT audience"), ("metacritic", "Metacritic"),
         ("metacriticuser", "Metacritic users"), ("letterboxd", "Letterboxd"), ("trakt", "Trakt"), ("rogerebert", "Roger Ebert"), ("myanimelist", "MyAnimeList")];
    public const int DefaultRowCount = 12;
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
    public int Count(string section) => HomeCounts?.TryGetValue(section, out var n) == true ? n : DefaultRowCount;
    public ListLayout LayoutFor(string page) => PageLayouts?.TryGetValue(page, out var l) == true ? l : ListLayout;
    public string? SortFor(string page) => PageSorts?.TryGetValue(page, out var s) == true ? s : null;
    /// <summary>Who can see this part: its own setting, or the profile's.</summary>
    public ProfileVisibility For(ProfilePart part, ProfileVisibility profile) =>
        PartVisibility?.TryGetValue(part, out var v) == true ? v : profile;

    /// <summary>Clamps everything to allowed values.</summary>
    public UserPrefs Clean() => this with
    {
        StartPage = StartPages.Contains(StartPage) ? StartPage : "home",
        Scores = Scores?.Where(s => ScoreSources.Any(x => x.Key == s)).Distinct().ToList(),
        HomeCounts = HomeCounts?.Where(kv => UiPrefs.HomeSections.Any(h => h.Key == kv.Key)).ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 4, 40)),
        ComingUpDays = ComingUpDays is 7 or 14 or 30 ? ComingUpDays : 14,
        HiddenScores = HiddenScores?.Where(h => h.Split('/') is [var src, var kind] && (src is "tmdb" or "friends" || ScoreSources.Any(x => x.Key == src))
            && ScoreKinds.Any(k => k.Key == kind)).Distinct().ToList(),
        ScoreSpoilers = Enum.IsDefined(ScoreSpoilers) ? ScoreSpoilers : ScoreSpoilers.Off,
        PosterScoreMovies = PosterScoreChoices(true).Any(c => c.Key == PosterScoreMovies) ? PosterScoreMovies : "tmdb",
        PosterScoreShows = PosterScoreChoices(false).Any(c => c.Key == PosterScoreShows) ? PosterScoreShows : "tmdb",
        PartVisibility = PartVisibility?.Where(kv => Enum.IsDefined(kv.Key) && Enum.IsDefined(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value),
        Accent = UiPrefs.Accents.Any(a => a.Key == Accent) ? Accent : null,
        EpisodeStyle = Enum.IsDefined(EpisodeStyle) ? EpisodeStyle : EpisodeStyle.Code,
        PageLayouts = PageLayouts?.Where(kv => UiPrefs.LayoutPages.Contains(kv.Key) && Enum.IsDefined(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value),
        PageSorts = PageSorts?.Where(kv => UiPrefs.SortPages.Contains(kv.Key) && kv.Value is { Length: > 0 and <= 30 }).ToDictionary(kv => kv.Key, kv => kv.Value),
        SavedViews = SavedViews?.Where(v => UiPrefs.ViewPages.Contains(v.Page) && !string.IsNullOrWhiteSpace(v.Name) && (v.Query?.Length ?? 0) <= 500)
            .Select(v => v with { Name = v.Name.Trim()[..Math.Min(v.Name.Trim().Length, 40)], Query = (v.Query ?? "").TrimStart('?') })
            .Take(UiPrefs.MaxSavedViews).ToList(),
        ProfileBanner = ProfileBanner is { Type: MediaType.Movie or MediaType.Show } ? ProfileBanner : null,
        ProfileBadge = ProfileBadge is { Length: > 0 and <= 80 } ? ProfileBadge : null,
    };
}

/// <summary>Who can see your profile, history and stats. Lists have their own public/private switch.</summary>
public enum ProfileVisibility { Public, Friends, Private }

/// <summary>The customizable bits of the UI and their allowed keys.</summary>
public static class UiPrefs
{
    public static readonly (string Key, string Label)[] HomeSections =
    [
        ("now", "Watching now"),
        ("onthisday", "On this day"),
        ("review", "Month / year in review banner"),
        ("goals", "Goals"),
        ("upnext", "Up next"),
        ("upcoming", "Coming up"),
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
    public static readonly string[] HiddenByDefault = ["w-month", "w-heatmap", "w-genres", "w-records"];

    /// <summary>Pages with their own posters/rows choice, sort memory and saved views.</summary>
    public static readonly string[] LayoutPages = ["watchlist", "ratings", "list"];
    public static readonly string[] SortPages = ["watchlist", "ratings", "lists-browse"];
    public static readonly string[] ViewPages = ["history", "watchlist", "progress"];
    public const int MaxSavedViews = 24;

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
    ];

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
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);
/// <summary>What this server can do, so clients can hide features it isn't set up for.</summary>
public record ServerFeatures(bool Email, bool OpenRegistration = true, string? Version = null);
public record InviteDto(string Code, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? UsedBy, DateTimeOffset? UsedAt);
/// <summary>Public check of an invite link before signing up.</summary>
public record InviteCheck(bool Valid, string? InvitedBy);
public record DeleteAccountRequest(string Password);
/// <summary>Wipes the chosen data but keeps the account, its settings and friends.</summary>
public record ClearDataRequest(string Password, ImportParts Parts);
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
    string? Overview);

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
    OwnedFormat? Owned = null,    // in your library, and how
    string? Note = null);         // your private note

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
    CollectionDto? Collection = null);

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
    ShowTrackStatus TrackStatus = ShowTrackStatus.Watching);

public record EpisodeRef(int SeasonNumber, int EpisodeNumber, string? Name, DateOnly? AirDate, bool Watched = false,
    DateTimeOffset? AirsAt = null, string? Network = null);   // exact time and channel, from TVmaze, when known

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
    bool Rewatched = false);   // played since the current rewatch started

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
public record WatchChoice(WatchTime When, DateTimeOffset? At = null, bool Through = false, bool SeasonOnly = false);

public record WatchRequest(
    MediaType Type,
    int TmdbId,                 // movie id, or show id for episodes/seasons/shows
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    DateTimeOffset? WatchedAt = null,
    WatchTime? When = null);    // null: Custom if WatchedAt is set, otherwise Now

/// <summary>Marks every aired episode of a show (or one season) watched.</summary>
public record BulkWatchRequest(int ShowTmdbId, int? SeasonNumber = null, DateTimeOffset? WatchedAt = null, bool OnlyUnwatched = true, WatchTime? When = null);

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
    IReadOnlyList<CastMember>? Cast = null);   // regulars in this episode; guests are in GuestStars

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
    bool Appearance = false);   // talk shows, news, "Self" roles: listed separately

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
    IReadOnlyList<EpisodeRef> Episodes);

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
    int Rated = 0);

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
    bool? Watched, DateTimeOffset? WatchedAt, double? Rating, bool OnWatchlist);
public record RecommendationsDto(IReadOnlyList<RecommendationDto> Received, IReadOnlyList<RecommendationDto> Sent);
public record RecommendRequest(MediaType Type, int TmdbId, IReadOnlyList<string> To, string? Note);
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
    bool? YouWatched = null);  // Someone else's play: whether the viewer has seen this episode too (null on your own)

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
    string? TimeZone = null);   // IANA id; decides where one day ends and the next begins

/// <summary>Totals for one day (null = unknown date) across every matching play, not just the visible page.</summary>
public record HistoryDay(DateOnly? Date, int Plays, long Minutes);

/// <summary>People: on friends' history, everyone whose history you can see (for the filter and each row's name).</summary>
public record HistoryPage(IReadOnlyList<HistoryEntry> Items, int Page, int TotalPages, int TotalResults, long TotalMinutes, IReadOnlyList<HistoryDay>? Days = null,
    IReadOnlyList<UserCard>? People = null);

public record WatchlistEntry(MediaSummary Media, DateTimeOffset AddedAt, DateOnly? ReleaseDate);
public record WatchlistRequest(MediaType Type, int TmdbId);

public record RatingRequest(MediaType Type, int TmdbId, double Rating, int? SeasonNumber = null, int? EpisodeNumber = null);
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
    public static string Label(UpNextSort s) => s switch { UpNextSort.NewestEpisode => "Newest episode", UpNextSort.FewestLeft => "Fewest left", _ => "Last watched" };
}

/// <summary>Paused and dropped shows are kept out of Up Next and the widget until you resume them.</summary>
public enum ShowTrackStatus { Watching, Paused, Dropped }

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
    bool Completed = false);
public record ShowStatusRequest(ShowTrackStatus Status);

public record NotInterestedEntry(MediaSummary Media, DateTimeOffset AddedAt);

/// <summary>How you own something in your library.</summary>
public enum OwnedFormat { Digital, BluRay, Uhd, Dvd, MediaServer, Other }
public static class OwnedFormats
{
    public static string Label(OwnedFormat f) => f switch
    {
        OwnedFormat.BluRay => "Blu-ray", OwnedFormat.Uhd => "4K Blu-ray", OwnedFormat.Dvd => "DVD",
        OwnedFormat.MediaServer => "Media server", OwnedFormat.Digital => "Digital", _ => "Other",
    };
}
public record LibraryEntry(MediaSummary Media, OwnedFormat Format, DateTimeOffset AddedAt, WatchSource Source, bool Watched);
public record LibraryRequest(MediaType Type, int TmdbId, OwnedFormat Format);
/// <summary>An empty Text removes the note.</summary>
public record NoteRequest(MediaType Type, int TmdbId, string? Text);
/// <summary>A recommendation and the title of yours that led to it ("Because you liked …").</summary>
public record ForYouItem(MediaSummary Media, string Because, MediaType BecauseType, int BecauseTmdbId);
public record ForYouGroup(MediaSummary Seed, IReadOnlyList<MediaSummary> Items);
/// <summary>Top: best overall. Groups: "Because you liked X" for a few of your favorites. Empty until you've rated or watched a few things.</summary>
public record ForYouDto(IReadOnlyList<ForYouItem> Top, IReadOnlyList<ForYouGroup> Groups);

public record NotificationDto(Guid Id, MediaType Type, int TmdbId, int? SeasonNumber, int? EpisodeNumber, string Title, string? Detail,
    string? PosterPath, DateOnly Day, bool Read, bool Recommendation = false);   // Recommendation: Title is "Sam recommends Heat", Detail their note
public record NotificationsDto(IReadOnlyList<NotificationDto> Items, int Unread);
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
    string? Via = null);             // a person you follow made it ("Christopher Nolan"); Type is Movie or Show

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
    string? Owner = null, int Saves = 0, bool Smart = false);   // Owner: set when it's someone else's list
public record ListDetail(Guid Id, string Name, string? Description, bool IsPublic, string OwnerUserName, bool IsOwner, IReadOnlyList<ListItemDto> Items,
    bool Ranked = false, int Saves = 0, bool Saved = false, SmartRules? Rules = null);

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
public record CreateListRequest(string Name, string? Description, bool IsPublic, bool Ranked = false, SmartRules? Rules = null);
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

public enum ImportKind { Trakt, Letterboxd, Imdb, TvTime, Simkl }
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
}

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
    IReadOnlyList<PendingTitle>? Pending = null);  // unmatched titles that can still be matched by hand

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

public record ReviewItem(
    MediaType Type,
    int TmdbId,
    string Title,
    string? PosterPath,
    int Plays,
    long Minutes,
    double? Rating,
    string? Detail);

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
    IReadOnlyList<ReviewItem>? AllMovies = null);

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
    bool? YouWatched = null);  // Someone else's check-in: whether the viewer has seen this episode (null on your own)

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
    string? Badge = null);                          // a milestone pinned next to the name

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
    IReadOnlyList<CompareItem> ALovedBNot);
/// <summary>A title in a comparison, with each person's rating if they rated it.</summary>
public record CompareItem(MediaSummary Media, double? A, double? B);

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
public record AppRelease(string Version, string Url, long Size, DateTimeOffset PublishedAt);
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

public enum GoalKind { Movies, NewMovies, Episodes, Hours, ShowsFinished }

public static class GoalKinds
{
    public static string Label(GoalKind k) => k switch
    {
        GoalKind.Movies => "Movies",
        GoalKind.NewMovies => "New-to-me movies",
        GoalKind.Episodes => "Episodes",
        GoalKind.Hours => "Hours watched",
        _ => "Shows finished",
    };

    /// <summary>"movies", "hours"… after a number.</summary>
    public static string Unit(GoalKind k, double n) => k switch
    {
        GoalKind.Movies => n == 1 ? "movie" : "movies",
        GoalKind.NewMovies => n == 1 ? "new movie" : "new movies",
        GoalKind.Episodes => n == 1 ? "episode" : "episodes",
        GoalKind.Hours => n == 1 ? "hour" : "hours",
        _ => n == 1 ? "show finished" : "shows finished",
    };

    public static string Hint(GoalKind k) => k switch
    {
        GoalKind.Movies => "Every movie play counts, rewatches too.",
        GoalKind.NewMovies => "Only movies you'd never seen before.",
        GoalKind.Episodes => "Every episode play counts.",
        GoalKind.Hours => "Movies and episodes, by runtime.",
        _ => "Shows you caught up on or finished this year.",
    };
}

/// <param name="Expected">Where you'd be by today on an even pace (the target once the year is over).</param>
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
public record WatchThroughRequest(int SeasonNumber, int? EpisodeNumber, WatchTime When, DateTimeOffset? WatchedAt = null, bool InOrder = false,
    bool SeasonOnly = false);   // TMDB order only: start from the season's first episode, not the show's
