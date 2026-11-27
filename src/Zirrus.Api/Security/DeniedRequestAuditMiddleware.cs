using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Zirrus.Application.Audit;
using Zirrus.Application.Identity;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Api.Security;

/// <summary>Persists minimal audit events for authenticated API requests denied with 403 or 404.</summary>
/// <param name="next">Next middleware.</param>
/// <param name="scopeFactory">Creates an independent database scope after the response is produced.</param>
/// <param name="logger">Audit persistence diagnostics.</param>
internal sealed class DeniedRequestAuditMiddleware(
    RequestDelegate next,
    IServiceScopeFactory scopeFactory,
    ILogger<DeniedRequestAuditMiddleware> logger)
{
    /// <summary>Runs the request and records an access-denied event when applicable.</summary>
    /// <param name="context">HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || context.Response.StatusCode is not (StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound)
            || context.GetEndpoint() is not RouteEndpoint endpoint
            || context.User.FindFirstValue("sub") is not { Length: > 0 } actor)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var audit = services.GetRequiredService<IAuditLog>();
            var database = services.GetRequiredService<ZirrusDbContext>();
            var organization = services.GetRequiredService<OrganizationContext>();
            var detail = $"method={context.Request.Method}; route={endpoint.RoutePattern.RawText ?? "unknown"}";

            audit.Record(new AuditEntry(
                AuditActions.AccessDenied,
                actor,
                organization.Id,
                RouteGuid(context, "catalogId"),
                RouteGuid(context, "assetId") ?? RouteGuid(context, "categoryId") ?? RouteGuid(context, "schemaId"),
                detail,
                "denied"));
            await database.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist an access-denied audit event.");
        }
    }

    private static Guid? RouteGuid(HttpContext context, string key) =>
        context.Request.RouteValues.TryGetValue(key, out var value) && Guid.TryParse(value?.ToString(), out var id)
            ? id
            : null;
}
