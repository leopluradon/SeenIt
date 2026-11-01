namespace SeenIt.UI.Services;

/// <summary>
/// Which SeenIt server the app talks to. The website always uses its own origin; the mobile
/// app lets people point it at any SeenIt server.
/// </summary>
public interface IServerConfig
{
    bool Configurable { get; }
    string Url { get; }
    Task SetAsync(string url);
}

public class FixedServerConfig(string url) : IServerConfig
{
    public bool Configurable => false;
    public string Url => url;
    public Task SetAsync(string url) => Task.CompletedTask;
}
