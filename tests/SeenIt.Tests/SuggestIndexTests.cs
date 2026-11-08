using SeenIt.Server.Services;
using Xunit;
using K = SeenIt.Server.Services.SuggestIndex.Kind;

namespace SeenIt.Tests;

public class SuggestIndexTests
{
    private static SuggestIndex With(params SuggestIndex.Entry[] entries)
    {
        var index = new SuggestIndex(null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SuggestIndex>.Instance);
        index.Use(entries);
        return index;
    }

    private static readonly SuggestIndex Index = With(
        new(K.Show, 95396, "Severance", 120),
        new(K.Movie, 1, "Seve", 2),
        new(K.Movie, 155, "The Dark Knight", 90),
        new(K.Movie, 438631, "Dune", 80),
        new(K.Movie, 2, "Dune Drifter", 3),
        new(K.Movie, 3, "Children of Dune Valley", 200),
        new(K.Movie, 194, "Le Fabuleux Destin d'Amélie Poulain", 30),
        new(K.Person, 525, "Christopher Nolan", 40),
        new(K.Show, 1396, "Breaking Bad", 300));

    private static List<string> Names(string q) => Index.Find(q, 6, 3).Select(x => x.Entry.Name).ToList();

    [Fact]
    public void Prefix_of_the_first_word_finds_the_popular_title() => Assert.Equal("Severance", Names("seve")[0]);

    [Fact]
    public void Matches_a_later_word() => Assert.Equal("The Dark Knight", Names("dark kn")[0]);

    [Fact]
    public void Exact_name_beats_a_more_popular_partial_match() => Assert.Equal("Dune", Names("dune")[0]);

    [Fact]
    public void Ignores_accents_and_case() => Assert.Contains("Le Fabuleux Destin d'Amélie Poulain", Names("AMELIE"));

    [Fact]
    public void Finds_people_by_any_name() => Assert.Contains("Christopher Nolan", Names("nol"));

    [Fact]
    public void Nothing_for_no_match() => Assert.Empty(Names("zzz"));

    [Fact]
    public void A_title_matching_at_two_words_counts_once() =>
        Assert.Single(With(new SuggestIndex.Entry(K.Movie, 9, "Dune Dune", 5)).Find("dune", 6, 3));

    [Theory]
    [InlineData("WALL·E", "wall e")]
    [InlineData("  Ocean's   Eleven! ", "oceans eleven")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Le Destin d’Amélie", "le destin d amelie")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, SuggestIndex.Normalize(input));

    [Fact]
    public void Parses_export_lines_and_skips_unpopular_and_video()
    {
        Assert.Equal(new SuggestIndex.Entry(K.Movie, 3924, "Blondie", 1.9707f),
            SuggestIndex.ParseLine("""{"adult":false,"id":3924,"original_title":"Blondie","popularity":1.9707,"video":false}""", K.Movie, "original_title", 1f));
        Assert.Null(SuggestIndex.ParseLine("""{"adult":false,"id":5,"original_title":"Obscure","popularity":0.2,"video":false}""", K.Movie, "original_title", 1f));
        Assert.Null(SuggestIndex.ParseLine("""{"adult":false,"id":6,"original_title":"Clip","popularity":9,"video":true}""", K.Movie, "original_title", 1f));
    }
}
