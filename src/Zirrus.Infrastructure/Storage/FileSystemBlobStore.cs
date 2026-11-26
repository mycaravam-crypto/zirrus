using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;

namespace Zirrus.Infrastructure.Storage;

/// <summary>
/// Managed-filesystem blob store (ADR 0006). Content is written to <c>.tmp/</c> under the root,
/// hashed while streaming, flushed to disk and renamed into place; a rename on one filesystem is
/// atomic, so a key never shows a partial file.
/// </summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    /// <summary>Name of the temporary directory under the root.</summary>
    public const string TemporaryDirectoryName = ".tmp";

    private const int BufferSize = 81920;
    private readonly string _root;
    private readonly string _temporary;

    /// <summary>Creates the store, creating the root and temporary directories if needed.</summary>
    /// <param name="root">Absolute root directory; must not be shared with anything else.</param>
    public FileSystemBlobStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _temporary = Path.Combine(_root, TemporaryDirectoryName);
        Directory.CreateDirectory(_temporary);
    }

    /// <inheritdoc />
    public async Task<StoredBlob> PutAsync(BlobKey key, Stream content, string? expectedSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var target = PathFor(key);
        var temporary = Path.Combine(_temporary, $"{Guid.NewGuid():N}.part");
        try
        {
            string sha256;
            long size;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                (sha256, size) = await CopyAndHashAsync(content, file, cancellationToken);
                file.Flush(flushToDisk: true);
            }

            if (expectedSha256 is not null && !string.Equals(expectedSha256, sha256, StringComparison.Ordinal))
            {
                throw new ChecksumMismatchException($"Expected SHA-256 {expectedSha256}, received {sha256}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Move(temporary, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // A retry or a concurrent writer got there first: identical content is success.
                var existing = await HashFileAsync(target, cancellationToken);
                if (!string.Equals(existing, sha256, StringComparison.Ordinal))
                {
                    throw new BlobConflictException($"Key '{key}' already holds different content.");
                }
            }

            return new StoredBlob(key, sha256, size);
        }
        finally
        {
            File.Delete(temporary); // no-op after a successful move
        }
    }

    /// <inheritdoc />
    public Task<BlobRead?> OpenAsync(BlobKey key, ByteRange? range, CancellationToken cancellationToken)
    {
        FileStream file;
        try
        {
            file = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<BlobRead?>(null);
        }

        try
        {
            var total = file.Length;
            var (offset, length) = Resolve(range, total);
            file.Seek(offset, SeekOrigin.Begin);
            Stream stream = length == total - offset ? file : new BoundedReadStream(file, length);
            return Task.FromResult<BlobRead?>(new BlobRead(stream, offset, length, total));
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<BlobInfo?> StatAsync(BlobKey key, CancellationToken cancellationToken)
    {
        var info = new FileInfo(PathFor(key));
        return Task.FromResult(info.Exists ? new BlobInfo(key, info.Length, info.LastWriteTimeUtc) : null);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(BlobKey key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        var existed = File.Exists(path);
        File.Delete(path);
        return Task.FromResult(existed);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TemporaryBlob> ListTemporaryAsync(DateTimeOffset olderThan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var file in new DirectoryInfo(_temporary).EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc < olderThan.UtcDateTime)
            {
                yield return new TemporaryBlob(file.Name, file.Length, file.LastWriteTimeUtc);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>Resolves a requested range against the blob size.</summary>
    /// <param name="range">Requested range, or null for all.</param>
    /// <param name="total">Blob size.</param>
    /// <returns>Offset and length to return.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range starts at or after the end of a non-empty blob, or is negative.</exception>
    internal static (long Offset, long Length) Resolve(ByteRange? range, long total)
    {
        if (range is not { } r)
        {
            return (0, total);
        }

        if (r.Offset < 0 || r.Length < 0 || (r.Offset >= total && !(r.Offset == 0 && total == 0)))
        {
            throw new ArgumentOutOfRangeException(nameof(range), $"Range starting at {r.Offset} is not satisfiable for {total} bytes.");
        }

        var available = total - r.Offset;
        return (r.Offset, r.Length is { } l ? Math.Min(l, available) : available);
    }

    /// <summary>Copies a stream while computing its SHA-256.</summary>
    /// <param name="source">Source.</param>
    /// <param name="destination">Destination.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>Lowercase hex SHA-256 and byte count.</returns>
    internal static async Task<(string Sha256, long Size)> CopyAndHashAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            size += read;
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), size);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
    }

    private string PathFor(BlobKey key)
    {
        if (key.Value is null)
        {
            throw new ArgumentException("Storage key is not initialized.", nameof(key));
        }

        // BlobKey admits only [a-z0-9/-] without empty segments, so the path stays under the root.
        var path = Path.GetFullPath(Path.Combine(_root, key.Value));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage key escapes the root.", nameof(key));
        }

        return path;
    }
}

/// <summary>Read-only view of the next <c>length</c> bytes of a stream; disposes the inner stream.</summary>
/// <param name="inner">Inner stream, positioned at the start of the range.</param>
/// <param name="length">Bytes to expose.</param>
internal sealed class BoundedReadStream(Stream inner, long length) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _length;

    /// <inheritdoc />
    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= read;
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
        _remaining -= read;
        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
