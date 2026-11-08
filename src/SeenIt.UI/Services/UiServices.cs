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
        services.AddScoped<BadgeScores>();
        services.AddScoped<WatchlistNudge>();
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
    public static string CalHref(CalendarEntry c) => c.Type == MediaType.Episode ? EpisodeHref(c.TmdbId, c.SeasonNumber, c.EpisodeNumber) : Href(c.Type, c.TmdbId);
    /// <summary>The line under a movie or new series on the calendar: "Movie release · Christopher Nolan".</summary>
    public static string CalKind(CalendarEntry c) => (c.Type == MediaType.Show ? "New series" : "Movie release") + (c.Via is { } v ? " · " + v : "");

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
        WatchTime.Custom when c.At is { } at => $" on {(at.LocalDateTime).ToString(Fmt.DateLong)}",
        _ => "",
    };

    public static string PlayedAt(DateTimeOffset d) =>
        WatchDates.IsUnknown(d) ? "Date unknown" : d.ToLocalTime().ToString(Fmt.DateTimeLong);
    public static string Source(WatchSource s) => WatchSources.Name(s);
    public static string Ep(int? s, int? e) => $"S{s:00}E{e:00}";
    public static string Runtime(int? minutes) => minutes is null or 0 ? "" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60}m";

    /// <summary>Display, tracking and privacy choices (set from the signed-in profile).</summary>
    public static UserPrefs Prefs { get; set; } = UserPrefs.Default;
    /// <summary>Settings → Display → Scores: whether this source's score shows for this kind of title.</summary>
    public static bool ShowsScore(string source, MediaType type, bool season = false) => Prefs.ShowsScore(source, UserPrefs.Kind(type, season));
    /// <summary>Whether scores start blurred (until tapped) for something you have or haven't watched.</summary>
    public static bool HideScores(bool watched) => Prefs.ScoreSpoilers == ScoreSpoilers.Always || (Prefs.ScoreSpoilers == ScoreSpoilers.Unwatched && !watched);

    // Date and time patterns in the user's style ("Nov 6, 2026" / "6 Nov 2026" / "2026-11-06", 12/24-hour clock).
    public static string DateLong => Prefs.DateStyle switch { DateStyle.DayFirst => "d MMM yyyy", DateStyle.Iso => "yyyy-MM-dd", _ => "MMM d, yyyy" };
    public static string DateShort => Prefs.DateStyle switch { DateStyle.DayFirst => "d MMM", DateStyle.Iso => "MM-dd", _ => "MMM d" };
    public static string DateFull => Prefs.DateStyle switch { DateStyle.DayFirst => "d MMMM yyyy", DateStyle.Iso => "yyyy-MM-dd", _ => "MMMM d, yyyy" };
    public static string TimeOnly => Prefs.Clock24 ? "HH:mm" : "h:mm tt";
    public static string DateTimeLong => Prefs.DateStyle == DateStyle.Iso ? $"yyyy-MM-dd {TimeOnly}" : $"{DateLong}, {TimeOnly}";
    public static string DayShort => Prefs.DateStyle switch { DateStyle.DayFirst => "ddd d MMM", DateStyle.Iso => "ddd MM-dd", _ => "ddd, MMM d" };
    public static string DayLong => Prefs.DateStyle switch { DateStyle.DayFirst => "dddd d MMMM", DateStyle.Iso => "dddd MM-dd", _ => "dddd, MMM d" };
    public static string DayLongYear => Prefs.DateStyle switch { DateStyle.DayFirst => "dddd d MMMM yyyy", DateStyle.Iso => "dddd yyyy-MM-dd", _ => "dddd, MMM d, yyyy" };
    /// <summary>The first day of the week for calendars and weekday charts.</summary>
    public static DayOfWeek WeekStart => Prefs.WeekStartsMonday ? DayOfWeek.Monday : DayOfWeek.Sunday;

    /// <summary>Weekday chart buckets (Sun…Sat from the server) in the user's week order.</summary>
    public static IReadOnlyList<CountBucket> WeekOrder(IReadOnlyList<CountBucket> days) =>
        Prefs.WeekStartsMonday && days.Count == 7 && days[0].Label.StartsWith("Sun") ? [.. days.Skip(1), days[0]] : days;

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
        WatchDates.IsUnknown(when) ? "Date unknown" : when.ToLocalTime().ToString($"{DayLongYear}, {TimeOnly}");

    public static string Relative(DateTimeOffset when)
    {
        if (WatchDates.IsUnknown(when)) return "date unknown";
        var d = DateTimeOffset.Now - when;
        if (d.TotalMinutes < -1) return when.ToLocalTime().ToString(Fmt.DateLong); // logged ahead of time
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return when.ToLocalTime().ToString(Fmt.DateLong);
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
        ({ } a, { Length: > 0 } n) => $"{(a.ToLocalTime()).ToString(Fmt.TimeOnly)} on {n}",
        ({ } a, _) => a.ToLocalTime().ToString(Fmt.TimeOnly),
        (null, { Length: > 0 } n) => $"on {n}",
        _ => null,
    };

    public static string Day(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (d == today) return "Today";
        if (d == today.AddDays(1)) return "Tomorrow";
        if (d == today.AddDays(-1)) return "Yesterday";
        return d.Year == today.Year ? d.ToString(DayLong) : d.ToString(DayLongYear);
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
    public record Toast(Guid Id, string Message, bool Error, Func<Task>? Undo = null, string? ActionLabel = null);
    private readonly List<Toast> _items = [];
    public IReadOnlyList<Toast> Items => _items;
    public event Action? Changed;

    /// <param name="undo">Shows an Undo button that runs this (stays up longer so there's time to tap it).</param>
    public void Show(string message, bool error = false, Func<Task>? undo = null) => Add(new Toast(Guid.NewGuid(), message, error, undo));

    /// <summary>A toast with its own button ("Remove") instead of Undo.</summary>
    public void Offer(string message, string label, Func<Task> action) => Add(new Toast(Guid.NewGuid(), message, false, action, label));

    private void Add(Toast t)
    {
        var undo = t.Undo; var error = t.Error;
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
            if (t.ActionLabel is null) Show("Undone");
        }
        catch (Exception ex) { Error(ex); }
    }

    public void Error(Exception ex) => Show(ex is ApiException ? ex.Message : "Something went wrong.", error: true);
}


