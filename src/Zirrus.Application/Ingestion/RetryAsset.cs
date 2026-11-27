using Zirrus.Application.Assets;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Assets;

namespace Zirrus.Application.Ingestion;

/// <summary>
/// Use case: an editor or administrator retries a failed asset, or a quarantined one whose scan job
/// was dead-lettered (e.g. the scanner was down). Viewers cannot retry.
/// </summary>
/// <param name="store">Ingestion writes.</param>
/// <param name="access">Catalog permissions.</param>
/// <param name="time">Time source.</param>
public sealed class RetryAsset(IIngestionStore store, CatalogAccess access, TimeProvider time)
{
    /// <summary>Requeues the stage that stopped.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The asset, or NotFound, Forbidden or Conflict.</returns>
    public async Task<OperationResult<AssetDetails>> ExecuteAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.NotFound, "Asset not found.");
        }

        var canEdit = await access.CanEditAsync(catalogId, cancellationToken);
        var asset = await store.FindAssetAsync(catalogId, assetId, cancellationToken);
        if (asset is null || !asset.IsVisibleTo(canEdit))
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.NotFound, "Asset not found.");
        }

        if (!canEdit)
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.Forbidden, "Retrying requires the editor role.");
        }

        var stage = asset.State switch
        {
            AssetState.Failed => PipelineStages.Process,
            AssetState.Quarantined => PipelineStages.Scan,
            _ => null,
        };
        var key = stage is null ? null : JobIntent.ForAsset(asset, stage, PipelineStages.Version, time.GetUtcNow()).JobKey;
        if (key is null || !await store.RequeueDeadJobAsync(key, cancellationToken))
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.Conflict, "Only failed assets or quarantined assets with a stopped scan can be retried.");
        }

        if (asset.State == AssetState.Failed)
        {
            asset.BeginProcessing(time.GetUtcNow());
            await store.SaveChangesAsync(cancellationToken);
        }

        return OperationResult.Ok(AssetDetails.From(asset));
    }
}
