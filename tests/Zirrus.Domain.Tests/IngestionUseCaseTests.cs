using System.Security.Cryptography;
using Zirrus.Application;
using Zirrus.Application.Catalogs;
using Zirrus.Application.Identity;
using Zirrus.Application.Ingestion;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Domain.Tests;

/// <summary>Upload, pipeline stages and retry with in-memory ports (ingestion-api-ui.md, ADR 0002).</summary>
public sealed class IngestionUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationContext Organization = new(Guid.NewGuid());
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    private readonly Catalog _press = Catalog.Create(Organization.Id, "Press", Now);
    private readonly Catalogs _catalogs = new();
    private readonly Store _store = new();
    private readonly Blobs _blobs = new();
    private readonly Queue _queue = new();
    private readonly Scanner _scanner = new();
    private readonly Renderer _renderer = new();

    public IngestionUseCaseTests()
    {
        _catalogs.Items.Add(_press);
        _catalogs.Grants[("erin", _press.Id)] = CatalogRole.Editor;
        _catalogs.Grants[("victor", _press.Id)] = CatalogRole.Viewer;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, null)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 }, null)]
    [InlineData(new byte[] { 0xFF }, null)]
    public void Formats_are_detected_from_magic_bytes(byte[] header, string? expected) =>
        Assert.Equal(expected, MediaFormat.Sniff(header));

    [Fact]
    public async Task Upload_stores_the_original_and_writes_the_scan_job_with_the_quarantine_state()
    {
        var other = Asset.Start(_press, "old.jpg", "image/jpeg", "erin", Now);
        other.MarkStored(Sha(Jpeg), Jpeg.Length, Now);
        _store.Assets.Add(other);

        var result = await Upload("erin").ExecuteAsync(_press.Id, "photo.jpg", new MemoryStream(Jpeg), Ct);

        Assert.Equal(OperationStatus.Ok, result.Status);
        var asset = _store.Assets[^1];
        Assert.Equal((AssetState.Quarantined, Sha(Jpeg), (long)Jpeg.Length, "image/jpeg"), (asset.State, asset.Sha256, asset.SizeBytes, asset.MediaType));
        Assert.Equal(Jpeg, _blobs.Content[BlobKey.Parse(asset.OriginalKey)]);
        Assert.Equal([$"{asset.Id:N}:scan:v1"], _store.Jobs.Select(j => j.JobKey));
        Assert.Equal([other.Id], result.Value!.Duplicates);
        Assert.True(_store.Saves >= 2); // the uploading row exists before the write
    }

    [Theory]
    [InlineData("victor", OperationStatus.Forbidden)]
    [InlineData("oscar", OperationStatus.NotFound)]
    [InlineData(null, OperationStatus.NotFound)]
    public async Task Only_editors_upload(string? user, OperationStatus expected) =>
        Assert.Equal(expected, (await Upload(user).ExecuteAsync(_press.Id, "a.jpg", new MemoryStream(Jpeg), Ct)).Status);

    [Fact]
    public async Task Upload_into_a_vanished_catalog_is_not_found()
    {
        _catalogs.Items.Clear();
        _catalogs.Exists = true; // admin check passes, catalog row is gone
        Assert.Equal(OperationStatus.NotFound, (await Upload("admin").ExecuteAsync(_press.Id, "a.jpg", new MemoryStream(Jpeg), Ct)).Status);
    }

    [Fact]
    public async Task Upload_rejects_unsupported_content_and_invalid_names_without_storing_anything()
    {
        Assert.Equal(OperationStatus.UnsupportedMediaType, (await Upload("erin").ExecuteAsync(_press.Id, "a.jpg", new MemoryStream("GIF89a"u8.ToArray()), Ct)).Status);
        Assert.Equal(OperationStatus.Invalid, (await Upload("erin").ExecuteAsync(_press.Id, "dir/", new MemoryStream(Jpeg), Ct)).Status);
        Assert.Empty(_store.Assets);
        Assert.Empty(_blobs.Content);
    }

    [Fact]
    public async Task Oversize_uploads_fail_the_asset_and_leave_no_blob()
    {
        var result = await Upload("erin").ExecuteAsync(_press.Id, "a.jpg", new EndlessJpeg(), Ct);
        Assert.Equal(OperationStatus.TooLarge, result.Status);
        Assert.Equal(AssetState.Failed, _store.Assets.Single().State);
        Assert.Empty(_blobs.Content);
        Assert.Empty(_store.Jobs);
    }

    [Fact]
    public async Task Interrupted_uploads_fail_the_asset_and_rethrow()
    {
        await Assert.ThrowsAsync<IOException>(() => Upload("erin").ExecuteAsync(_press.Id, "a.jpg", new BrokenStream(Jpeg), Ct));
        Assert.Equal(("upload: interrupted", AssetState.Failed), (_store.Assets.Single().StatusReason, _store.Assets.Single().State));
    }

    [Fact]
    public async Task Scan_and_process_publish_the_asset_with_renditions_and_embedded_metadata()
    {
        var asset = StoredAsset();
        _store.Extracted = AssetMetadata.Parse("""{"camera":"x"}""");

        Assert.Equal(StageOutcome.Completed, await Pipeline().RunAsync(Job(asset, PipelineStages.Scan), Ct));
        Assert.Equal(AssetState.Processing, asset.State);
        Assert.Equal([$"{asset.Id:N}:process:v1"], _store.Jobs.Select(j => j.JobKey));

        Assert.Equal(StageOutcome.Completed, await Pipeline().RunAsync(Job(asset, PipelineStages.Process), Ct));
        Assert.Equal(AssetState.Active, asset.State);
        Assert.Equal(["thumbnail", "preview"], _store.Renditions.Select(r => r.Kind));
        Assert.Equal("""{"camera":"x"}""", asset.EmbeddedMetadata);
        Assert.Equal(2, _store.Completed);
    }

    [Fact]
    public async Task Infected_assets_are_rejected()
    {
        var asset = StoredAsset();
        _scanner.Result = new ScanResult(false, "Eicar");
        Assert.Equal(StageOutcome.Completed, await Pipeline().RunAsync(Job(asset, PipelineStages.Scan), Ct));
        Assert.Equal((AssetState.Rejected, "malware: Eicar"), (asset.State, asset.StatusReason));
        Assert.Empty(_store.Jobs);

        _scanner.Result = new ScanResult(false);
        var unnamed = StoredAsset();
        await Pipeline().RunAsync(Job(unnamed, PipelineStages.Scan), Ct);
        Assert.Equal("malware: threat detected", unnamed.StatusReason);
    }

    [Fact]
    public async Task An_unavailable_scanner_retries_and_keeps_the_asset_quarantined()
    {
        var asset = StoredAsset();
        _scanner.Unavailable = true;
        Assert.Equal(StageOutcome.Retrying, await Pipeline().RunAsync(Job(asset, PipelineStages.Scan), Ct));
        Assert.Equal(AssetState.Quarantined, asset.State);
        Assert.Equal([(false, true)], _queue.Failures.Select(f => (f.Permanent, f.Error.StartsWith("scan: scanner unavailable", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task Redelivered_stages_are_no_ops()
    {
        var asset = StoredAsset();
        asset.BeginProcessing(Now);
        asset.Activate(Now);
        Assert.Equal(StageOutcome.Completed, await Pipeline().RunAsync(Job(asset, PipelineStages.Scan), Ct));
        Assert.Equal(StageOutcome.Completed, await Pipeline().RunAsync(Job(asset, PipelineStages.Process), Ct));
        Assert.Equal(0, _scanner.Scans);
        Assert.Equal(0, _renderer.Renders);
    }

    [Fact]
    public async Task Jobs_for_missing_assets_complete_and_unknown_stages_dead_letter()
    {
        Assert.Equal(StageOutcome.Completed, await Pipeline().RunAsync(new ClaimedJob(1, "k", "scan", _press.Id, Guid.NewGuid(), 1, 4, Guid.NewGuid()), Ct));
        Assert.Equal(StageOutcome.Completed, await Pipeline().RunAsync(new ClaimedJob(1, "k", "scan", null, null, 1, 4, Guid.NewGuid()), Ct));

        var asset = StoredAsset();
        Assert.Equal(StageOutcome.Retrying, await Pipeline().RunAsync(Job(asset, "mystery"), Ct));
        Assert.True(_queue.Failures.Single().Permanent);
    }

    [Fact]
    public async Task Undecodable_images_are_rejected()
    {
        var asset = ProcessingAsset();
        _renderer.Unsupported = true;
        Assert.Equal(StageOutcome.Completed, await Pipeline().RunAsync(Job(asset, PipelineStages.Process), Ct));
        Assert.Equal((AssetState.Rejected, "decode: bad"), (asset.State, asset.StatusReason));
    }

    [Fact]
    public async Task Transient_processing_errors_retry_and_fail_the_asset_on_the_last_attempt()
    {
        var asset = ProcessingAsset();
        _renderer.Crash = true;
        Assert.Equal(StageOutcome.Retrying, await Pipeline().RunAsync(Job(asset, PipelineStages.Process, attempt: 1), Ct));
        Assert.Equal(AssetState.Processing, asset.State);

        Assert.Equal(StageOutcome.Retrying, await Pipeline().RunAsync(Job(asset, PipelineStages.Process, attempt: 4), Ct));
        Assert.Equal(AssetState.Failed, asset.State);
        Assert.StartsWith("process: InvalidOperationException", asset.StatusReason, StringComparison.Ordinal);
        Assert.All(_queue.Failures, f => Assert.False(f.Permanent));
    }

    [Fact]
    public async Task A_missing_original_is_a_transient_failure()
    {
        var asset = StoredAsset();
        _blobs.Content.Clear();
        Assert.Equal(StageOutcome.Retrying, await Pipeline().RunAsync(Job(asset, PipelineStages.Scan), Ct));
        Assert.Contains("missing", _queue.Failures.Single().Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_leftover_rendition_is_replaced_and_a_lost_lease_is_reported()
    {
        var asset = ProcessingAsset();
        _blobs.Content[BlobKey.ForRendition(asset.CatalogId, asset.Id, "thumbnail", PipelineStages.Version)] = [9, 9, 9];
        _store.LeaseHeld = false;
        Assert.Equal(StageOutcome.LeaseLost, await Pipeline().RunAsync(Job(asset, PipelineStages.Process), Ct));
        Assert.Equal([1, 2], _blobs.Content[BlobKey.ForRendition(asset.CatalogId, asset.Id, "thumbnail", PipelineStages.Version)]);
    }

    [Fact]
    public async Task Editors_retry_failed_and_stalled_assets()
    {
        var failed = ProcessingAsset();
        failed.Fail("process: boom", Now);
        _store.DeadJobs.Add($"{failed.Id:N}:process:v1");
        var result = await Retry("erin").ExecuteAsync(_press.Id, failed.Id, Ct);
        Assert.Equal((OperationStatus.Ok, AssetState.Processing), (result.Status, failed.State));

        var stalled = StoredAsset();
        _store.DeadJobs.Add($"{stalled.Id:N}:scan:v1");
        Assert.Equal(OperationStatus.Ok, (await Retry("admin").ExecuteAsync(_press.Id, stalled.Id, Ct)).Status);
        Assert.Equal(AssetState.Quarantined, stalled.State);
        Assert.Equal(OperationStatus.Conflict, (await Retry("erin").ExecuteAsync(_press.Id, stalled.Id, Ct)).Status); // already requeued
    }

    [Fact]
    public async Task Retry_checks_roles_visibility_and_state()
    {
        var active = ProcessingAsset();
        active.Activate(Now);
        var stalled = StoredAsset();
        Assert.Equal(OperationStatus.Forbidden, (await Retry("victor").ExecuteAsync(_press.Id, active.Id, Ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Retry("victor").ExecuteAsync(_press.Id, stalled.Id, Ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Retry("oscar").ExecuteAsync(_press.Id, active.Id, Ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Retry("erin").ExecuteAsync(_press.Id, Guid.NewGuid(), Ct)).Status);
        Assert.Equal(OperationStatus.Conflict, (await Retry("erin").ExecuteAsync(_press.Id, active.Id, Ct)).Status);
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static ClaimedJob Job(Asset asset, string stage, int attempt = 1) =>
        new(1, $"{asset.Id:N}:{stage}:v1", stage, asset.CatalogId, asset.Id, attempt, JobIntent.DefaultMaxAttempts, Guid.NewGuid());

    private Asset StoredAsset()
    {
        var asset = Asset.Start(_press, "a.jpg", "image/jpeg", "erin", Now);
        asset.MarkStored(Sha(Jpeg), Jpeg.Length, Now);
        _store.Assets.Add(asset);
        _blobs.Content[BlobKey.Parse(asset.OriginalKey)] = Jpeg;
        return asset;
    }

    private Asset ProcessingAsset()
    {
        var asset = StoredAsset();
        asset.BeginProcessing(Now);
        return asset;
    }

    private CatalogAccess Access(string? who) => new(_catalogs, new StaticUser(who switch
    {
        null => null,
        "admin" => new CurrentUser("alice", "Alice", IsOrganizationAdmin: true),
        _ => new CurrentUser(who, who, IsOrganizationAdmin: false),
    }), Organization);

    private UploadAsset Upload(string? who) => new(_catalogs, _store, _blobs, Access(who),
        new StaticUser(who is null ? null : new CurrentUser(who, who, who == "admin")), Organization, new FixedTime(Now));

    private IngestionPipeline Pipeline() => new(_store, _blobs, _scanner, _renderer, _store, _queue, new FixedTime(Now));

    private RetryAsset Retry(string who) => new(_store, Access(who), new FixedTime(Now));

    private sealed class Catalogs : ICatalogReader, ICatalogStore
    {
        public List<Catalog> Items { get; } = [];

        public Dictionary<(string, Guid), CatalogRole> Grants { get; } = [];

        public bool Exists { get; set; }

        public Task<IReadOnlyList<(Guid Id, string Name, DateTimeOffset CreatedAt, CatalogRole? Role)>> ListAsync(Guid organizationId, string? subject, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogRole?> FindRoleAsync(Guid organizationId, Guid catalogId, string subject, CancellationToken cancellationToken) =>
            Task.FromResult(Grants.TryGetValue((subject, catalogId), out var role) ? role : (CatalogRole?)null);

        public Task<bool> ExistsAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
            Task.FromResult(Exists || Items.Any(c => c.Id == catalogId));

        public Task<Catalog?> FindAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(c => c.Id == catalogId));

        public Task<bool> NameExistsAsync(Guid organizationId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogMembership?> FindMembershipAsync(Catalog catalog, string subject, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(Catalog catalog) => throw new NotSupportedException();

        public void Add(CatalogMembership membership) => throw new NotSupportedException();

        public void Remove(CatalogMembership membership) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Store : IIngestionStore, IMetadataExtractor
    {
        public List<Asset> Assets { get; } = [];

        public List<JobIntent> Jobs { get; } = [];

        public List<Rendition> Renditions { get; } = [];

        public HashSet<string> DeadJobs { get; } = [];

        public AssetMetadata? Extracted { get; set; }

        public bool LeaseHeld { get; set; } = true;

        public int Saves { get; private set; }

        public int Completed { get; private set; }

        public Task<Asset?> FindAssetAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(Assets.SingleOrDefault(a => a.CatalogId == catalogId && a.Id == assetId));

        public void Add(Asset asset) => Assets.Add(asset);

        public void Add(JobIntent job) => Jobs.Add(job);

        public void AddOrReplace(Rendition rendition) => Renditions.Add(rendition);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.CompletedTask;
        }

        public Task<bool> CompleteAsync(ClaimedJob job, CancellationToken cancellationToken)
        {
            Completed += LeaseHeld ? 1 : 0;
            return Task.FromResult(LeaseHeld);
        }

        public Task<IReadOnlyList<Guid>> FindDuplicatesAsync(Guid catalogId, string sha256, Guid exceptAssetId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(Assets.Where(a => a.CatalogId == catalogId && a.Sha256 == sha256 && a.Id != exceptAssetId).Select(a => a.Id).ToList());

        public Task<bool> RequeueDeadJobAsync(string jobKey, CancellationToken cancellationToken) => Task.FromResult(DeadJobs.Remove(jobKey));

        public Task<AssetMetadata?> ExtractAsync(Stream source, CancellationToken cancellationToken) => Task.FromResult(Extracted);
    }

    private sealed class Blobs : IBlobStore
    {
        public Dictionary<BlobKey, byte[]> Content { get; } = [];

        public async Task<StoredBlob> PutAsync(BlobKey key, Stream content, string? expectedSha256, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var data = buffer.ToArray();
            if (Content.TryGetValue(key, out var existing) && !existing.SequenceEqual(data))
            {
                throw new BlobConflictException("different content");
            }

            Content[key] = data;
            return new StoredBlob(key, Sha(data), data.Length);
        }

        public Task<BlobRead?> OpenAsync(BlobKey key, ByteRange? range, CancellationToken cancellationToken) =>
            Task.FromResult(Content.TryGetValue(key, out var data) ? new BlobRead(new MemoryStream(data), 0, data.Length, data.Length) : null);

        public Task<BlobInfo?> StatAsync(BlobKey key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(BlobKey key, CancellationToken cancellationToken) => Task.FromResult(Content.Remove(key));

        public IAsyncEnumerable<TemporaryBlob> ListTemporaryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Queue : IJobQueue
    {
        public List<(string Error, bool Permanent)> Failures { get; } = [];

        public Task<ClaimedJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RenewAsync(ClaimedJob job, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task FailAsync(ClaimedJob job, string errorDetail, bool permanent, CancellationToken cancellationToken)
        {
            Failures.Add((errorDetail, permanent));
            return Task.CompletedTask;
        }
    }

    private sealed class Scanner : IMalwareScanner
    {
        public ScanResult Result { get; set; } = new(true);

        public bool Unavailable { get; set; }

        public int Scans { get; private set; }

        public Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            Scans++;
            return Unavailable ? throw new ScannerUnavailableException("down") : Task.FromResult(Result);
        }
    }

    private sealed class Renderer : IImageRenderer
    {
        public bool Unsupported { get; set; }

        public bool Crash { get; set; }

        public int Renders { get; private set; }

        public Task<IReadOnlyList<RenderedImage>> RenderAsync(Stream source, IReadOnlyList<RenditionSpec> specs, CancellationToken cancellationToken)
        {
            Renders++;
            if (Unsupported)
            {
                throw new UnsupportedImageException("bad");
            }

            if (Crash)
            {
                throw new InvalidOperationException("native crash");
            }

            return Task.FromResult<IReadOnlyList<RenderedImage>>(specs.Select(s => new RenderedImage(s, 10, 10, new byte[] { 1, 2 })).ToList());
        }
    }

    private sealed class StaticUser(CurrentUser? user) : ICurrentUserAccessor
    {
        public CurrentUser? User { get; } = user;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A JPEG header followed by an endless body.</summary>
    private sealed class EndlessJpeg : Stream
    {
        private bool _headerSent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_headerSent)
            {
                _headerSent = true;
                Jpeg.AsSpan(0, Math.Min(count, Jpeg.Length)).CopyTo(buffer.AsSpan(offset));
                return Math.Min(count, Jpeg.Length);
            }

            Array.Clear(buffer, offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Returns the header, then fails like a dropped connection.</summary>
    /// <param name="header">Bytes returned before failing.</param>
    private sealed class BrokenStream(byte[] header) : MemoryStream(header)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            return read > 0 ? read : throw new IOException("connection reset");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            return read > 0 ? read : throw new IOException("connection reset");
        }
    }
}
