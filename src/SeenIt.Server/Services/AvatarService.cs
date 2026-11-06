using Microsoft.EntityFrameworkCore;
using SeenIt.Server.Data;
using SkiaSharp;

namespace SeenIt.Server.Services;

/// <summary>Profile pictures: uploads are turned into a square 256×256 JPEG (centered crop, phone rotation applied).</summary>
public class AvatarService(AppDbContext db)
{
    public const int MaxUploadBytes = 10 * 1024 * 1024;
    private const int Size = 256;

    public static string? Path(string userName, long? version) =>
        version is { } v ? $"api/users/{Uri.EscapeDataString(userName)}/avatar?v={v}" : null;

    /// <summary>Returns the new version, or throws TrackingException if the file isn't a picture we can read.</summary>
    public async Task<long> SetAsync(Guid userId, Stream upload, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw new TrackingException("That file is empty.");
        var jpeg = Process(buffer.ToArray()) ?? throw new TrackingException("That doesn't look like a picture SeenIt can read. Try a JPEG, PNG or WebP.");

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var row = await db.Avatars.FirstOrDefaultAsync(a => a.UserId == userId, ct);
        if (row is null) db.Avatars.Add(new UserAvatar { UserId = userId, Data = jpeg });
        else row.Data = jpeg;
        user.AvatarVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        return user.AvatarVersion.Value;
    }

    public async Task RemoveAsync(Guid userId, CancellationToken ct)
    {
        await db.Avatars.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarVersion, (long?)null), ct);
    }

    public Task<byte[]?> GetAsync(string userName, CancellationToken ct) =>
        db.Users.Where(u => u.NormalizedUserName == userName.ToUpperInvariant())
            .Join(db.Avatars, u => u.Id, a => a.UserId, (u, a) => a.Data).FirstOrDefaultAsync(ct);

    /// <summary>Decode, undo the camera's rotation, crop to the middle square, resize, JPEG. Null if it isn't an image.</summary>
    public static byte[]? Process(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data));
        if (codec is null) return null;
        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null) return null;
        using var upright = Orient(decoded, codec.EncodedOrigin);

        var side = Math.Min(upright.Width, upright.Height);
        var src = SKRectI.Create((upright.Width - side) / 2, (upright.Height - side) / 2, side, side);
        using var square = new SKBitmap(Size, Size);
        using (var canvas = new SKCanvas(square))
        {
            canvas.Clear(SKColors.White);   // transparent PNGs get a white background
            using var image = SKImage.FromBitmap(upright);
            canvas.DrawImage(image, src, SKRect.Create(Size, Size), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        using var encoded = SKImage.FromBitmap(square).Encode(SKEncodedImageFormat.Jpeg, 86);
        return encoded.ToArray();
    }

    private static SKBitmap Orient(SKBitmap bmp, SKEncodedOrigin origin)
    {
        var (w, h, rotate, flip) = origin switch
        {
            SKEncodedOrigin.TopRight => (bmp.Width, bmp.Height, 0, true),
            SKEncodedOrigin.BottomRight => (bmp.Width, bmp.Height, 180, false),
            SKEncodedOrigin.BottomLeft => (bmp.Width, bmp.Height, 180, true),
            SKEncodedOrigin.LeftTop => (bmp.Height, bmp.Width, 90, true),
            SKEncodedOrigin.RightTop => (bmp.Height, bmp.Width, 90, false),
            SKEncodedOrigin.RightBottom => (bmp.Height, bmp.Width, 270, true),
            SKEncodedOrigin.LeftBottom => (bmp.Height, bmp.Width, 270, false),
            _ => (bmp.Width, bmp.Height, 0, false),
        };
        var result = new SKBitmap(w, h);
        using var canvas = new SKCanvas(result);
        canvas.Translate(w / 2f, h / 2f);
        if (flip) canvas.Scale(-1, 1);
        canvas.RotateDegrees(rotate);
        canvas.Translate(-bmp.Width / 2f, -bmp.Height / 2f);
        canvas.DrawBitmap(bmp, 0, 0);
        return result;
    }
}
