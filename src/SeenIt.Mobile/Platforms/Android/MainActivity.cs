using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace SeenIt.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>Intent extra with an app page to open, e.g. "show/1396/season/1/episode/2".</summary>
    public const string PathExtra = "seenit.path";

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        DeepLinks.Open(intent?.GetStringExtra(PathExtra));
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
        DeepLinks.Open(Intent?.GetStringExtra(PathExtra));
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
