using System.Text.Json;
using Zirrus.Application.Catalogs;
using Zirrus.Application.Storage;
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

/// <summary>An asset in a list (grid view).</summary>
/// <param name="Id">Asset identifier.</param>
/// <param name="FileName">Original file name.</param>
/// <param name="MediaType">Media type.</param>
/// <param name="SizeBytes">Size of the original, once stored.</param>
/// <param name="State">Lifecycle state in lowercase.</param>
/// <param name="StatusReason">Reason for rejected or failed assets.</param>
/// <param name="CreatedAt">Upload time (UTC).</param>
/// <param name="UpdatedAt">Last change (UTC).</param>
public sealed record AssetSummary(Guid Id, string FileName, string MediaType, long? SizeBytes, string State, string? StatusReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>One page of a catalog's assets, newest first.</summary>
/// <param name="Items">Assets on this page.</param>
/// <param name="NextCursor">Opaque cursor for the next page, or null at the end.</param>
public sealed record AssetPage(IReadOnlyList<AssetSummary> Items, string? NextCursor);

/// <summary>Keyset position in a newest-first asset list.</summary>
/// <param name="CreatedAt">Creation time of the last item seen.</param>
/// <param name="Id">ID of the last item seen (tie-breaker).</param>
public readonly record struct AssetListPosition(DateTimeOffset CreatedAt, Guid Id)
{
    /// <summary>Encodes the position as an opaque URL-safe cursor.</summary>
    /// <returns>The cursor.</returns>
    public string Encode() =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{CreatedAt.UtcTicks}:{Id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes a cursor produced by <see cref="Encode"/>.</summary>
    /// <param name="cursor">Cursor text.</param>
    /// <param name="position">Decoded position.</param>
    /// <returns>Whether the cursor was valid.</returns>
    public static bool TryDecode(string? cursor, out AssetListPosition position)
    {
        position = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 100)
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && Guid.TryParseExact(parts[1], "N", out var id))
            {
                position = new AssetListPosition(new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}

/// <summary>Port for loading and saving assets; implemented by the infrastructure layer.</summary>
public interface IAssetStore
{
    /// <summary>Loads an asset for change tracking, matching both catalog and ID so a foreign catalog ID never resolves it.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The tracked asset, or null.</returns>
    Task<Asset?> FindAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken);

    /// <summary>Lists a catalog's assets newest first, excluding deleted ones.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="activeOnly">Only active assets (viewers).</param>
    /// <param name="after">Keyset position to continue after, or null for the first page.</param>
    /// <param name="limit">Maximum rows.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<AssetSummary>> ListAsync(Guid catalogId, bool activeOnly, AssetListPosition? after, int limit, CancellationToken cancellationToken);

    /// <summary>Loads the latest immutable metadata schema for the asset's organization and catalog.</summary>
    /// <param name="organizationId">Organization.</param>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The current schema, or null if none is configured.</returns>
    Task<MetadataSchema?> FindMetadataSchemaAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken);

    /// <summary>Finds a rendition of an asset.</summary>
    /// <param name="assetId">Asset.</param>
    /// <param name="kind">Rendition kind.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The rendition, or null.</returns>
    Task<Rendition?> FindRenditionAsync(Guid assetId, string kind, CancellationToken cancellationToken);

    /// <summary>Commits changes to tracked assets, checking the revision each was loaded with.</summary>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>False when another writer changed the row first (optimistic concurrency).</returns>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>An open download: content, its type and a file name for <c>Content-Disposition</c>.</summary>
/// <param name="Read">Blob read (possibly a range); the caller disposes it.</param>
/// <param name="MediaType">Content type.</param>
/// <param name="FileName">Download file name.</param>
public sealed record AssetContent(BlobRead Read, string MediaType, string FileName);

/// <summary>
/// Asset reads and metadata edits with catalog permission checks. Viewers see only active assets;
/// a hidden asset is reported as not found so its existence does not leak.
/// </summary>
/// <param name="assets">Asset port.</param>
/// <param name="access">Catalog permission checks.</param>
/// <param name="time">Time source.</param>
/// <param name="blobs">Blob store for downloads.</param>
public sealed class AssetUseCases(IAssetStore assets, CatalogAccess access, TimeProvider time, IBlobStore blobs)
{
    private const string NotFound = "Asset not found.";

    /// <summary>Default page size (ingestion-api-ui.md REST conventions).</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maximum page size.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Lists the assets of a catalog the caller may see: active ones for viewers, all non-deleted ones for editors.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="cursor">Cursor from the previous page, or null.</param>
    /// <param name="limit">Page size (1–200; default 50).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The page, or NotFound or Invalid.</returns>
    public async Task<OperationResult<AssetPage>> ListAsync(Guid catalogId, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<AssetPage>(OperationStatus.NotFound, "Catalog not found.");
        }

        var size = limit ?? DefaultPageSize;
        if (size is < 1 or > MaxPageSize)
        {
            return OperationResult.Fail<AssetPage>(OperationStatus.Invalid, $"limit must be 1–{MaxPageSize}.");
        }

        AssetListPosition? after = null;
        if (cursor is not null)
        {
            if (!AssetListPosition.TryDecode(cursor, out var position))
            {
                return OperationResult.Fail<AssetPage>(OperationStatus.Invalid, "Invalid cursor.");
            }

            after = position;
        }

        var canEdit = await access.CanEditAsync(catalogId, cancellationToken);
        var rows = await assets.ListAsync(catalogId, activeOnly: !canEdit, after, size + 1, cancellationToken);
        var items = rows.Take(size).ToList();
        var next = rows.Count > size ? new AssetListPosition(items[^1].CreatedAt, items[^1].Id).Encode() : null;
        return OperationResult.Ok(new AssetPage(items, next));
    }

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

        if (expectedRevision != asset.Revision)
        {
            return OperationResult.Fail<AssetDetails>(OperationStatus.PreconditionFailed, "The asset revision is stale.");
        }

        AssetMetadata metadata;
        try
        {
            metadata = AssetMetadata.Parse(metadataJson);
            var schema = await assets.FindMetadataSchemaAsync(asset.OrganizationId, catalogId, cancellationToken);
            if (schema is not null)
            {
                MetadataValidation.Validate(metadata, schema);
            }
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

    /// <summary>Opens the original of an active asset, optionally a byte range (HTTP Range).</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="range">Requested range, or null.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The content, or NotFound; only active assets are ever served (ADR 0006).</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is not satisfiable.</exception>
    public async Task<OperationResult<AssetContent>> OpenOriginalAsync(Guid catalogId, Guid assetId, ByteRange? range, CancellationToken cancellationToken)
    {
        var asset = await FindActiveAsync(catalogId, assetId, cancellationToken);
        var read = asset is null ? null : await blobs.OpenAsync(BlobKey.Parse(asset.OriginalKey), range, cancellationToken);
        return read is null
            ? OperationResult.Fail<AssetContent>(OperationStatus.NotFound, NotFound)
            : OperationResult.Ok(new AssetContent(read, asset!.MediaType, asset.FileName));
    }

    /// <summary>Opens a rendition of an active asset.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="kind">Rendition kind, e.g. <c>thumbnail</c>.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The content, or NotFound.</returns>
    public async Task<OperationResult<AssetContent>> OpenRenditionAsync(Guid catalogId, Guid assetId, string kind, CancellationToken cancellationToken)
    {
        var asset = await FindActiveAsync(catalogId, assetId, cancellationToken);
        var rendition = asset is null ? null : await assets.FindRenditionAsync(asset.Id, kind, cancellationToken);
        var read = rendition is null ? null : await blobs.OpenAsync(BlobKey.Parse(rendition.StorageKey), null, cancellationToken);
        if (read is null)
        {
            return OperationResult.Fail<AssetContent>(OperationStatus.NotFound, "Rendition not found.");
        }

        var extension = rendition!.MediaType == "image/webp" ? "webp" : "jpg";
        return OperationResult.Ok(new AssetContent(read, rendition.MediaType, $"{Path.GetFileNameWithoutExtension(asset!.FileName)}-{kind}.{extension}"));
    }

    private async Task<Asset?> FindActiveAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return null;
        }

        var asset = await assets.FindAsync(catalogId, assetId, cancellationToken);
        return asset?.State == AssetState.Active ? asset : null;
    }
}
