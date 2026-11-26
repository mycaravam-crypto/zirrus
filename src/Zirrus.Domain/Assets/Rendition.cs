namespace Zirrus.Domain.Assets;

/// <summary>
/// A derived file of an asset (thumbnail, preview), produced by a versioned pipeline. One row per
/// asset and kind, so retried renders replace rather than duplicate (ADR 0002 idempotent effects).
/// </summary>
public sealed class Rendition
{
    /// <summary>Maximum length of a rendition kind.</summary>
    public const int MaxKindLength = 32;

    // Materialization constructor for the persistence layer.
    private Rendition()
    {
        Kind = StorageKey = MediaType = Sha256 = string.Empty;
    }

    private Rendition(Asset asset, string kind, int pipelineVersion, string mediaType, int width, int height, long sizeBytes, string sha256, DateTimeOffset now)
    {
        CatalogId = asset.CatalogId;
        AssetId = asset.Id;
        Kind = kind;
        PipelineVersion = pipelineVersion;
        StorageKey = BlobKey.ForRendition(asset.CatalogId, asset.Id, kind, pipelineVersion).Value;
        MediaType = mediaType;
        Width = width;
        Height = height;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        CreatedAt = now;
    }

    /// <summary>Catalog of the asset (composite reference).</summary>
    public Guid CatalogId { get; private set; }

    /// <summary>Asset the rendition belongs to.</summary>
    public Guid AssetId { get; private set; }

    /// <summary>Kind, e.g. <c>thumbnail</c> or <c>preview</c>.</summary>
    public string Kind { get; private set; }

    /// <summary>Version of the pipeline that produced it.</summary>
    public int PipelineVersion { get; private set; }

    /// <summary>Storage key.</summary>
    public string StorageKey { get; private set; }

    /// <summary>Media type of the rendition.</summary>
    public string MediaType { get; private set; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; private set; }

    /// <summary>Lowercase hex SHA-256.</summary>
    public string Sha256 { get; private set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Records a stored rendition.</summary>
    /// <param name="asset">Source asset.</param>
    /// <param name="kind">Kind: lowercase letters, digits and '-'.</param>
    /// <param name="pipelineVersion">Pipeline version (≥ 1).</param>
    /// <param name="mediaType">Media type of the output.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="sizeBytes">Stored size.</param>
    /// <param name="sha256">Lowercase hex SHA-256 from the store.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The rendition.</returns>
    public static Rendition Create(Asset asset, string kind, int pipelineVersion, string mediaType, int width, int height, long sizeBytes, string sha256, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(mediaType);
        ValidateKind(kind);
        ArgumentOutOfRangeException.ThrowIfLessThan(pipelineVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        if (!Asset.IsSha256Hex(sha256))
        {
            throw new ArgumentException("SHA-256 must be 64 lowercase hex characters.", nameof(sha256));
        }

        return new Rendition(asset, kind, pipelineVersion, mediaType, width, height, sizeBytes, sha256, now);
    }

    /// <summary>Validates a rendition kind.</summary>
    /// <param name="kind">Kind to check.</param>
    /// <exception cref="ArgumentException">The kind is empty, too long or uses other characters than <c>[a-z0-9-]</c>.</exception>
    public static void ValidateKind(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (kind.Length == 0 || kind.Length > MaxKindLength || !kind.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            throw new ArgumentException($"Rendition kind must be 1–{MaxKindLength} characters of [a-z0-9-].", nameof(kind));
        }
    }
}
