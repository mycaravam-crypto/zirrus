using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Zirrus.Api.Security;
using Zirrus.Application.Identity;

namespace Zirrus.Api.Endpoints;

/// <summary>The signed-in user, as returned by <c>GET /api/v1/me</c>.</summary>
/// <param name="Subject">Identity-provider subject.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsOrganizationAdmin">True for organization administrators.</param>
public sealed record CurrentUserInfo(string Subject, string Name, bool IsOrganizationAdmin);

/// <summary>Antiforgery token for the SPA, as returned by <c>GET /api/v1/antiforgery</c>.</summary>
/// <param name="Token">Value to send in the <c>X-CSRF-TOKEN</c> header or the logout form field.</param>
/// <param name="FormFieldName">Form field name for the logout form.</param>
public sealed record CsrfToken(string Token, string FormFieldName);

/// <summary>Sign-in, sign-out and current-user endpoints.</summary>
internal static class AccountEndpoints
{
    /// <summary>Maps the browser routes (<c>/login</c>, <c>/logout</c>) and the account API.</summary>
    /// <param name="app">Application.</param>
    /// <param name="api">The authenticated <c>/api/v1</c> group.</param>
    public static void MapAccountEndpoints(this WebApplication app, RouteGroupBuilder api)
    {
        // Starts the OIDC code + PKCE flow; returns to a local path only.
        app.MapGet("/login", async (string? returnUrl, IAuthenticationSchemeProvider schemes) =>
            {
                if (await schemes.GetSchemeAsync(OpenIdConnectDefaults.AuthenticationScheme) is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Sign-in is not configured (Auth:Authority).");
                }

                var target = returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
                    ? returnUrl
                    : "/";
                return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        // Ends the server-side session and the identity-provider session (RP-initiated logout).
        // A form POST with the antiforgery token, so a cross-site page cannot sign users out.
        app.MapPost("/logout", async (HttpContext context, IAuthenticationSchemeProvider schemes) =>
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return await schemes.GetSchemeAsync(OpenIdConnectDefaults.AuthenticationScheme) is null
                    ? Results.Redirect("/")
                    : Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]);
            })
            .AddEndpointFilter(AuthenticationSetup.ValidateAntiforgery)
            .ExcludeFromDescription();

        // Returns the signed-in user; 401 when anonymous.
        api.MapGet("/me", (ICurrentUserAccessor users) =>
                users.User is { } user ? Results.Ok(new CurrentUserInfo(user.Subject, user.DisplayName, user.IsOrganizationAdmin)) : Results.Unauthorized())
            .WithName("GetMe")
            .WithTags("Account")
            .WithSummary("Get the signed-in user and whether they are an organization administrator")
            .Produces<CurrentUserInfo>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // Issues the antiforgery token the SPA must send with unsafe requests and the logout form.
        api.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext context) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                return TypedResults.Ok(new CsrfToken(tokens.RequestToken!, tokens.FormFieldName));
            })
            .WithName("GetCsrfToken")
            .WithTags("Account")
            .WithSummary("Get the CSRF token for unsafe requests and the logout form");
    }
}
