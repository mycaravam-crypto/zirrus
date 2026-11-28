using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zirrus.Application.Assets;
using Zirrus.Application.Identity;
using Zirrus.Application.Ingestion;
using Zirrus.Application.Catalogs;
using Zirrus.Application.Storage;
using Zirrus.Infrastructure.Identity;
using Zirrus.Infrastructure.Ingestion;
using Zirrus.Infrastructure.Persistence;
using Zirrus.Infrastructure.Storage;

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
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new OrganizationContext(
            configuration.GetValue<Guid?>("Zirrus:OrganizationId")
            ?? throw new InvalidOperationException("Zirrus:OrganizationId is not configured.")));
        services.AddScoped<ICatalogReader, CatalogReader>();
        services.AddScoped<ICatalogStore, CatalogStore>();
        services.AddScoped<ListCatalogs>();
        services.AddScoped<CatalogAccess>();
        services.AddScoped<Zirrus.Application.Audit.IAuditLog, Zirrus.Infrastructure.Audit.AuditLog>();
        services.AddScoped<Zirrus.Infrastructure.Audit.SessionAudit>();
        services.Configure<Zirrus.Infrastructure.Audit.AuditDigestOptions>(configuration.GetSection("Audit"));
        services.AddScoped<Zirrus.Infrastructure.Audit.AuditDigests>();
        services.AddScoped<CatalogAdministration>();
        services.AddScoped<ISchemaStore, SchemaStore>();
        services.AddScoped<SchemaAdministration>();
        services.AddScoped<ICategoryStore, CategoryStore>();
        services.AddScoped<CategoryManagement>();
        services.AddScoped<IAssetStore, AssetStore>();
        services.AddScoped<AssetUseCases>();
        services.AddScoped<IAssetDeletionStore, AssetDeletionStore>();
        services.AddScoped<DeleteAsset>();
        services.AddScoped<IStorageInventory, StorageInventory>();
        services.AddScoped<ReconcileStorage>();
        services.AddScoped<IIngestionStore, IngestionStore>();
        services.AddScoped<UploadAsset>();
        services.AddScoped<RetryAsset>();
        services.AddHostedService<OrganizationBootstrap>();
        services.AddZirrusBlobStore(configuration);
        services.AddHealthChecks().AddDbContextCheck<ZirrusDbContext>("database", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Adds the ingestion pipeline for the worker: the PostgreSQL job queue, ClamAV scanner
    /// (<c>Scanner</c> section), the renderer and metadata extractor. Production uses the isolated
    /// renderer at <c>Renderer:Url</c>; in-process rendering and no-op metadata are for tests/development.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddZirrusIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(_ => Npgsql.NpgsqlDataSource.Create(
            configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.")));
        services.AddSingleton<IJobQueue, NpgsqlJobQueue>();
        services.AddSingleton<IMalwareScanner>(_ => new ClamAvScanner(configuration.GetSection("Scanner").Get<ClamAvOptions>() ?? new ClamAvOptions()));
        var renderer = configuration.GetSection("Renderer").Get<RendererClientOptions>() ?? new RendererClientOptions();
        if (string.IsNullOrWhiteSpace(renderer.Url))
        {
            services.AddSingleton<IImageRenderer, SkiaImageRenderer>(); // in-process: tests and development only
        }
        else
        {
            services.AddHttpClient<IImageRenderer, HttpImageRenderer>(client =>
            {
                client.BaseAddress = new Uri(renderer.Url);
                client.Timeout = renderer.Timeout;
            });
            services.AddHttpClient<IMetadataExtractor, HttpMetadataExtractor>(client =>
            {
                client.BaseAddress = new Uri(renderer.Url);
                client.Timeout = renderer.Timeout;
            });
        }

        if (string.IsNullOrWhiteSpace(renderer.Url))
        {
            services.AddSingleton<IMetadataExtractor, NoMetadataExtractor>(); // Development and tests only; production requires the isolated renderer.
        }
        services.AddScoped<IngestionPipeline>();
        return services;
    }

    /// <summary>
    /// Adds the blob store selected by <c>Storage:Provider</c>: <c>filesystem</c> (default, under
    /// <c>Storage:Root</c>) or <c>s3</c> (<c>Storage:S3</c>). The store is created on first use.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddZirrusBlobStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<IBlobStore>(_ =>
        {
            var options = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
            return options.Provider.ToUpperInvariant() switch
            {
                "FILESYSTEM" => new FileSystemBlobStore(options.Root),
                "S3" => new S3BlobStore(options.S3),
                _ => throw new InvalidOperationException($"Unknown Storage:Provider '{options.Provider}'; use 'filesystem' or 's3'."),
            };
        });
        return services;
    }

    /// <summary>
    /// Adds the shared Data Protection key ring (PostgreSQL, encrypted at rest) and the PostgreSQL
    /// session store, so cookies and antiforgery tokens work across restarts and replicas.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration; reads <c>DataProtection:Key</c>.</param>
    /// <param name="environment">Host environment; only Development may run without the key.</param>
    /// <returns>The service collection.</returns>
    /// <exception cref="InvalidOperationException">The key is missing outside Development.</exception>
    public static IServiceCollection AddZirrusSessions(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        services.AddDataProtection()
            .SetApplicationName("zirrus")
            .PersistKeysToDbContext<ZirrusDbContext>();

        var key = configuration["DataProtection:Key"];
        if (!string.IsNullOrWhiteSpace(key))
        {
            var encryptionKey = new KeyRingEncryptionKey(key);
            services.AddSingleton(encryptionKey);
            services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new AesGcmXmlEncryptor(encryptionKey));
        }
        else if (!environment.IsDevelopment() && !Hosting.HostingExtensions.IsBuildTimeDocumentGeneration)
        {
            throw new InvalidOperationException(
                "DataProtection:Key is required outside Development: the key ring must be encrypted at rest (security.md).");
        }

        services.AddSingleton<PostgresTicketStore>();
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
