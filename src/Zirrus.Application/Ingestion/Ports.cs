using Zirrus.Domain.Assets;

namespace Zirrus.Application.Ingestion;

/// <summary>A job claimed by a worker, valid while its lease has not expired.</summary>
/// <param name="Id">Database identifier.</param>
/// <param name="JobKey">Deterministic key.</param>
/// <param name="Stage">Pipeline stage.</param>
/// <param name="CatalogId">Catalog of the asset.</param>
/// <param name="AssetId">Asset.</param>
/// <param name="Attempt">Attempt number, starting at 1.</param>
/// <param name="MaxAttempts">Attempts allowed before dead-lettering.</param>
/// <param name="LeaseToken">Proves this worker still holds the lease.</param>
public sealed record ClaimedJob(long Id, string JobKey, string Stage, Guid? CatalogId, Guid? AssetId, int Attempt, int MaxAttempts, Guid LeaseToken)
{
    /// <summary>True when a failure now would dead-letter the job.</summary>
    public bool IsLastAttempt => Attempt >= MaxAttempts;
}

/// <summary>
/// Durable job queue (ADR 0002): PostgreSQL rows claimed with <c>FOR UPDATE SKIP LOCKED</c> under
/// leases. Dispatch is at least once, so stage handlers must be idempotent.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "IJobQueue is the port name used in ADR 0002.")]
public interface IJobQueue
{
    /// <summary>Claims the next ready job (or one whose lease expired).</summary>
    /// <param name="workerId">Claiming worker, for diagnostics.</param>
    /// <param name="lease">Lease duration.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>The job, or null when none is ready.</returns>
    Task<ClaimedJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Extends a lease; false if it was lost.</summary>
    /// <param name="job">Claimed job.</param>
    /// <param name="lease">New duration from now.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <returns>Whether the lease is still held.</returns>
    Task<bool> RenewAsync(ClaimedJob job, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failure: transient failures retry with exponential backoff until the attempt limit,
    /// permanent ones and exhausted retries are dead-lettered.
    /// </summary>
    /// <param name="job">Claimed job.</param>
    /// <param name="errorDetail">Error category and message (no secrets or raw metadata).</param>
    /// <param name="permanent">True when retrying cannot help.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    Task FailAsync(ClaimedJob job, string errorDetail, bool permanent, CancellationToken cancellationToken);
}

/// <summary>Transactional writes of the ingestion pipeline.</summary>
public interface IIngestionStore
{
    /// <summary>Loads an asset for a stage.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The tracked asset, or null.</returns>
    Task<Asset?> FindAssetAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken);

    /// <summary>Stages a new asset.</summary>
    /// <param name="asset">Asset.</param>
    void Add(Asset asset);

    /// <summary>Stages job intent; an existing row with the same key is kept (idempotent re-dispatch).</summary>
    /// <param name="job">Job intent.</param>
    void Add(JobIntent job);

    /// <summary>Stages a rendition, replacing an existing one of the same kind.</summary>
    /// <param name="rendition">Rendition.</param>
    void AddOrReplace(Rendition rendition);

    /// <summary>Commits staged changes in one transaction.</summary>
    /// <param name="cancellationToken">Cancels the save.</param>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Commits staged changes and marks the job done in one transaction, only while the lease is
    /// held. If the lease was lost, nothing is committed (another worker owns the job now).
    /// </summary>
    /// <param name="job">Claimed job.</param>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>False when the lease was lost.</returns>
    Task<bool> CompleteAsync(ClaimedJob job, CancellationToken cancellationToken);

    /// <summary>IDs of other non-deleted assets in the catalog with the same checksum.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="sha256">Checksum.</param>
    /// <param name="exceptAssetId">The asset itself.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Duplicate asset IDs.</returns>
    Task<IReadOnlyList<Guid>> FindDuplicatesAsync(Guid catalogId, string sha256, Guid exceptAssetId, CancellationToken cancellationToken);

