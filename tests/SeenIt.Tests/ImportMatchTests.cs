using SeenIt.Contracts;
using SeenIt.Server.Import;
using SeenIt.Server.Services;
using SeenIt.Server.Tmdb;

namespace SeenIt.Tests;

public class ImportMatchTests
{
    private static TmdbListItem Movie(int id, string title, string date, string? original = null) =>
        new(id, "movie", title, null, date, null, null, null, null, null, original);

    [Fact]
    public void Exact_title_wins_over_popularity_and_years_may_be_off_by_one()
    {
        // TMDB dates 300 to 2007; Letterboxd says 2006. A 2006 search only finds other films.
        var search2006 = new List<TmdbListItem> { Movie(1317499, "My Poetic Works 300 Yen", "2006-07-16") };
        var search2007 = new List<TmdbListItem> { Movie(53182, "300: Rise of an Empire", "2014-03-05"), Movie(1271, "300", "2007-03-07") };
        Assert.Null(ImportRunner.Exact(search2006, "300", 2006));
        Assert.Equal(1271, ImportRunner.Exact(search2007, "300", 2006)?.Id);
    }

    [Fact]
    public void Titles_compare_without_case_punctuation_or_accents()
    {
        Assert.Equal(ImportRunner.Normalize("Spider-Man: No Way Home"), ImportRunner.Normalize("spider man no way home"));
        Assert.Equal(ImportRunner.Normalize("Amélie"), ImportRunner.Normalize("Amelie"));
        Assert.Equal(ImportRunner.Normalize("Fast & Furious"), ImportRunner.Normalize("Fast and Furious"));
        Assert.Equal(ImportRunner.Normalize("The School of Rock"), ImportRunner.Normalize("School of Rock"));
        Assert.Equal("a", ImportRunner.Normalize("A"));   // a lone article is the whole title
        Assert.Equal(2, ImportRunner.Exact([Movie(1, "Le Fabuleux", "2001-04-25"), Movie(2, "Amélie", "2001-04-25", "Le Fabuleux Destin d'Amélie Poulain")],
            "Le Fabuleux Destin d'Amelie Poulain", 2001)?.Id);
    }

    private static readonly ImportRef Film = new(MediaType.Movie, 1, null, "Film", 2000);
    private static readonly DateTimeOffset Day = new(2024, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static int Added(List<ImportRunner.ExistingPlay> existing, params ImportWatch[] imports)
    {
        var added = 0;
        foreach (var w in imports.OrderBy(w => w.Approximate).ThenBy(w => w.WatchedAt))
            if (ImportRunner.MatchPlay(existing, w) is null)
            {
                existing.Add(new ImportRunner.ExistingPlay(null, w.WatchedAt) { Used = true });
                added++;
            }
        return added;
    }

    [Fact]
    public void Same_viewing_from_another_source_is_skipped_but_rewatches_are_kept()
    {
        // Trakt logged it at 01:30 UTC the next day (evening in the US); Letterboxd says noon UTC that day.
        var existing = new List<ImportRunner.ExistingPlay> { new(Guid.NewGuid(), Day.AddHours(13.5)) };
        Assert.Equal(0, Added(existing, new ImportWatch(Film, null, null, Day)));

        // Watched twice in one day, and again a month later: three plays from one file, all kept.
        var fresh = new List<ImportRunner.ExistingPlay>();
        Assert.Equal(3, Added(fresh, new ImportWatch(Film, null, null, Day), new ImportWatch(Film, null, null, Day), new ImportWatch(Film, null, null, Day.AddDays(30))));
        // Importing the same file again adds nothing.
        foreach (var p in fresh) p.Used = false;
        Assert.Equal(0, Added(fresh, new ImportWatch(Film, null, null, Day), new ImportWatch(Film, null, null, Day), new ImportWatch(Film, null, null, Day.AddDays(30))));
    }

    [Fact]
    public void Each_existing_play_absorbs_only_one_import()
    {
        var existing = new List<ImportRunner.ExistingPlay> { new(Guid.NewGuid(), Day) };
        // One play here, two in the export on that day: one more is added.
        Assert.Equal(1, Added(existing, new ImportWatch(Film, null, null, Day), new ImportWatch(Film, null, null, Day.AddHours(3))));
    }

    [Fact]
    public void Stand_in_dates_match_any_play_and_unknown_dates_take_a_real_one()
    {
        // Letterboxd "watched" without a diary date: you already have it, from any day.
        var existing = new List<ImportRunner.ExistingPlay> { new(Guid.NewGuid(), Day.AddYears(-3)) };
        Assert.Equal(0, Added(existing, new ImportWatch(Film, null, null, Day, Approximate: true)));

        var unknown = new ImportRunner.ExistingPlay(Guid.NewGuid(), WatchDates.Unknown);
        var match = ImportRunner.MatchPlay([unknown], new ImportWatch(Film, null, null, Day));
        Assert.Same(unknown, match);
    }

    [Fact]
    public void Parts_filter_drops_unchosen_kinds()
    {
        var show = new ImportRef(MediaType.Show, 2, null, "Show", 2010);
        var parsed = new ParsedImport();
        parsed.History.Add(new ImportWatch(Film, null, null, Day));
        parsed.History.Add(new ImportWatch(show, 1, 1, Day));
        parsed.Ratings.Add(new ImportRating(Film, null, null, 8, Day));
        parsed.Lists.Add(new ImportList("Shows", null, [new ImportListItem(show, Day)]));
        parsed.Keep(ImportParts.Movies | ImportParts.History);
        Assert.Single(parsed.History);
        Assert.Equal(MediaType.Movie, parsed.History[0].Item.Type);
        Assert.Empty(parsed.Ratings);
        Assert.Empty(parsed.Lists);
    }

    [Fact]
    public void Season_completed_is_when_the_last_episode_was_first_watched()
    {
        var plays = new List<(int, DateTimeOffset)> { (1, Day), (2, Day.AddDays(2)), (3, Day.AddDays(5)), (1, Day.AddDays(40)) };
        Assert.Equal(Day.AddDays(5), CatalogService.SeasonCompleted(plays, 3));
        Assert.Null(CatalogService.SeasonCompleted(plays, 4));                                // not finished
        Assert.Null(CatalogService.SeasonCompleted([(1, Day), (2, WatchDates.Unknown)], 2));   // finished, day unknown
    }
}
