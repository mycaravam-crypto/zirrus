using Zirrus.Application.Audit;
using Zirrus.Application.Assets;
using Zirrus.Application.Catalogs;
using Zirrus.Application.Identity;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;

namespace Zirrus.Application.Ingestion;

/// <summary>An accepted upload.</summary>
/// <param name="Asset">The quarantined asset.</param>
/// <param name="Duplicates">Other assets in the same catalog with the same SHA-256 (reported, not refused).</param>
public sealed record UploadResult(AssetDetails Asset, IReadOnlyList<Guid> Duplicates);

/// <summary>The upload exceeded <see cref="MediaFormat.MaxUploadBytes"/>; nothing was stored.</summary>
public sealed class UploadTooLargeException : IOException
{
    /// <summary>Creates the exception.</summary>
    public UploadTooLargeException()
        : base($"Uploads are limited to {MediaFormat.MaxUploadBytes} bytes.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Message.</param>
    public UploadTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public UploadTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Use case: stream an upload into quarantine (ingestion-api-ui.md step 1). The format comes from
/// magic bytes; the asset row exists before the write (so reconciliation sees stale uploads), and the
/// checksum, quarantine state and scan job intent are committed together afterwards.
/// </summary>
/// <param name="catalogs">Catalog lookup.</param>
/// <param name="store">Ingestion writes.</param>
/// <param name="blobs">Blob store.</param>
/// <param name="access">Catalog permissions.</param>
/// <param name="users">Current user.</param>
/// <param name="organization">Organization context.</param>
/// <param name="time">Time source.</param>
/// <param name="audit">Audit log; entries are committed with the change they describe.</param>
public sealed class UploadAsset(
    ICatalogStore catalogs,
    IIngestionStore store,
    IBlobStore blobs,
    CatalogAccess access,
    ICurrentUserAccessor users,
    OrganizationContext organization,
    TimeProvider time,
    IAuditLog audit)
{
    /// <summary>Uploads one file.</summary>
    /// <param name="catalogId">Target catalog.</param>
    /// <param name="fileName">Original file name (display only).</param>
    /// <param name="content">File content, read once.</param>
    /// <param name="cancellationToken">Cancels the upload; the asset is then marked failed.</param>
    /// <returns>The quarantined asset, or NotFound, Forbidden, Invalid, UnsupportedMediaType or TooLarge.</returns>
    public async Task<OperationResult<UploadResult>> ExecuteAsync(Guid catalogId, string fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<UploadResult>(OperationStatus.NotFound, "Catalog not found.");
        }

        if (!await access.CanEditAsync(catalogId, cancellationToken) || users.User is not { } user)
        {
            return OperationResult.Fail<UploadResult>(OperationStatus.Forbidden, "Uploading requires the editor role.");
        }

        var catalog = await catalogs.FindAsync(organization.Id, catalogId, cancellationToken);
        if (catalog is null)
        {
            return OperationResult.Fail<UploadResult>(OperationStatus.NotFound, "Catalog not found.");
        }

        var header = new byte[MediaFormat.HeaderLength];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var mediaType = MediaFormat.Sniff(header.AsSpan(0, headerLength));
        if (mediaType is null)
        {
            return OperationResult.Fail<UploadResult>(OperationStatus.UnsupportedMediaType, "Only JPEG, PNG and WebP images are accepted.");
        }

        Asset asset;
        try
        {
            asset = Asset.Start(catalog, fileName, mediaType, user.Subject, time.GetUtcNow());
        }
        catch (ArgumentException e)
        {
            return OperationResult.Fail<UploadResult>(OperationStatus.Invalid, e.Message);
        }

        store.Add(asset);
        await store.SaveChangesAsync(cancellationToken);

        StoredBlob stored;
        try
        {
            await using var upload = new UploadStream(header.AsMemory(0, headerLength), content, MediaFormat.MaxUploadBytes);
            stored = await blobs.PutAsync(BlobKey.Parse(asset.OriginalKey), upload, null, cancellationToken);
        }
        catch (UploadTooLargeException e)
        {
            asset.Fail($"upload: {e.Message}", time.GetUtcNow());
            await store.SaveChangesAsync(CancellationToken.None);
            return OperationResult.Fail<UploadResult>(OperationStatus.TooLarge, e.Message);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // The client went away: nothing is visible under the key; record why and let the caller see the error.
            asset.Fail("upload: interrupted", time.GetUtcNow());
            await store.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        asset.MarkStored(stored.Sha256, stored.SizeBytes, time.GetUtcNow());
        store.Add(JobIntent.ForAsset(asset, PipelineStages.Scan, PipelineStages.Version, time.GetUtcNow()));
        audit.Record(new AuditEntry(AuditActions.Upload, user.Subject, organization.Id, catalogId, asset.Id, $"type={mediaType}; bytes={stored.SizeBytes}; sha256={stored.Sha256}"));
        await store.SaveChangesAsync(CancellationToken.None);

        var duplicates = await store.FindDuplicatesAsync(catalogId, stored.Sha256, asset.Id, cancellationToken);
        return OperationResult.Ok(new UploadResult(AssetDetails.From(asset), duplicates));
    }

    /// <summary>Replays the sniffed header, then the rest of the body, failing once the limit is exceeded.</summary>
    /// <param name="prefix">Bytes already read.</param>
    /// <param name="inner">Remaining body.</param>
    /// <param name="limit">Maximum total bytes.</param>
    private sealed class UploadStream(ReadOnlyMemory<byte> prefix, Stream inner, long limit) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;
        private long _total;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _total;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read;
            if (!_prefix.IsEmpty)
            {
                read = Math.Min(buffer.Length, _prefix.Length);
                _prefix[..read].CopyTo(buffer);
                _prefix = _prefix[read..];
            }
            else
            {
                read = await inner.ReadAsync(buffer, cancellationToken);
            }

            _total += read;
            return _total > limit ? throw new UploadTooLargeException() : read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
