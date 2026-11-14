using Android.App;
using Android.App.Job;
using Android.Content;
using AndroidX.Core.App;
using SeenIt.Contracts;
using SeenIt.UI.Services;

namespace SeenIt.Mobile;

/// <summary>
/// Alerts as Android notifications. The app isn't a browser, so it can't get web push; instead it registers with the
/// server as a device and a background job collects what's waiting (Android runs it about every 15 minutes, later when
/// the phone is idle). The server decides what's sent, using the same settings as web push (kinds, quiet hours).
/// </summary>
public class AppNotifications : INativeNotifications
{
    public const string Channel = "alerts";
    private const int JobId = 4711;
    private const string OnKey = "seenit.notifications", InstallKey = "seenit.install";

    public bool IsOn => Preferences.Default.Get(OnKey, false);
    public string DeviceEndpoint => "app:" + InstallId;

    /// <summary>A random id for this install, made on first use.</summary>
    public static string InstallId
    {
        get
        {
            var id = Preferences.Default.Get(InstallKey, "");
            if (Guid.TryParse(id, out var g)) return g.ToString("N");
            id = Guid.NewGuid().ToString("N");
            Preferences.Default.Set(InstallKey, id);
            return id;
        }
    }

    public async Task<string?> TurnOnAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && await Permissions.RequestAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            return "Notifications are off for SeenIt. Allow them in Android Settings → Apps → SeenIt → Notifications.";
        var api = Api(out var scope);
        using (scope)
        {
            if (api is null) return "SeenIt isn't ready yet. Try again in a moment.";
            await api.AppPushRegisterAsync(InstallId, $"Android app · {DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim());
        }
        Preferences.Default.Set(OnKey, true);
        var context = Android.App.Application.Context;
        EnsureChannel(context);
        Schedule(context);
        return null;
    }

    public async Task TurnOffAsync()
    {
        Preferences.Default.Set(OnKey, false);
        Cancel(Android.App.Application.Context);
        var api = Api(out var scope);
        using (scope) { if (api is not null) await api.PushUnsubscribeAsync(DeviceEndpoint); }
    }

    public Task CheckNowAsync() => CheckAsync(Android.App.Application.Context);

    /// <summary>Started with the app: keeps the background check scheduled (e.g. after an update).</summary>
    public static void Resume(Context context)
    {
        if (!Preferences.Default.Get(OnKey, false)) return;
        EnsureChannel(context);
        Schedule(context);
    }

    private static void Schedule(Context context)
    {
        var scheduler = (JobScheduler?)context.GetSystemService(Context.JobSchedulerService);
        if (scheduler is null || scheduler.GetPendingJob(JobId) is not null) return;
        var job = new JobInfo.Builder(JobId, new ComponentName(context, Java.Lang.Class.FromType(typeof(AlertsJob))))
            .SetPeriodic(15 * 60 * 1000L)
            .SetRequiredNetworkType(NetworkType.Any)
            .SetPersisted(true)   // survives a reboot (RECEIVE_BOOT_COMPLETED)
            .Build();
        scheduler.Schedule(job);
    }

    private static void Cancel(Context context) =>
        ((JobScheduler?)context.GetSystemService(Context.JobSchedulerService))?.Cancel(JobId);

    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager?.GetNotificationChannel(Channel) is not null) return;
        manager?.CreateNotificationChannel(new NotificationChannel(Channel, "Alerts", NotificationImportance.Default)
        {
            Description = "New episodes and releases, recommendations, and friends watching what you recommended",
        });
    }

    private static ApiClient? Api(out IDisposable? scope)
    {
        scope = null;
        if (IPlatformApplication.Current?.Services is not { } services) return null;
        var s = services.CreateScope();
        scope = s;
        return s.ServiceProvider.GetRequiredService<ApiClient>();
    }

    /// <summary>Collects waiting notifications and shows them.</summary>
    public static async Task CheckAsync(Context context)
    {
        if (!Preferences.Default.Get(OnKey, false)) return;
        var api = Api(out var scope);
        using (scope)
        {
            if (api is null) return;
            List<PushPayload> items;
            try { items = await api.AppPushTakeAsync(InstallId); }
            catch (ApiException) { return; }          // signed out, offline, server down: try next time
            catch (HttpRequestException) { return; }
            EnsureChannel(context);
            foreach (var p in items) Show(context, p);
        }
    }

    private static void Show(Context context, PushPayload p)
    {
        var id = (p.Tag ?? p.Title + p.Body).GetHashCode() & 0x7fffffff;
        // Tapping it opens that page in the app (MainActivity hands PathExtra to the web view).
        var open = new Intent(context, typeof(MainActivity))
            .SetAction($"app.seenit.mobile.ALERT.{id}")
            .PutExtra(MainActivity.PathExtra, p.Url)
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        var tap = PendingIntent.GetActivity(context, id, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var notification = new NotificationCompat.Builder(context, Channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_seenit)
            .SetColor(Android.Graphics.Color.ParseColor("#E5484D"))
            .SetContentTitle(p.Title)
            .SetContentText(p.Body)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(p.Body))
            .SetAutoCancel(true)
            .SetContentIntent(tap)
            .Build();
        try { NotificationManagerCompat.From(context).Notify(p.Tag, id, notification); }
        catch (Java.Lang.SecurityException) { }   // permission taken back since: nothing to show
    }
}

/// <summary>The background check Android runs about every 15 minutes while notifications are on.</summary>
[Service(Name = "app.seenit.mobile.AlertsJob", Permission = "android.permission.BIND_JOB_SERVICE", Exported = true)]
public class AlertsJob : JobService
{
    public override bool OnStartJob(JobParameters? args)
    {
        Task.Run(async () =>
        {
            try { await AppNotifications.CheckAsync(this); }
            catch (Exception ex) { Android.Util.Log.Warn("SeenIt", $"Alert check failed: {ex.Message}"); }
            finally { JobFinished(args, false); }
        });
        return true;   // still working (on the task above)
    }

    public override bool OnStopJob(JobParameters? args) => true;   // cut short: try again
}
