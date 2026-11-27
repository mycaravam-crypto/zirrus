using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Api.Tests;

/// <summary>
/// W5 search on PostgreSQL: permission scope before results and counts (Q3), English and German
/// language handling, and the metadata filter allowlist.
/// </summary>
/// <param name="fixture">Database and API.</param>
public sealed class SearchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// Q3: a user granted one catalog never sees, by results or counts, assets from another catalog,
    /// non-active assets, or assets reached through another catalog's category.
    /// </summary>
    [Fact]
    public async Task Results_and_counts_never_include_assets_outside_the_callers_scope()
    {
        var press = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        var archive = await Seed.CatalogAsync(fixture, ("anna", CatalogRole.Viewer));
        const string Text = """{"title":"Lighthouse at dusk"}""";
        var visible = await SeedAsync(press, Text);
        await SeedAsync(press, Text, AssetState.Quarantined);
        await SeedAsync(press, Text, AssetState.Failed);
        await SeedAsync(press, Text, AssetState.Deleting);
        var foreign = await SeedAsync(archive, Text);
        var pressCategory = await CategoryAsync(press, "Coast", visible);
        var archiveCategory = await CategoryAsync(archive, "Restricted", foreign);

        var path = $"/api/v1/catalogs/{press.Id}/assets";
        foreach (var user in new[] { "erin", "victor" })
        {
            using var client = fixture.Api.ClientAs(user);
            var page = await client.GetFromJsonAsync<JsonElement>(Url($"{path}?q=lighthouse"), Api.Ct);
            Assert.Equal(visible.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

            var facets = await client.GetFromJsonAsync<JsonElement>(Url($"{path}/facets?q=lighthouse"), Api.Ct);
            Assert.Equal(1, facets.GetProperty("total").GetInt32());
            var facet = Assert.Single(facets.GetProperty("categories").EnumerateArray());
            Assert.Equal(pressCategory.Id, facet.GetProperty("categoryId").GetGuid());
            Assert.Equal(1, facet.GetProperty("count").GetInt32());

            // Another catalog's category used as a filter here matches nothing, in results and counts.
            var crossed = await client.GetFromJsonAsync<JsonElement>(Url($"{path}?q=lighthouse&categoryId={archiveCategory.Id}"), Api.Ct);
            Assert.Empty(crossed.GetProperty("items").EnumerateArray());
            var crossedFacets = await client.GetFromJsonAsync<JsonElement>(Url($"{path}/facets?categoryId={archiveCategory.Id}"), Api.Ct);
            Assert.Equal(0, crossedFacets.GetProperty("total").GetInt32());

            // The other catalog is not found at all, for lists and for counts.
            foreach (var hidden in new[] { $"/api/v1/catalogs/{archive.Id}/assets?q=lighthouse", $"/api/v1/catalogs/{archive.Id}/assets/facets?q=lighthouse" })
            {
                using var denied = await client.GetAsync(Url(hidden), Api.Ct);
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            }
        }

        using var anna = fixture.Api.ClientAs("anna");
        var own = await anna.GetFromJsonAsync<JsonElement>(Url($"/api/v1/catalogs/{archive.Id}/assets/facets?q=lighthouse"), Api.Ct);
        Assert.Equal(1, own.GetProperty("total").GetInt32());
        using var outsider = fixture.Api.ClientAs("oscar");
        using var none = await outsider.GetAsync(Url($"{path}/facets"), Api.Ct);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    /// <summary>
    /// German umlauts, ß and transliterations fold through the German stemmer; plurals stem in German
    /// and English. Every query runs against all three configurations (english, german, simple).
    /// </summary>
    [Theory]
    [InlineData("München Rathaus", "Munchen")]
    [InlineData("München Rathaus", "Muenchen")]
    [InlineData("München Rathaus", "münchen")]
    [InlineData("Straße am Fluss", "Strasse")]
    [InlineData("Häfen im Norden", "Hafen")]
    [InlineData("Hafen im Norden", "Häfen")]
    [InlineData("Alte Häuser", "Haus")]
    [InlineData("Neue Fotografien", "Fotografie")]
    [InlineData("Weihnachtsmärkte", "Weihnachtsmarkt")]
    [InlineData("Fishing boats", "boat")]
    [InlineData("Children running", "run")]
    [InlineData("Photographer 17", "photographer 17")]
    public async Task Language_handling_matches_stems_and_diacritics(string title, string query)
    {
        var catalog = await Seed.CatalogAsync(fixture, ("victor", CatalogRole.Viewer));
        var asset = await SeedAsync(catalog, JsonSerializer.Serialize(new { title }));
        await SeedAsync(catalog, """{"title":"Unrelated subject"}""");

        using var victor = fixture.Api.ClientAs("victor");
        var page = await victor.GetFromJsonAsync<JsonElement>(
            Url($"/api/v1/catalogs/{catalog.Id}/assets?q={Uri.EscapeDataString(query)}"), Api.Ct);
        Assert.Equal(asset.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    /// <summary>File names are searchable too, and unrelated terms match nothing.</summary>
    [Fact]
    public async Task File_names_are_searched_and_unrelated_terms_match_nothing()
    {
        var catalog = await Seed.CatalogAsync(fixture, ("victor", CatalogRole.Viewer));
        await SeedAsync(catalog, "{}");
        using var victor = fixture.Api.ClientAs("victor");
        var byName = await victor.GetFromJsonAsync<JsonElement>(Url($"/api/v1/catalogs/{catalog.Id}/assets?q=photo"), Api.Ct);
        Assert.Single(byName.GetProperty("items").EnumerateArray()); // Seed file name: photo.jpg
        var nothing = await victor.GetFromJsonAsync<JsonElement>(Url($"/api/v1/catalogs/{catalog.Id}/assets/facets?q=zebra"), Api.Ct);
        Assert.Equal(0, nothing.GetProperty("total").GetInt32());
    }

    /// <summary>With a schema, metadata filters may only name schema fields (allowlist).</summary>
    [Fact]
    public async Task Metadata_filters_are_limited_to_schema_fields_once_a_schema_exists()
    {
        var catalog = await Seed.CatalogAsync(fixture, ("victor", CatalogRole.Viewer));
        var asset = await SeedAsync(catalog, """{"title":"Quay","internal_note":"x"}""");
        var path = $"/api/v1/catalogs/{catalog.Id}/assets";
        using var victor = fixture.Api.ClientAs("victor");

        // Without a schema every valid key may be filtered.
        var before = await victor.GetFromJsonAsync<JsonElement>(Url($"{path}?metadata={Uri.EscapeDataString("""{"internal_note":"x"}""")}"), Api.Ct);
        Assert.Single(before.GetProperty("items").EnumerateArray());

        await using (var db = fixture.CreateContext())
        {
            db.Add(MetadataSchema.Create(catalog, 1, [new("title", "Title", MetadataFieldType.Text)], DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Api.Ct);
        }

        var allowed = await victor.GetFromJsonAsync<JsonElement>(Url($"{path}?metadata={Uri.EscapeDataString("""{"title":"Quay"}""")}"), Api.Ct);
        Assert.Equal(asset.Id, Assert.Single(allowed.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        foreach (var route in new[] { path, $"{path}/facets" })
        {
            using var rejected = await victor.GetAsync(Url($"{route}?metadata={Uri.EscapeDataString("""{"internal_note":"x"}""")}"), Api.Ct);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
    }

    /// <summary>Editors assign and remove categories; readers list them; viewers cannot change them.</summary>
    [Fact]
    public async Task Asset_categories_are_listed_assigned_and_removed_by_role()
    {
        var catalog = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        var asset = await SeedAsync(catalog, "{}");
        var category = await CategoryAsync(catalog, "Harbour", await SeedAsync(catalog, "{}"));
        var path = $"/api/v1/catalogs/{catalog.Id}/assets/{asset.Id}/categories";
        using var erin = fixture.Api.ClientAs("erin");
        using var victor = fixture.Api.ClientAs("victor");
        using var oscar = fixture.Api.ClientAs("oscar");

        Assert.Empty((await victor.GetFromJsonAsync<JsonElement>(Url(path), Api.Ct)).EnumerateArray());
        using (var denied = await Api.SendAsync(victor, HttpMethod.Put, $"{path}/{category.Id}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var assigned = await Api.SendAsync(erin, HttpMethod.Put, $"{path}/{category.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        }

        var listed = await victor.GetFromJsonAsync<JsonElement>(Url(path), Api.Ct);
        Assert.Equal(category.Id, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
        using (var hidden = await oscar.GetAsync(Url(path), Api.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        using (var forbidden = await Api.SendAsync(victor, HttpMethod.Delete, $"{path}/{category.Id}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var removed = await Api.SendAsync(erin, HttpMethod.Delete, $"{path}/{category.Id}");
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Empty((await removed.Content.ReadFromJsonAsync<JsonElement>(Api.Ct)).EnumerateArray());
        }

        using var withoutCsrf = await Api.SendAsync(erin, HttpMethod.Delete, $"{path}/{category.Id}", csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, withoutCsrf.StatusCode);
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<Asset> SeedAsync(Catalog catalog, string metadataJson, AssetState state = AssetState.Active)
    {
        var seeded = await Seed.AssetAsync(fixture, catalog, state switch
        {
            AssetState.Deleting => AssetState.Active,
            AssetState.Failed => AssetState.Processing,
            _ => state,
        });
        await using var db = fixture.CreateContext();
        var asset = await db.Assets.SingleAsync(a => a.Id == seeded.Id, Api.Ct);
        asset.UpdateMetadata(AssetMetadata.Parse(metadataJson), asset.Revision, DateTimeOffset.UtcNow);
        if (state is AssetState.Failed)
        {
            asset.Fail("render: timeout", DateTimeOffset.UtcNow);
        }

        if (state is AssetState.Deleting)
        {
            asset.MarkForDeletion(DateTimeOffset.UtcNow);
        }

        await db.SaveChangesAsync(Api.Ct);
        return asset;
    }

    private async Task<Category> CategoryAsync(Catalog catalog, string name, Asset asset)
    {
        await using var db = fixture.CreateContext();
        var category = Category.CreateRoot(catalog, name, DateTimeOffset.UtcNow);
        db.Categories.Add(category);
        db.AssetCategories.Add(AssetCategory.Assign(asset, category, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Api.Ct);
        return category;
    }
}
