namespace SeenIt.Contracts;

public enum MediaType { Movie, Show, Episode }

public enum WatchSource { Manual, Jellyfin, TraktImport, LetterboxdImport }

// ---------- Auth ----------

public record RegisterRequest(string UserName, string Email, string Password);
public record LoginRequest(string UserNameOrEmail, string Password);
public record RefreshRequest(string RefreshToken);
public record TokenResponse(string AccessToken, string RefreshToken, long ExpiresIn);
public record ProblemInfo(string Title, Dictionary<string, string[]>? Errors = null);

public record MeDto(
    Guid Id, string UserName, string Email, string? DisplayName, DateTimeOffset CreatedAt,
    bool HideSpoilers = false, bool BlurSpoilerImages = false,
    IReadOnlyList<string>? HiddenHomeSections = null, IReadOnlyList<string>? Tabs = null,
    string? Region = null, ProfileVisibility Visibility = ProfileVisibility.Friends);

/// <summary>Only the fields that are set are changed.</summary>
public record PreferencesRequest(
    bool? HideSpoilers = null, bool? BlurSpoilerImages = null,
    IReadOnlyList<string>? HiddenHomeSections = null, IReadOnlyList<string>? Tabs = null,
    string? Region = null,          // "" clears it (back to the device's region)
    ProfileVisibility? Visibility = null);

/// <summary>Who can see your profile, history and stats. Lists have their own public/private switch.</summary>
public enum ProfileVisibility { Public, Friends, Private }

/// <summary>The customisable bits of the UI and their allowed keys.</summary>
public static class UiPrefs
{
    public static readonly (string Key, string Label)[] HomeSections =
    [
        ("now", "Watching now"),
        ("review", "Month / year in review banner"),
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

    public const int MaxTabs = 4; // plus the fixed "More" tab
    public static readonly string[] DefaultTabs = ["home", "discover", "progress", "calendar"];
}
public record UpdateProfileRequest(string? DisplayName);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record DeleteAccountRequest(string Password);

// ---------- Catalogue (TMDB) ----------

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

public record CastMember(int Id, string Name, string? Character, string? ProfilePath, int? EpisodeCount = null);
public record CrewMember(int Id, string Name, string Job, string? ProfilePath);
public record Video(string Key, string Name, string Site, string Type);

/// <summary>What the signed-in user has done with a title. All empty when signed out.</summary>
public record UserMediaState(
    bool InWatchlist,
    int? Rating,
    int Plays,
    DateTimeOffset? LastWatchedAt,
    IReadOnlyList<Guid> ListIds);

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
    UserMediaState? User);

public record SeasonSummary(int SeasonNumber, string Name, int EpisodeCount, DateOnly? AirDate, string? PosterPath, int Watched);

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
    int? Rating);

public record SeasonDetail(
    int ShowTmdbId,
    string ShowName,
    int SeasonNumber,
    string Name,
    string? Overview,
    string? PosterPath,
    IReadOnlyList<EpisodeDto> Episodes,
    IReadOnlyList<CastMember>? Cast = null);

// ---------- Tracking ----------

/// <summary>When a play happened. Custom uses WatchedAt; ReleaseDate uses each item's own release/air date.</summary>
public enum WatchTime { Now, ReleaseDate, Custom, Unknown }

/// <summary>What the "when did you watch it?" dialog returns.</summary>
public record WatchChoice(WatchTime When, DateTimeOffset? At = null);

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
    int? Rating,
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
    int? Runtime = null);

/// <summary>History filters. From is inclusive, To exclusive; both are absolute instants computed by the client in its own time zone.</summary>
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

public record RatingRequest(MediaType Type, int TmdbId, int Rating, int? SeasonNumber = null, int? EpisodeNumber = null);
public record RatingEntry(MediaType Type, int TmdbId, string Title, int? SeasonNumber, int? EpisodeNumber, string? PosterPath, int Rating, DateTimeOffset RatedAt);

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
    ShowTrackStatus Status = ShowTrackStatus.Watching);

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
public record ListDetail(Guid Id, string Name, string? Description, bool IsPublic, string OwnerUserName, bool IsOwner, IReadOnlyList<ListItemDto> Items);
public record ListItemDto(Guid Id, MediaSummary Media, DateTimeOffset AddedAt, int Position);
public record CreateListRequest(string Name, string? Description, bool IsPublic);
public record ListItemRequest(MediaType Type, int TmdbId);

// ---------- Integrations ----------

public record JellyfinSettingsDto(string WebhookUrlPath, int WatchedThresholdPercent, IReadOnlyList<JellyfinEventDto> RecentEvents);
public record JellyfinEventDto(DateTimeOffset ReceivedAt, string? ItemName, string Outcome);
public record JellyfinThresholdRequest(int WatchedThresholdPercent);

public enum ImportKind { Trakt, Letterboxd }
public enum ImportStatus { Queued, Running, Completed, Failed }

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
    IReadOnlyList<string> Unmatched);

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
    int? Rating,
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
    ReviewItem? MostRewatched);

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

public record UserCard(string UserName, string? DisplayName, bool Following, bool FollowsYou)
{
    public string Name => DisplayName ?? UserName;
}

public record ProfileStats(int Movies, int Shows, int Episodes, long Minutes, int Ratings);

public record ProfileDto(
    UserCard User, DateTimeOffset JoinedAt, bool IsMe, ProfileVisibility Visibility, bool CanView,
    int Followers, int Following,
    NowWatchingDto? NowWatching, ProfileStats? Stats,
    IReadOnlyList<HistoryEntry> Recent, IReadOnlyList<RatingEntry> Favourites, IReadOnlyList<ListSummary> Lists);

public record ActivityItem(UserCard User, HistoryEntry Entry);
public record FriendWatching(UserCard User, NowWatchingDto Item);
public record FeedDto(IReadOnlyList<FriendWatching> WatchingNow, IReadOnlyList<ActivityItem> Activity);

public record RatingPair(MediaSummary Media, int You, int Them);
public record CompareDto(
    UserCard Them, ProfileStats You, ProfileStats TheirStats,
    int? Compatibility,            // 0–100 from titles you both rated; null when there are too few
    int SharedMovies, int SharedShows,
    IReadOnlyList<MediaSummary> BothMovies, IReadOnlyList<MediaSummary> BothShows,
    IReadOnlyList<RatingPair> RatedBoth,
    IReadOnlyList<MediaSummary> TheyLoved);   // rated 8+ by them, not watched by you