    /// <summary>Makes a dead-lettered job of an asset claimable again with fresh attempts.</summary>
    /// <param name="jobKey">Job key.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>False when no dead job has this key.</returns>
    Task<bool> RequeueDeadJobAsync(string jobKey, CancellationToken cancellationToken);
}

/// <summary>Outcome of a malware scan.</summary>
/// <param name="Clean">True when no threat was found.</param>
/// <param name="Signature">Detected signature name when infected.</param>
public sealed record ScanResult(bool Clean, string? Signature = null);

/// <summary>The scanner could not be reached or answered with an error; the asset stays quarantined.</summary>
public sealed class ScannerUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ScannerUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Message.</param>
    public ScannerUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public ScannerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Malware scanner port (ClamAV in production). Fail-closed: errors throw <see cref="ScannerUnavailableException"/>.</summary>
public interface IMalwareScanner
{
    /// <summary>Scans content.</summary>
    /// <param name="content">Stream to scan.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The result.</returns>
    Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>One requested rendition.</summary>
/// <param name="Kind">Rendition kind.</param>
/// <param name="LongestEdge">Maximum longest edge in pixels; never upscaled.</param>
/// <param name="MediaType">Output type: <c>image/jpeg</c> or <c>image/webp</c>.</param>
/// <param name="Quality">Encoder quality 1–100.</param>
public sealed record RenditionSpec(string Kind, int LongestEdge, string MediaType, int Quality)
{
    /// <summary>Thumbnail and preview from ingestion-api-ui.md.</summary>
    public static IReadOnlyList<RenditionSpec> Default { get; } =
    [
        new("thumbnail", 320, "image/webp", 80),
        new("preview", 1600, "image/jpeg", 85),
    ];
}

/// <summary>An encoded rendition.</summary>
/// <param name="Spec">What was requested.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Content">Encoded bytes.</param>
public sealed record RenderedImage(RenditionSpec Spec, int Width, int Height, ReadOnlyMemory<byte> Content);

/// <summary>The file cannot be decoded within the limits; the asset is rejected (permanent).</summary>
public sealed class UnsupportedImageException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedImageException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Message.</param>
    public UnsupportedImageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public UnsupportedImageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Renderer port (SkiaSharp in the MVP). Implementations enforce <see cref="MediaFormat.MaxPixels"/>.</summary>
public interface IImageRenderer
{
    /// <summary>Decodes an image once and encodes every requested rendition, applying EXIF orientation.</summary>
    /// <param name="source">Original image.</param>
    /// <param name="specs">Renditions to produce.</param>
    /// <param name="cancellationToken">Cancels rendering.</param>
    /// <returns>The renditions, in request order.</returns>
    /// <exception cref="UnsupportedImageException">Corrupt, unsupported or oversize image.</exception>
    Task<IReadOnlyList<RenderedImage>> RenderAsync(Stream source, IReadOnlyList<RenditionSpec> specs, CancellationToken cancellationToken);
}

/// <summary>
/// Embedded-metadata port (EXIF/IPTC/XMP). The MVP default extracts nothing until the
/// MetadataExtractor dependency clears its license review (#20).
/// </summary>
public interface IMetadataExtractor
{
    /// <summary>Extracts metadata as a flat JSON object with lowercase keys.</summary>
    /// <param name="source">Original image.</param>
    /// <param name="cancellationToken">Cancels extraction.</param>
    /// <returns>The metadata, or null when nothing is extracted.</returns>
    /// <exception cref="UnsupportedImageException">The file's metadata segments are malformed.</exception>
    Task<AssetMetadata?> ExtractAsync(Stream source, CancellationToken cancellationToken);
}

/// <summary>Extracts nothing; used until #20 approves MetadataExtractor.</summary>
public sealed class NoMetadataExtractor : IMetadataExtractor
{
    /// <inheritdoc />
    public Task<AssetMetadata?> ExtractAsync(Stream source, CancellationToken cancellationToken) => Task.FromResult<AssetMetadata?>(null);
}
