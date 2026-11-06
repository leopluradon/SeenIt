using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeenIt.Contracts;

namespace SeenIt.UI.Services;

public class ApiException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Typed client for the SeenIt API. Used unchanged by the website and the mobile app.</summary>
public class ApiClient(HttpClient http, TokenManager tokens, IOfflineStore offline, ConnectivityState connectivity)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ---------- Auth ----------

    public Task<TokenResponse> RegisterAsync(RegisterRequest r) => SendAsync<TokenResponse>(HttpMethod.Post, "api/auth/register", r, auth: false)!;
    public Task<TokenResponse> LoginAsync(LoginRequest r) => SendAsync<TokenResponse>(HttpMethod.Post, "api/auth/login", r, auth: false)!;
    public Task<MeDto> MeAsync() => GetAsync<MeDto>("api/auth/me");
    public Task UpdateProfileAsync(UpdateProfileRequest r) => SendAsync(HttpMethod.Put, "api/auth/me", r);
    public Task SetPreferencesAsync(PreferencesRequest r) => SendAsync(HttpMethod.Put, "api/auth/preferences", r);
    public Task ChangePasswordAsync(ChangePasswordRequest r) => SendAsync(HttpMethod.Post, "api/auth/password", r);
    private Task<ServerFeatures?>? _features;

    /// <summary>What the server supports. Cached for the session; a failed lookup is retried next time.</summary>
    public Task<ServerFeatures?> FeaturesAsync()
    {
        var t = _features ??= SendAsync<ServerFeatures>(HttpMethod.Get, "api/auth/features", null, auth: false);
        if (t.IsFaulted || t.IsCanceled) _features = null;
        return t;
    }

    public Task<InviteCheck?> CheckInviteAsync(string code) => SendAsync<InviteCheck>(HttpMethod.Get, $"api/auth/invite/{Uri.EscapeDataString(code)}", null, auth: false);
    public Task<List<InviteDto>?> InvitesAsync() => SendAsync<List<InviteDto>>(HttpMethod.Get, "api/auth/invites");
    public Task<InviteDto?> CreateInviteAsync() => SendAsync<InviteDto>(HttpMethod.Post, "api/auth/invites");
    public Task DeleteInviteAsync(string code) => SendAsync(HttpMethod.Delete, $"api/auth/invites/{Uri.EscapeDataString(code)}");
    public Task ForgotPasswordAsync(ForgotPasswordRequest r) => SendAsync<object>(HttpMethod.Post, "api/auth/forgot", r, auth: false);
    public Task ResetPasswordAsync(ResetPasswordRequest r) => SendAsync<object>(HttpMethod.Post, "api/auth/reset", r, auth: false);
    public Task SendTestEmailAsync() => SendAsync(HttpMethod.Post, "api/auth/test-email", null);
    public Task DeleteAccountAsync(DeleteAccountRequest r) => SendAsync(HttpMethod.Post, "api/auth/delete", r);
    public Task<ClearDataResult?> ClearDataAsync(ClearDataRequest r) => SendAsync<ClearDataResult>(HttpMethod.Post, "api/auth/clear", r);

    public Task<List<TimeZoneOption>> TimeZonesAsync() => GetAsync<List<TimeZoneOption>>("api/auth/timezones");
    public Task<AppRelease?> AndroidReleaseAsync() => SendAsync<AppRelease>(HttpMethod.Get, "api/app/android", null, auth: false);

    // ---------- Admin ----------

    public Task<AdminOverview> AdminOverviewAsync() => GetAsync<AdminOverview>("api/admin/overview");
    public Task<List<AdminUser>> AdminUsersAsync() => GetAsync<List<AdminUser>>("api/admin/users");
    public Task<AdminActivityPage> AdminActivityAsync(string? user, string? group, int page) =>
        GetAsync<AdminActivityPage>($"api/admin/activity?page={page}"
            + (string.IsNullOrEmpty(user) ? "" : $"&user={Uri.EscapeDataString(user)}")
            + (string.IsNullOrEmpty(group) ? "" : $"&group={group}"));
    public Task<List<AdminImport>> AdminImportsAsync() => GetAsync<List<AdminImport>>("api/admin/imports");
    public Task<List<AdminJellyfinEvent>> AdminJellyfinAsync() => GetAsync<List<AdminJellyfinEvent>>("api/admin/jellyfin");
    public Task<List<AdminInvite>> AdminInvitesAsync() => GetAsync<List<AdminInvite>>("api/admin/invites");
    public Task<List<AdminEndpointTiming>> AdminPerformanceAsync() => GetAsync<List<AdminEndpointTiming>>("api/admin/performance");
    public Task<List<AdminLogEntry>> AdminLogsAsync() => GetAsync<List<AdminLogEntry>>("api/admin/logs");
    public Task AdminUnlockAsync(Guid id) => SendAsync(HttpMethod.Post, $"api/admin/users/{id}/unlock", null);
    public Task<AdminResetLink?> AdminResetLinkAsync(Guid id) => SendAsync<AdminResetLink>(HttpMethod.Post, $"api/admin/users/{id}/reset-link");

    // ---------- Catalog ----------

    public Task<List<ExternalScore>> ScoresAsync(MediaType type, int id) =>
        GetAsync<List<ExternalScore>>($"api/catalog/{(type == MediaType.Movie ? "movie" : "show")}/{id}/scores");

    public Task<List<ExternalScore>> EpisodeScoresAsync(int showId, int season, int episode) =>
        GetAsync<List<ExternalScore>>($"api/catalog/show/{showId}/season/{season}/episode/{episode}/scores");

    /// <summary>An IMDb/TMDB id or link; null when the query isn't one.</summary>
    public Task<LookupResult?> LookupAsync(string q) => SendAsync<LookupResult>(HttpMethod.Get, $"api/catalog/lookup?q={Uri.EscapeDataString(q)}");

    public Task<PagedResult<PersonHit>> SearchPeopleAsync(string q, int page = 1) =>
        GetAsync<PagedResult<PersonHit>>($"api/catalog/search/people?q={Uri.EscapeDataString(q)}&page={page}");

    public Task<PagedResult<MediaSummary>> SearchAsync(string q, string? type = null, int page = 1) =>
        GetAsync<PagedResult<MediaSummary>>($"api/catalog/search?q={Uri.EscapeDataString(q)}&page={page}" + (type is null ? "" : $"&type={type}"));
    public Task<PagedResult<MediaSummary>> BrowseAsync(MediaType type, string list, int page = 1) =>
        GetAsync<PagedResult<MediaSummary>>($"api/catalog/browse/{type}/{list}?page={page}&region={DeviceRegion}");
    public Task<MovieDetail> MovieAsync(int id) => GetAsync<MovieDetail>($"api/catalog/movie/{id}");
    public Task<ShowDetail> ShowAsync(int id) => GetAsync<ShowDetail>($"api/catalog/show/{id}");
    public Task<SeasonDetail> SeasonAsync(int id, int season) => GetAsync<SeasonDetail>($"api/catalog/show/{id}/season/{season}");
    public Task<PersonDetail> PersonAsync(int id) => GetAsync<PersonDetail>($"api/catalog/person/{id}");
    public Task<EpisodeDetail> EpisodeAsync(int id, int season, int episode) => GetAsync<EpisodeDetail>($"api/catalog/show/{id}/season/{season}/episode/{episode}");

    // ---------- Tracking ----------

    // "Now" is pinned to the moment of the tap, so a play queued offline keeps its real time.
    public Task WatchAsync(WatchRequest r) => SendQueueableAsync(HttpMethod.Post, "api/watch",
        r.When is null or WatchTime.Now && r.WatchedAt is null ? r with { When = WatchTime.Custom, WatchedAt = DateTimeOffset.UtcNow } : r);
    public Task BulkWatchAsync(BulkWatchRequest r) => SendQueueableAsync(HttpMethod.Post, "api/watch/bulk",
        r.When is null or WatchTime.Now && r.WatchedAt is null ? r with { When = WatchTime.Custom, WatchedAt = DateTimeOffset.UtcNow } : r);
    /// <summary>Returns what was removed, for <see cref="RestorePlaysAsync"/> (undo).</summary>
    public async Task<RemovedPlays> UnwatchAsync(MediaType type, int tmdbId, int? season = null, int? episode = null) =>
        await SendAsync<RemovedPlays>(HttpMethod.Delete, $"api/watch?type={type}&tmdbId={tmdbId}" + (season is null ? "" : $"&season={season}") + (episode is null ? "" : $"&episode={episode}"))
        ?? new RemovedPlays(0, []);
    public async Task<RemovedPlays> DeleteHistoryAsync(IReadOnlyList<Guid> ids) =>
        await SendAsync<RemovedPlays>(HttpMethod.Post, "api/history/delete", ids) ?? new RemovedPlays(0, []);
    public Task<PlayDto?> EditPlayAsync(Guid id, EditPlayRequest r) => SendAsync<PlayDto>(HttpMethod.Put, $"api/history/{id}", r);
    /// <summary>Puts a play back on the date it had (undo for an edit).</summary>
    public Task<PlayDto?> RevertPlayAsync(Guid id, DateTimeOffset before) =>
        EditPlayAsync(id, WatchDates.IsUnknown(before) ? new EditPlayRequest(WatchTime.Unknown) : new EditPlayRequest(WatchTime.Custom, before));
    public Task RestorePlaysAsync(IReadOnlyList<RestorePlay> plays) => SendAsync(HttpMethod.Post, "api/history/restore", plays);
    public Task<RematchResult?> RematchAsync(RematchRequest r) => SendAsync<RematchResult>(HttpMethod.Post, "api/rematch", r);
    public Task<List<DuplicateGroup>> DuplicatesAsync() => GetAsync<List<DuplicateGroup>>("api/duplicates");
    /// <summary>Your history, or someone else's (read-only) when userName is given.</summary>
    public Task<HistoryPage> HistoryAsync(HistoryQuery f, string? userName = null)
    {
        var qs = new List<string> { $"page={f.Page}", $"pageSize={f.PageSize}", $"tz={Tz}" };
        if (f.Type is { } t) qs.Add($"type={t}");
        if (!string.IsNullOrWhiteSpace(f.Q)) qs.Add($"q={Uri.EscapeDataString(f.Q)}");
        if (f.Source is { } s) qs.Add($"source={s}");
        if (!string.IsNullOrWhiteSpace(f.Genre)) qs.Add($"genre={Uri.EscapeDataString(f.Genre)}");
        if (f.From is { } from) qs.Add($"from={Uri.EscapeDataString(from.ToString("O"))}");
        if (f.To is { } to) qs.Add($"to={Uri.EscapeDataString(to.ToString("O"))}");
        if (f.Oldest) qs.Add("sort=oldest");
        return GetAsync<HistoryPage>((userName is null ? "api/history?" : $"api/users/{Uri.EscapeDataString(userName)}/history?") + string.Join('&', qs));
    }
    public Task<List<string>> HistoryGenresAsync(string? userName = null) =>
        GetAsync<List<string>>(userName is null ? "api/history/genres" : $"api/users/{Uri.EscapeDataString(userName)}/history/genres");
    public async Task<RemovedPlays> DeleteHistoryAsync(Guid id) =>
        await SendAsync<RemovedPlays>(HttpMethod.Delete, $"api/history/{id}") ?? new RemovedPlays(0, []);

    public Task<List<WatchlistEntry>> WatchlistAsync() => GetAsync<List<WatchlistEntry>>("api/watchlist");
    public Task AddToWatchlistAsync(MediaType type, int id) => SendQueueableAsync(HttpMethod.Post, "api/watchlist", new WatchlistRequest(type, id));
    public Task RemoveFromWatchlistAsync(MediaType type, int id) => SendAsync(HttpMethod.Delete, $"api/watchlist?type={type}&tmdbId={id}");

    public Task<List<FavoriteEntry>> FavoritesAsync() => GetAsync<List<FavoriteEntry>>("api/favorites");
    public Task AddFavoriteAsync(MediaType type, int id) => SendQueueableAsync(HttpMethod.Post, "api/favorites", new FavoriteRequest(type, id));
    public Task RemoveFavoriteAsync(MediaType type, int id) => SendAsync(HttpMethod.Delete, $"api/favorites?type={type}&tmdbId={id}");
    public Task ReorderFavoritesAsync(IReadOnlyList<FavoriteRequest> order) => SendAsync(HttpMethod.Put, "api/favorites/order", order);

    public Task<List<RatingEntry>> RatingsAsync(MediaType? type = null) => GetAsync<List<RatingEntry>>("api/ratings" + (type is null ? "" : $"?type={type}"));
    public Task RateAsync(RatingRequest r) => SendQueueableAsync(HttpMethod.Post, "api/ratings", r);
    public Task UnrateAsync(MediaType type, int id, int? season = null, int? episode = null) =>
        SendAsync(HttpMethod.Delete, $"api/ratings?type={type}&tmdbId={id}" + (season is null ? "" : $"&season={season}") + (episode is null ? "" : $"&episode={episode}"));

    public Task<List<ShowProgressDto>> ProgressAsync() => GetAsync<List<ShowProgressDto>>("api/progress");
    public Task<List<CalendarEntry>> CalendarAsync(DateOnly from, int days) => GetAsync<List<CalendarEntry>>($"api/calendar?from={from:yyyy-MM-dd}&days={days}");
    public Task<DashboardDto> DashboardAsync() => GetAsync<DashboardDto>("api/dashboard");
    public Task<int> StartRewatchAsync(int showId) => SendAsync<int>(HttpMethod.Post, $"api/shows/{showId}/rewatch")!;
    public Task StopRewatchAsync(int showId) => SendAsync(HttpMethod.Delete, $"api/shows/{showId}/rewatch");
    public Task<List<OnThisDayItem>> OnThisDayAsync() => GetAsync<List<OnThisDayItem>>($"api/onthisday?tz={Tz}");

    /// <summary>A random pick, or null when nothing matches the filters.</summary>
    public Task<PickDto?> PickAsync(PickKind kind, PickSource source, int? maxMinutes, bool streaming, IEnumerable<string> exclude) =>
        SendAsync<PickDto>(HttpMethod.Get,
            $"api/pick?kind={kind}&source={source}&streaming={(streaming ? "true" : "false")}&region={DeviceRegion}"
            + (maxMinutes is { } m ? $"&maxMinutes={m}" : "")
            + $"&exclude={Uri.EscapeDataString(string.Join(',', exclude))}");

    public Task SetShowStatusAsync(int showId, ShowTrackStatus status) => SendAsync(HttpMethod.Put, $"api/shows/{showId}/status", new ShowStatusRequest(status));

    public Task<NowWatchingDto?> CheckInAsync() => SendAsync<NowWatchingDto>(HttpMethod.Get, "api/checkin");
    public Task<NowWatchingDto?> StartCheckInAsync(CheckInRequest r) => SendAsync<NowWatchingDto>(HttpMethod.Post, "api/checkin", r);
    public Task CancelCheckInAsync() => SendAsync(HttpMethod.Delete, "api/checkin");

    public Task<WatchProvidersDto> ProvidersAsync(MediaType type, int id) => GetAsync<WatchProvidersDto>($"api/catalog/providers/{type}/{id}?region={DeviceRegion}");
    public Task<List<RegionDto>> RegionsAsync() => GetAsync<List<RegionDto>>("api/catalog/regions");

    /// <summary>The device's country, used for "where to watch" until the user picks one.</summary>
    public static string DeviceRegion
    {
        get
        {
            try
            {
                var name = System.Globalization.CultureInfo.CurrentCulture.Name;
                if (name.Length > 0 && new System.Globalization.RegionInfo(name).TwoLetterISORegionName is { Length: 2 } r) return r;
            }
            catch (ArgumentException) { }
            return "US";
        }
    }

    public async Task<byte[]> ExportAsync()
    {
        using var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/export"), auth: true);
        await EnsureOkAsync(resp);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    // ---------- Social ----------

    public Task<ProfileDto> ProfileAsync(string userName) => GetAsync<ProfileDto>($"api/users/{Uri.EscapeDataString(userName)}");
    public Task<FeedDto> FeedAsync(int take = 40) => GetAsync<FeedDto>($"api/social/feed?take={take}");
    public Task<List<NotInterestedEntry>> NotInterestedAsync() => GetAsync<List<NotInterestedEntry>>("api/not-interested");
    public Task MarkNotInterestedAsync(MediaType type, int id) => SendAsync(HttpMethod.Post, "api/not-interested", new FavoriteRequest(type, id));
    public Task UndoNotInterestedAsync(MediaType type, int id) => SendAsync(HttpMethod.Delete, $"api/not-interested?type={type}&tmdbId={id}");
    public Task<ForYouDto> ForYouAsync() => GetAsync<ForYouDto>("api/recommendations");

    public Task<ListFeedsDto> ListFeedsAsync(Guid id) => GetAsync<ListFeedsDto>($"api/lists/{id}/feeds");
    public Task<List<LibraryEntry>> LibraryAsync() => GetAsync<List<LibraryEntry>>("api/library");
    public Task AddToLibraryAsync(MediaType type, int id, OwnedFormat format) => SendAsync(HttpMethod.Post, "api/library", new LibraryRequest(type, id, format));
    public Task RemoveFromLibraryAsync(MediaType type, int id) => SendAsync(HttpMethod.Delete, $"api/library?type={type}&tmdbId={id}");
    public Task<List<string>> GenresAsync() => GetAsync<List<string>>("api/catalog/genres");
    public Task<List<ListSummary>> SavedListsAsync() => GetAsync<List<ListSummary>>("api/lists/saved");
    public Task<List<ListSummary>> BrowseListsAsync(string? q, bool recent) =>
        GetAsync<List<ListSummary>>($"api/lists/browse?sort={(recent ? "recent" : "popular")}" + (string.IsNullOrWhiteSpace(q) ? "" : $"&q={Uri.EscapeDataString(q.Trim())}"));
    public Task SaveListAsync(Guid id, bool save) => SendAsync(save ? HttpMethod.Post : HttpMethod.Delete, $"api/lists/{id}/save");

    public Task<NotificationsDto> NotificationsAsync() => GetAsync<NotificationsDto>("api/notifications");
    public async Task<int> UnreadNotificationsAsync() => (await GetAsync<UnreadResult>("api/notifications/unread")).Unread;
    public Task MarkNotificationsReadAsync() => SendAsync(HttpMethod.Post, "api/notifications/read");
    public Task<AlertSettingsDto> AlertSettingsAsync() => GetAsync<AlertSettingsDto>("api/alerts");
    public async Task<AlertSettingsDto> SetAlertSettingsAsync(AlertSettingsRequest r) => (await SendAsync<AlertSettingsDto>(HttpMethod.Put, "api/alerts", r))!;
    private sealed record UnreadResult(int Unread);

    public Task<List<FriendSeen>> SeenByAsync(MediaType type, int id, int? season = null, int? episode = null) =>
        GetAsync<List<FriendSeen>>($"api/social/seen/{type}/{id}" + (season is { } s ? $"?season={s}" + (episode is { } e ? $"&episode={e}" : "") : ""));

    public Task<List<UserCard>> SearchUsersAsync(string q) => GetAsync<List<UserCard>>($"api/social/search?q={Uri.EscapeDataString(q)}");
    public Task<List<UserCard>> FollowingAsync() => GetAsync<List<UserCard>>("api/social/following");
    public Task<List<UserCard>> FollowersAsync() => GetAsync<List<UserCard>>("api/social/followers");
    /// <summary>Someone else's lists: kind is "following" or "followers".</summary>
    public Task<List<UserCard>> ConnectionsOfAsync(string userName, string kind) =>
        GetAsync<List<UserCard>>($"api/users/{Uri.EscapeDataString(userName)}/{kind}");
    public Task FollowAsync(string userName, bool follow) =>
        SendAsync(follow ? HttpMethod.Post : HttpMethod.Delete, $"api/social/follow/{Uri.EscapeDataString(userName)}");
    public Task<CompareDto> CompareAsync(string a, string b) =>
        GetAsync<CompareDto>($"api/social/compare/{Uri.EscapeDataString(a)}/{Uri.EscapeDataString(b)}");
    public Task<MoreStatsDto> MoreStatsAsync() => GetAsync<MoreStatsDto>($"api/stats/more?tz={Tz}");
    public Task<StatsDto> StatsAsync() => GetAsync<StatsDto>($"api/stats?tz={Tz}");
    public Task<List<ReviewYear>> ReviewPeriodsAsync() => GetAsync<List<ReviewYear>>($"api/review/periods?tz={Tz}");
    public Task<ReviewDto> ReviewAsync(int year, int? month) =>
        GetAsync<ReviewDto>($"api/review/{year}" + (month is null ? "" : $"/{month}") + $"?tz={Tz}");

    /// <summary>The device's IANA time zone, so reviews use the viewer's own calendar days.</summary>
    private static string Tz => Uri.EscapeDataString(TimeZoneInfo.Local.Id);

    // ---------- Lists ----------

    public Task<List<ListSummary>> ListsAsync() => GetAsync<List<ListSummary>>("api/lists");
    public Task<ListDetail> ListAsync(Guid id) => GetAsync<ListDetail>($"api/lists/{id}");
    public async Task<Guid> CreateListAsync(CreateListRequest r) => (await SendAsync<IdResult>(HttpMethod.Post, "api/lists", r))!.Id;
    public Task UpdateListAsync(Guid id, CreateListRequest r) => SendAsync(HttpMethod.Put, $"api/lists/{id}", r);
    public Task DeleteListAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/lists/{id}");
    public Task AddToListAsync(Guid id, MediaType type, int tmdbId) => SendAsync(HttpMethod.Post, $"api/lists/{id}/items", new ListItemRequest(type, tmdbId));
    public Task RemoveFromListAsync(Guid id, MediaType type, int tmdbId) => SendAsync(HttpMethod.Delete, $"api/lists/{id}/items?type={type}&tmdbId={tmdbId}");

    // ---------- Integrations ----------

    public Task<List<ListSearchHit>> SearchListsAsync(string q) => GetAsync<List<ListSearchHit>>($"api/lists/search?q={Uri.EscapeDataString(q)}");

    // ---------- Goals ----------

    public Task<List<GoalDto>> GoalsAsync(int year) => GetAsync<List<GoalDto>>($"api/goals?year={year}&tz={Tz}");
    public Task SetGoalAsync(GoalRequest r) => SendAsync(HttpMethod.Put, "api/goals", r);
    public Task DeleteGoalAsync(Guid id) => SendAsync(HttpMethod.Delete, $"api/goals/{id}");

    // ---------- Letterboxd feed sync ----------

    public Task<LetterboxdSyncDto> LetterboxdSyncAsync() => GetAsync<LetterboxdSyncDto>("api/letterboxd-sync");
    public async Task<LetterboxdSyncDto> SetLetterboxdSyncAsync(string username) => (await SendAsync<LetterboxdSyncDto>(HttpMethod.Put, "api/letterboxd-sync", new LetterboxdSyncRequest(username)))!;
    public async Task<LetterboxdSyncDto> LetterboxdSyncNowAsync() => (await SendAsync<LetterboxdSyncDto>(HttpMethod.Post, "api/letterboxd-sync/now"))!;
    public Task StopLetterboxdSyncAsync() => SendAsync(HttpMethod.Delete, "api/letterboxd-sync");

    // ---------- Share images ----------

    public Task<byte[]> ReviewCardAsync(int year, int? month) =>
        BytesAsync($"api/share/review/{year}?{(month is { } m ? $"month={m}&" : "")}tz={Tz}");
    public Task<byte[]> CompareCardAsync(string a, string b) => BytesAsync($"api/share/compare/{Uri.EscapeDataString(a)}/{Uri.EscapeDataString(b)}");
    public Task<byte[]> KodiAddonAsync() => BytesAsync("api/jellyfin/kodi-addon");

    private async Task<byte[]> BytesAsync(string path)
    {
        using var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, path), auth: true);
        await EnsureOkAsync(resp);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public Task<JellyfinSettingsDto> JellyfinAsync() => GetAsync<JellyfinSettingsDto>("api/jellyfin");
    public Task RegenerateJellyfinTokenAsync() => SendAsync(HttpMethod.Post, "api/jellyfin/regenerate");
    public Task SetPlexUserAsync(string? name) => SendAsync(HttpMethod.Put, "api/jellyfin/plex-user", new PlexUserRequest(name));
    public Task SetJellyfinThresholdAsync(int pct) => SendAsync(HttpMethod.Put, "api/jellyfin/threshold", new JellyfinThresholdRequest(pct));

    public Task<UndoImportResult?> UndoImportAsync(Guid id) => SendAsync<UndoImportResult>(HttpMethod.Post, $"api/imports/{id}/undo");
    public Task<ResolveImportResult?> ResolveImportAsync(Guid id, ResolveImportRequest r) => SendAsync<ResolveImportResult>(HttpMethod.Post, $"api/imports/{id}/resolve", r);
    public Task<List<PlayDate>?> EditPlaysAsync(BulkEditPlaysRequest r) => SendAsync<List<PlayDate>>(HttpMethod.Post, "api/history/edit", r);
    public Task SetPlayDatesAsync(IReadOnlyList<PlayDate> dates) => SendAsync(HttpMethod.Post, "api/history/dates", dates);
    public Task ReorderListAsync(Guid id, IReadOnlyList<Guid> itemIds) => SendAsync(HttpMethod.Put, $"api/lists/{id}/order", new ListOrderRequest(itemIds));
    public Task SetListNoteAsync(Guid id, Guid itemId, string? note) => SendAsync(HttpMethod.Put, $"api/lists/{id}/items/{itemId}/note", new ListNoteRequest(note));
    public Task<WatchOrdersDto> WatchOrdersAsync(int showId) => GetAsync<WatchOrdersDto>($"api/shows/{showId}/orders");
    /// <summary>The order you follow for the show, or null for TMDB's seasons.</summary>
    public Task<WatchOrderDto?> WatchOrderAsync(int showId) => SendAsync<WatchOrderDto>(HttpMethod.Get, $"api/shows/{showId}/order");
    public Task SetWatchOrderAsync(int showId, string? orderId) => SendAsync(HttpMethod.Put, $"api/shows/{showId}/order", new SetWatchOrderRequest(orderId));
    public async Task<int> WatchThroughAsync(int showId, WatchThroughRequest r) =>
        (await SendAsync<AddedResult>(HttpMethod.Post, $"api/shows/{showId}/watch-through", r))?.Added ?? 0;
    private sealed record AddedResult(int Added);
    public Task<List<ImportJobDto>> ImportsAsync() => GetAsync<List<ImportJobDto>>("api/imports");
    public async Task UploadImportAsync(ImportKind kind, ImportParts parts, string fileName, Stream content)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Post, $"api/imports/{kind}?parts={(int)parts}") { Content = form }, auth: true);
        await EnsureOkAsync(resp);
    }

    public Uri BaseAddress => http.BaseAddress!;

    public async Task UploadAvatarAsync(string fileName, string contentType, Stream content)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        form.Add(file, "file", fileName);
        using var resp = await SendRawAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/auth/avatar") { Content = form }, auth: true);
        await EnsureOkAsync(resp);
    }

    public Task RemoveAvatarAsync() => SendAsync(HttpMethod.Delete, "api/auth/avatar");

    // ---------- Plumbing ----------

    private record IdResult(Guid Id);

    /// <summary>GETs go through the offline cache on the app: fresh copies are saved, and used when the server can't be reached.</summary>
    private async Task<T> GetAsync<T>(string url)
    {
        string json;
        try
        {
            using var resp = await SendRawAsync(() => Build(HttpMethod.Get, url, null), auth: true);
            await EnsureOkAsync(resp);
            json = await resp.Content.ReadAsStringAsync();
        }
        catch (ApiException ex) when (ex.Status == 0 && offline.Enabled)
        {
            if (await offline.GetAsync(url) is not { } cached) throw;
            connectivity.Set(offline: true);
            return JsonSerializer.Deserialize<T>(cached, Json)!;
        }
        if (offline.Enabled) await offline.SetAsync(url, json);
        return JsonSerializer.Deserialize<T>(json, Json)!;
    }

    /// <summary>Changes that can wait: when offline on the app they're queued and sent once the server is back.</summary>
    private async Task SendQueueableAsync(HttpMethod method, string url, object body)
    {
        try { await SendAsync(method, url, body); }
        catch (ApiException ex) when (ex.Status == 0 && offline.Enabled)
        {
            var queue = await offline.GetQueueAsync();
            queue.Add(new QueuedRequest(method.Method, url, JsonSerializer.Serialize(body, body.GetType(), Json), DateTimeOffset.UtcNow));
            await offline.SetQueueAsync(queue);
            connectivity.Set(offline: true, queue.Count);
        }
    }

    private readonly SemaphoreSlim _flushGate = new(1, 1);

    /// <summary>Sends changes made offline, oldest first. Returns true if the server was reachable.</summary>
    public async Task<bool> SyncAsync()
    {
        if (!offline.Enabled) return true;
        if (!await _flushGate.WaitAsync(0)) return !connectivity.Offline;
        try
        {
            var queue = await offline.GetQueueAsync();
            while (queue.Count > 0)
            {
                var q = queue[0];
                using var resp = await SendRawAsync(() => new HttpRequestMessage(new HttpMethod(q.Method), q.Url)
                {
                    Content = q.Body is null ? null : new StringContent(q.Body, System.Text.Encoding.UTF8, "application/json"),
                }, auth: true);
                // Server errors are retried later; a rejected change (e.g. the title is gone) is dropped.
                if ((int)resp.StatusCode >= 500) break;
                queue.RemoveAt(0);
                await offline.SetQueueAsync(queue);
            }
            connectivity.Set(offline: false, queue.Count);
            return true;
        }
        catch (ApiException ex) when (ex.Status == 0)
        {
            connectivity.Set(offline: true);
            return false;
        }
        finally { _flushGate.Release(); }
    }

    public async Task<bool> PingAsync()
    {
        try
        {
            using var resp = await http.GetAsync("healthz");
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    private async Task SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var resp = await SendRawAsync(() => Build(method, url, body), auth: true);
        await EnsureOkAsync(resp);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? body = null, bool auth = true)
    {
        using var resp = await SendRawAsync(() => Build(method, url, body), auth);
        await EnsureOkAsync(resp);
        if (resp.StatusCode == HttpStatusCode.NoContent) return default;
        return await resp.Content.ReadFromJsonAsync<T>(Json);
    }

    private static HttpRequestMessage Build(HttpMethod method, string url, object? body) =>
        new(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: Json) };

    /// <summary>Attaches the access token and, on a 401, refreshes it once and retries.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> build, bool auth)
    {
        var req = build();
        if (auth && await tokens.GetAccessTokenAsync() is { } token)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            connectivity.Set(offline: true);
            throw new ApiException("Can't reach the SeenIt server. Check your connection.", 0);
        }
        if (connectivity.Offline)
        {
            connectivity.Set(offline: false);
            if (connectivity.Pending > 0) _ = SyncAsync();
        }

        if (auth && resp.StatusCode == HttpStatusCode.Unauthorized && req.Content is not MultipartFormDataContent
            && await tokens.RefreshAsync(http) is { } fresh)
        {
            resp.Dispose();
            var retry = build();
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
            resp = await http.SendAsync(retry);
        }
        if (auth && resp.StatusCode == HttpStatusCode.Unauthorized)
            await tokens.SignedOutAsync();
        return resp;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        string message = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Please sign in again.",
            HttpStatusCode.NotFound => "Not found.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
            _ => $"Something went wrong ({(int)resp.StatusCode}).",
        };
        try
        {
            var problem = await resp.Content.ReadFromJsonAsync<ProblemInfo>(Json);
            if (problem?.Errors is { Count: > 0 } errs) message = string.Join(" ", errs.Values.SelectMany(v => v));
            else if (!string.IsNullOrWhiteSpace(problem?.Title)) message = problem.Title;
        }
        catch { /* not JSON */ }
        throw new ApiException(message, resp.StatusCode);
    }
}
