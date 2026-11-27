using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zirrus.Application.Assets;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IAssetStore"/>.</summary>
/// <param name="db">Database context.</param>
internal sealed class AssetStore(ZirrusDbContext db) : IAssetStore
{
    /// <inheritdoc />
    public Task<Asset?> FindAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
        db.Assets.SingleOrDefaultAsync(a => a.CatalogId == catalogId && a.Id == assetId, cancellationToken);

    /// <inheritdoc />
    public Task<MetadataSchema?> FindMetadataSchemaAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
        db.Set<MetadataSchema>().AsNoTracking().Include(s => s.Fields)
            .Where(s => s.OrganizationId == organizationId && s.CatalogId == catalogId)
            .OrderByDescending(s => s.Version).FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Rendition?> FindRenditionAsync(Guid assetId, string kind, CancellationToken cancellationToken) =>
        db.Renditions.AsNoTracking().SingleOrDefaultAsync(r => r.AssetId == assetId && r.Kind == kind, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}

/// <summary>Reads asset, rendition and job rows for reconciliation (no tracking, batched scans).</summary>
/// <param name="db">Database context.</param>
internal sealed class StorageInventory(ZirrusDbContext db) : IStorageInventory
{
    private const int BatchSize = 500;

    /// <inheritdoc />
    public async IAsyncEnumerable<ExpectedBlob> ListExpectedBlobsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var states = AssetLifecycle.StatesWithOriginal.ToArray();
        Guid? after = null;
        while (true)
        {
            // Keyset pagination by id keeps memory flat however many assets exist.
            var query = db.Assets.AsNoTracking().Where(a => states.Contains(a.State));
            if (after is { } last)
            {
                query = query.Where(a => a.Id.CompareTo(last) > 0);
            }

            var batch = await query.OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.OriginalKey, a.SizeBytes, a.State })
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                yield break;
            }

            var ids = batch.Select(a => a.Id).ToList();
            var renditions = await db.Renditions.AsNoTracking()
                .Where(r => ids.Contains(r.AssetId))
                .Select(r => new { r.AssetId, r.StorageKey, r.SizeBytes, r.Kind })
                .ToListAsync(cancellationToken);
            var byAsset = renditions.ToLookup(r => r.AssetId);

            foreach (var asset in batch)
            {
                yield return new ExpectedBlob(asset.Id, BlobKey.Parse(asset.OriginalKey), asset.SizeBytes, "original", asset.State);
                foreach (var rendition in byAsset[asset.Id])
                {
                    yield return new ExpectedBlob(asset.Id, BlobKey.Parse(rendition.StorageKey), rendition.SizeBytes, rendition.Kind, asset.State);
                }
            }

            after = batch[^1].Id;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StaleAsset>> ListStaleAsync(DateTimeOffset changedBefore, int limit, CancellationToken cancellationToken)
    {
        var states = AssetLifecycle.TransitionalStates.ToArray();
        var rows = await db.Assets.AsNoTracking()
            .Where(a => states.Contains(a.State) && a.StateChangedAt < changedBefore)
            .OrderBy(a => a.StateChangedAt)
            .Take(limit)
            .Select(a => new { a.Id, a.State, a.StateChangedAt })
            .ToListAsync(cancellationToken);
        return rows.Select(a => new StaleAsset(a.Id, a.State, a.StateChangedAt)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiredLease>> ListExpiredLeasesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Running && j.LeaseUntil < now)
            .OrderBy(j => j.LeaseUntil)
            .Take(limit)
            .Select(j => new { j.JobKey, j.LockedBy, j.LeaseUntil })
            .ToListAsync(cancellationToken);
        return rows.Select(j => new ExpiredLease(j.JobKey, j.LockedBy, j.LeaseUntil!.Value)).ToList();
    }
}
