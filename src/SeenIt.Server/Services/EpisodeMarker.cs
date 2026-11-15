using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;

namespace SeenIt.Server.Services;

/// <summary>
/// Premiere and finale badges (EpisodeMark) for episodes of a set of shows, from the cached episode lists:
/// TMDB's episode type marks finales, a show that has ended makes its last one the series finale.
/// </summary>
public sealed class EpisodeMarker
{
    private sealed record ShowInfo(bool Ended, int LastSeason, Dictionary<int, int> LastEpisode, Dictionary<(int, int), string?> Types);
    private readonly Dictionary<int, ShowInfo> _shows;
    private EpisodeMarker(Dictionary<int, ShowInfo> shows) => _shows = shows;

    public static async Task<EpisodeMarker> LoadAsync(AppDbContext db, IEnumerable<int> showIds, CancellationToken ct)
    {
        var ids = showIds.Distinct().ToList();
        if (ids.Count == 0) return new([]);
        var status = await db.Shows.AsNoTracking().Where(s => ids.Contains(s.TmdbId)).Select(s => new { s.TmdbId, s.Status }).ToDictionaryAsync(s => s.TmdbId, s => s.Status, ct);
        var eps = await db.Episodes.AsNoTracking().Where(e => ids.Contains(e.ShowTmdbId) && e.SeasonNumber > 0)
            .Select(e => new { e.ShowTmdbId, e.SeasonNumber, e.EpisodeNumber, e.EpisodeType }).ToListAsync(ct);
        return new(eps.GroupBy(e => e.ShowTmdbId).ToDictionary(g => g.Key, g => new ShowInfo(
            status.GetValueOrDefault(g.Key) is "Ended" or "Canceled",
            g.Max(e => e.SeasonNumber),
            g.GroupBy(e => e.SeasonNumber).ToDictionary(s => s.Key, s => s.Max(e => e.EpisodeNumber)),
            g.ToDictionary(e => (e.SeasonNumber, e.EpisodeNumber), e => e.EpisodeType))));
    }

    public EpisodeMark Of(int showId, int? season, int? episode)
    {
        if (season is not { } s || episode is not { } e) return EpisodeMark.None;
        if (!_shows.TryGetValue(showId, out var show))
            return EpisodeMarks.Of(s, e, null, false, 0, 0);   // nothing cached: premieres only
        return EpisodeMarks.Of(s, e, show.Types.GetValueOrDefault((s, e)), show.Ended, show.LastSeason, show.LastEpisode.GetValueOrDefault(s));
    }

    public EpisodeRef? Mark(int showId, EpisodeRef? r) => r is null ? null : r with { Mark = Of(showId, r.SeasonNumber, r.EpisodeNumber) };
    public ShowProgressDto Mark(ShowProgressDto p) => p with { NextEpisode = Mark(p.ShowTmdbId, p.NextEpisode) };
    public HistoryEntry Mark(HistoryEntry h) => h.Type == MediaType.Episode ? h with { Mark = Of(h.TmdbId, h.SeasonNumber, h.EpisodeNumber) } : h;
    public CalendarEntry Mark(CalendarEntry c) => c.Type == MediaType.Episode ? c with { Mark = Of(c.TmdbId, c.SeasonNumber, c.EpisodeNumber) } : c;
}
