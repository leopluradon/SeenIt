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

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.")));

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
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<TrackingService>();
builder.Services.AddScoped<ListService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<PersonService>();
builder.Services.AddScoped<JellyfinService>();
builder.Services.AddScoped<CheckInService>();
builder.Services.AddScoped<SocialService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<InviteService>();
builder.Services.AddHostedService<CheckInWorker>();
builder.Services.Configure<SmtpOptions>(config.GetSection("Smtp"));
builder.Services.AddSingleton<EmailService>();
builder.Services.AddHostedService<EmailWorker>();
// Password reset links are good for two hours.
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
builder.Services.AddScoped<ImportRunner>();
builder.Services.AddHostedService<ImportWorker>();
builder.Services.AddHostedService<ShowRefreshWorker>();

var app = builder.Build();

if (string.IsNullOrWhiteSpace(config["Tmdb:ReadAccessToken"]))
    app.Logger.LogWarning("Tmdb:ReadAccessToken is not set; catalogue requests will fail.");

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

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapAuth();
app.MapApi();
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
