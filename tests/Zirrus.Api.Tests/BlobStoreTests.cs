using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;
using Zirrus.Infrastructure.Storage;

namespace Zirrus.Api.Tests;

/// <summary>
/// W3 storage contract (ADR 0006): SHA-256 round trips, range reads, interrupted writes, retries and
/// idempotency. Every adapter must pass the same tests.
/// </summary>
public abstract class BlobStoreContract
{
    /// <summary>The store under test.</summary>
    protected abstract IBlobStore Store { get; }

    /// <summary>Directory holding the store's temporary writes.</summary>
    protected abstract string TemporaryDirectory { get; }

    [Fact]
    public async Task Put_and_open_round_trip_with_a_sha256_checksum()
    {
        var data = RandomNumberGenerator.GetBytes(1024 * 1024 + 17);
        var key = NewKey();
        var stored = await Store.PutAsync(key, new MemoryStream(data), Sha256(data), Api.Ct);
        Assert.Equal((Sha256(data), (long)data.Length), (stored.Sha256, stored.SizeBytes));

        await using var read = await Store.OpenAsync(key, null, Api.Ct);
        Assert.NotNull(read);
        Assert.Equal((0L, (long)data.Length, (long)data.Length), (read.Offset, read.Length, read.TotalLength));
        Assert.Equal(data, await ReadAllAsync(read.Content));
        Assert.Equal(data.Length, (await Store.StatAsync(key, Api.Ct))!.SizeBytes);
    }

    [Theory]
    [InlineData(0L, 10L, 0L, 10L)]
    [InlineData(100L, null, 100L, 900L)]
    [InlineData(995L, 100L, 995L, 5L)]
    [InlineData(999L, 1L, 999L, 1L)]
    public async Task Range_reads_return_exactly_the_requested_bytes(long offset, long? length, long expectedOffset, long expectedLength)
    {
        var data = RandomNumberGenerator.GetBytes(1000);
        var key = NewKey();
        await Store.PutAsync(key, new MemoryStream(data), null, Api.Ct);

        await using var read = await Store.OpenAsync(key, new ByteRange(offset, length), Api.Ct);
        Assert.Equal((expectedOffset, expectedLength, 1000L), (read!.Offset, read.Length, read.TotalLength));
        Assert.Equal(data.AsSpan((int)expectedOffset, (int)expectedLength).ToArray(), await ReadAllAsync(read.Content));
    }

    [Fact]
    public async Task Ranges_beyond_the_end_are_not_satisfiable()
    {
        var key = NewKey();
        await Store.PutAsync(key, new MemoryStream(new byte[10]), null, Api.Ct);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store.OpenAsync(key, new ByteRange(10), Api.Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store.OpenAsync(key, new ByteRange(-1), Api.Ct));
    }

    [Fact]
    public async Task Empty_blobs_round_trip()
    {
        var key = NewKey();
        var stored = await Store.PutAsync(key, new MemoryStream(), null, Api.Ct);
        Assert.Equal((Sha256([]), 0L), (stored.Sha256, stored.SizeBytes));
        await using var read = await Store.OpenAsync(key, new ByteRange(0), Api.Ct);
        Assert.Empty(await ReadAllAsync(read!.Content));
    }

    [Fact]
    public async Task An_interrupted_write_leaves_no_blob_and_no_temporary_file_and_can_be_retried()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);
        var key = NewKey();
        await Assert.ThrowsAsync<IOException>(() => Store.PutAsync(key, new FailingStream(data, failAfter: 100_000), null, Api.Ct));
        Assert.Null(await Store.StatAsync(key, Api.Ct));
        Assert.Null(await Store.OpenAsync(key, null, Api.Ct));
        Assert.Empty(Directory.GetFiles(TemporaryDirectory));

        var retried = await Store.PutAsync(key, new MemoryStream(data), null, Api.Ct);
        Assert.Equal(Sha256(data), retried.Sha256);
    }

    [Fact]
    public async Task A_cancelled_write_leaves_no_blob()
    {
        var key = NewKey();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.PutAsync(key, new MemoryStream(new byte[1000]), null, cancel.Token));
        Assert.Null(await Store.StatAsync(key, Api.Ct));
    }

    [Fact]
    public async Task Re_putting_identical_content_is_idempotent_and_different_content_conflicts()
    {
        var data = RandomNumberGenerator.GetBytes(5000);
        var key = NewKey();
        await Store.PutAsync(key, new MemoryStream(data), null, Api.Ct);
        var again = await Store.PutAsync(key, new MemoryStream(data), null, Api.Ct);
        Assert.Equal(Sha256(data), again.Sha256);

        await Assert.ThrowsAsync<BlobConflictException>(() => Store.PutAsync(key, new MemoryStream(new byte[5000]), null, Api.Ct));
        await using var read = await Store.OpenAsync(key, null, Api.Ct);
        Assert.Equal(data, await ReadAllAsync(read!.Content));
    }

    [Fact]
    public async Task A_checksum_mismatch_discards_the_write()
    {
        var key = NewKey();
        await Assert.ThrowsAsync<ChecksumMismatchException>(() => Store.PutAsync(key, new MemoryStream(new byte[10]), new string('0', 64), Api.Ct));
        Assert.Null(await Store.StatAsync(key, Api.Ct));
        Assert.Empty(Directory.GetFiles(TemporaryDirectory));
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        var key = NewKey();
        await Store.PutAsync(key, new MemoryStream(new byte[3]), null, Api.Ct);
        Assert.True(await Store.DeleteAsync(key, Api.Ct));
        Assert.False(await Store.DeleteAsync(key, Api.Ct));
        Assert.Null(await Store.OpenAsync(key, null, Api.Ct));
    }

    [Fact]
    public async Task Old_temporary_files_are_listed_as_orphans()
    {
        var orphan = Path.Combine(TemporaryDirectory, $"{Guid.NewGuid():N}.part");
        await File.WriteAllBytesAsync(orphan, new byte[7], Api.Ct);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));
        try
        {
            var listed = new List<TemporaryBlob>();
            await foreach (var t in Store.ListTemporaryAsync(DateTimeOffset.UtcNow.AddHours(-1), Api.Ct))
            {
                listed.Add(t);
            }

            Assert.Contains(listed, t => t.Name == Path.GetFileName(orphan) && t.SizeBytes == 7);
        }
        finally
        {
            File.Delete(orphan);
        }
    }

    /// <summary>A fresh original key.</summary>
    /// <returns>The key.</returns>
    protected static BlobKey NewKey() => BlobKey.ForOriginal(Guid.NewGuid(), Guid.NewGuid());

    /// <summary>Lowercase hex SHA-256.</summary>
    /// <param name="data">Bytes.</param>
    /// <returns>The hash.</returns>
    protected static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Api.Ct);
        return buffer.ToArray();
    }

    /// <summary>Yields bytes, then fails like a dropped client connection.</summary>
    private sealed class FailingStream(byte[] data, int failAfter) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfter)
            {
                throw new IOException("Simulated client disconnect.");
            }

            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, failAfter - Position)], cancellationToken);
        }
    }
}

