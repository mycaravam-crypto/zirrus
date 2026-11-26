using Zirrus.Domain.Catalogs;

namespace Zirrus.Domain.Assets;

/// <summary>The revision supplied with an update is not the asset's current revision.</summary>
public sealed class RevisionMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RevisionMismatchException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Message.</param>
    public RevisionMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public RevisionMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A digital asset in a catalog: an immutable original referenced by an opaque key, its integrity
/// data, lifecycle state and metadata (data-storage-search.md, ADR 0006).
/// </summary>
public sealed class Asset
{
    /// <summary>Maximum length of the original file name.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>Maximum length of a media type.</summary>
    public const int MaxMediaTypeLength = 127;

    /// <summary>Maximum length of a failure or rejection reason.</summary>
    public const int MaxReasonLength = 500;

    // Materialization constructor for the persistence layer.
    private Asset()
    {
        OriginalKey = FileName = MediaType = CreatedBy = Metadata = string.Empty;
    }

    private Asset(Guid id, Guid organizationId, Guid catalogId, string fileName, string mediaType, string createdBy, DateTimeOffset now)
    {
        Id = id;
        OrganizationId = organizationId;
        CatalogId = catalogId;
        OriginalKey = BlobKey.ForOriginal(catalogId, id).Value;
        FileName = fileName;
        MediaType = mediaType;
        CreatedBy = createdBy;
        Metadata = AssetMetadata.Empty.Json;
        CreatedAt = now;
        UpdatedAt = now;
        StateChangedAt = now;
        State = AssetState.Uploading;
        Revision = 1;
    }

    /// <summary>Opaque asset identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning organization (composite reference to the catalog).</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Owning catalog; part of every authorization check.</summary>
    public Guid CatalogId { get; private set; }

    /// <summary>Storage key of the original; generated, never derived from the file name.</summary>
    public string OriginalKey { get; private set; }

    /// <summary>File name as uploaded, for display and downloads only.</summary>
    public string FileName { get; private set; }

    /// <summary>Media type determined at upload.</summary>
    public string MediaType { get; private set; }

    /// <summary>Size of the original in bytes, known once stored.</summary>
    public long? SizeBytes { get; private set; }

    /// <summary>Lowercase hex SHA-256 of the original, known once stored.</summary>
    public string? Sha256 { get; private set; }

    /// <summary>Current lifecycle state.</summary>
    public AssetState State { get; private set; }

    /// <summary>When the state last changed; transitional states older than a threshold are reported.</summary>
    public DateTimeOffset StateChangedAt { get; private set; }

    /// <summary>Error category and message for rejected or failed assets.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>User-editable metadata (JSONB object).</summary>
    public string Metadata { get; private set; }

    /// <summary>Metadata extracted from the file (EXIF/IPTC/XMP), kept apart from user edits.</summary>
    public string? EmbeddedMetadata { get; private set; }

    /// <summary>Optimistic concurrency revision; the API exposes it as the ETag.</summary>
    public int Revision { get; private set; }

