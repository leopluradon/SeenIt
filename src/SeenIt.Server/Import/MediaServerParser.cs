using System.Text.RegularExpressions;
using SeenIt.Contracts;

namespace SeenIt.Server.Import;

/// <summary>
/// A list of what's on your media server, as a CSV: Jellyfin's Reports plugin export (Name, Year), or anything
/// with similar columns. Everything goes into the library as "Media server"; nothing counts as watched.
/// Recognised columns (any case): Name/Title, Year/Production Year; optional TMDb/IMDb/TVDb ids; Type (Movie,
/// Series, Season, Episode); and for TV, Series/Series Name with Season/Season Number, which owns that season.
/// </summary>
public static class MediaServerParser
{
    public static ParsedImport Parse(IReadOnlyDictionary<string, string> files)
    {
        var result = new ParsedImport();
        var now = DateTimeOffset.UtcNow;
        foreach (var (_, text) in files.Where(f => f.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var row in LetterboxdParser.Rows(text.TrimStart('﻿')))
            {
                string? Get(params string[] keys) => ImportText.Get(row, keys);
                var type = Get("Type", "Item Type", "ItemType", "Media Type")?.Trim().ToLowerInvariant();
                var series = Get("Series", "Series Name", "SeriesName", "Show");
                var season = ImportText.Int(Get("Season", "Season Number", "SeasonNumber", "ParentIndexNumber"));
                var name = Get("Name", "Title");
                var year = ImportText.Int(Get("Year", "Production Year", "ProductionYear"));
                var added = ImportText.Date(Get("Date Added", "DateCreated", "Date Created", "Added")) ?? now;
                int? tmdb = ImportText.Id(Get("TMDb", "Tmdb", "TMDB", "TMDb Id", "TmdbId", "Provider_tmdb"));
                var imdb = ImportText.Imdb(Get("IMDb", "Imdb", "IMDB", "IMDb Id", "ImdbId", "Provider_imdb"));
                int? tvdb = ImportText.Id(Get("TVDb", "Tvdb", "TVDB", "TVDb Id", "TvdbId", "Provider_tvdb"));

                if (type is "episode" or "season" || (type is null && !string.IsNullOrWhiteSpace(series)))
                {
                    // A season (or an episode of one): the show is the series column, the season its number.
                    var show = (series ?? (type == "season" ? null : name))?.Trim();
                    if (string.IsNullOrEmpty(show)) continue;
                    season ??= type == "season" && name is not null && Regex.Match(name, @"\d+") is { Success: true } m ? int.Parse(m.Value) : null;
                    // Ids on an episode row are the episode's, not the show's, so match the show by name.
                    result.Library.Add(new ImportOwned(new ImportRef(MediaType.Show, null, null, show, null), OwnedFormat.MediaServer, added, season));
                }
                else if (type is "series" or "show" or "tv" or "tvshow")
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    result.Library.Add(new ImportOwned(new ImportRef(MediaType.Show, tmdb, imdb, name.Trim(), year, TvdbId: tvdb), OwnedFormat.MediaServer, added));
                }
                else if (type is null or "movie")
                {
                    if (string.IsNullOrWhiteSpace(name) && tmdb is null && imdb is null) continue;
                    result.Library.Add(new ImportOwned(new ImportRef(MediaType.Movie, tmdb, imdb, name?.Trim(), year), OwnedFormat.MediaServer, added));
                }
            }
        }

        // One row per copy; a show listed season by season doesn't also need its whole-show row.
        var withSeasons = result.Library.Where(o => o.Season is not null).Select(o => o.Item.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = result.Library
            .Where(o => o.Season is not null || o.Item.Type == MediaType.Movie || !withSeasons.Contains(o.Item.Label))
            .DistinctBy(o => (o.Item.Type, o.Item.Label.ToLowerInvariant(), o.Season)).ToList();
        result.Library.Clear();
        result.Library.AddRange(kept);
        return result;
    }
}
