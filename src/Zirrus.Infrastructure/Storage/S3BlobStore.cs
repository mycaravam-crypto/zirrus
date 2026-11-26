using System.Net;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;

namespace Zirrus.Infrastructure.Storage;

/// <summary>Settings of the S3-compatible store (<c>Storage:S3</c>; keys come from Docker secrets).</summary>
public sealed class S3StorageOptions
{
    /// <summary>Endpoint, e.g. <c>http://seaweedfs:8333</c>; empty uses AWS.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Bucket name.</summary>
    public string Bucket { get; set; } = "zirrus";

    /// <summary>Region used for request signing.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Access key ID.</summary>
    public string? AccessKey { get; set; }

    /// <summary>Secret access key.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Use path-style URLs (required by most self-hosted servers).</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Local directory for spooling uploads while they are hashed.</summary>
    public string SpoolDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "zirrus-s3-spool");
}

/// <summary>
/// S3-compatible blob store (ADR 0006), tested against SeaweedFS. Uploads are spooled to a local
/// file while hashed, then sent with one PutObject, which S3 makes visible atomically. The SHA-256
/// travels as object metadata so idempotent retries can compare content without downloading it.
/// </summary>
public sealed class S3BlobStore : IBlobStore, IDisposable
{
    private const string Sha256Metadata = "x-amz-meta-zirrus-sha256";
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _spool;

    /// <summary>Creates the store.</summary>
    /// <param name="options">Settings.</param>
    public S3BlobStore(S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,

            // Only send and validate checksums where S3 requires them: many S3-compatible servers do
            // not support the SDK's default trailing CRC checksums. Integrity is our own SHA-256.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        }

        _client = string.IsNullOrEmpty(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        _bucket = options.Bucket;
        _spool = options.SpoolDirectory;
        Directory.CreateDirectory(_spool);
    }

    /// <summary>Creates the bucket if it does not exist (test and first-run setup).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task.</returns>
    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    /// <inheritdoc />
    public async Task<StoredBlob> PutAsync(BlobKey key, Stream content, string? expectedSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var spoolFile = Path.Combine(_spool, $"{Guid.NewGuid():N}.part");
        try
        {
            string sha256;
            long size;
            await using (var file = new FileStream(spoolFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                (sha256, size) = await FileSystemBlobStore.CopyAndHashAsync(content, file, cancellationToken);
            }

            if (expectedSha256 is not null && !string.Equals(expectedSha256, sha256, StringComparison.Ordinal))
            {
                throw new ChecksumMismatchException($"Expected SHA-256 {expectedSha256}, received {sha256}.");
            }

            var existing = await HeadAsync(key, cancellationToken);
            if (existing is not null)
            {
                return string.Equals(existing.Metadata[Sha256Metadata], sha256, StringComparison.Ordinal)
                    ? new StoredBlob(key, sha256, size)
                    : throw new BlobConflictException($"Key '{key}' already holds different content.");
            }

            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key.Value,
                FilePath = spoolFile,
                ContentType = "application/octet-stream",
                DisablePayloadSigning = false,
            };
            request.Metadata.Add(Sha256Metadata, sha256);
            await _client.PutObjectAsync(request, cancellationToken);
            return new StoredBlob(key, sha256, size);
        }
        finally
        {
            File.Delete(spoolFile);
        }
    }

    /// <inheritdoc />
    public async Task<BlobRead?> OpenAsync(BlobKey key, Application.Storage.ByteRange? range, CancellationToken cancellationToken)
    {
        var head = await HeadAsync(key, cancellationToken);
        if (head is null)
        {
            return null;
        }

        var total = head.ContentLength;
        var (offset, length) = FileSystemBlobStore.Resolve(range, total);
        if (length == 0)
        {
            return new BlobRead(Stream.Null, offset, 0, total);
        }

        var request = new GetObjectRequest { BucketName = _bucket, Key = key.Value };
        if (offset != 0 || length != total)
        {
            request.ByteRange = new Amazon.S3.Model.ByteRange(offset, offset + length - 1);
        }

        try
        {
            var response = await _client.GetObjectAsync(request, cancellationToken);
            return new BlobRead(new ResponseStream(response), offset, length, total);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<BlobInfo?> StatAsync(BlobKey key, CancellationToken cancellationToken)
    {
        var head = await HeadAsync(key, cancellationToken);
        return head is null ? null : new BlobInfo(key, head.ContentLength, head.LastModified?.ToUniversalTime() ?? DateTimeOffset.MinValue);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(BlobKey key, CancellationToken cancellationToken)
    {
        var existed = await HeadAsync(key, cancellationToken) is not null;
        await _client.DeleteObjectAsync(_bucket, key.Value, cancellationToken);
        return existed;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TemporaryBlob> ListTemporaryAsync(DateTimeOffset olderThan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Single PutObject uploads leave nothing behind in the bucket; orphans are local spool files.
        foreach (var file in new DirectoryInfo(_spool).EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc < olderThan.UtcDateTime)
            {
                yield return new TemporaryBlob(file.Name, file.Length, file.LastWriteTimeUtc);
            }
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private async Task<GetObjectMetadataResponse?> HeadAsync(BlobKey key, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetObjectMetadataAsync(_bucket, key.Value, cancellationToken);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Exposes the response body and disposes the response with it.</summary>
    /// <param name="response">GetObject response.</param>
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => response.ContentLength;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
