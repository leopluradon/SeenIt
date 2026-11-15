using SeenIt.Contracts;

namespace SeenIt.Tests;

public class EpisodeMarkTests
{
    [Fact]
    public void Premieres_and_finales()
    {
        // Breaking Bad: 5 seasons, ended. S05E08 is TMDB's "mid_season", S05E16 "finale".
        Assert.Equal(EpisodeMark.SeriesPremiere, EpisodeMarks.Of(1, 1, "standard", true, 5, 7));
        Assert.Equal(EpisodeMark.SeasonPremiere, EpisodeMarks.Of(3, 1, "standard", true, 5, 13));
        Assert.Equal(EpisodeMark.SeasonFinale, EpisodeMarks.Of(3, 13, "finale", true, 5, 13));
        Assert.Equal(EpisodeMark.None, EpisodeMarks.Of(5, 8, "mid_season", true, 5, 16));
        Assert.Equal(EpisodeMark.SeriesFinale, EpisodeMarks.Of(5, 16, "finale", true, 5, 16));
        Assert.Equal(EpisodeMark.None, EpisodeMarks.Of(2, 4, "standard", true, 5, 13));
    }

    [Fact]
    public void Still_running_shows_have_no_series_finale_and_specials_get_nothing()
    {
        Assert.Equal(EpisodeMark.SeasonFinale, EpisodeMarks.Of(2, 10, "finale", false, 2, 10));
        Assert.Equal(EpisodeMark.None, EpisodeMarks.Of(0, 1, "standard", true, 5, 9));
        // An ended show's last episode before TMDB's type is cached still counts as the series finale.
        Assert.Equal(EpisodeMark.SeriesFinale, EpisodeMarks.Of(5, 16, null, true, 5, 16));
        // ...but not if TMDB says it's an ordinary episode (a list that runs past the real end).
        Assert.Equal(EpisodeMark.None, EpisodeMarks.Of(5, 16, "standard", true, 5, 16));
    }
}
