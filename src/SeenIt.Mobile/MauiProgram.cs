using Microsoft.Extensions.Logging;
using SeenIt.UI.Services;

namespace SeenIt.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        var server = new PreferencesServerConfig();
        builder.Services.AddSingleton<IServerConfig>(server);
        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<IOfflineStore, FileOfflineStore>();
        builder.Services.AddScoped<IPlatform, MobilePlatform>();

        // The shared UI uses relative API paths against a placeholder base; the handler points
        // each request at whichever server is configured right now, so it can change at runtime.
        builder.Services.AddSeenItUi(ServerUrlHandler.PlaceholderBase, new ServerUrlHandler(server) { InnerHandler = new HttpClientHandler() });

        return builder.Build();
    }
}
