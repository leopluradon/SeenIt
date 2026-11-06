using Microsoft.AspNetCore.Identity;
using SeenIt.Contracts;

namespace SeenIt.Server.Data;

public class AppUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Secret in the user's Jellyfin webhook URL; identifies the user without a login.</summary>
    public string JellyfinToken { get; set; } = NewToken();
    public int JellyfinThresholdPercent { get; set; } = 90;

    /// <summary>Blur titles and descriptions of episodes the user hasn't watched.</summary>
    public bool HideSpoilers { get; set; }
    /// <summary>Also blur those episodes' images.</summary>
    public bool BlurSpoilerImages { get; set; }

    /// <summary>Home page sections the user has hidden (keys from UiPrefs.HomeSections).</summary>
    public List<string> HiddenHomeSections { get; set; } = [];
    /// <summary>Home page section order (keys from UiPrefs.HomeSections); empty means the default.</summary>
    public List<string> HomeOrder { get; set; } = [];
    /// <summary>Color theme key (UiPrefs.Themes); null means the default.</summary>
    public string? Theme { get; set; }
    /// <summary>Rate as 1–10, 0.5–5 stars, or 1–10 with one decimal (stored as 1–10 either way).</summary>
    public RatingScale RatingScale { get; set; }
    /// <summary>Secret in the private calendar (iCal) feed URL.</summary>
    public string CalendarToken { get; set; } = NewToken();
    /// <summary>Email a digest on days something you follow comes out.</summary>
    public bool ReleaseEmails { get; set; }
    /// <summary>The last local day a release digest was emailed, so it goes once a day.</summary>
    public DateOnly? LastReleaseEmail { get; set; }

    /// <summary>Email the full export once a month.</summary>
    public bool MonthlyBackup { get; set; }
    /// <summary>The month (first day) the last backup email went out.</summary>
    public DateOnly? LastMonthlyBackup { get; set; }

    /// <summary>When the profile picture last changed (its URL's version); null when there's none.</summary>
    public long? AvatarVersion { get; set; }
    /// <summary>"48d 7h" or "1,159h" for long stretches of time.</summary>
    public TimeFormat TimeFormat { get; set; }
    /// <summary>Bottom tab bar choice (keys from UiPrefs.Tabs); empty means the default.</summary>
    public List<string> Tabs { get; set; } = [];

    /// <summary>ISO 3166-1 country for "where to watch"; null means use the device's region.</summary>
    public string? Region { get; set; }
    public ProfileVisibility Visibility { get; set; } = ProfileVisibility.Friends;

    /// <summary>
    /// The day the user started logging as they watch. Plays before it were back-filled, so their
    /// timestamps say when they were entered, not watched; habit stats (weekday, hour, streaks) skip them.
    /// </summary>
    public DateOnly? TrackingSince { get; set; }

    /// <summary>IANA time zone for stats, reviews and "on this day"; null = whatever the device says.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Plex account name to accept webhook events from; null accepts every account on the server.</summary>
    public string? PlexUsername { get; set; }

    /// <summary>Last time the app loaded the user's profile (updated at most every few minutes).</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    public static string NewToken() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}

// ---------- Cached TMDB metadata (shared by every user) ----------

/// <summary>A person on a title's credits, kept for review stats (top actors, directors).</summary>
public class PersonCredit
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? ProfilePath { get; set; }
}

public static class MetaVersions
{
    /// <summary>Bump when MovieMeta/ShowMeta gain fields, so cached rows are refetched.</summary>
    public const int Current = 4; // 3: movie countries stored as ISO codes; 4: movie collection ids
}

