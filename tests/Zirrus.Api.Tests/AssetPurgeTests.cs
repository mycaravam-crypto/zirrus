using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zirrus.Application.Assets;
using Zirrus.Application.Audit;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;
using Zirrus.Infrastructure.Audit;

namespace Zirrus.Api.Tests;

/// <summary>
/// #49 retention purge on PostgreSQL and the filesystem blob store: only soft-deleted assets past the
/// retention period are purged, the purge is audited, bounded and idempotent, and a failed blob
/// deletion leaves the asset soft-deleted and unreachable until a later run succeeds.
/// </summary>
/// <param name="fixture">Database and API host services.</param>
public sealed class AssetPurgeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>An expired soft-deleted asset loses its blobs, renditions and category links; nothing else changes.</summary>
    [Fact]
    public async Task Purges_only_expired_soft_deleted_assets_and_audits_each_once()
    {
        var catalog = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor));
        var expired = await SeedAsync(catalog, deletedDaysAgo: 31);
        var recent = await SeedAsync(catalog, deletedDaysAgo: 29);
        var active = await SeedAsync(catalog, deletedDaysAgo: null);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var report = await scope.ServiceProvider.GetRequiredService<PurgeDeletedAssets>().RunAsync(Retention, 100, Api.Ct);

        Assert.Empty(report.Failures);
        Assert.True(report.Purged >= 1);
        await AssertPurgedAsync(blobs, expired);
        await AssertIntactAsync(blobs, recent, AssetState.Deleting);
        await AssertIntactAsync(blobs, active, AssetState.Active);

        // Running again finds nothing new for these assets and records no second purge event.
        await using var again = fixture.Api.Services.CreateAsyncScope();
        await again.ServiceProvider.GetRequiredService<PurgeDeletedAssets>().RunAsync(Retention, 100, Api.Ct);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.Purge && e.ResourceId == expired.Asset.Id, Api.Ct));
        Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.Purge && e.ResourceId == recent.Asset.Id, Api.Ct));
        var purgeEvent = await db.Set<AuditEvent>().SingleAsync(e => e.Action == AuditActions.Purge && e.ResourceId == expired.Asset.Id, Api.Ct);
        Assert.Equal(PurgeDeletedAssets.Actor, purgeEvent.Actor);
        Assert.Equal(catalog.Id, purgeEvent.CatalogId);
    }

    /// <summary>The batch size bounds a run; the remaining expired assets are purged by later runs.</summary>
    [Fact]
    public async Task A_run_purges_at_most_the_batch_size()
    {
        var catalog = await Seed.CatalogAsync(fixture);
        var seeded = new List<Seeded>();
        for (var i = 0; i < 3; i++)
        {
            seeded.Add(await SeedAsync(catalog, deletedDaysAgo: 400 + i)); // older than anything else in the fixture
        }

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<PurgeDeletedAssets>().RunAsync(Retention, 2, Api.Ct);
            Assert.Equal(2, report.Purged);
            Assert.True(report.BatchFull);
        }

        await using (var db = fixture.CreateContext())
        {
            var ids = seeded.Select(s => s.Asset.Id).ToList();
            var states = await db.Assets.Where(a => ids.Contains(a.Id)).Select(a => a.State).ToListAsync(Api.Ct);
            Assert.Equal(2, states.Count(s => s == AssetState.Deleted));
            Assert.Equal(1, states.Count(s => s == AssetState.Deleting));
        }

        await using var next = fixture.Api.Services.CreateAsyncScope();
        await next.ServiceProvider.GetRequiredService<PurgeDeletedAssets>().RunAsync(Retention, 100, Api.Ct);
        var store = next.ServiceProvider.GetRequiredService<IBlobStore>();
        foreach (var asset in seeded)
        {
            await AssertPurgedAsync(store, asset);
        }
    }

    /// <summary>A failed blob deletion keeps the asset soft-deleted and hidden; a later run completes the purge.</summary>
    [Fact]
    public async Task A_failed_blob_deletion_never_restores_access_and_is_retried()
    {
        var catalog = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor));
        var asset = await SeedAsync(catalog, deletedDaysAgo: 31);

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var real = scope.ServiceProvider.GetRequiredService<IBlobStore>();
            var failing = new FailingDeletes(real, BlobKey.Parse(asset.Asset.OriginalKey));
            var purge = new PurgeDeletedAssets(scope.ServiceProvider.GetRequiredService<IAssetPurgeStore>(), failing, TimeProvider.System);
            var report = await purge.RunAsync(Retention, 100, Api.Ct);
            Assert.Contains(report.Failures, f => f.AssetId == asset.Asset.Id);
        }

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(AssetState.Deleting, await db.Assets.Where(a => a.Id == asset.Asset.Id).Select(a => a.State).SingleAsync(Api.Ct));
            Assert.Equal(0, await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.Purge && e.ResourceId == asset.Asset.Id, Api.Ct));
        }

        using var erin = fixture.Api.ClientAs("erin");
        foreach (var path in new[] { string.Empty, "/content", "/renditions/thumbnail" })
        {
            using var response = await erin.GetAsync(new Uri($"/api/v1/catalogs/{catalog.Id}/assets/{asset.Asset.Id}{path}", UriKind.Relative), Api.Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        await using var retry = fixture.Api.Services.CreateAsyncScope();
        await retry.ServiceProvider.GetRequiredService<PurgeDeletedAssets>().RunAsync(Retention, 100, Api.Ct);
        await AssertPurgedAsync(retry.ServiceProvider.GetRequiredService<IBlobStore>(), asset);
    }

    private async Task AssertPurgedAsync(IBlobStore blobs, Seeded seeded)
    {
        Assert.Null(await blobs.StatAsync(BlobKey.Parse(seeded.Asset.OriginalKey), Api.Ct));
        Assert.Null(await blobs.StatAsync(seeded.RenditionKey, Api.Ct));
        await using var db = fixture.CreateContext();
        Assert.Equal(AssetState.Deleted, await db.Assets.Where(a => a.Id == seeded.Asset.Id).Select(a => a.State).SingleAsync(Api.Ct));
        Assert.Equal(0, await db.Renditions.CountAsync(r => r.AssetId == seeded.Asset.Id, Api.Ct));
        Assert.Equal(0, await db.AssetCategories.CountAsync(c => c.AssetId == seeded.Asset.Id, Api.Ct));
    }

    private async Task AssertIntactAsync(IBlobStore blobs, Seeded seeded, AssetState state)
    {
        Assert.NotNull(await blobs.StatAsync(BlobKey.Parse(seeded.Asset.OriginalKey), Api.Ct));
        Assert.NotNull(await blobs.StatAsync(seeded.RenditionKey, Api.Ct));
        await using var db = fixture.CreateContext();
        Assert.Equal(state, await db.Assets.Where(a => a.Id == seeded.Asset.Id).Select(a => a.State).SingleAsync(Api.Ct));
        Assert.Equal(1, await db.Renditions.CountAsync(r => r.AssetId == seeded.Asset.Id, Api.Ct));
        Assert.Equal(1, await db.AssetCategories.CountAsync(c => c.AssetId == seeded.Asset.Id, Api.Ct));
    }

    /// <summary>An active asset with a stored original, a thumbnail and a category; optionally soft-deleted some days ago.</summary>
    private async Task<Seeded> SeedAsync(Catalog catalog, int? deletedDaysAgo)
    {
        var seeded = await Seed.AssetAsync(fixture, catalog);
        Rendition rendition;
        await using (var db = fixture.CreateContext())
        {
            var asset = await db.Assets.SingleAsync(a => a.Id == seeded.Id, Api.Ct);
            rendition = Rendition.Create(asset, "thumbnail", 1, "image/webp", 1, 1, 4, Seed.Sha, DateTimeOffset.UtcNow);
            var category = Category.CreateRoot(catalog, $"Purge {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            db.Renditions.Add(rendition);
            db.Categories.Add(category);
            db.AssetCategories.Add(AssetCategory.Assign(asset, category, DateTimeOffset.UtcNow));
            if (deletedDaysAgo is not null)
            {
                asset.MarkForDeletion(DateTimeOffset.UtcNow);
            }

            await db.SaveChangesAsync(Api.Ct);
            if (deletedDaysAgo is { } days)
            {
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE assets SET state_changed_at = now() - make_interval(days => {days}) WHERE id = {asset.Id}", Api.Ct);
            }
        }

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var renditionKey = BlobKey.Parse(rendition.StorageKey);
        await blobs.PutAsync(BlobKey.Parse(seeded.OriginalKey), new MemoryStream(new byte[4]), null, Api.Ct);
        await blobs.PutAsync(renditionKey, new MemoryStream(new byte[4]), null, Api.Ct);
        return new Seeded(seeded, renditionKey);
    }

    private sealed record Seeded(Asset Asset, BlobKey RenditionKey);

    /// <summary>Delegates to the real store but fails to delete one key, like an unavailable volume or bucket.</summary>
    private sealed class FailingDeletes(IBlobStore inner, BlobKey failing) : IBlobStore
    {
        public Task<StoredBlob> PutAsync(BlobKey key, Stream content, string? expectedSha256, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, expectedSha256, cancellationToken);

        public Task<BlobRead?> OpenAsync(BlobKey key, ByteRange? range, CancellationToken cancellationToken) => inner.OpenAsync(key, range, cancellationToken);

        public Task<BlobInfo?> StatAsync(BlobKey key, CancellationToken cancellationToken) => inner.StatAsync(key, cancellationToken);

        public Task<bool> DeleteAsync(BlobKey key, CancellationToken cancellationToken) =>
            key == failing ? throw new IOException("storage unavailable") : inner.DeleteAsync(key, cancellationToken);

        public IAsyncEnumerable<TemporaryBlob> ListTemporaryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) =>
            inner.ListTemporaryAsync(olderThan, cancellationToken);
    }
}
