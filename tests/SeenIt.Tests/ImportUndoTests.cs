using SeenIt.Contracts;
using SeenIt.Server.Import;

namespace SeenIt.Tests;

public class ImportUndoTests
{
    [Fact]
    public void Unmatched_titles_keep_their_rows_and_come_back_as_the_picked_title()
    {
        var odd = new ImportRef(MediaType.Movie, null, null, "Some Obscure Film", 1971);
        var known = new ImportRef(MediaType.Movie, 949, null, "Heat", 1995);
        var parsed = new ParsedImport();
        var at = new DateTimeOffset(2024, 1, 2, 20, 0, 0, TimeSpan.Zero);
        parsed.History.Add(new ImportWatch(odd, null, null, at));
        parsed.History.Add(new ImportWatch(known, null, null, at));
        parsed.Ratings.Add(new ImportRating(odd, null, null, 8, at));
        parsed.Lists.Add(new ImportList("Weird ones", null, [new ImportListItem(odd, at)]));
        var resolved = new Dictionary<ImportRef, int?> { [odd] = null, [known] = 949 };

        var pending = PendingImport.Parse(PendingImport.Serialize(PendingImport.From(parsed, resolved)));
        var item = Assert.Single(pending);
        Assert.Equal("Some Obscure Film (1971)", item.Label);
        Assert.Equal("1 play · rated 8 · list “Weird ones”", item.Summary());

        var again = item.ToParsed(12345);
        Assert.Equal(12345, Assert.Single(again.History).Item.TmdbId);
        Assert.Equal(8, Assert.Single(again.Ratings).Rating);
        Assert.Equal("Weird ones", Assert.Single(again.Lists).Name);
    }

    [Fact]
    public void Undo_keeps_the_rating_from_before_the_first_change()
    {
        var id = Guid.NewGuid();
        var first = new ImportUndo { Ratings = [new RatingBefore(id, 6, DateTimeOffset.UnixEpoch)] };
        var second = new ImportUndo { Ratings = [new RatingBefore(id, 9, DateTimeOffset.UtcNow)], Dated = [Guid.NewGuid()] };
        var merged = ImportUndo.Parse(ImportUndo.Merge(ImportUndo.Merge(null, first), second));
        Assert.Equal(6, Assert.Single(merged.Ratings).Value);
        Assert.Single(merged.Dated);
    }
}
