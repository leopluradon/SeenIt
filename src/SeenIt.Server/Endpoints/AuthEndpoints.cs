using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SeenIt.Contracts;
using SeenIt.Server.Data;

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
            RegisterRequest req, UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
        {
            var userName = req.UserName?.Trim() ?? "";
            if (!UserNamePattern().IsMatch(userName))
                return Bad("Usernames are 3–32 letters, numbers, dots, dashes or underscores.");
            if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
                return Bad("Enter a valid email address.");

            var user = new AppUser { UserName = userName, Email = req.Email.Trim() };
            var result = await users.CreateAsync(user, req.Password ?? "");
            if (!result.Succeeded)
                return TypedResults.BadRequest(new ProblemInfo("Couldn't create the account.",
                    result.Errors.GroupBy(e => e.Code).ToDictionary(e => e.Key, e => e.Select(x => x.Description).ToArray())));

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

        var me = g.MapGroup("").RequireAuthorization();

        me.MapGet("/me", async Task<Results<Ok<MeDto>, UnauthorizedHttpResult>> (ClaimsPrincipal p, UserManager<AppUser> users) =>
            await users.FindByIdAsync(p.UserId().ToString()) is { } u
                ? TypedResults.Ok(new MeDto(u.Id, u.UserName!, u.Email!, u.DisplayName, u.CreatedAt, u.HideSpoilers, u.BlurSpoilerImages, u.HiddenHomeSections, u.Tabs))
                : TypedResults.Unauthorized());

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
            if (req.BlurSpoilerImages is { } blur) u.BlurSpoilerImages = blur;
            if (req.HiddenHomeSections is { } hidden)
                u.HiddenHomeSections = hidden.Where(k => UiPrefs.HomeSections.Any(s => s.Key == k)).Distinct().ToList();
            if (req.Tabs is { } tabs)
                u.Tabs = tabs.Where(k => UiPrefs.Tabs.Any(t => t.Key == k)).Distinct().Take(UiPrefs.MaxTabs).ToList();
            await users.UpdateAsync(u);
            return Results.NoContent();
        });

        me.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            // Changing the password rotates the security stamp, which invalidates other devices' refresh tokens.
            var r = await users.ChangePasswordAsync(u, req.CurrentPassword, req.NewPassword);
            return r.Succeeded ? Results.NoContent()
                : Results.BadRequest(new ProblemInfo(string.Join(" ", r.Errors.Select(e => e.Description))));
        }).RequireRateLimiting("auth");

        me.MapPost("/delete", async (DeleteAccountRequest req, ClaimsPrincipal p, UserManager<AppUser> users) =>
        {
            var u = await users.FindByIdAsync(p.UserId().ToString());
            if (u is null) return Results.Unauthorized();
            if (!await users.CheckPasswordAsync(u, req.Password))
                return Results.BadRequest(new ProblemInfo("Wrong password."));
            await users.DeleteAsync(u);
            return Results.NoContent();
        }).RequireRateLimiting("auth");
    }

    private static BadRequest<ProblemInfo> Bad(string message) => TypedResults.BadRequest(new ProblemInfo(message));
}
