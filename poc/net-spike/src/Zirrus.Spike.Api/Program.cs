using System.Collections.Concurrent;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.HttpResults;

// Spike API for ADR 0007 / security.md: OIDC code + PKCE against Keycloak, server-side cookie
// session (logout invalidates it), antiforgery on unsafe methods, policy authorization.
var builder = WebApplication.CreateBuilder(args);
var authority = builder.Configuration["Oidc:Authority"] ?? "http://127.0.0.1:8180/realms/zirrus";
var ticketStore = new InMemoryTicketStore();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        // API requests answer 401/403 from the cookie scheme; only /login starts the OIDC flow.
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "zirrus.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Spike runs over plain HTTP on localhost; production behind Caddy uses CookieSecurePolicy.Always.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SessionStore = ticketStore; // server-side session: the cookie only holds a session key
        options.Events.OnRedirectToLogin = ctx => ApiStatus(ctx, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = ctx => ApiStatus(ctx, StatusCodes.Status403Forbidden);
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = authority;
        options.ClientId = "zirrus-web";
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"] ?? "spike-only-secret";
        options.ResponseType = "code";
        options.UsePkce = true;
        options.ResponseMode = "query";
        options.RequireHttpsMetadata = false; // spike only: local Keycloak over HTTP
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "preferred_username";
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.SaveTokens = true; // id_token is needed for RP-initiated logout
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("CanEditCatalog", policy => policy.RequireRole("editor", "admin"));

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "zirrus.csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

// Browser entry point: starts the OIDC code flow, then returns to /api/me.
app.MapGet("/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/api/me" },
    [OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous();

// All /api routes require an authenticated user explicitly (also the fallback policy), so the docs show it.
var api = app.MapGroup("/api").RequireAuthorization();

// Returns the signed-in user name and roles from the ID token.
api.MapGet("/me", (HttpContext ctx) => new
{
    user = ctx.User.Identity?.Name,
    roles = ctx.User.FindAll("roles").Select(c => c.Value).ToArray(),
});

// The SPA fetches a token, then echoes it in X-CSRF-TOKEN on unsafe requests.
api.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext ctx) =>
    new { token = antiforgery.GetAndStoreTokens(ctx).RequestToken });

// Reads an asset; any authenticated catalog member may read.
api.MapGet("/assets/{id:guid}", (Guid id) => new { id, title = "Harbor at dawn" });

// Edits asset metadata; editors and admins only, CSRF token required.
api.MapPut("/assets/{id:guid}/metadata", (Guid id, MetadataUpdate update) => new { id, update.Title, saved = true })
    .RequireAuthorization("CanEditCatalog")
    .AddEndpointFilter(ValidateAntiforgery);

// Ends the session server-side, so every copy of the session cookie stops working.
api.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).AddEndpointFilter(ValidateAntiforgery);

// Spike diagnostics: number of active server-side sessions (not for production).
api.MapGet("/sessions", () => new { active = ticketStore.Count }).AllowAnonymous();

app.Run();

static Task ApiStatus(RedirectContext<CookieAuthenticationOptions> ctx, int status)
{
    if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}

static async ValueTask<object?> ValidateAntiforgery(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var antiforgery = ctx.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
    try
    {
        await antiforgery.ValidateRequestAsync(ctx.HttpContext);
    }
    catch (AntiforgeryValidationException)
    {
        return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid or missing CSRF token");
    }

    return await next(ctx);
}

/// <summary>Metadata edit request.</summary>
/// <param name="Title">New title.</param>
internal sealed record MetadataUpdate(string Title);

/// <summary>
/// Server-side session store: the cookie carries only a random key, so removing the entry on
/// logout invalidates every copy of the cookie. Production would back this with PostgreSQL.
/// </summary>
internal sealed class InMemoryTicketStore : ITicketStore
{
    private readonly ConcurrentDictionary<string, AuthenticationTicket> _tickets = new();

    /// <summary>Number of active sessions.</summary>
    public int Count => _tickets.Count;

    /// <inheritdoc />
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _tickets[key] = ticket;
        return Task.FromResult(key);
    }

    /// <inheritdoc />
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        _tickets[key] = ticket;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AuthenticationTicket?> RetrieveAsync(string key) =>
        Task.FromResult(_tickets.TryGetValue(key, out var ticket) ? ticket : null);

    /// <inheritdoc />
    public Task RemoveAsync(string key)
    {
        _tickets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
