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
        services.AddScoped(_ => handler is null ? new HttpClient { BaseAddress = apiBase } : new HttpClient(handler, disposeHandler: false) { BaseAddress = apiBase });
        services.AddScoped<TokenManager>();
        services.AddScoped<ApiClient>();
        services.AddScoped<SessionState>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<SessionState>());
        services.AddScoped<Toasts>();
        services.AddScoped<ThemeService>();
        services.AddScoped<ConnectivityState>();
        if (services.All(s => s.ServiceType != typeof(IOfflineStore)))
            services.AddSingleton<IOfflineStore, NoOfflineStore>();
        if (services.All(s => s.ServiceType != typeof(IPlatform)))
            services.AddScoped<IPlatform, BrowserPlatform>();
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

    /// <summary>Poster sizes for srcset: the browser takes the smallest that's sharp on this screen.</summary>
    public static string? PosterSet(string? path, params int[] widths) =>
        path is null ? null : string.Join(", ", (widths.Length > 0 ? widths : [185, 342, 500]).Select(w => $"{Base}w{w}{path} {w}w"));

    /// <summary>
    /// A backdrop as CSS variables: phones get the 780-wide image (it's under a dark shade anyway,
    /// and half the download), wider screens the 1280. See .bg-responsive in seenit.css.
    /// </summary>
    public static string BackdropStyle(string? path) =>
        path is null ? "" : $"--bg-sm:url('{Base}w780{path}'); --bg-lg:url('{Base}w1280{path}')";
}

public static class Fmt
{
    public static string Href(MediaType type, int id) => type == MediaType.Movie ? $"movie/{id}" : $"show/{id}";
    public static string UserHref(string userName) => $"u/{Uri.EscapeDataString(userName)}";
    public static string CompareHref(string a, string b) => $"compare/{Uri.EscapeDataString(a)}/{Uri.EscapeDataString(b)}";
    public static string EpisodeHref(int showId, int? season, int? episode) => $"show/{showId}/season/{season}/episode/{episode}";

    /// <summary>Request body for a single play from a dialog choice.</summary>
    public static WatchRequest Watch(MediaType type, int id, WatchChoice c, int? season = null, int? episode = null) =>
        new(type, id, season, episode, c.At, c.When);

    /// <summary>How the signed-in user gives and sees ratings (stored as 1–10 either way).</summary>
    public static RatingScale Scale { get; set; }
    public static bool FiveStars => Scale == RatingScale.FiveStars;
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>A rating in the user's scale: "8/10", "8.1/10" or "4/5".</summary>
    public static string Rating(double r) => FiveStars ? $"{Stars(r)}/5" : $"{Stars(r)}/10";
    /// <summary>The number alone: "8", "8.1" (decimals), or "4.5" in stars.</summary>
    public static string Stars(double r) => Scale switch
    {
        RatingScale.FiveStars => (r / 2).ToString("0.#", Inv),
        RatingScale.Decimal => r.ToString("0.0", Inv),
        _ => r.ToString("0.#", Inv),   // whole numbers, unless a friend rated in decimals
    };
    /// <summary>Poster badge: "★ 8", "★ 8.1" or "★ 4.5".</summary>
    public static string RatingBadge(double r) => $"★ {Stars(r)}";
    /// <summary>An average or difference of 1–10 ratings, in the user's scale.</summary>
    public static string Avg(double v) => (FiveStars ? v / 2 : v).ToString("0.0");
    /// <summary>Rating distribution buckets labeled "1".."10", relabeled for stars.</summary>
    public static IReadOnlyList<CountBucket> RatingBuckets(IReadOnlyList<CountBucket> b) =>
        FiveStars ? b.Select(x => int.TryParse(x.Label, out var n) ? x with { Label = Stars(n) } : x).ToList() : b;

