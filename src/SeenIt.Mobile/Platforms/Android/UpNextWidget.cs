using System.Collections.Concurrent;
using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics;
using Android.Widget;
using SeenIt.Contracts;
using SeenIt.UI.Services;

namespace SeenIt.Mobile;

/// <summary>
/// Home-screen widget listing the next episode of up to four shows. Tapping a row opens that
/// episode in the app; the tick marks it watched (queued if the phone is offline).
/// </summary>
[BroadcastReceiver(Label = "SeenIt: Up next", Exported = true)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_upnext_info")]
public class UpNextWidget : AppWidgetProvider
{
    private const string ActionRefresh = "app.seenit.mobile.widget.REFRESH";
    private const string ActionWatch = "app.seenit.mobile.widget.WATCH";
    private const int Rows = 4;
    private static readonly ConcurrentDictionary<string, Bitmap?> Posters = new();
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly int[] RowIds = [Resource.Id.row1, Resource.Id.row2, Resource.Id.row3, Resource.Id.row4];
    private static readonly int[] PosterIds = [Resource.Id.poster1, Resource.Id.poster2, Resource.Id.poster3, Resource.Id.poster4];
    private static readonly int[] TitleIds = [Resource.Id.title1, Resource.Id.title2, Resource.Id.title3, Resource.Id.title4];
    private static readonly int[] SubIds = [Resource.Id.sub1, Resource.Id.sub2, Resource.Id.sub3, Resource.Id.sub4];
    private static readonly int[] CheckIds = [Resource.Id.check1, Resource.Id.check2, Resource.Id.check3, Resource.Id.check4];

    /// <summary>Asks every placed widget to reload (e.g. when the app goes to the background).</summary>
    public static void RequestUpdate(Context context)
    {
        var ids = AppWidgetManager.GetInstance(context)?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(UpNextWidget))));
        if (ids is not { Length: > 0 }) return;
        context.SendBroadcast(new Intent(context, typeof(UpNextWidget)).SetAction(ActionRefresh));
    }

    public override void OnUpdate(Context? context, AppWidgetManager? manager, int[]? ids)
    {
        if (context is null) return;
        RunAsync(context, () => RefreshAsync(context));
    }

    public override void OnReceive(Context? context, Intent? intent)
    {
        base.OnReceive(context, intent);
        if (context is null || intent is null) return;
        switch (intent.Action)
        {
            case ActionRefresh:
                RunAsync(context, () => RefreshAsync(context));
                break;
            case ActionWatch:
                var show = intent.GetIntExtra("show", 0);
                var season = intent.GetIntExtra("season", -1);
                var episode = intent.GetIntExtra("episode", -1);
                var name = intent.GetStringExtra("name") ?? "";
                if (show > 0 && season >= 0 && episode > 0)
                    RunAsync(context, () => WatchAsync(context, show, season, episode, name));
                break;
        }
    }

    /// <summary>Keeps the receiver alive while async work runs.</summary>
    private void RunAsync(Context context, Func<Task> work)
    {
        var pending = GoAsync();
        Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { Android.Util.Log.Warn("SeenIt", $"Widget update failed: {ex.Message}"); }
            finally { pending?.Finish(); }
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

    private static async Task WatchAsync(Context context, int show, int season, int episode, string name)
    {
        var api = Api(out var scope);
        using (scope)
        {
            if (api is null) return;
            try
            {
                await api.WatchAsync(new WatchRequest(MediaType.Episode, show, season, episode));
                Toast(context, $"Watched {name} {Fmt.Ep(season, episode)}");
            }
            catch (ApiException ex) { Toast(context, ex.Message); }
        }
        await RefreshAsync(context);
    }

    private static async Task RefreshAsync(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        var ids = manager?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(UpNextWidget))));
        if (manager is null || ids is not { Length: > 0 }) return;

        List<ShowProgressDto>? items = null;
        string? message = null;
        var api = Api(out var scope);
        using (scope)
        {
            if (api is null) message = "Open SeenIt to load your shows.";
            else
            {
                try
                {
                    items = (await api.ProgressAsync())
                        .Where(p => p.NextEpisode is not null && p.Status == ShowTrackStatus.Watching)
                        .Take(Rows).ToList();
                    if (items.Count == 0) message = "Nothing up next. Start a show in SeenIt.";
                }
                catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized) { message = "Sign in to SeenIt to see what's next."; }
                catch (ApiException) { message = "Can't reach SeenIt right now."; }
            }
        }

        var bitmaps = new Bitmap?[Rows];
        if (items is not null)
            await Task.WhenAll(items.Select(async (p, i) => bitmaps[i] = await PosterAsync(p.PosterPath)));

        var views = new RemoteViews(context.PackageName, Resource.Layout.widget_upnext);
        views.SetOnClickPendingIntent(Resource.Id.header, OpenPage(context, 90, "progress"));
        views.SetOnClickPendingIntent(Resource.Id.refresh,
            PendingIntent.GetBroadcast(context, 91, new Intent(context, typeof(UpNextWidget)).SetAction(ActionRefresh), Flags));
        views.SetViewVisibility(Resource.Id.empty, message is null ? Android.Views.ViewStates.Gone : Android.Views.ViewStates.Visible);
        views.SetTextViewText(Resource.Id.empty, message ?? "");
        views.SetOnClickPendingIntent(Resource.Id.empty, OpenPage(context, 92, "progress"));

        for (var i = 0; i < Rows; i++)
        {
            if (items is null || i >= items.Count)
            {
                views.SetViewVisibility(RowIds[i], Android.Views.ViewStates.Gone);
                continue;
            }
            var p = items[i];
            var n = p.NextEpisode!;
            var left = p.Aired - p.Watched;
            views.SetViewVisibility(RowIds[i], Android.Views.ViewStates.Visible);
            views.SetTextViewText(TitleIds[i], p.Name);
            views.SetTextViewText(SubIds[i], $"{Fmt.Ep(n.SeasonNumber, n.EpisodeNumber)} · {left} left");
            if (bitmaps[i] is { } bmp) views.SetImageViewBitmap(PosterIds[i], bmp);
            else views.SetImageViewResource(PosterIds[i], Android.Resource.Color.Transparent);
            views.SetOnClickPendingIntent(RowIds[i], OpenPage(context, 100 + i, Fmt.EpisodeHref(p.ShowTmdbId, n.SeasonNumber, n.EpisodeNumber)));

            var watch = new Intent(context, typeof(UpNextWidget)).SetAction(ActionWatch)
                .PutExtra("show", p.ShowTmdbId).PutExtra("season", n.SeasonNumber).PutExtra("episode", n.EpisodeNumber).PutExtra("name", p.Name);
            views.SetOnClickPendingIntent(CheckIds[i], PendingIntent.GetBroadcast(context, 200 + i, watch, Flags));
        }

        manager.UpdateAppWidget(ids, views);
    }

    private const PendingIntentFlags Flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;

    private static PendingIntent? OpenPage(Context context, int requestCode, string path)
    {
        var intent = new Intent(context, typeof(MainActivity))
            .PutExtra(MainActivity.PathExtra, path)
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(context, requestCode, intent, Flags);
    }

    private static async Task<Bitmap?> PosterAsync(string? path)
    {
        if (path is null) return null;
        if (Posters.TryGetValue(path, out var cached)) return cached;
        try
        {
            var bytes = await ImageHttp.GetByteArrayAsync(Img.Poster(path, "w92"));
            var bmp = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length);
            if (Posters.Count > 40) Posters.Clear();
            Posters[path] = bmp;
            return bmp;
        }
        catch (Exception) { return null; }
    }

    private static void Toast(Context context, string text) =>
        MainThread.BeginInvokeOnMainThread(() => Android.Widget.Toast.MakeText(context, text, ToastLength.Short)?.Show());
}
