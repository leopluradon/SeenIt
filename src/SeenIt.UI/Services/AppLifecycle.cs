namespace SeenIt.UI.Services;

/// <summary>The Android app going to the background and coming back (raised by SeenIt.Mobile's window events).</summary>
public static class AppLifecycle
{
    public static event Action? Resumed;
    public static event Action? Stopped;
    public static void RaiseResumed() => Resumed?.Invoke();
    public static void RaiseStopped() => Stopped?.Invoke();
}