/// <summary>Contract tests for the managed-filesystem store.</summary>
public sealed class FileSystemBlobStoreTests : BlobStoreContract, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zirrus-fs-{Guid.NewGuid():N}");
    private readonly FileSystemBlobStore _store;

    public FileSystemBlobStoreTests() => _store = new FileSystemBlobStore(_root);

    protected override IBlobStore Store => _store;

    protected override string TemporaryDirectory => Path.Combine(_root, FileSystemBlobStore.TemporaryDirectoryName);

    [Fact]
    public async Task Blobs_are_stored_under_their_opaque_key_inside_the_root()
    {
        var key = NewKey();
        await _store.PutAsync(key, new MemoryStream([1, 2, 3]), null, Api.Ct);
        Assert.True(File.Exists(Path.Combine(_root, key.Value)));
    }

    [Fact]
    public async Task Uninitialized_keys_are_refused() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _store.StatAsync(default, Api.Ct));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

/// <summary>
/// SeaweedFS (Apache-2.0) via Testcontainers, or an existing S3-compatible endpoint from
/// <c>ZIRRUS_TEST_S3</c> (e.g. <c>http://127.0.0.1:8333</c>) when Docker is unavailable.
/// </summary>
public sealed class S3Fixture : IAsyncLifetime
{
    private const string Image = "chrislusf/seaweedfs:4.53@sha256:ccad23e18639c8b98cee17e7100b95830a06077b56d8e5b006d6dcf8ec5e94a0";
    private IContainer? _container;

    /// <summary>The store, on a fresh bucket.</summary>
    public S3BlobStore Store { get; private set; } = null!;

    /// <summary>Local spool directory of the store.</summary>
    public string SpoolDirectory { get; } = Path.Combine(Path.GetTempPath(), $"zirrus-s3-spool-{Guid.NewGuid():N}");

    public async ValueTask InitializeAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("ZIRRUS_TEST_S3");
        if (string.IsNullOrEmpty(endpoint))
        {
            _container = new ContainerBuilder(Image)
                .WithCommand("server", "-s3", "-dir=/data", "-ip.bind=0.0.0.0")
                .WithPortBinding(8333, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8333))
                .Build();
            await _container.StartAsync();
            endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8333)}";
        }

        Store = new S3BlobStore(new S3StorageOptions
        {
            ServiceUrl = endpoint,
            Bucket = $"zirrus-test-{Guid.NewGuid():N}"[..30],
            AccessKey = "test-only",
            SecretKey = "test-only",
            SpoolDirectory = SpoolDirectory,
        });

        // The S3 gateway accepts connections before the filer is ready; retry bucket creation briefly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Store.EnsureBucketAsync(TestContext.Current.CancellationToken);
                break;
            }
            catch (Exception) when (attempt < 30)
            {
                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Store.Dispose();
        Directory.Delete(SpoolDirectory, recursive: true);
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>Contract tests for the S3-compatible store against SeaweedFS.</summary>
/// <param name="fixture">S3 endpoint.</param>
public sealed class S3BlobStoreTests(S3Fixture fixture) : BlobStoreContract, IClassFixture<S3Fixture>
{
    protected override IBlobStore Store => fixture.Store;

    protected override string TemporaryDirectory => fixture.SpoolDirectory;
}
