using Microsoft.JSInterop;
using SeenIt.Contracts;

namespace SeenIt.UI.Services;

/// <summary>
/// Applies the color theme: the CSS (via seenit.applyTheme), the browser's theme color and, in the
/// app, the system bar area. The account's choice wins once signed in; before that, whatever this
/// device last used.
/// </summary>
public class ThemeService(IJSRuntime js, IPlatform platform)
{
    private string? _applied;

    public string Current => _applied ?? UiPrefs.Themes[0].Key;

    public async Task ApplyAsync(string? key)
    {
        var theme = UiPrefs.Theme(key);
        if (_applied == theme.Key) return;
        _applied = theme.Key;
        try { await js.InvokeVoidAsync("seenit.applyTheme", theme.Key, theme.Bg); }
        catch (JSException) { }
        platform.SetChromeColor(theme.Bg);
    }

    /// <summary>On start-up, before the profile has loaded: match the system bars to this device's theme.</summary>
    public async Task ApplyDeviceThemeAsync()
    {
        if (_applied is not null) return;
        string? key = null;
        try { key = await js.InvokeAsync<string?>("localStorage.getItem", "seenit.theme"); }
        catch (JSException) { }
        await ApplyAsync(key);
    }
}
