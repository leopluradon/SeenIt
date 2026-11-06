using SeenIt.Server.Services;
using SkiaSharp;

namespace SeenIt.Tests;

public class AvatarTests
{
    [Fact]
    public void A_wide_picture_becomes_a_256_square_jpeg()
    {
        using var bmp = new SKBitmap(400, 200);
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.Red); }
        using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);

        var jpeg = AvatarService.Process(png.ToArray());

        Assert.NotNull(jpeg);
        using var back = SKBitmap.Decode(jpeg);
        Assert.Equal((256, 256), (back.Width, back.Height));
        Assert.Equal(SKEncodedImageFormat.Jpeg, SKCodec.Create(new MemoryStream(jpeg!)).EncodedFormat);
    }

    [Fact]
    public void Something_that_isnt_a_picture_is_refused() =>
        Assert.Null(AvatarService.Process("not an image"u8.ToArray()));
}
