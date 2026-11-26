using System.Text.Json;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Assets;

namespace Zirrus.Application.Assets;

/// <summary>Asset as returned by the API.</summary>
/// <param name="Id">Asset identifier.</param>
/// <param name="CatalogId">Catalog.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="MediaType">Media type.</param>
/// <param name="SizeBytes">Size of the original, once stored.</param>
/// <param name="Sha256">SHA-256 of the original, once stored.</param>
/// <param name="State">Lifecycle state in lowercase, e.g. <c>active</c>.</param>
/// <param name="StatusReason">Reason for rejected or failed assets.</param>
/// <param name="Metadata">User metadata as a JSON object.</param>
/// <param name="Revision">Current revision; the ETag is its quoted form.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last change (UTC).</param>
public sealed record AssetDetails(
    Guid Id,
    Guid CatalogId,
    string FileName,
    string MediaType,
    long? SizeBytes,
    string? Sha256,
    string State,
    string? StatusReason,
    JsonElement Metadata,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Maps an asset to the read model.</summary>
    /// <param name="asset">Asset.</param>
    /// <returns>The details.</returns>
    public static AssetDetails From(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new(asset.Id, asset.CatalogId, asset.FileName, asset.MediaType, asset.SizeBytes, asset.Sha256,
            asset.State.ToString().ToLowerInvariant(), asset.StatusReason, ParseObject(asset.Metadata), asset.Revision, asset.CreatedAt, asset.UpdatedAt);
    }

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>Port for loading and saving assets; implemented by the infrastructure layer.</summary>
public interface IAssetStore
{
    /// <summary>Finds an asset in a catalog.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The tracked asset, or null.</returns>
    Task<Asset?> FindAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken);

    /// <summary>Commits changes to tracked assets, checking the revision each was loaded with.</summary>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>False when another writer changed the row first (optimistic concurrency).</returns>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Asset reads and metadata edits with catalog permission checks. Viewers see only active assets;
/// a hidden asset is reported as not found so its existence does not leak.
/// </summary>
/// <param name="assets">Asset port.</param>
/// <param name="access">Catalog permission checks.</param>
/// <param name="time">Time source.</param>
public sealed class AssetUseCases(IAssetStore assets, CatalogAccess access, TimeProvider time)
{
    private const string NotFound = "Asset not found.";

    /// <summary>Gets an asset the caller may see.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The asset or NotFound.</returns>
    public async Task<OperationResult<AssetDetails>> GetAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.NotFound, NotFound);
        }

        var canEdit = await access.CanEditAsync(catalogId, cancellationToken);
        var asset = await assets.FindAsync(catalogId, assetId, cancellationToken);
        return asset is not null && asset.IsVisibleTo(canEdit)
            ? OperationResult.Ok(AssetDetails.From(asset))
            : OperationResult.Fail<AssetDetails>(OperationStatus.NotFound, NotFound);
    }

    /// <summary>
    /// Replaces an asset's user metadata. The edit is based on <paramref name="expectedRevision"/>
    /// (If-Match); if the asset changed since, nothing is written and the result is PreconditionFailed.
    /// </summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="metadataJson">New metadata as a JSON object.</param>
    /// <param name="expectedRevision">Revision the client edited.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The updated asset, or NotFound, Forbidden, Invalid, Conflict or PreconditionFailed.</returns>
    public async Task<OperationResult<AssetDetails>> UpdateMetadataAsync(
        Guid catalogId, Guid assetId, string metadataJson, int expectedRevision, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.NotFound, NotFound);
        }

        var canEdit = await access.CanEditAsync(catalogId, cancellationToken);
        var asset = await assets.FindAsync(catalogId, assetId, cancellationToken);
        if (asset is null || !asset.IsVisibleTo(canEdit))
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.NotFound, NotFound);
        }

        if (!canEdit)
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.Forbidden, "Editing requires the editor role.");
        }

        AssetMetadata metadata;
        try
        {
            metadata = AssetMetadata.Parse(metadataJson);
        }
        catch (ArgumentException e)
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.Invalid, e.Message);
        }

        try
        {
            asset.UpdateMetadata(metadata, expectedRevision, time.GetUtcNow());
        }
        catch (RevisionMismatchException e)
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.PreconditionFailed, e.Message);
        }
        catch (InvalidOperationException e)
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.Conflict, e.Message);
        }

        // The revision check above covers stale clients; the store's concurrency token covers two
        // writers that loaded the same revision at the same time.
        return await assets.TrySaveChangesAsync(cancellationToken)
            ? OperationResult.Ok(AssetDetails.From(asset))
            : OperationResult.Fail<AssetDetails>(OperationStatus.PreconditionFailed, "The asset was changed by another request.");
    }
}
