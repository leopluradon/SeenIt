using SeenIt.Server.Data;
using SeenIt.Server.Services;
using Tv = SeenIt.Server.Services.TvmazeService.TvmazeEpisode;

namespace SeenIt.Tests;

public class AirDateTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Fact]
    public void Streaming_first_wins_when_it_is_up_to_a_week_earlier()
    {
        Assert.Equal(D("2026-11-13"), AirDates.Effective(D("2026-11-15"), D("2026-11-13")));   // Paramount+ Friday, Showtime Sunday
        Assert.Equal(D("2026-11-15"), AirDates.Effective(D("2026-11-15"), D("2026-11-16")));   // streams the day after (FX → Hulu): TV date
        Assert.Equal(D("2026-11-15"), AirDates.Effective(D("2026-11-15"), D("2026-11-01")));   // two weeks apart: not the same release
        Assert.Equal(D("2026-11-15"), AirDates.Effective(D("2026-11-15"), null));
        Assert.Null(AirDates.Effective(null, D("2026-11-13")));                                 // TMDB has no date yet: stay TBA
    }

    [Fact]
    public void Tvmaze_dates_move_episodes_and_blank_times_stay_blank()
    {
        // Dexter: Resurrection S02E03: TMDB Sunday Nov 15; TVmaze Friday Nov 13 on Paramount+ with no time.
        var ep = new EpisodeMeta { ShowTmdbId = 259909, SeasonNumber = 2, EpisodeNumber = 3, AirDate = D("2026-11-15"), TmdbAirDate = D("2026-11-15") };
        var timed = new EpisodeMeta { ShowTmdbId = 1, SeasonNumber = 1, EpisodeNumber = 1, AirDate = D("2026-11-15"), TmdbAirDate = D("2026-11-15") };

        var changed = TvmazeService.Apply([ep], [new Tv(2, 3, "2026-11-13", "", "2026-11-13T12:00:00+00:00", "regular")], streamingOnly: true);
        Assert.Equal(1, changed);
        Assert.Equal(D("2026-11-13"), ep.AirDate);
        Assert.Equal(D("2026-11-15"), ep.TmdbAirDate);
        Assert.Equal(D("2026-11-13"), ep.TvmazeAirDate);
        Assert.Null(ep.AirStamp);   // not a made-up 12:00 UTC

        // A real time a day off in UTC (9 PM Eastern) is kept.
        TvmazeService.Apply([timed], [new Tv(1, 1, "2026-11-15", "21:00", "2026-11-16T02:00:00+00:00", "regular")], streamingOnly: false);
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T02:00:00Z"), timed.AirStamp);
        Assert.Equal(D("2026-11-15"), timed.AirDate);

        // Running it again changes nothing.
        Assert.Equal(0, TvmazeService.Apply([ep], [new Tv(2, 3, "2026-11-13", "", "2026-11-13T12:00:00+00:00", "regular")], streamingOnly: true));
    }

    [Fact]
    public void Shows_on_a_tv_network_keep_tmdbs_dates()
    {
        // Beavis and Butt-Head (Comedy Central): TVmaze's S02E05 is a week before TMDB's; the numbering differs, so leave it.
        var ep = new EpisodeMeta { ShowTmdbId = 2, SeasonNumber = 2, EpisodeNumber = 5, AirDate = D("2023-04-20"), TmdbAirDate = D("2023-04-27"), TvmazeAirDate = D("2023-04-20") };
        TvmazeService.Apply([ep], [new Tv(2, 5, "2023-04-20", "", "2023-04-20T12:00:00+00:00", "regular")], streamingOnly: false);
        Assert.Equal(D("2023-04-27"), ep.AirDate);   // an earlier wrong move is undone
        Assert.Null(ep.TvmazeAirDate);
        Assert.Equal(D("2023-04-27"), AirDates.Effective(ep.TmdbAirDate, ep.TvmazeAirDate));   // and a TMDB refresh keeps it
    }
}