/// <summary>
/// Scores for poster badges from a site other than TMDB (Settings → Display → Scores). Cards ask
/// for their title; requests made in the same moment go to the server as one batch per source.
/// </summary>
public sealed class BadgeScores(ApiClient api)
{
    private static readonly TimeSpan RetryMissing = TimeSpan.FromMinutes(2);
    private readonly Dictionary<(string, MediaType, int), BadgeScore> known = [];
    private readonly Dictionary<(string, MediaType, int), DateTimeOffset> missing = [];
    private readonly Dictionary<string, (HashSet<BadgeTitle> Titles, TaskCompletionSource Done)> pending = [];

    public BadgeScore? Get(string source, MediaType type, int tmdbId) => known.GetValueOrDefault((source, type, tmdbId));

    public Task LoadAsync(string source, MediaType type, int tmdbId)
    {
        var key = (source, type, tmdbId);
        if (known.ContainsKey(key) || missing.TryGetValue(key, out var at) && DateTimeOffset.UtcNow - at < RetryMissing) return Task.CompletedTask;
        if (!pending.TryGetValue(source, out var batch))
        {
            batch = ([], new TaskCompletionSource());
            pending[source] = batch;
            _ = FlushAsync(source);
        }
        batch.Titles.Add(new BadgeTitle(type, tmdbId));
        return batch.Done.Task;
    }

    private async Task FlushAsync(string source)
    {
        await Task.Delay(50);   // let the rest of the row ask too
        var (titles, done) = pending[source];
        pending.Remove(source);
        try
        {
            var got = await api.BadgesAsync(new BadgeRequest(source, [.. titles]));
            foreach (var b in got) known[(source, b.Type, b.TmdbId)] = b;
            foreach (var t in titles.Where(t => !known.ContainsKey((source, t.Type, t.TmdbId)))) missing[(source, t.Type, t.TmdbId)] = DateTimeOffset.UtcNow;
        }
        catch { /* badges are a nicety */ }
        done.TrySetResult();
    }

    public static string Format(BadgeScore b) => b.Percent ? $"{b.Value:0}%" : b.Max >= 100 ? $"{b.Value:0}" : b.Value.ToString("0.0");
}

/// <summary>
/// "Ask" for Settings → Tracking → watchlist: after marking something watched that's still on your
/// watchlist, offers to take it off.
/// </summary>
public sealed class WatchlistNudge
{
    private readonly ApiClient api;
    private readonly Toasts toasts;
    public WatchlistNudge(ApiClient api, Toasts toasts)
    {
        this.api = api;
        this.toasts = toasts;
        api.Watched += OnWatched;
    }

    private async Task OnWatched(MediaType type, int tmdbId)
    {
        if (Fmt.Prefs.WatchlistOnWatch != WatchlistOnWatch.Ask) return;
        var kind = type == MediaType.Movie ? MediaType.Movie : MediaType.Show;
        try
        {
            if (!await api.OnWatchlistAsync(kind, tmdbId)) return;
            toasts.Offer(kind == MediaType.Movie ? "This movie is still on your watchlist." : "This show is still on your watchlist.", "Remove",
                async () => { await api.RemoveFromWatchlistAsync(kind, tmdbId); toasts.Show("Removed from watchlist"); });
        }
        catch { /* offline or similar: no prompt */ }
    }
}
