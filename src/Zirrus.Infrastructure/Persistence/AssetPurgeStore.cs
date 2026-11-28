using Microsoft.EntityFrameworkCore;
using Zirrus.Application.Assets;
using Zirrus.Application.Audit;
using Zirrus.Domain.Assets;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>Retention purge persistence: lists expired soft-deleted assets and completes their purge atomically.</summary>
/// <param name="db">Database context.</param>
/// <param name="audit">Audit log; the purge event is committed with the state change.</param>
internal sealed class AssetPurgeStore(ZirrusDbContext db, IAuditLog audit) : IAssetPurgeStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<PurgeCandidate>> ListExpiredAsync(DateTimeOffset deletedBefore, int limit, CancellationToken cancellationToken)
    {
        var assets = await db.Assets.AsNoTracking()
            .Where(a => a.State == AssetState.Deleting && a.StateChangedAt < deletedBefore)
            .OrderBy(a => a.StateChangedAt).ThenBy(a => a.Id)
            .Take(limit)
            .Select(a => new { a.Id, a.CatalogId, a.OriginalKey })
            .ToListAsync(cancellationToken);
        var ids = assets.Select(a => a.Id).ToList();
        var renditions = (await db.Renditions.AsNoTracking()
                .Where(r => ids.Contains(r.AssetId))
                .Select(r => new { r.AssetId, r.StorageKey })
                .ToListAsync(cancellationToken))
            .ToLookup(r => r.AssetId, r => BlobKey.Parse(r.StorageKey));
        return assets.Select(a => new PurgeCandidate(a.Id, a.CatalogId, BlobKey.Parse(a.OriginalKey), renditions[a.Id].ToList())).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(PurgeCandidate candidate, DateTimeOffset deletedBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock the row until commit, so a concurrent purge run cannot complete the same asset twice.
        var asset = (await db.Assets
            .FromSqlInterpolated($"SELECT * FROM assets WHERE catalog_id = {candidate.CatalogId} AND id = {candidate.AssetId} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        if (asset is null || asset.State != AssetState.Deleting || asset.StateChangedAt >= deletedBefore)
        {
            return false;
        }

        var renditions = await db.Renditions.Where(r => r.CatalogId == asset.CatalogId && r.AssetId == asset.Id).ExecuteDeleteAsync(cancellationToken);
        var links = await db.AssetCategories.Where(c => c.CatalogId == asset.CatalogId && c.AssetId == asset.Id).ExecuteDeleteAsync(cancellationToken);
        asset.MarkDeleted(now);
        audit.Record(new AuditEntry(AuditActions.Purge, PurgeDeletedAssets.Actor, asset.OrganizationId, asset.CatalogId, asset.Id,
            $"renditions={renditions} categories={links}"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
