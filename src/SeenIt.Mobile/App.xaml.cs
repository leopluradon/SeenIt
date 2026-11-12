namespace SeenIt.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "SeenIt" };
        // Pages refresh what changed while the app was in the background (SeenIt.UI's LiveConnection).
        window.Resumed += (_, _) => SeenIt.UI.Services.AppLifecycle.RaiseResumed();
        window.Stopped += (_, _) => SeenIt.UI.Services.AppLifecycle.RaiseStopped();
        return window;
    }
}
