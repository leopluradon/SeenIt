using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Endpoints;
using SeenIt.Server.Import;
using SeenIt.Server.Services;
using SeenIt.Server.Tmdb;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// The factory also registers AppDbContext itself as scoped; ActivityService uses the factory for
// a separate context so logging never saves (or trips over) the caller's pending changes.
builder.Services.AddDbContextFactory<AppDbContext>(o =>
    o.UseNpgsql(config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.")),
    ServiceLifetime.Scoped);

// Warnings and errors, kept in memory for the admin page.
var recentLogs = new RecentLogs();
builder.Logging.AddProvider(recentLogs);
builder.Services.AddSingleton(recentLogs);

// Bearer tokens are data-protected, so the key ring must survive restarts or everyone is signed out.
var dp = builder.Services.AddDataProtection().SetApplicationName("SeenIt");
if (config["SeenIt:KeyRing"] is { Length: > 0 } keyDir)
    dp.PersistKeysToFileSystem(new DirectoryInfo(keyDir));

builder.Services.AddAuthentication(IdentityConstants.BearerScheme)
    .AddBearerToken(IdentityConstants.BearerScheme, o =>
    {
        o.BearerTokenExpiration = TimeSpan.FromHours(1);
        o.RefreshTokenExpiration = TimeSpan.FromDays(90);
    });
builder.Services.AddAuthorization();
builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Lockout.MaxFailedAccessAttempts = 8;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("webhook", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.RouteValues["token"]?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Runs behind the homelab's reverse proxy.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddMemoryCache();
builder.Services.Configure<TmdbOptions>(config.GetSection("Tmdb"));
builder.Services.AddHttpClient<TmdbClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
// Posters for share images.
builder.Services.AddHttpClient("tmdb-images", c => c.Timeout = TimeSpan.FromSeconds(10));
// Letterboxd film pages, for imports whose titles TMDB's search can't match.
builder.Services.AddHttpClient("letterboxd", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SeenIt import)");
});
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<TrackingService>();
builder.Services.AddScoped<ListService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<PersonService>();
builder.Services.AddScoped<JellyfinService>();
builder.Services.AddScoped<CheckInService>();
builder.Services.AddScoped<PushService>();
builder.Services.AddHostedService<PushWorker>();
builder.Services.AddHttpClient("webpush", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<SocialService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddScoped<PickService>();
builder.Services.AddScoped<OnThisDayService>();
builder.Services.AddScoped<MoreStatsService>();
builder.Services.AddScoped<ProfileWidgetService>();
builder.Services.AddScoped<ActivityService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<EndpointTimings>();
builder.Services.AddSingleton<LiveHub>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<BackupStatus>();
builder.Services.AddHostedService<AlertWorker>();

// API responses are JSON and compress ~6×; static files are already pre-compressed.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    o.MimeTypes = ["application/json", "application/problem+json"];
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.AddHostedService<CheckInWorker>();
builder.Services.AddSingleton<SuggestIndex>();
builder.Services.AddHostedService<SuggestIndexWorker>();
builder.Services.AddScoped<SuggestService>();
builder.Services.Configure<SmtpOptions>(config.GetSection("Smtp"));
builder.Services.AddSingleton<EmailService>();
builder.Services.AddHostedService<EmailWorker>();
// Password reset links are good for two hours.
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
builder.Services.AddScoped<ImportRunner>();
builder.Services.AddScoped<SeenIt.Server.Import.ImportService>();
builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddHostedService<ShowRefreshWorker>();
builder.Services.AddScoped<LetterboxdSyncService>();
builder.Services.AddHostedService<LetterboxdSyncWorker>();
builder.Services.AddScoped<GoalService>();
builder.Services.AddScoped<UserDataCache>();
builder.Services.AddScoped<WatchOrderService>();
builder.Services.AddScoped<ShareCardService>();
builder.Services.Configure<MdbListOptions>(config.GetSection("MdbList"));
builder.Services.AddHttpClient("mdblist", c => c.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient("imdb", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddScoped<ScoreService>();
builder.Services.Configure<TraktOptions>(config.GetSection("Trakt"));
builder.Services.AddHttpClient("trakt", c =>
{
    c.BaseAddress = new Uri("https://api.trakt.tv/");
    c.Timeout = TimeSpan.FromSeconds(8);
    c.DefaultRequestHeaders.Add("trakt-api-version", "2");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SeenIt/1.0");
});
builder.Services.AddSingleton<TraktScores>();
builder.Services.AddScoped<AvatarService>();
builder.Services.AddScoped<ReleaseAlertService>();
builder.Services.AddScoped<RecommendationService>();
builder.Services.AddScoped<FriendRecommendationService>();
builder.Services.AddScoped<SmartListService>();
builder.Services.AddScoped<FeedService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddHttpClient("tvmaze", c =>
{
    c.BaseAddress = new Uri("https://api.tvmaze.com/");
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("SeenIt (self-hosted tracker)");
});
builder.Services.AddScoped<TvmazeService>();
builder.Services.AddHostedService<TvmazeWorker>();
builder.Services.AddHostedService<MonthlyBackupWorker>();
builder.Services.AddHostedService<ReleaseAlertWorker>();
builder.Services.AddHostedService<PersonFollowWorker>();
builder.Services.AddHostedService<ImdbRatingsWorker>();

var app = builder.Build();

if (string.IsNullOrWhiteSpace(config["Tmdb:ReadAccessToken"]))
    app.Logger.LogWarning("Tmdb:ReadAccessToken is not set; catalog requests will fail.");

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try { await db.Database.MigrateAsync(); break; }
        catch (Exception ex) when (attempt < 10)
        {
            app.Logger.LogWarning("Database not ready ({Message}); retrying", ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    // Record each new build the first time it starts, for the admin page's deployment history.
    // (A weekly rebuild of the same commit counts too: it's a new build with new base images.)
    if (!AppInfo.IsDev && !await db.Deployments.AnyAsync(d => d.Commit == AppInfo.Commit && d.BuiltAt == AppInfo.BuiltAt))
    {
        db.Deployments.Add(new Deployment { Version = AppInfo.Version, Commit = AppInfo.Commit, Subject = AppInfo.Subject, BuiltAt = AppInfo.BuiltAt });
        await db.SaveChangesAsync();
    }
    app.Logger.LogInformation("SeenIt {Version} ({Commit}) starting", AppInfo.Version, AppInfo.Commit);
}

app.UseForwardedHeaders();

// Turn domain validation errors into 400s the clients can show.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (TrackingException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new ProblemInfo(ex.Message));
    }
    catch (HttpRequestException ex) when (!ctx.Response.HasStarted && ctx.Request.Path.StartsWithSegments("/api"))
    {
        app.Logger.LogWarning(ex, "TMDB request failed");
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new ProblemInfo("TMDB didn't respond. Try again shortly."));
    }
});

if (app.Environment.IsDevelopment())
    app.UseWebAssemblyDebugging();

// Not on sign-in responses (tokens): compressing secrets next to attacker-chosen input is how
// BREACH-style attacks work.
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api/auth"), a => a.UseResponseCompression());

app.UseBlazorFrameworkFiles();
// Most files keep the same URL across deploys (seenit.css, seenit.js, …). Without this, browsers
// guess how long to cache them and can run old styles/scripts against a new app after a deploy;
// "no-cache" makes them check first, which is a cheap 304 when nothing changed.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
// The Android app, published by scripts/publish-apk.sh into the downloads folder.
if (config["SeenIt:DownloadsDir"] is { Length: > 0 } downloads && Directory.Exists(downloads))
{
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    types.Mappings[".apk"] = "application/vnd.android.package-archive";
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(downloads),
        RequestPath = "/download",
        ContentTypeProvider = types,
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
    });
}

app.UseRateLimiter();
app.Use(EndpointTimings.Middleware);
app.UseAuthentication();
app.UseAuthorization();
app.Use(LiveHub.Middleware);

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapAuth();
app.MapApi();
app.MapAdmin();
app.MapAppRelease();
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