public class MovieMeta
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public DateOnly? ReleaseDate { get; set; }
    public int? Runtime { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public List<string> Genres { get; set; } = [];
    public string? ImdbId { get; set; }
    public double? VoteAverage { get; set; }
    public string? Overview { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string? OriginalLanguage { get; set; }
    public List<string> Countries { get; set; } = [];
    public List<string> Studios { get; set; } = [];
    public List<PersonCredit> Cast { get; set; } = [];
    public List<PersonCredit> Directors { get; set; } = [];
    public int MetaVersion { get; set; }
    /// <summary>TMDB collection (franchise) the movie belongs to, if any.</summary>
    public int? CollectionId { get; set; }
}

public class ShowMeta
{
    public int TmdbId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly? FirstAirDate { get; set; }
    public string? Status { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public List<string> Genres { get; set; } = [];
    public int? EpisodeRuntime { get; set; }
    public double? VoteAverage { get; set; }
    public string? Overview { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string? OriginalLanguage { get; set; }
    public List<string> Countries { get; set; } = [];
    public List<string> Networks { get; set; } = [];
    public List<PersonCredit> Cast { get; set; } = [];
    public List<PersonCredit> Creators { get; set; } = [];
    public int MetaVersion { get; set; }

    /// <summary>When the episode list was last pulled. Null until the first full refresh.</summary>
    public DateTimeOffset? EpisodesUpdatedAt { get; set; }

    public bool IsOngoing => Status is "Returning Series" or "In Production" or "Planned" or "Pilot";
}

public class EpisodeMeta
{
    public int ShowTmdbId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public int? TmdbId { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public DateOnly? AirDate { get; set; }
    public int? Runtime { get; set; }
    public string? StillPath { get; set; }
    public double? VoteAverage { get; set; }
}

// ---------- Per-user tracking ----------

public class WatchEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>Movie or Episode only.</summary>
    public MediaType Type { get; set; }

    /// <summary>Movie id for movies, show id for episodes.</summary>
    public int TmdbId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public DateTimeOffset WatchedAt { get; set; }
    public WatchSource Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The import that added this, so the import can be undone.</summary>
    public Guid? ImportJobId { get; set; }
}

public class WatchlistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The import that added this, so the import can be undone.</summary>
    public Guid? ImportJobId { get; set; }
}

public class Rating
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    /// <summary>1–10, to one decimal place (8.1). Whole numbers unless the user rates in decimals.</summary>
    public double Value { get; set; }
    public DateTimeOffset RatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The import that added this, so the import can be undone.</summary>
    public Guid? ImportJobId { get; set; }
}

public class UserList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsPublic { get; set; }
    /// <summary>A ranking: shown numbered, in the owner's order.</summary>
    public bool Ranked { get; set; }
    /// <summary>Smart list rules (SmartRules as JSON); its entries are worked out, not stored. Null for ordinary lists.</summary>
    public string? RulesJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The import that created this list (undoing the import removes it once it's empty).</summary>
    public Guid? ImportJobId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<UserListItem> Items { get; set; } = [];
}

public class UserListItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListId { get; set; }
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public int Position { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The owner's note on this entry ("better than the book").</summary>
    public string? Note { get; set; }
    /// <summary>The import that added this, so the import can be undone.</summary>
    public Guid? ImportJobId { get; set; }
}

public class ImportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ImportKind Kind { get; set; }
    public ImportParts Parts { get; set; } = ImportParts.All;
    public string FileName { get; set; } = "";
    public byte[]? Payload { get; set; }
    public ImportStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string? Message { get; set; }
    public List<string> Unmatched { get; set; } = [];
    public DateTimeOffset? UndoneAt { get; set; }
    /// <summary>JSON (ImportUndo): what the import changed in place, so undo can put it back.</summary>
    public string? UndoJson { get; set; }
    /// <summary>JSON (List&lt;PendingImport&gt;): unmatched titles with their rows, for matching them by hand later.</summary>
    public string? PendingJson { get; set; }
}

public class JellyfinEvent
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ItemName { get; set; }
    public string Outcome { get; set; } = "";
}

