using Zirrus.Domain.Assets;

namespace Zirrus.Application.Storage;

/// <summary>A blob as stored: its key, checksum and size.</summary>
/// <param name="Key">Storage key.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the content.</param>
/// <param name="SizeBytes">Size in bytes.</param>
public sealed record StoredBlob(BlobKey Key, string Sha256, long SizeBytes);

/// <summary>Size and modification time of a stored blob.</summary>
/// <param name="Key">Storage key.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="LastModified">Last modification time (UTC).</param>
public sealed record BlobInfo(BlobKey Key, long SizeBytes, DateTimeOffset LastModified);

/// <summary>An incomplete write left in the store's temporary area (reconciliation).</summary>
/// <param name="Name">Temporary object name.</param>
/// <param name="SizeBytes">Bytes written so far.</param>
/// <param name="LastModified">Last write (UTC).</param>
public sealed record TemporaryBlob(string Name, long SizeBytes, DateTimeOffset LastModified);

/// <summary>A byte range to read: from <paramref name="Offset"/>, at most <paramref name="Length"/> bytes (to the end when null).</summary>
/// <param name="Offset">First byte, zero-based.</param>
/// <param name="Length">Maximum number of bytes, or null for the rest of the blob.</param>
public readonly record struct ByteRange(long Offset, long? Length = null);

/// <summary>An open read of a blob or a range of it.</summary>
/// <param name="Content">Stream positioned at <paramref name="Offset"/>, yielding <paramref name="Length"/> bytes.</param>
/// <param name="Offset">First byte returned.</param>
/// <param name="Length">Bytes returned.</param>
/// <param name="TotalLength">Size of the whole blob.</param>
public sealed record BlobRead(Stream Content, long Offset, long Length, long TotalLength) : IAsyncDisposable
{
    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>The key already holds different content; originals and renditions are immutable.</summary>
public sealed class BlobConflictException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BlobConflictException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Message.</param>
    public BlobConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public BlobConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The written content does not match the checksum the caller expected.</summary>
public sealed class ChecksumMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChecksumMismatchException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Message.</param>
    public ChecksumMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public ChecksumMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Storage port for originals and renditions (ADR 0006). Writes go to a temporary area, are
/// checksummed and become visible atomically; a key never shows partial content. All operations
/// are idempotent so that at-least-once jobs can retry them.
/// </summary>
public interface IBlobStore
{
    /// <summary>
    /// Stores content under a key. Re-putting identical content is a no-op; different content
    /// throws <see cref="BlobConflictException"/>. An interrupted source stream leaves the key absent.
    /// </summary>
    /// <param name="key">Destination key.</param>
    /// <param name="content">Source stream, read to the end.</param>
    /// <param name="expectedSha256">If given, the write is discarded unless the content matches.</param>
    /// <param name="cancellationToken">Cancels the write; nothing becomes visible.</param>
    /// <returns>The stored blob.</returns>
    Task<StoredBlob> PutAsync(BlobKey key, Stream content, string? expectedSha256, CancellationToken cancellationToken);

    /// <summary>Opens a blob or a byte range of it.</summary>
    /// <param name="key">Key.</param>
    /// <param name="range">Range to read; null reads everything.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The read, or null when the key does not exist.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range starts beyond the end of the blob (HTTP 416).</exception>
    Task<BlobRead?> OpenAsync(BlobKey key, ByteRange? range, CancellationToken cancellationToken);

    /// <summary>Gets size and modification time.</summary>
    /// <param name="key">Key.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The info, or null when the key does not exist.</returns>
    Task<BlobInfo?> StatAsync(BlobKey key, CancellationToken cancellationToken);

    /// <summary>Deletes a blob; deleting a missing key succeeds.</summary>
    /// <param name="key">Key.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <returns>True when a blob was removed.</returns>
    Task<bool> DeleteAsync(BlobKey key, CancellationToken cancellationToken);

    /// <summary>Lists temporary writes last modified before a time (orphans of crashed writers).</summary>
    /// <param name="olderThan">Cut-off time.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>Orphan candidates.</returns>
    IAsyncEnumerable<TemporaryBlob> ListTemporaryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
