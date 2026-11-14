using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Tests;

public class AlertDeliveryTests
{
    [Fact]
    public void Every_kind_defaults_to_both_and_the_old_switch_still_counts()
    {
        Assert.All(Enum.GetValues<AlertKind>(), k => Assert.Equal(AlertDelivery.Both, UserPrefs.Default.DeliveryFor(k)));
        var old = UserPrefs.Default with { AlertRecWatched = false };
        Assert.Equal(AlertDelivery.Off, old.DeliveryFor(AlertKind.RecWatched));
        var chosen = old with { Alerts = new Dictionary<AlertKind, AlertDelivery> { [AlertKind.RecWatched] = AlertDelivery.Push } };
        Assert.Equal(AlertDelivery.Push, chosen.DeliveryFor(AlertKind.RecWatched));   // the new setting wins
        Assert.False(chosen.ListsAlert(AlertKind.RecWatched));
        Assert.True(chosen.PushesAlert(AlertKind.RecWatched));
        // Saved and read back (enum keys as names), junk dropped.
        var back = UserPrefsStore.Read(UserPrefsStore.Write(chosen with { Alerts = new Dictionary<AlertKind, AlertDelivery>
            { [AlertKind.Movie] = AlertDelivery.Off, [(AlertKind)99] = AlertDelivery.Alert } }));
        Assert.Equal(AlertDelivery.Off, back.DeliveryFor(AlertKind.Movie));
        Assert.Single(back.Alerts!);
    }

    [Fact]
    public async Task The_alerts_list_bell_and_push_each_follow_the_setting_per_kind()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var user = Guid.NewGuid();
        var prefs = UserPrefs.Default with { Alerts = new Dictionary<AlertKind, AlertDelivery>
            { [AlertKind.Movie] = AlertDelivery.Push, [AlertKind.Person] = AlertDelivery.Off, [AlertKind.Recommendation] = AlertDelivery.Alert } };
        db.Users.Add(new AppUser { Id = user, UserName = "sam", TimeZone = "UTC", PrefsJson = UserPrefsStore.Write(prefs) });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var (kind, i) in Enum.GetValues<AlertKind>().Select((k, i) => (k, i)))
            db.Notifications.Add(new Notification { UserId = user, Kind = kind, Type = MediaType.Movie, TmdbId = 100 + i, Title = kind.ToString(), Day = today });
        await db.SaveChangesAsync();

        var alerts = new ReleaseAlertService(db, null!, null!, new ConfigurationBuilder().Build(), NullLogger<ReleaseAlertService>.Instance);
        var list = await alerts.ListAsync(user, default);
        Assert.Equal(["Episode", "RecWatched", "Recommendation"], list.Items.Select(i => i.Title).Order(StringComparer.Ordinal));   // Movie is push-only, Person off
        Assert.Equal(3, list.Unread);
        Assert.Equal(3, await alerts.UnreadAsync(user, default));
    }
}
