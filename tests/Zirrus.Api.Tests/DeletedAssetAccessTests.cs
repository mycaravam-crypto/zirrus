using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Api.Tests;

/// <summary>
/// #49: once an asset is soft-deleted through the API, no route reveals or changes it, for editors
/// and viewers alike: search results and counts, category and metadata filters, previews and
/// thumbnails, its category list, and every write. #52 covers details, content, Range and HEAD.
/// </summary>
/// <param name="fixture">Database and API.</param>
public sealed class DeletedAssetAccessTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>A deleted asset disappears from every read path and rejects every write.</summary>
    [Fact]
    public async Task A_soft_deleted_asset_is_unreachable_through_every_route()
    {
        var catalog = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        var (deleted, category) = await SeedAsync(catalog, """{"title":"Harbour crane"}""");
        var (kept, _) = await SeedAsync(catalog, """{"title":"Harbour crane"}""", category);

        var assets = $"/api/v1/catalogs/{catalog.Id}/assets";
        var path = $"{assets}/{deleted.Id}";
        using var erin = fixture.Api.ClientAs("erin");
        using (var removed = await SendWithIfMatch(erin, HttpMethod.Delete, path, null, $"\"{deleted.Revision}\""))
        {
            Assert.True(removed.IsSuccessStatusCode, $"delete returned {(int)removed.StatusCode}");
        }

        await using (var db = fixture.CreateContext())
        {
            var state = await db.Assets.Where(a => a.Id == deleted.Id).Select(a => a.State).SingleAsync(Api.Ct);
            Assert.True(state is AssetState.Deleting or AssetState.Deleted, $"unexpected state {state}");
        }

        foreach (var user in new[] { "erin", "victor" })
        {
            using var client = fixture.Api.ClientAs(user);

            // Search, category and metadata filters return only the remaining asset, in results and counts.
            foreach (var query in new[] { "?q=harbour", $"?categoryId={category.Id}", $"?q=crane&categoryId={category.Id}", "" })
            {
                var page = await client.GetFromJsonAsync<JsonElement>(Url($"{assets}{query}"), Api.Ct);
                var ids = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
                Assert.Contains(kept.Id, ids);
                Assert.DoesNotContain(deleted.Id, ids);

                var facets = await client.GetFromJsonAsync<JsonElement>(Url($"{assets}/facets{query}"), Api.Ct);
                Assert.Equal(1, facets.GetProperty("total").GetInt32());
                var facet = Assert.Single(facets.GetProperty("categories").EnumerateArray());
                Assert.Equal(category.Id, facet.GetProperty("categoryId").GetGuid());
                Assert.Equal(1, facet.GetProperty("count").GetInt32());
            }

            // Detail-level reads: every rendition kind (GET and HEAD) and the asset's categories.
            foreach (var kind in new[] { "thumbnail", "preview" })
            {
                using var rendition = await client.GetAsync(Url($"{path}/renditions/{kind}"), Api.Ct);
                Assert.Equal(HttpStatusCode.NotFound, rendition.StatusCode);
                using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url($"{path}/renditions/{kind}")), Api.Ct);
                Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
            }

            using (var categories = await client.GetAsync(Url($"{path}/categories"), Api.Ct))
            {
                Assert.Equal(HttpStatusCode.NotFound, categories.StatusCode);
            }
        }

        // Writes by an editor are refused as not found, and none of them changes the asset.
        var revision = await RevisionAsync(deleted.Id);
        foreach (var (method, target, json) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Put, $"{path}/metadata", """{"title":"Revived"}"""),
            (HttpMethod.Put, $"{path}/categories/{category.Id}", null),
            (HttpMethod.Delete, $"{path}/categories/{category.Id}", null),
            (HttpMethod.Post, $"{path}/retry", null),
            (HttpMethod.Delete, path, null),
        })
        {
            using var response = await SendWithIfMatch(erin, method, target, json, $"\"{revision}\"");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        await using var check = fixture.CreateContext();
        var after = await check.Assets.AsNoTracking().SingleAsync(a => a.Id == deleted.Id, Api.Ct);
        Assert.Equal(revision, after.Revision);
        Assert.True(after.State is AssetState.Deleting or AssetState.Deleted, $"unexpected state {after.State}");
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task<HttpResponseMessage> SendWithIfMatch(HttpClient client, HttpMethod method, string path, string? json, string ifMatch)
    {
        using var request = new HttpRequestMessage(method, Url(path));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Headers.Add("X-CSRF-TOKEN", await Api.CsrfAsync(client));
        return await client.SendAsync(request, Api.Ct);
    }

    private async Task<int> RevisionAsync(Guid assetId)
    {
        await using var db = fixture.CreateContext();
        return await db.Assets.Where(a => a.Id == assetId).Select(a => a.Revision).SingleAsync(Api.Ct);
    }

    private async Task<(Asset Asset, Category Category)> SeedAsync(Catalog catalog, string metadataJson, Category? existing = null)
    {
        var seeded = await Seed.AssetAsync(fixture, catalog);
        await using var db = fixture.CreateContext();
        var asset = await db.Assets.SingleAsync(a => a.Id == seeded.Id, Api.Ct);
        asset.UpdateMetadata(AssetMetadata.Parse(metadataJson), asset.Revision, DateTimeOffset.UtcNow);
        var category = existing ?? Category.CreateRoot(catalog, $"Port {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        if (existing is null)
        {
            db.Categories.Add(category);
        }

        db.AssetCategories.Add(AssetCategory.Assign(asset, category, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Api.Ct);
        return (asset, category);
    }
}
