using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SeenIt.Contracts;

namespace SeenIt.UI.Services;

public static class UiServices
{
    /// <summary>Registers everything the shared UI needs. Hosts add an HttpClient base address and, optionally, an ITokenStore.</summary>
    public static IServiceCollection AddSeenItUi(this IServiceCollection services, Uri apiBase, HttpMessageHandler? handler = null)
    {
        services.AddAuthorizationCore();
        services.AddCascadingAuthenticationState();
        services.AddScoped(_ => handler is null ? new HttpClient { BaseAddress = apiBase } : new HttpClient(handler) { BaseAddress = apiBase });
        services.AddScoped<TokenManager>();
        services.AddScoped<ApiClient>();
        services.AddScoped<SessionState>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<SessionState>());
        services.AddScoped<Toasts>();
        if (services.All(s => s.ServiceType != typeof(IServerConfig)))
            services.AddSingleton<IServerConfig>(new FixedServerConfig(apiBase.ToString()));
        if (services.All(s => s.ServiceType != typeof(ITokenStore)))
            services.AddScoped<ITokenStore, BrowserTokenStore>();
        return services;
    }
}

public static class Img
{
    private const string Base = "https://image.tmdb.org/t/p/";
    public static string? Poster(string? path, string size = "w342") => path is null ? null : Base + size + path;
    public static string? Backdrop(string? path, string size = "w1280") => path is null ? null : Base + size + path;
    public static string? Profile(string? path) => path is null ? null : Base + "w185" + path;
    public static string? Still(string? path) => path is null ? null : Base + "w300" + path;
}

public static class Fmt
{
    public static string Href(MediaType type, int id) => type == MediaType.Movie ? $"movie/{id}" : $"show/{id}";
    public static string EpisodeHref(int showId, int? season, int? episode) => $"show/{showId}/season/{season}/episode/{episode}";

    /// <summary>Request body for a single play from a dialog choice.</summary>
    public static WatchRequest Watch(MediaType type, int id, WatchChoice c, int? season = null, int? episode = null) =>
        new(type, id, season, episode, c.At, c.When);

    /// <summary>Toast text for a watch choice, e.g. "on 3 Feb 2024".</summary>
    public static string When(WatchChoice c) => c.When switch
    {
        WatchTime.ReleaseDate => " (release date)",
        WatchTime.Unknown => " (date unknown)",
        WatchTime.Custom when c.At is { } at => $" on {at.LocalDateTime:d MMM yyyy}",
        _ => "",
    };

    public static string PlayedAt(DateTimeOffset d) =>
        WatchDates.IsUnknown(d) ? "Date unknown" : d.ToLocalTime().ToString("d MMM yyyy, HH:mm");
    public static string Ep(int? s, int? e) => $"S{s:00}E{e:00}";
    public static string Runtime(int? minutes) => minutes is null or 0 ? "" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60}m";

    public static string Duration(long minutes)
    {
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        return days > 0 ? $"{days}d {hours}h" : $"{hours}h {minutes % 60}m";
    }

    public static string Relative(DateTimeOffset when)
    {
        if (WatchDates.IsUnknown(when)) return "date unknown";
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return when.ToLocalTime().ToString("d MMM yyyy");
    }

    public static string Day(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (d == today) return "Today";
        if (d == today.AddDays(1)) return "Tomorrow";
        if (d == today.AddDays(-1)) return "Yesterday";
        return d.ToString("dddd, d MMM");
    }
}

/// <summary>Little pop-up confirmations ("Added to watchlist").</summary>
public class Toasts
{
    public record Toast(Guid Id, string Message, bool Error);
    private readonly List<Toast> _items = [];
    public IReadOnlyList<Toast> Items => _items;
    public event Action? Changed;

    public void Show(string message, bool error = false)
    {
        var t = new Toast(Guid.NewGuid(), message, error);
        _items.Add(t);
        Changed?.Invoke();
        _ = Task.Delay(error ? 5000 : 2800).ContinueWith(_ =>
        {
            _items.Remove(t);
            Changed?.Invoke();
        });
    }

    public void Error(Exception ex) => Show(ex is ApiException ? ex.Message : "Something went wrong.", error: true);
}
