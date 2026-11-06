using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SeenIt.Contracts;
using SeenIt.Server.Data;
using SeenIt.Server.Services;

namespace SeenIt.Server.Endpoints;

public static partial class AuthEndpoints
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());

    public static Guid? OptionalUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$")]
    private static partial Regex UserNamePattern();

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/register", async Task<Results<SignInHttpResult, BadRequest<ProblemInfo>>> (
            RegisterRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn, InviteService invites, ActivityService activity) =>
        {
            var userName = req.UserName?.Trim() ?? "";
            if (!UserNamePattern().IsMatch(userName))
                return Bad("Usernames are 3–32 letters, numbers, dots, dashes or underscores.");
            if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
                return Bad("Enter a valid email address.");

            // An invite is required unless sign-ups are open; with open sign-ups it's still honored (auto-follow).
            var inviter = await invites.ClaimAsync(req.InviteCode);
            if (inviter is null && !invites.OpenRegistration)
                return Bad(string.IsNullOrWhiteSpace(req.InviteCode)
                    ? "SeenIt is invite-only. Ask a member for an invite link."
                    : "This invite has expired or has already been used. Ask for a new one.");

            var user = new AppUser { UserName = userName, Email = req.Email.Trim() };
            var result = await users.CreateAsync(user, req.Password ?? "");
            if (!result.Succeeded)
            {
                if (inviter is not null) await invites.ReleaseAsync(req.InviteCode!);
                return TypedResults.BadRequest(new ProblemInfo("Couldn't create the account.",
                    result.Errors.GroupBy(e => e.Code).ToDictionary(e => e.Key, e => e.Select(x => x.Description).ToArray())));
            }
            if (inviter is { } by) await invites.CompleteAsync(req.InviteCode!, by, user.Id);
            var invitedBy = inviter is { } inv ? (await users.FindByIdAsync(inv.ToString()))?.UserName : null;
            await activity.LogAsync(user.Id, ActivityKind.SignUp, detail: invitedBy is null ? "open sign-up" : $"invited by {invitedBy}", userName: user.UserName);

            var principal = await signIn.CreateUserPrincipalAsync(user);
            return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
        }).RequireRateLimiting("auth");

        g.MapPost("/login", async Task<Results<SignInHttpResult, BadRequest<ProblemInfo>>> (
            LoginRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var key = req.UserNameOrEmail?.Trim() ?? "";
            var user = key.Contains('@') ? await users.FindByEmailAsync(key) : await users.FindByNameAsync(key);
            if (user is null) return Bad("Wrong username or password.");

            var check = await signIn.CheckPasswordSignInAsync(user, req.Password ?? "", lockoutOnFailure: true);
            if (check.IsLockedOut) return Bad("Too many failed attempts. Try again in a few minutes.");
            if (!check.Succeeded) return Bad("Wrong username or password.");

            var principal = await signIn.CreateUserPrincipalAsync(user);
            return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
        }).RequireRateLimiting("auth");

        // Same as Identity's MapIdentityApi refresh: a valid, unexpired refresh ticket whose
        // security stamp still matches gets a fresh token pair.
        g.MapPost("/refresh", async Task<Results<SignInHttpResult, ChallengeHttpResult>> (
            RefreshRequest req, IOptionsMonitor<BearerTokenOptions> options, SignInManager<AppUser> signIn, TimeProvider time) =>
        {
            var ticket = options.Get(IdentityConstants.BearerScheme).RefreshTokenProtector.Unprotect(req.RefreshToken);
            if (ticket?.Properties?.ExpiresUtc is not { } expires || time.GetUtcNow() >= expires
                || await signIn.ValidateSecurityStampAsync(ticket.Principal) is not { } user)
                return TypedResults.Challenge();

            var principal = await signIn.CreateUserPrincipalAsync(user);
            return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
        });

        g.MapGet("/features", (EmailService email, InviteService invites) => new ServerFeatures(email.Enabled, invites.OpenRegistration, AppInfo.Version));

        // IANA zones the server knows, for the time zone picker.
        g.MapGet("/timezones", () => TimeZoneInfo.GetSystemTimeZones()
            .Where(z => z.Id.Contains('/') && !z.Id.StartsWith("Etc/") && !z.Id.StartsWith("SystemV/"))
            .Select(z => (z, Offset: z.GetUtcOffset(DateTimeOffset.UtcNow)))
            .OrderBy(x => x.Offset).ThenBy(x => x.z.Id)
            .Select(x => new TimeZoneOption(x.z.Id, $"(UTC{(x.Offset < TimeSpan.Zero ? "-" : "+")}{x.Offset:hh\\:mm}) {x.z.Id.Replace('_', ' ')}"))
            .ToList());

        g.MapGet("/invite/{code}", (string code, InviteService invites) => invites.CheckAsync(code)).RequireRateLimiting("auth");

        // Always answers 204, so it can't be used to find out which emails have accounts.
        g.MapPost("/forgot", async (ForgotPasswordRequest req, UserManager<AppUser> users, EmailService email, HttpContext http, IConfiguration config) =>
        {
            if (!email.Enabled) return Results.BadRequest(new ProblemInfo("Password reset by email isn't set up on this server."));
            var address = req.Email?.Trim() ?? "";
            if (address.Contains('@') && await users.FindByEmailAsync(address) is { Email: not null } u)
            {
                var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(u)));
                var link = $"{PublicBase(http, config)}reset-password?user={u.Id}&token={token}";
                var name = WebUtility.HtmlEncode(u.UserName);
                email.Queue(new EmailMessage(u.Email, "Reset your SeenIt password",
                    $"Hi {u.UserName},\n\nSomeone (hopefully you) asked to reset your SeenIt password. Open this link within 2 hours to choose a new one:\n\n{link}\n\nIf it wasn't you, ignore this email and your password stays the same.\n",
                    $"<p>Hi {name},</p><p>Someone (hopefully you) asked to reset your SeenIt password. Open this link within 2 hours to choose a new one:</p>" +
                    $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Reset my password</a></p><p>If it wasn't you, ignore this email and your password stays the same.</p>"));
            }
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        g.MapPost("/reset", async (ResetPasswordRequest req, UserManager<AppUser> users, ActivityService activity) =>
        {
            const string expired = "This reset link is invalid or has expired. Ask for a new one.";
            if (await users.FindByIdAsync(req.UserId.ToString()) is not { } u) return Results.BadRequest(new ProblemInfo(expired));
            string token;
            try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(req.Token ?? "")); }
            catch (FormatException) { return Results.BadRequest(new ProblemInfo(expired)); }

            // Also rotates the security stamp, signing out every device.
            var r = await users.ResetPasswordAsync(u, token, req.NewPassword ?? "");
            if (!r.Succeeded)
                return Results.BadRequest(new ProblemInfo(r.Errors.Any(e => e.Code == "InvalidToken") ? expired : string.Join(" ", r.Errors.Select(e => e.Description))));
            await users.SetLockoutEndDateAsync(u, null);
            await users.ResetAccessFailedCountAsync(u);
            await activity.LogAsync(u.Id, ActivityKind.PasswordReset, detail: "with a reset link", userName: u.UserName);
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        var me = g.MapGroup("").RequireAuthorization();

        // Sends straight away (not queued) so Settings can show the mail server's error.
        me.MapPost("/test-email", async (ClaimsPrincipal p, UserManager<AppUser> users, EmailService email, HttpContext http, IConfiguration config) =>
        {
            if (!email.Enabled) return Results.BadRequest(new ProblemInfo("SMTP isn't configured on this server."));
            if (await users.FindByIdAsync(p.UserId().ToString()) is not { Email: not null } u) return Results.Unauthorized();
            try
            {
                await email.SendAsync(new EmailMessage(u.Email, "SeenIt test email",
                    $"Email from {PublicBase(http, config)} is working.", $"<p>Email from <a href=\"{PublicBase(http, config)}\">SeenIt</a> is working.</p>"));
                return Results.NoContent();
            }
            catch (Exception ex) { return Results.BadRequest(new ProblemInfo($"Couldn't send: {ex.Message}")); }
        }).RequireRateLimiting("auth");

        me.MapGet("/me", async Task<Results<Ok<MeDto>, UnauthorizedHttpResult>> (ClaimsPrincipal p, UserManager<AppUser> users, AdminService admins) =>
        {
            if (await users.FindByIdAsync(p.UserId().ToString()) is not { } u) return TypedResults.Unauthorized();
            // Every app start loads /me, which makes it a cheap "last seen".
            if (u.LastSeenAt is null || u.LastSeenAt < DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                u.LastSeenAt = DateTimeOffset.UtcNow;
                await users.UpdateAsync(u);
            }
            return TypedResults.Ok(new MeDto(u.Id, u.UserName!, u.Email!, u.DisplayName, u.CreatedAt, u.HideSpoilers, u.BlurSpoilerImages,
                u.HiddenHomeSections, u.Tabs, u.Region, u.Visibility, u.TrackingSince, admins.IsAdmin(u.UserName), u.TimeZone, u.HomeOrder, u.Theme, u.RatingScale, u.TimeFormat));
        });

        me.MapPut("/me", async (UpdateProfileRequest req, ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            var name = req.DisplayName?.Trim();
            u.DisplayName = string.IsNullOrEmpty(name) ? null : name.Length > 64 ? name[..64] : name;
            await users.UpdateAsync(u);
            return Results.NoContent();
        });

        me.MapPut("/preferences", async (PreferencesRequest req, ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            if (req.HideSpoilers is { } hide) u.HideSpoilers = hide;
            if (req.RatingScale is { } scale && Enum.IsDefined(scale)) u.RatingScale = scale;
            if (req.TimeFormat is { } tf && Enum.IsDefined(tf)) u.TimeFormat = tf;
            if (req.BlurSpoilerImages is { } blur) u.BlurSpoilerImages = blur;
            if (req.HiddenHomeSections is { } hidden)
                u.HiddenHomeSections = hidden.Where(k => UiPrefs.HomeSections.Any(s => s.Key == k)).Distinct().ToList();
            if (req.HomeOrder is { } order) u.HomeOrder = UiPrefs.HomeOrder(order);
            if (req.Theme is { } theme)
            {
                if (theme is "" or "seenit") u.Theme = null;
                else if (UiPrefs.Themes.Any(t => t.Key == theme)) u.Theme = theme;
                else return Results.BadRequest(new ProblemInfo("Unknown theme."));
            }
            if (req.Tabs is { } tabs)
                u.Tabs = tabs.Where(k => UiPrefs.Tabs.Any(t => t.Key == k)).Distinct().Take(UiPrefs.MaxTabs).ToList();
            if (req.Region is { } region)
            {
                region = region.Trim().ToUpperInvariant();
                if (region.Length == 0) u.Region = null;
                else if (region.Length == 2 && region.All(char.IsAsciiLetterUpper)) u.Region = region;
                else return Results.BadRequest(new ProblemInfo("Pick a country from the list."));
            }
            if (req.Visibility is { } vis && Enum.IsDefined(vis)) u.Visibility = vis;
            if (req.TimeZone is { } zone)
            {
                zone = zone.Trim();
                if (zone.Length == 0) u.TimeZone = null;
                else if (zone.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var found)) u.TimeZone = found.Id;
                else return Results.BadRequest(new ProblemInfo("Unknown time zone."));
            }
            if (req.TrackingSince is { } since)
            {
                if (since.Length == 0) u.TrackingSince = null;
                else if (DateOnly.TryParseExact(since, "yyyy-MM-dd", out var day) && day.Year >= 1900) u.TrackingSince = day;
                else return Results.BadRequest(new ProblemInfo("That isn't a valid date."));
            }
            await users.UpdateAsync(u);
            return Results.NoContent();
        });

        me.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal p, UserManager<AppUser> users, ActivityService activity) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            // Changing the password rotates the security stamp, which invalidates other devices' refresh tokens.
            var r = await users.ChangePasswordAsync(u, req.CurrentPassword, req.NewPassword);
            if (r.Succeeded) await activity.LogAsync(p, ActivityKind.PasswordChanged);
            return r.Succeeded ? Results.NoContent()
                : Results.BadRequest(new ProblemInfo(string.Join(" ", r.Errors.Select(e => e.Description))));
        }).RequireRateLimiting("auth");

        me.MapGet("/invites", (ClaimsPrincipal p, InviteService invites) => invites.ListAsync(p.UserId()));
        me.MapPost("/invites", (ClaimsPrincipal p, InviteService invites) => invites.CreateAsync(p.UserId()));
        me.MapDelete("/invites/{code}", async (string code, ClaimsPrincipal p, InviteService invites) =>
            await invites.DeleteAsync(p.UserId(), code) ? Results.NoContent() : Results.NotFound());

        me.MapPost("/clear", async (ClearDataRequest req, ClaimsPrincipal p, UserManager<AppUser> users, TrackingService tracking, ActivityService activity, CancellationToken ct) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            if (!await users.CheckPasswordAsync(u, req.Password))
                return Results.BadRequest(new ProblemInfo("Wrong password."));
            var r = await tracking.ClearAsync(u.Id, req.Parts & ImportParts.All, ct);
            await activity.LogAsync(p, ActivityKind.ClearData,
                detail: $"{req.Parts & ImportParts.All}: {r.Plays} plays, {r.Ratings} ratings, {r.Watchlist} watchlist, {r.Lists} lists");
            return Results.Ok(r);
        }).RequireRateLimiting("auth");

        me.MapPost("/delete", async (DeleteAccountRequest req, ClaimsPrincipal p, UserManager<AppUser> users, ActivityService activity) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            if (!await users.CheckPasswordAsync(u, req.Password))
                return Results.BadRequest(new ProblemInfo("Wrong password."));
            await users.DeleteAsync(u);
            await activity.LogAsync(u.Id, ActivityKind.AccountDeleted, detail: u.Email, userName: u.UserName);
            return Results.NoContent();
        }).RequireRateLimiting("auth");
    }

    /// <summary>Where links in emails point: SeenIt:PublicUrl if set, otherwise the address this request came in on.</summary>
    internal static string PublicBase(HttpContext http, IConfiguration config) =>
        config["SeenIt:PublicUrl"] is { Length: > 0 } url ? url.TrimEnd('/') + "/" : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/";

    private static BadRequest<ProblemInfo> Bad(string message) => TypedResults.BadRequest(new ProblemInfo(message));
}
