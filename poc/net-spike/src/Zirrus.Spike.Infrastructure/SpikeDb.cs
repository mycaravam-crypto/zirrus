using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Zirrus.Spike.Infrastructure;

/// <summary>Connection settings shared by the spike programs.</summary>
public static class SpikeDb
{
    /// <summary>Connection string from ZIRRUS_SPIKE_DB, defaulting to the local spike database.</summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("ZIRRUS_SPIKE_DB")
        ?? "Host=127.0.0.1;Database=zirrus_spike;Username=zirrus;Password=spike-only";

    /// <summary>Creates an EF Core context.</summary>
    /// <returns>A new context.</returns>
    public static SpikeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SpikeDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Creates an Npgsql data source for raw queue access.</summary>
    /// <returns>A new data source.</returns>
    public static NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString);
}

/// <summary>Lets dotnet-ef create the context for migrations and migration bundles.</summary>
public sealed class SpikeDbContextFactory : IDesignTimeDbContextFactory<SpikeDbContext>
{
    /// <inheritdoc />
    public SpikeDbContext CreateDbContext(string[] args) => SpikeDb.CreateContext();
}