    /// <summary>Toast text after "everything up to here".</summary>
    public static string Through(int added, WatchChoice c) =>
        added == 0 ? "Already watched all of those" : $"Marked {added} episode{(added == 1 ? "" : "s")} watched{When(c)}";

    /// <summary>Whether an "Up to here" button makes sense: not on a show's very first episode.</summary>
    public static bool CanThrough(int season, int episode) => !(season <= 1 && episode <= 1);

    /// <summary>Toast text for a watch choice, e.g. "on 3 Feb 2024".</summary>
    public static string When(WatchChoice c) => c.When switch
    {
        WatchTime.ReleaseDate => " (release date)",
        WatchTime.Unknown => " (date unknown)",
        WatchTime.Custom when c.At is { } at => $" on {at.LocalDateTime:MMM d, yyyy}",
        _ => "",
    };

    public static string PlayedAt(DateTimeOffset d) =>
        WatchDates.IsUnknown(d) ? "Date unknown" : d.ToLocalTime().ToString("MMM d, yyyy, h:mm tt");
    public static string Source(WatchSource s) => WatchSources.Name(s);
    public static string Ep(int? s, int? e) => $"S{s:00}E{e:00}";
    public static string Runtime(int? minutes) => minutes is null or 0 ? "" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60}m";

    /// <summary>The user's choice for long durations: days and hours, or hours.</summary>
    public static TimeFormat Time { get; set; }

    /// <summary>"48d 7h" or "1,159h" (the user's choice) from a day up; "5h 12m" below that.</summary>
    public static string Duration(long minutes) => minutes >= 1440 ? Long(minutes, Time) : Short(minutes);

    private static string Short(long minutes) => minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";
    private static string Long(long minutes, TimeFormat f) =>
        f == TimeFormat.Hours ? $"{minutes / 60:N0}h" : $"{minutes / 1440}d {minutes % 1440 / 60}h";

    /// <summary>
    /// Hover text for a duration of a day or more, in the other format: "2d 1h" → "49 hours",
    /// "49h" → "2 days 1 hour". Null below a day.
    /// </summary>
    public static string? HoursTip(long minutes) => minutes < 1440 ? null
        : Time == TimeFormat.Hours ? $"{minutes / 1440:N0} day{(minutes / 1440 == 1 ? "" : "s")} {minutes % 1440 / 60} hour{(minutes % 1440 / 60 == 1 ? "" : "s")}"
        : $"{minutes / 60:N0} hours";

    private static readonly System.Text.RegularExpressions.Regex DaysRe = new(@"(\d+)d (\d+)h");
    private static readonly System.Text.RegularExpressions.Regex HoursRe = new(@"^~?([\d,]+)h$");
    /// <summary>The same, for text holding a duration (stat boxes, table cells).</summary>
    public static string? HoursTip(string? text)
    {
        if (text is null) return null;
        if (DaysRe.Match(text) is { Success: true } d) return HoursTip((long.Parse(d.Groups[1].Value) * 24 + long.Parse(d.Groups[2].Value)) * 60);
        if (HoursRe.Match(text.Trim()) is { Success: true } h && long.TryParse(h.Groups[1].Value.Replace(",", ""), out var hours)) return HoursTip(hours * 60);
        return null;
    }

    /// <summary>The full date and time, for hovering over a relative one ("3d ago").</summary>
    public static string Exact(DateTimeOffset when) =>
        WatchDates.IsUnknown(when) ? "Date unknown" : when.ToLocalTime().ToString("dddd, MMMM d, yyyy, h:mm tt");

    public static string Relative(DateTimeOffset when)
    {
        if (WatchDates.IsUnknown(when)) return "date unknown";
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < -1) return when.ToLocalTime().ToString("MMM d, yyyy"); // logged ahead of time
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return when.ToLocalTime().ToString("MMM d, yyyy");
    }

    /// <summary>Time until a release day: "today", "tomorrow", "in 5 days", "in 3 weeks", "in 2 months". Null once it's past.</summary>
    public static string? Countdown(DateOnly d)
    {
        var days = d.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
        return days switch
        {
            < 0 => null,
            0 => "today",
            1 => "tomorrow",
            < 14 => $"in {days} days",
            < 60 => $"in {(int)Math.Round(days / 7.0)} weeks",
            < 365 => $"in {(int)Math.Round(days / 30.4)} months",
            _ => $"in {days / 365} year{(days / 365 == 1 ? "" : "s")}{(days % 365 / 30 is > 0 and var m ? $" {m} month{(m == 1 ? "" : "s")}" : "")}",
        };
    }

    /// <summary>With an exact air time: "in 25 minutes", "in 5 hours" up to a day and a half out, then by day.</summary>
    public static string? Countdown(DateTimeOffset at)
    {
        var left = at - DateTimeOffset.Now;
        if (left < TimeSpan.Zero) return null;
        if (left < TimeSpan.FromMinutes(60)) return $"in {Math.Max(1, (int)left.TotalMinutes)} minute{((int)left.TotalMinutes == 1 ? "" : "s")}";
        if (left < TimeSpan.FromHours(36)) return $"in {(int)Math.Round(left.TotalHours)} hours";
        return Countdown(DateOnly.FromDateTime(at.LocalDateTime));
    }

    /// <summary>"9:00 PM on HBO", "9:00 PM", "on HBO", or null.</summary>
    public static string? AirTime(DateTimeOffset? at, string? network) => (at, network) switch
    {
        ({ } a, { Length: > 0 } n) => $"{a.ToLocalTime():h:mm tt} on {n}",
        ({ } a, _) => a.ToLocalTime().ToString("h:mm tt"),
        (null, { Length: > 0 } n) => $"on {n}",
        _ => null,
    };

    public static string Day(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (d == today) return "Today";
        if (d == today.AddDays(1)) return "Tomorrow";
        if (d == today.AddDays(-1)) return "Yesterday";
        return d.Year == today.Year ? d.ToString("dddd, MMM d") : d.ToString("dddd, MMM d, yyyy");
    }
}

