using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Zirrus.Application.Identity;
using Zirrus.Infrastructure.Identity;

namespace Zirrus.Api.Security;

/// <summary>Authentication settings (<c>Auth</c> section; the client secret comes from the <c>Auth__ClientSecret</c> secret).</summary>
public sealed class AuthOptions
{
    /// <summary>OIDC authority, e.g. <c>https://id.example.org/realms/zirrus</c>. Without it, sign-in is disabled.</summary>
    public string? Authority { get; set; }

    /// <summary>OIDC client ID.</summary>
    public string ClientId { get; set; } = "zirrus-web";

    /// <summary>OIDC client secret (confidential client).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Identity-provider role that makes a user an organization administrator.</summary>
    public string AdminRole { get; set; } = "admin";

    /// <summary>
    /// Test environments only: allow plain HTTP (cookies without Secure, Lax correlation cookies and
    /// query response mode). Production always runs behind TLS (security.md).
    /// </summary>
    public bool AllowInsecureHttp { get; set; }
}

/// <summary>Cookie session, OIDC login, antiforgery and authorization policies (security.md, W2).</summary>
public static class AuthenticationSetup
{
    /// <summary>Policy for organization-administrator endpoints.</summary>
    public const string OrganizationAdminPolicy = "OrganizationAdmin";

    /// <summary>Header the SPA sends the antiforgery token in.</summary>
    public const string CsrfHeader = "X-CSRF-TOKEN";

    /// <summary>Registers authentication, authorization and antiforgery.</summary>
    /// <param name="builder">Web application builder.</param>
    /// <returns>The builder.</returns>
    public static WebApplicationBuilder AddZirrusAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var auth = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
        var cookieSecurity = auth.AllowInsecureHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        var authentication = builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // API calls answer 401/403 from the cookie scheme; only /login starts the OIDC flow.
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "zirrus.session";
                options.Cookie.Path = "/"; // independent of the request path base
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = cookieSecurity;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = ctx => StatusOnly(ctx, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = ctx => StatusOnly(ctx, StatusCodes.Status403Forbidden);
            });

        // The cookie only carries a session key; tickets live in PostgreSQL (logout ends every copy).
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<PostgresTicketStore>((options, store) => options.SessionStore = store);

        if (!string.IsNullOrWhiteSpace(auth.Authority))
        {
            authentication.AddOpenIdConnect(options =>
            {
                options.Authority = auth.Authority;
                options.ClientId = auth.ClientId;
                options.ClientSecret = auth.ClientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = true; // id_token is needed for RP-initiated logout
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = "roles";
                options.RequireHttpsMetadata = !auth.AllowInsecureHttp;
                if (auth.AllowInsecureHttp)
                {
                    // Plain HTTP cannot carry SameSite=None cookies; use query mode with Lax cookies (tests only).
                    options.ResponseMode = "query";
                    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.NonceCookie.SameSite = SameSiteMode.Lax;
                    options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                }
            });
        }

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(OrganizationAdminPolicy, policy => policy.RequireRole(auth.AdminRole));

        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeader;
            options.Cookie.Name = "zirrus.csrf";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = cookieSecurity;
        });

        // The API is reachable only from Caddy on the internal backend network (compose.yaml), so the
        // forwarded headers it sends are trusted; production then sees HTTPS and issues Secure cookies.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();
        return builder;
    }

    /// <summary>
    /// Endpoint filter rejecting unsafe requests (POST, PUT, PATCH, DELETE) without a valid
    /// antiforgery token in the <c>X-CSRF-TOKEN</c> header or the form field.
    /// </summary>
    /// <param name="context">Filter context.</param>
    /// <param name="next">Next filter.</param>
    /// <returns>The endpoint result, or 400 problem details.</returns>
    public static async ValueTask<object?> ValidateAntiforgery(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return await next(context);
        }

        // Uploads must carry the token in the header: looking for a form field would buffer the whole file.
        var request = context.HttpContext.Request;
        if (request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true && !request.Headers.ContainsKey(CsrfHeader))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing or invalid CSRF token");
        }

        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing or invalid CSRF token");
        }

        return await next(context);
    }

    private static Task StatusOnly(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}

/// <summary>Maps the request principal to the application's <see cref="CurrentUser"/>.</summary>
/// <param name="http">HTTP context accessor.</param>
/// <param name="options">Authentication settings.</param>
internal sealed class HttpCurrentUserAccessor(IHttpContextAccessor http, IOptions<AuthOptions> options) : ICurrentUserAccessor
{
    /// <inheritdoc />
    public CurrentUser? User
    {
        get
        {
            var principal = http.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var subject = principal.FindFirstValue("sub");
            return subject is null
                ? null
                : new CurrentUser(subject, principal.Identity.Name ?? string.Empty, principal.IsInRole(options.Value.AdminRole));
        }
    }
}