/// <summary>A show the user has paused or dropped. No row means "watching".</summary>
public class ShowState
{
    public Guid UserId { get; set; }
    public int ShowTmdbId { get; set; }
    public ShowTrackStatus Status { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Going through a show again. While CompletedAt is null, progress and Up Next count only plays
/// from StartedAt on. It completes itself once every aired episode has been played again.
/// </summary>
public class ShowRewatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public int ShowTmdbId { get; set; }
    /// <summary>2 for the second time through, and so on.</summary>
    public int Number { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>What the user is watching right now (at most one per user).</summary>
public class CheckIn
{
    public Guid UserId { get; set; }
    /// <summary>Movie or Episode.</summary>
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset EndsAt { get; set; }
    /// <summary>Manual check-ins become a play at EndsAt; Jellyfin ones are scrobbled by the webhook.</summary>
    public WatchSource Source { get; set; }
}

/// <summary>A single-use sign-up link. Signing up with it makes the inviter and the new member follow each other.</summary>
public class Invite
{
    public string Code { get; set; } = "";
    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? UsedById { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public class Follow
{
    public Guid FollowerId { get; set; }
    public Guid FolloweeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One thing a user did (tracking, ratings, watchlist, lists, imports, account changes), for the
/// admin page. Not tied to the user row, so the trail survives the account being deleted.
/// </summary>
public class ActivityEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid UserId { get; set; }
    public string UserName { get; set; } = "";
    public ActivityKind Kind { get; set; }
    public WatchSource Source { get; set; }
    public MediaType? Type { get; set; }
    public int? TmdbId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string? Detail { get; set; }
}

/// <summary>A version of the app that started up here for the first time.</summary>
public class Deployment
{
    public int Id { get; set; }
    public string Version { get; set; } = "";
    public string Commit { get; set; } = "";
    public string? Subject { get; set; }
    public DateTimeOffset? BuiltAt { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A favorite movie or show. Rank orders them (Trakt's favorites have an order).</summary>
public class Favorite
{
    public Guid UserId { get; set; }
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public int Rank { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The import that added this, so the import can be undone.</summary>
    public Guid? ImportJobId { get; set; }
}

/// <summary>A TMDB collection (franchise) and its movies, for franchise progress in stats.</summary>
public class CollectionMeta
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? PosterPath { get; set; }
    public List<CollectionPartMeta> Parts { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CollectionPartMeta
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public DateOnly? ReleaseDate { get; set; }
    public string? PosterPath { get; set; }
}

/// <summary>A yearly target, e.g. 100 movies in 2027.</summary>
public class Goal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public int Year { get; set; }
    public GoalKind Kind { get; set; }
    public int Target { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Pulls new diary entries from the user's public Letterboxd RSS feed every so often.</summary>
public class LetterboxdSync
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastAddedAt { get; set; }
    public string? LastResult { get; set; }
    public bool LastFailed { get; set; }
    /// <summary>Feed entries already handled (newest last), so a play you delete here isn't brought back.</summary>
    public List<string> SeenGuids { get; set; } = [];
}

/// <summary>
/// A show's alternative episode order from TMDB (an "episode group": chronological, DVD, story
/// arcs...), cached. Parts are stored as JSON (<see cref="EpisodeOrderPart"/>).
/// </summary>
public class EpisodeOrder
{
    public string Id { get; set; } = "";
    public int ShowTmdbId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Type { get; set; }
    public string PartsJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public record EpisodeOrderSlot(int Season, int Episode);
public record EpisodeOrderPart(string Name, List<EpisodeOrderSlot> Episodes);

/// <summary>The order a user is watching a show in. No row means TMDB's seasons.</summary>
public class ShowWatchOrder
{
    public Guid UserId { get; set; }
    public int ShowTmdbId { get; set; }
    public string OrderId { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>IMDb's own rating for a title, from its daily public ratings file (datasets.imdbws.com).</summary>
public class ImdbRating
{
    public string ImdbId { get; set; } = "";
    public double Rating { get; set; }
    public int Votes { get; set; }
}

/// <summary>Scores from other sites (Rotten Tomatoes, Metacritic, Letterboxd...) via MDBList, cached for a few days.</summary>
public class ExternalScoreCache
{
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public string Json { get; set; } = "[]";
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Small bits of server state (e.g. when the IMDb ratings were last loaded).</summary>
public class ServerState
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A profile picture: a 256×256 JPEG, cropped and resized on upload.</summary>
public class UserAvatar
{
    public Guid UserId { get; set; }
    public byte[] Data { get; set; } = [];
}

/// <summary>"A new episode / movie you follow is out today", shown under the bell (and in the daily email).</summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public MediaType Type { get; set; }          // Movie or Episode
    public int TmdbId { get; set; }              // movie, or the show for episodes
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public string Title { get; set; } = "";
    public string? Detail { get; set; }          // "S02E05 · Episode name"
    public string? PosterPath { get; set; }
    public DateOnly Day { get; set; }            // release day
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>"Not interested": kept out of Discover, trending, "you might also like" and recommendations.</summary>
public class NotInterested
{
    public Guid UserId { get; set; }
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Someone saved (liked) a public list; shows under Lists → Saved, and counts toward "popular".</summary>
public class ListLike
{
    public Guid UserId { get; set; }
    public Guid ListId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Something you own (disc, digital purchase, on your media server), separate from what you've watched.</summary>
public class LibraryItem
{
    public Guid UserId { get; set; }
    public MediaType Type { get; set; }   // Movie or Show
    public int TmdbId { get; set; }
    public OwnedFormat Format { get; set; }
    /// <summary>Manual, or the media server that added it (Plex, Jellyfin).</summary>
    public WatchSource Source { get; set; } = WatchSource.Manual;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The import that added this, so the import can be undone.</summary>
    public Guid? ImportJobId { get; set; }
}
