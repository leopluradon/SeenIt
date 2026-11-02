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
    public const int Current = 3; // 3: movie countries stored as ISO codes
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
}

public class WatchlistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Rating
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public MediaType Type { get; set; }
    public int TmdbId { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public int Value { get; set; }
    public DateTimeOffset RatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class UserList
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsPublic { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
