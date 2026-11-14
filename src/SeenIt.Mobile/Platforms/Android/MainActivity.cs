using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace SeenIt.Mobile;

// Share an IMDb, TMDB, Letterboxd or Trakt link from another app → open it in SeenIt (search resolves the link).
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain", Label = "SeenIt")]
// App Links: seenit.vohiyo.com pages open here instead of the browser (verified by /.well-known/assetlinks.json).
// Only page links: downloads, the API and feeds stay with the browser.
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], AutoVerify = true,
    DataScheme = "https", DataHost = "seenit.vohiyo.com",
    DataPathPrefixes = ["/movie/", "/show/", "/person/", "/u/", "/list/", "/lists", "/history", "/progress", "/calendar", "/watchlist",
        "/notifications", "/recommendations", "/friends", "/people", "/discover", "/search", "/stats", "/invite/", "/pick", "/favorites", "/ratings"])]
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Intent extra with an app page to open, e.g. "show/1396/season/1/episode/2".</summary>
    public const string PathExtra = "seenit.path";

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        DeepLinks.Open(PathFor(intent));
    }

    /// <summary>An app page to open: our own deep link, or a shared link/text sent to search.</summary>
    private static string? PathFor(Intent? intent)
    {
        if (intent?.GetStringExtra(PathExtra) is { Length: > 0 } path) return path;
        // An App Link: the same page, in the app.
        if (intent?.Action == Intent.ActionView && intent.Data is { Scheme: "https" } appLink && appLink.Host == "seenit.vohiyo.com")
            return ((appLink.EncodedPath ?? "").TrimStart('/') + (appLink.EncodedQuery is { Length: > 0 } q ? "?" + q : "")) is { Length: > 0 } page ? page : null;
        if (intent?.Action == Intent.ActionSend && intent.GetStringExtra(Intent.ExtraText) is { Length: > 0 } text)
        {
            var link = System.Text.RegularExpressions.Regex.Match(text, @"https?://\S+");
            var query = link.Success ? link.Value : text.Trim();
            return "search?q=" + Uri.EscapeDataString(query.Length > 500 ? query[..500] : query);
        }
        return null;
    }

    protected override void OnPause()
    {
        base.OnPause();
        // Whatever was just marked watched in the app should show on the widget too.
        UpNextWidget.RequestUpdate(this);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        DeepLinks.Open(PathFor(Intent));
        AppNotifications.Resume(this);
        if (Window is not { } window) return;

        // Android 15+ draws apps edge-to-edge, and the WebView doesn't report safe-area insets to
        // CSS, so pad the content by the status/navigation bars (and keyboard) ourselves.
        window.DecorView.SetBackgroundColor(Android.Graphics.Color.ParseColor("#0E1014"));
        new WindowInsetsControllerCompat(window, window.DecorView)
        {
            AppearanceLightStatusBars = false,
            AppearanceLightNavigationBars = false,
        };

        var content = window.DecorView.FindViewById(Android.Resource.Id.Content);
        if (content is not null)
            ViewCompat.SetOnApplyWindowInsetsListener(content, new InsetsListener());
    }

    private sealed class InsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(Android.Views.View? view, WindowInsetsCompat? insets)
        {
            if (view is null || insets is null) return insets;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout() | WindowInsetsCompat.Type.Ime());
            if (bars is null) return insets;
            view.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return WindowInsetsCompat.Consumed;
        }
    }
}
