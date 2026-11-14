using SeenIt.Contracts;
using SeenIt.Server.Import;

namespace SeenIt.Tests;

public class ImportSinceTests
{
    private static readonly DateTimeOffset Cutoff = DateTimeOffset.Parse("2026-11-01T00:00:00-04:00");
    private static ImportRef Film(string t) => new(MediaType.Movie, null, null, t, 2020);

    [Fact]
    public void Only_whats_dated_on_or_after_the_day_is_kept()
    {
        var p = new ParsedImport();
        p.History.Add(new ImportWatch(Film("Old"), null, null, Cutoff.AddMinutes(-1)));
        p.History.Add(new ImportWatch(Film("New"), null, null, Cutoff));
        p.History.Add(new ImportWatch(Film("Undated"), null, null, WatchDates.Unknown));
        p.History.Add(new ImportWatch(Film("Logged"), null, null, Cutoff.AddDays(2), Approximate: true));
        p.Ratings.Add(new ImportRating(Film("Old"), null, null, 7, Cutoff.AddDays(-10)));
        p.Ratings.Add(new ImportRating(Film("New"), null, null, 9, Cutoff.AddHours(5)));
        p.Watchlist.Add(new ImportListItem(Film("Old"), Cutoff.AddDays(-1)));
        p.Lists.Add(new ImportList("Old list", null, [new ImportListItem(Film("Old"), Cutoff.AddDays(-3))]));
        p.Lists.Add(new ImportList("Mixed", null, [new ImportListItem(Film("Old"), Cutoff.AddDays(-3)), new ImportListItem(Film("New"), Cutoff.AddDays(1))]));
        p.Progress.Add(new ImportProgress(new ImportRef(MediaType.Show, null, null, "Lapsed", 2019), 2, 5, Cutoff.AddDays(-40)));
        p.Progress.Add(new ImportProgress(new ImportRef(MediaType.Show, null, null, "Current", 2019), 3, 1, Cutoff.AddDays(3)));
        p.Progress.Add(new ImportProgress(new ImportRef(MediaType.Show, null, null, "Unknown", 2019), 1, 2, null));
        p.Favorites.Add(new ImportListItem(Film("Fave"), DateTimeOffset.MinValue));

        p.KeepSince(Cutoff);

        Assert.Equal(["New", "Logged"], p.History.Select(h => h.Item.Title));
        Assert.Equal("New", p.Ratings.Single().Item.Title);
        Assert.Empty(p.Watchlist);
        Assert.Equal("Mixed", p.Lists.Single().Name);
        Assert.Equal("New", p.Lists.Single().Items.Single().Item.Title);
        Assert.Equal("Current", p.Progress.Single().Show.Title);
        Assert.Single(p.Favorites);   // no dates: kept, and skipped on import if already there
    }
}