public static class TextMatch
{
    /// <summary>Whether the title contains the query, ignoring case, accents and punctuation. An empty query matches everything.</summary>
    public static bool Contains(string title, string? query)
    {
        var q = Fold(query);
        return q.Length == 0 || Fold(title).Contains(q, StringComparison.Ordinal);
    }

    /// <summary>The whole title equals the query, ignoring case, accents and punctuation ("exact match").</summary>
    public static bool Same(string title, string? query)
    {
        var q = Fold(query);
        return q.Length > 0 && Fold(title) == q;
    }

    private static string Fold(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var c in s.Replace("&", " and ").Normalize(System.Text.NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>Little pop-up confirmations ("Added to watchlist").</summary>
public class Toasts
{
    public record Toast(Guid Id, string Message, bool Error, Func<Task>? Undo = null);
    private readonly List<Toast> _items = [];
    public IReadOnlyList<Toast> Items => _items;
    public event Action? Changed;

    /// <param name="undo">Shows an Undo button that runs this (stays up longer so there's time to tap it).</param>
    public void Show(string message, bool error = false, Func<Task>? undo = null)
    {
        var t = new Toast(Guid.NewGuid(), message, error, undo);
        _items.Add(t);
        Changed?.Invoke();
        _ = Task.Delay(undo is not null ? 7000 : error ? 5000 : 2800).ContinueWith(_ => Dismiss(t));
    }

    public void Dismiss(Toast t)
    {
        if (_items.Remove(t)) Changed?.Invoke();
    }

    public async Task UndoAsync(Toast t)
    {
        if (t.Undo is not { } undo) return;
        Dismiss(t);
        try
        {
            await undo();
            Show("Undone");
        }
        catch (Exception ex) { Error(ex); }
    }

    public void Error(Exception ex) => Show(ex is ApiException ? ex.Message : "Something went wrong.", error: true);
}
