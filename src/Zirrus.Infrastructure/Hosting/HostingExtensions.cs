using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;

namespace Zirrus.Infrastructure.Hosting;

/// <summary>Health endpoints and the container health probe shared by the API and the worker.</summary>
public static class HostingExtensions
{
    /// <summary>Port both services listen on inside their containers.</summary>
    public const int ContainerPort = 8080;

    /// <summary>
    /// Maps <c>/health/live</c> (process is up, no dependencies) and <c>/health/ready</c>
    /// (checks tagged "ready", e.g. the database).
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The route builder.</returns>
    public static IEndpointRouteBuilder MapZirrusHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
        return endpoints;
    }

    /// <summary>
    /// True while the build-time OpenAPI generator (Microsoft.Extensions.ApiDescription.Server) starts the
    /// app to read its endpoints. No request is served then, so secrets such as the key-ring key are not needed.
    /// </summary>
    public static bool IsBuildTimeDocumentGeneration =>
        string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal);

    /// <summary>
    /// Container health probe. Chiseled runtime images have no shell or curl, so Compose runs
    /// <c>dotnet App.dll --healthcheck</c>, which calls the local readiness endpoint and exits 0 or 1.
    /// </summary>
    /// <param name="args">Process arguments.</param>
    /// <returns>An exit code when probing was requested; otherwise null.</returns>
    public static async Task<int?> RunHealthProbeIfRequestedAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.Contains("--healthcheck", StringComparer.Ordinal))
        {
            return null;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{ContainerPort}/health/ready"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
