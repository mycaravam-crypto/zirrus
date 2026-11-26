using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zirrus.Application.Catalogs;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Infrastructure;

/// <summary>Registers infrastructure services for the API and the worker.</summary>
public static class DependencyInjection
{
    /// <summary>Name of the PostgreSQL connection string in configuration (<c>ConnectionStrings__Zirrus</c>).</summary>
    public const string ConnectionStringName = "Zirrus";

    /// <summary>Directory where Docker secrets are mounted, one file per configuration key.</summary>
    public const string SecretsDirectory = "/run/secrets";

    /// <summary>
    /// Adds Docker secrets as configuration: a file named <c>ConnectionStrings__Zirrus</c> becomes
    /// <c>ConnectionStrings:Zirrus</c>. Secrets never appear in images, Compose files or environment variables.
    /// </summary>
    /// <param name="configuration">Configuration builder.</param>
    /// <returns>The configuration builder.</returns>
    public static IConfigurationBuilder AddZirrusSecrets(this IConfigurationBuilder configuration) =>
        configuration.AddKeyPerFile(SecretsDirectory, optional: true, reloadOnChange: false);

    /// <summary>Adds the database context, ports and the database health check.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection.</returns>
    /// <remarks>
    /// The connection string is read when the context is first created, not at registration, so
    /// build-time OpenAPI generation can start the API without a database. A missing connection
    /// string still fails the first request and the readiness check.
    /// </remarks>
    public static IServiceCollection AddZirrusInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddDbContext<ZirrusDbContext>(options => options.UseNpgsql(
            configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.")));
        services.AddScoped<ICatalogReader, CatalogReader>();
        services.AddScoped<ListCatalogs>();
        services.AddHealthChecks().AddDbContextCheck<ZirrusDbContext>("database", tags: ["ready"]);
        return services;
    }
}

/// <summary>Design-time factory for <c>dotnet ef</c> (migrations and migration bundles).</summary>
internal sealed class ZirrusDbContextFactory : IDesignTimeDbContextFactory<ZirrusDbContext>
{
    /// <summary>Creates a context for migrations, using the environment, the mounted secret or a local default.</summary>
    /// <param name="args">Arguments passed by <c>dotnet ef</c>; not used.</param>
    /// <returns>A context bound to the resolved connection string.</returns>
    public ZirrusDbContext CreateDbContext(string[] args)
    {
        // Used by `dotnet ef` and by the migration bundle at run time. In containers the connection
        // string comes from the mounted Docker secret; locally from the environment.
        var secretFile = Path.Combine(DependencyInjection.SecretsDirectory, "ConnectionStrings__Zirrus");
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Zirrus")
            ?? (File.Exists(secretFile) ? File.ReadAllText(secretFile).Trim() : null)
            ?? "Host=localhost;Database=zirrus;Username=zirrus";
        return new ZirrusDbContext(new DbContextOptionsBuilder<ZirrusDbContext>().UseNpgsql(connectionString).Options);
    }
}
