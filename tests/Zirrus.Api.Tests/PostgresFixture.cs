using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Api.Tests;

/// <summary>
/// Provides a migrated PostgreSQL 18 database and an API host for integration tests.
/// Uses Testcontainers by default; set ZIRRUS_TEST_DB to an admin connection string to use an
/// existing server instead (a fresh database is created per fixture and dropped afterwards).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Image = "postgres:18";
    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;
    private string? _databaseName;

    /// <summary>Connection string for the test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>API host wired to the test database.</summary>
    public WebApplicationFactory<Program> Api { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("ZIRRUS_TEST_DB");
        if (string.IsNullOrEmpty(_adminConnectionString))
        {
            _container = new PostgreSqlBuilder(Image).Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        else
        {
            _databaseName = $"zirrus_test_{Guid.NewGuid():N}";
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", admin))
            {
                await create.ExecuteNonQueryAsync();
            }

            ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = _databaseName }.ConnectionString;
        }

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        Api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Zirrus", ConnectionString));
    }

    /// <summary>Creates a context on the test database.</summary>
    /// <returns>A new context; the caller disposes it.</returns>
    public ZirrusDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ZirrusDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Runs an action with a scoped context from the API host's container.</summary>
    /// <param name="action">Action to run.</param>
    /// <returns>A task.</returns>
    public async Task WithApiContextAsync(Func<ZirrusDbContext, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = Api.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ZirrusDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        else if (_databaseName is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
