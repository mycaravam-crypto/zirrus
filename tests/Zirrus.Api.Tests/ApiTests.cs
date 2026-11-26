using System.Net;
using System.Net.Http.Json;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Api.Tests;

public sealed class ApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        using var client = fixture.Api.CreateClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Catalogs_are_listed_by_name()
    {
        var organization = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await fixture.WithApiContextAsync(async db =>
        {
            db.Catalogs.AddRange(
                Catalog.Create(organization, "Zeta archive", now),
                Catalog.Create(organization, "Alpha press", now));
            await db.SaveChangesAsync();
        });

        using var client = fixture.Api.CreateClient();
        var catalogs = await client.GetFromJsonAsync<List<CatalogSummary>>(
            new Uri("/api/v1/catalogs", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.NotNull(catalogs);
        var names = catalogs.Select(c => c.Name).Where(n => n is "Alpha press" or "Zeta archive").ToList();
        Assert.Equal(["Alpha press", "Zeta archive"], names);
    }

    [Fact]
    public async Task Duplicate_catalog_name_in_one_organization_is_rejected_by_the_database()
    {
        var organization = Guid.NewGuid();
        await using var db = fixture.CreateContext();
        db.Catalogs.AddRange(
            Catalog.Create(organization, "Duplicate", DateTimeOffset.UtcNow),
            Catalog.Create(organization, "Duplicate", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        using var client = fixture.Api.CreateClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"ListCatalogs\"", body, StringComparison.Ordinal);
    }
}
