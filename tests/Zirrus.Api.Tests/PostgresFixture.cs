using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Api.Tests;

/// <summary>
/// Provides a migrated PostgreSQL 18 database and API hosts for integration tests.
/// Uses Testcontainers by default; set ZIRRUS_TEST_DB to an admin connection string to use an
/// existing server instead (a fresh database is created per fixture and dropped afterwards).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Organization ID from the API's appsettings.json.</summary>
    public static readonly Guid OrganizationId = Guid.Parse("6f0d3d2a-7c1e-4b8e-9a61-1d5c0a7e2b01");

    private const string Image = "postgres:18";
    private readonly List<IAsyncDisposable> _hosts = [];
    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;
    private string? _databaseName;

    /// <summary>Connection string for the test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Key-ring encryption key shared by the hosts of this fixture (like replicas sharing a secret).</summary>
    public string DataProtectionKey { get; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>API host whose users authenticate with the <c>X-Test-User</c> header.</summary>
    public ApiFactory Api { get; private set; } = null!;

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

        Api = CreateApi(headerAuthentication: true);
    }

    /// <summary>Creates an API host on the test database. Hosts share the database and key-ring key.</summary>
    /// <param name="headerAuthentication">Authenticate with <c>X-Test-User</c> instead of the session cookie.</param>
    /// <param name="dataProtectionKey">Key-ring key; null uses the fixture key, empty omits it.</param>
    /// <returns>The host; disposed with the fixture.</returns>
    public ApiFactory CreateApi(bool headerAuthentication, string? dataProtectionKey = null)
    {
        var factory = new ApiFactory(ConnectionString, dataProtectionKey ?? DataProtectionKey, headerAuthentication);
        _hosts.Add(factory);
        return factory;
    }

    /// <summary>Creates a context on the test database.</summary>
    /// <returns>A new context; the caller disposes it.</returns>
    public ZirrusDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ZirrusDbContext>().UseNpgsql(ConnectionString).Options);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
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

/// <summary>
/// API host in the Production environment (Secure cookies, key-ring key required), reached over HTTPS.
/// </summary>
/// <param name="connectionString">Test database.</param>
/// <param name="dataProtectionKey">Key-ring key; empty to omit it.</param>
/// <param name="headerAuthentication">Use <see cref="HeaderAuthenticationHandler"/> instead of the session cookie.</param>
public sealed class ApiFactory(string connectionString, string dataProtectionKey, bool headerAuthentication) : WebApplicationFactory<Program>
{
    /// <summary>Path of the test-only sign-in endpoint that issues a real session cookie.</summary>
    public const string SignInPath = "/test/sign-in";

    /// <summary>Creates a client over HTTPS that keeps cookies (session and CSRF).</summary>
    /// <param name="user">Value for <c>X-Test-User</c> ("subject" or "subject,admin"); null for anonymous.</param>
    /// <returns>The client.</returns>
    public HttpClient ClientAs(string? user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.Header, user);
        }

        return client;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Zirrus", connectionString);
        if (dataProtectionKey.Length > 0)
        {
            builder.UseSetting("DataProtection:Key", dataProtectionKey);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            if (headerAuthentication)
            {
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = HeaderAuthenticationHandler.SchemeName;
                });
            }
            else
            {
                // Stands in for the OIDC callback: signs in with the real cookie scheme and session store.
                services.AddSingleton<IStartupFilter, SignInEndpointFilter>();
            }
        });
    }

    private sealed class SignInEndpointFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(SignInPath, signIn => signIn.Run(async context =>
            {
                var subject = context.Request.Query["sub"].ToString();
                var claims = new List<Claim> { new("sub", subject), new("preferred_username", subject) };
                if (context.Request.Query.ContainsKey("admin"))
                {
                    claims.Add(new Claim("roles", "admin"));
                }

                var identity = new ClaimsIdentity(claims, "test", "preferred_username", "roles");
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }));
            next(app);
        };
    }
}

/// <summary>Test authentication: <c>X-Test-User: subject[,admin]</c> signs the request in as that user.</summary>
/// <param name="options">Scheme options.</param>
/// <param name="logger">Logger factory.</param>
/// <param name="encoder">URL encoder.</param>
public sealed class HeaderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Scheme name.</summary>
    public const string SchemeName = "TestHeader";

    /// <summary>Request header carrying the test user.</summary>
    public const string Header = "X-Test-User";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var parts = value.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var claims = new List<Claim> { new("sub", parts[0]), new("preferred_username", parts[0]) };
        if (parts.Contains("admin", StringComparer.Ordinal))
        {
            claims.Add(new Claim("roles", "admin"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "preferred_username", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
