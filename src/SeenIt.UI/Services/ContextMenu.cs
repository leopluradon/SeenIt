using System.Text.Json;
using System.Text.Json.Serialization;
using SeenIt.Contracts;

namespace SeenIt.UI.Services;

public enum MenuKind { Title, Episode, User }

/// <summary>
/// What a right-click / long-press menu is about. Elements carry it as JSON in a data-ctx attribute (see Menu.*),
/// so any card or row gets the menu just by having the attribute.
/// Play: one of your own plays (history rows, Recently watched), which adds "change date / watch again / delete".
/// </summary>
public record MenuTarget(
    MenuKind Kind,
    MediaType Type = MediaType.Movie,
    int Id = 0,
    string? Title = null,
    string? Poster = null,
    int? S = null,
    int? E = null,
    string? EpTitle = null,
    Guid? Play = null,
    DateTimeOffset? At = null,
    string? User = null,
    string? Name = null,
    string? Avatar = null,
    bool Following = false);

/// <summary>Builds data-ctx values.</summary>
public static class Menu
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Json(MenuTarget t) => JsonSerializer.Serialize(t, Opts);
    public static MenuTarget? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<MenuTarget>(json, Opts); }
        catch (JsonException) { return null; }
    }

    public static string Title(MediaType type, int id, string? title, string? poster) =>
        Json(new MenuTarget(MenuKind.Title, type == MediaType.Movie ? MediaType.Movie : MediaType.Show, id, title, poster));
    public static string Title(MediaSummary m) => Title(m.Type, m.TmdbId, m.Title, m.PosterPath);

    public static string Episode(int showId, int season, int episode, string? show, string? epTitle, string? poster, Guid? play = null, DateTimeOffset? at = null) =>
        Json(new MenuTarget(MenuKind.Episode, MediaType.Episode, showId, show, poster, season, episode, epTitle, play, at));

    /// <summary>A history entry. Mine: it's your play, so the play's own actions are offered.</summary>
    public static string Play(HistoryEntry h, bool mine) => h.Type switch
    {
        MediaType.Episode when h.SeasonNumber is { } s && h.EpisodeNumber is { } e =>
            Episode(h.TmdbId, s, e, h.Title, h.EpisodeTitle, h.PosterPath, mine ? h.Id : null, mine ? h.WatchedAt : null),
        MediaType.Movie => Json(new MenuTarget(MenuKind.Title, MediaType.Movie, h.TmdbId, h.Title, h.PosterPath, Play: mine ? h.Id : null, At: mine ? h.WatchedAt : null)),
        _ => Title(MediaType.Show, h.TmdbId, h.Title, h.PosterPath),
    };

    public static string Cal(CalendarEntry c) => c.Type == MediaType.Episode && c.SeasonNumber is { } s && c.EpisodeNumber is { } e
        ? Episode(c.TmdbId, s, e, c.Title, c.EpisodeTitle, c.PosterPath)
        : Title(c.Type, c.TmdbId, c.Title, c.PosterPath);

    public static string User(UserCard u) =>
        Json(new MenuTarget(MenuKind.User, User: u.UserName, Name: u.Name, Avatar: u.Avatar, Following: u.Following));
}

/// <summary>Where the menu was asked for (viewport pixels) and whether by touch (then it opens as a bottom sheet).</summary>
public record MenuPoint(double X, double Y, double Width, double Height, bool Touch);

/// <summary>Opens the menu, and tells pages when something was changed through it so they can reload.</summary>
public class ContextMenu
{
    public event Action<MenuTarget, MenuPoint>? Opened;
    /// <summary>Something was rated, marked, added or removed from the menu.</summary>
    public event Func<Task>? Changed;

    public void Open(MenuTarget t, MenuPoint p) => Opened?.Invoke(t, p);

    public async Task RaiseChangedAsync()
    {
        if (Changed is null) return;
        foreach (var h in Changed.GetInvocationList().Cast<Func<Task>>())
        {
            try { await h(); }
            catch (Exception) { /* a page that failed to reload shouldn't break the others */ }
        }
    }
}
