using Microsoft.EntityFrameworkCore;
using Zirrus.Application;
using Zirrus.Application.Assets;
using Zirrus.Domain.Assets;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>Commits the asset lifecycle change and its audit record (staged by <c>IAuditLog</c>) together.</summary>
/// <param name="db">Database context.</param>
internal sealed class AssetDeletionStore(ZirrusDbContext db) : IAssetDeletionStore
{
    /// <inheritdoc />
    public async Task<OperationStatus> DeleteAsync(Guid catalogId, Guid assetId, int expectedRevision, string actor,
        string correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.CatalogId == catalogId && a.Id == assetId, cancellationToken);
        if (asset is null || !asset.IsVisibleTo(true))
        {
            return OperationStatus.NotFound;
        }

        if (asset.Revision != expectedRevision)
        {
            return OperationStatus.PreconditionFailed;
        }

        if (!AssetLifecycle.CanTransition(asset.State, AssetState.Deleting))
        {
            return OperationStatus.Conflict;
        }

        asset.MarkForDeletion(now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return OperationStatus.PreconditionFailed;
        }

        await transaction.CommitAsync(cancellationToken);
        return OperationStatus.Ok;
    }
}
