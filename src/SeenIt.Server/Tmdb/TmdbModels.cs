using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeenIt.Server.Tmdb;

// Raw TMDB v3 response shapes. Deserialised with snake_case naming, so property names map directly.

public record TmdbPage<T>(int Page, List<T> Results, int TotalPages, int TotalResults);

/// <summary>Multi-shape search/list item: movies use Title/ReleaseDate, shows use Name/FirstAirDate.</summary>
public record TmdbListItem(
    int Id,
    string? MediaType,
    string? Title,
    string? Name,
    string? ReleaseDate,
    string? FirstAirDate,
    string? PosterPath,
    string? BackdropPath,
    double? VoteAverage,
    string? Overview);

public record TmdbGenre(int Id, string Name);
public record TmdbRole(string? Character, int EpisodeCount);
/// <summary>Movie credits carry Character; TV aggregate credits carry Roles instead.</summary>
public record TmdbCast(int Id, string Name, string? Character, string? ProfilePath, int Order, List<TmdbRole>? Roles)
{
    public string? AnyCharacter => Character ?? Roles?.FirstOrDefault()?.Character;
}
public record TmdbCrew(int Id, string Name, string Job, string? Department, string? ProfilePath);
public record TmdbCredits(List<TmdbCast>? Cast, List<TmdbCrew>? Crew);
public record TmdbVideo(string Key, string Name, string Site, string Type, bool Official);
public record TmdbVideos(List<TmdbVideo>? Results);
public record TmdbRecommendations(List<TmdbListItem>? Results);

public record TmdbMovie(
    int Id,
    string Title,
    string? Tagline,
    string? Overview,
    string? ReleaseDate,
    int? Runtime,
    List<TmdbGenre>? Genres,
    string? PosterPath,
    string? BackdropPath,
    double? VoteAverage,
    int? VoteCount,
    string? ImdbId,
    string? Status,
    TmdbCredits? Credits,
    TmdbVideos? Videos,
    TmdbRecommendations? Recommendations);

public record TmdbNetwork(int Id, string Name);

public record TmdbSeasonSummary(int SeasonNumber, string? Name, int EpisodeCount, string? AirDate, string? PosterPath);

public record TmdbEpisodeRef(int Id, int SeasonNumber, int EpisodeNumber, string? Name, string? AirDate);

public record TmdbShow(
    int Id,
    string Name,
    string? Tagline,
    string? Overview,
    string? FirstAirDate,
    string? LastAirDate,
    string? Status,
    List<TmdbNetwork>? Networks,
    List<TmdbGenre>? Genres,
    List<int>? EpisodeRunTime,
    string? PosterPath,
    string? BackdropPath,
    double? VoteAverage,
    int? VoteCount,
    int NumberOfSeasons,
    int NumberOfEpisodes,
    List<TmdbSeasonSummary>? Seasons,
    TmdbEpisodeRef? NextEpisodeToAir,
    TmdbEpisodeRef? LastEpisodeToAir,
    TmdbCredits? AggregateCredits,
    TmdbCredits? Credits,
    TmdbVideos? Videos,
    TmdbRecommendations? Recommendations)
{
    /// <summary>"season/N" blocks pulled in via append_to_response.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public record TmdbEpisode(
    int Id,
    int SeasonNumber,
    int EpisodeNumber,
    string? Name,
    string? Overview,
    string? AirDate,
    int? Runtime,
    string? StillPath,
    double? VoteAverage);

public record TmdbEpisodeDetail(
    int Id,
    int SeasonNumber,
    int EpisodeNumber,
    string? Name,
    string? Overview,
    string? AirDate,
    int? Runtime,
    string? StillPath,
    double? VoteAverage,
    int? VoteCount,
    List<TmdbCast>? GuestStars,
    List<TmdbCrew>? Crew);

public record TmdbSeason(int Id, int SeasonNumber, string? Name, string? Overview, string? PosterPath, string? AirDate, List<TmdbEpisode>? Episodes);

public record TmdbFindEpisode(int Id, int ShowId, int SeasonNumber, int EpisodeNumber, string? Name);

public record TmdbFindResult(
    List<TmdbListItem>? MovieResults,
    List<TmdbListItem>? TvResults,
    List<TmdbFindEpisode>? TvEpisodeResults);

public static class TmdbDates
{
    public static DateOnly? Parse(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d) ? d : null;
}