    /// <summary>Subject of the uploading user.</summary>
    public string CreatedBy { get; private set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Last change (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Starts an upload into a catalog.</summary>
    /// <param name="catalog">Target catalog.</param>
    /// <param name="fileName">Original file name; directory parts are stripped.</param>
    /// <param name="mediaType">Media type, e.g. <c>image/jpeg</c>.</param>
    /// <param name="createdBy">Subject of the uploading user.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The asset in <see cref="AssetState.Uploading"/>.</returns>
    /// <exception cref="ArgumentException">A value is empty, too long or contains control characters.</exception>
    public static Asset Start(Catalog catalog, string fileName, string mediaType, string createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(createdBy);

        var name = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        if (name.Length == 0 || name.Length > MaxFileNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException($"File name must be 1–{MaxFileNameLength} characters without control characters.", nameof(fileName));
        }

        if (mediaType.Length == 0 || mediaType.Length > MaxMediaTypeLength || !mediaType.Contains('/', StringComparison.Ordinal) || mediaType.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("Media type must look like 'type/subtype'.", nameof(mediaType));
        }

        if (createdBy.Length == 0 || createdBy.Length > CatalogMembership.MaxSubjectLength)
        {
            throw new ArgumentException("Creator subject is required.", nameof(createdBy));
        }

        return new Asset(Guid.NewGuid(), catalog.OrganizationId, catalog.Id, name, mediaType.ToLowerInvariant(), createdBy, now);
    }

    /// <summary>Records the stored original's checksum and size, moving the asset to quarantine.</summary>
    /// <param name="sha256">Lowercase hex SHA-256 computed while storing.</param>
    /// <param name="sizeBytes">Stored size.</param>
    /// <param name="now">Current time.</param>
    public void MarkStored(string sha256, long sizeBytes, DateTimeOffset now)
    {
        if (!IsSha256Hex(sha256))
        {
            throw new ArgumentException("SHA-256 must be 64 lowercase hex characters.", nameof(sha256));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        TransitionTo(AssetState.Quarantined, null, now);
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    /// <summary>Starts or retries processing.</summary>
    /// <param name="now">Current time.</param>
    public void BeginProcessing(DateTimeOffset now) => TransitionTo(AssetState.Processing, null, now);

    /// <summary>Publishes the processed asset.</summary>
    /// <param name="now">Current time.</param>
    public void Activate(DateTimeOffset now) => TransitionTo(AssetState.Active, null, now);

    /// <summary>Rejects the asset (validation or malware); it is never served.</summary>
    /// <param name="reason">Error category and message.</param>
    /// <param name="now">Current time.</param>
    public void Reject(string reason, DateTimeOffset now) => TransitionTo(AssetState.Rejected, RequireReason(reason), now);

    /// <summary>Marks processing or upload as failed; editors may retry processing.</summary>
    /// <param name="reason">Error category and message.</param>
    /// <param name="now">Current time.</param>
    public void Fail(string reason, DateTimeOffset now) => TransitionTo(AssetState.Failed, RequireReason(reason), now);

    /// <summary>Soft-deletes the asset; access is denied from now on.</summary>
    /// <param name="now">Current time.</param>
    public void MarkForDeletion(DateTimeOffset now) => TransitionTo(AssetState.Deleting, null, now);

    /// <summary>Records that the asset's blobs were purged.</summary>
    /// <param name="now">Current time.</param>
    public void MarkDeleted(DateTimeOffset now) => TransitionTo(AssetState.Deleted, null, now);

    /// <summary>Replaces user metadata if the caller saw the current revision.</summary>
    /// <param name="metadata">New metadata.</param>
    /// <param name="expectedRevision">Revision the caller based the edit on (from If-Match).</param>
    /// <param name="now">Current time.</param>
    /// <exception cref="RevisionMismatchException">The revision is stale.</exception>
    /// <exception cref="InvalidOperationException">The asset is being deleted.</exception>
    public void UpdateMetadata(AssetMetadata metadata, int expectedRevision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (expectedRevision != Revision)
        {
            throw new RevisionMismatchException($"Expected revision {expectedRevision}, current revision is {Revision}.");
        }

        if (State is AssetState.Deleting or AssetState.Deleted)
        {
            throw new InvalidOperationException("Deleted assets cannot be edited.");
        }

        Metadata = metadata.Json;
        Touch(now);
    }

    /// <summary>Stores metadata extracted from the file (W4).</summary>
    /// <param name="metadata">Extracted values as a JSON object.</param>
    /// <param name="now">Current time.</param>
    public void SetEmbeddedMetadata(AssetMetadata metadata, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        EmbeddedMetadata = metadata.Json;
        Touch(now);
    }

    /// <summary>True when a user with the given catalog role may see the asset.</summary>
    /// <param name="canEdit">The user is an editor (or administrator) of the catalog.</param>
    /// <returns>Viewers see only active assets; editors also see pending, rejected and failed ones.</returns>
    public bool IsVisibleTo(bool canEdit) =>
        State == AssetState.Active || (canEdit && State is not (AssetState.Deleting or AssetState.Deleted));

    /// <summary>True for 64 lowercase hex characters.</summary>
    /// <param name="value">Candidate checksum.</param>
    /// <returns>Whether the value is a SHA-256 hex string.</returns>
    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string RequireReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;
    }

    private void TransitionTo(AssetState target, string? reason, DateTimeOffset now)
    {
        if (!AssetLifecycle.CanTransition(State, target))
        {
            throw new InvalidOperationException($"An asset cannot move from {State} to {target}.");
        }

        State = target;
        StatusReason = reason;
        StateChangedAt = now;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Revision++;
    }
}
