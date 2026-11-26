using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zirrus.Application.Identity;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>
/// Ensures the configured organization (<c>Zirrus:OrganizationId</c>) exists before requests are
/// served, so catalogs always reference a real organization row. Idempotent across replicas.
/// </summary>
/// <param name="services">Root service provider.</param>
/// <param name="organization">Configured organization.</param>
internal sealed class OrganizationBootstrap(IServiceProvider services, OrganizationContext organization) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Hosting.HostingExtensions.IsBuildTimeDocumentGeneration)
        {
            return; // build-time OpenAPI generation starts the host without a database
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZirrusDbContext>();
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO organizations (id, name, created_at) VALUES ({organization.Id}, 'Default organization', now()) ON CONFLICT (id) DO NOTHING",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
