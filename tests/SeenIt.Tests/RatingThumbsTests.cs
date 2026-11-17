using SeenIt.Contracts;

namespace SeenIt.Tests;

public class RatingThumbsTests
{
    [Fact]
    public void Thumbs_are_told_apart_from_ordinary_ratings()
    {
        Assert.True(RatingThumbs.Is(RatingThumbs.Up));
        Assert.True(RatingThumbs.Is(RatingThumbs.Down));
        Assert.False(RatingThumbs.Is(8));
        Assert.False(RatingThumbs.Is(8.1));   // decimal ratings have one decimal; thumbs have two
        Assert.False(RatingThumbs.Is(3));
        Assert.Equal("👍", RatingThumbs.Emoji(RatingThumbs.Up));
        Assert.Equal("👎", RatingThumbs.Emoji(RatingThumbs.Down));
        // Numbers, for someone who rates in thumbs: 6 and up is liked.
        Assert.True(RatingThumbs.Liked(6));
        Assert.False(RatingThumbs.Liked(5.9));
        // In stats they land in the 8 and 3 buckets.
        Assert.Equal(8, SeenIt.Server.Services.TrackingService.Bucket(RatingThumbs.Up));
        Assert.Equal(3, SeenIt.Server.Services.TrackingService.Bucket(RatingThumbs.Down));
    }
}
