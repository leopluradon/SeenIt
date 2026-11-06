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
    bool HideSpoilers = false, bool BlurSpoilerImages = false,
    IReadOnlyList<string>? HiddenHomeSections = null, IReadOnlyList<string>? Tabs = null,
    string? Region = null, ProfileVisibility Visibility = ProfileVisibility.Friends,
    DateOnly? TrackingSince = null, bool IsAdmin = false, string? TimeZone = null,
    IReadOnlyList<string>? HomeOrder = null, string? Theme = null, RatingScale RatingScale = RatingScale.Ten);

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
    RatingScale? RatingScale = null);

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
        ("friends", "Friends' activity"),
        ("trending-movies", "Trending movies"),
        ("trending-shows", "Trending shows"),
    ];

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
    bool IsFavorite = false);

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

public record EpisodeRef(int SeasonNumber, int EpisodeNumber, string? Name, DateOnly? AirDate, bool Watched = false);

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
    int? PreviousSeason = null, int? NextSeason = null);   // neighbors in season order (specials sit outside, except Specials → Season 1)

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
    long WatchedMinutes);

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
    string? Range = null);     // Shows view: "S04E01–S04E05", or one episode's number

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

public record HistoryPage(IReadOnlyList<HistoryEntry> Items, int Page, int TotalPages, int TotalResults, long TotalMinutes, IReadOnlyList<HistoryDay>? Days = null);

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
    string? WatchOrder = null); // name of the alternative order being followed ("Chronological"), if any

/// <summary>Paused and dropped shows are kept out of Up Next and the widget until you resume them.</summary>
public enum ShowTrackStatus { Watching, Paused, Dropped }
public record ShowStatusRequest(ShowTrackStatus Status);

public record CalendarEntry(
    DateOnly Date,
    MediaType Type,
    int TmdbId,
    string Title,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    string? PosterPath,
    bool Watched);

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

public record ListSummary(Guid Id, string Name, string? Description, bool IsPublic, int ItemCount, DateTimeOffset UpdatedAt, IReadOnlyList<string?> Posters);
public record ListDetail(Guid Id, string Name, string? Description, bool IsPublic, string OwnerUserName, bool IsOwner, IReadOnlyList<ListItemDto> Items,
    bool Ranked = false);
public record ListItemDto(Guid Id, MediaSummary Media, DateTimeOffset AddedAt, int Position, string? Note = null);
public record CreateListRequest(string Name, string? Description, bool IsPublic, bool Ranked = false);
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
}

/// <summary>What an import brings in. Movies/Shows pick the kind of title; the rest pick the kind of data.</summary>
[Flags]
public enum ImportParts
{
    None = 0,
    Movies = 1, Shows = 2,
    History = 4, Ratings = 8, Watchlist = 16, Lists = 32,   // Lists includes favorites
    All = Movies | Shows | History | Ratings | Watchlist | Lists,
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
public record UndoImportResult(int Plays, int Ratings, int Watchlist, int ListItems, int Favorites, int Lists);

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
    DateOnly? TrackingSince = null);   // reviews only count from here (see Settings → Stats)

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
    string? PosterPath, string? BackdropPath, DateTimeOffset StartedAt, DateTimeOffset EndsAt, WatchSource Source);

// ---------- Social ----------

/// <summary>
/// Someone you follow (or who follows you) who has watched or rated a title. Plays: for an episode or
/// movie, how many times; for a show or season, how many different episodes (out of the aired count).
/// </summary>
public record FriendSeen(UserCard User, int Plays, DateTimeOffset? LastWatched, double? Rating);

public record UserCard(string UserName, string? DisplayName, bool Following, bool FollowsYou)
{
    public string Name => DisplayName ?? UserName;
}

public record ProfileStats(int Movies, int Shows, int Episodes, long Minutes, int Ratings);

public record ProfileDto(
    UserCard User, DateTimeOffset JoinedAt, bool IsMe, ProfileVisibility Visibility, bool CanView,
    int Followers, int Following,
    NowWatchingDto? NowWatching, ProfileStats? Stats,
    IReadOnlyList<HistoryEntry> Recent, IReadOnlyList<RatingEntry> TopRated, IReadOnlyList<ListSummary> Lists,
    IReadOnlyList<FavoriteEntry>? Favorites = null);

/// <summary>A movie or show the user marked as a favorite, in their order.</summary>
public record FavoriteEntry(MediaSummary Media, DateTimeOffset AddedAt);
/// <summary>Four favorite movies and four favorite shows.</summary>
public static class FavoriteLimits { public const int PerType = 4; }

public record FavoriteRequest(MediaType Type, int TmdbId);

public record ActivityItem(UserCard User, HistoryEntry Entry);
public record FriendWatching(UserCard User, NowWatchingDto Item);
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
    int Candidates);                       // how many titles matched the filters

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
public record FranchiseProgress(int Id, string Name, string? PosterPath, int Seen, int Released, MediaSummary? Next);

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
