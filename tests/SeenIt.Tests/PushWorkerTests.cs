using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class PushWorkerTests
{
    private static PushService Push(AppDbContext db, bool keys) => new(db, null!,
        new ConfigurationBuilder().AddInMemoryCollection(keys
            ? new Dictionary<string, string?> { ["SeenIt:VapidPublicKey"] = "pub", ["SeenIt:VapidPrivateKey"] = "priv" }
            : []).Build(), NullLogger<PushService>.Instance);

    [Fact]
    public async Task Alerts_for_people_without_devices_are_marked_done_and_nothing_is_sent()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var user = Guid.NewGuid();
        db.Users.Add(new AppUser { Id = user, UserName = "sam", TimeZone = "UTC" });
        db.Notifications.Add(new Notification { UserId = user, Type = MediaType.Movie, TmdbId = 1, Title = "Heat", Day = DateOnly.FromDateTime(DateTime.UtcNow) });
        db.Notifications.Add(new Notification { UserId = user, Type = MediaType.Movie, TmdbId = 2, Title = "Old", Day = new DateOnly(2026, 1, 1),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5) });
        await db.SaveChangesAsync();

        var push = Push(db, keys: true);
        Assert.True(push.Enabled);
        await PushWorker.RunOnceAsync(db, push, default);   // no devices: no HTTP client needed, nothing sent
        Assert.All(await db.Notifications.ToListAsync(), n => Assert.NotNull(n.PushedAt));
        Assert.False(Push(db, keys: false).Enabled);
    }

    [Fact]
    public async Task Subscribing_twice_from_one_device_keeps_one_row_and_bad_input_is_refused()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var push = Push(db, keys: true);
        var req = new PushSubscribeRequest("https://web.push.apple.com/abc", "p256", "auth", "iPhone (Home Screen app)");
        await push.SubscribeAsync(a, req, default);
        await push.SubscribeAsync(b, req, default);   // a shared device signed in as someone else takes it over
        var row = Assert.Single(await db.PushSubscriptions.ToListAsync());
        Assert.Equal(b, row.UserId);
        await Assert.ThrowsAsync<TrackingException>(() => push.SubscribeAsync(a, req with { Endpoint = "http://insecure.example/x" }, default));
        await push.UnsubscribeAsync(a, req.Endpoint, default);   // not theirs: stays
        Assert.Equal(1, await db.PushSubscriptions.CountAsync());
        await push.UnsubscribeAsync(b, req.Endpoint, default);
        Assert.Equal(0, await db.PushSubscriptions.CountAsync());
    }
}
