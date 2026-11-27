using System.Runtime.CompilerServices;
using Zirrus.Application;
using Zirrus.Application.Assets;
using Zirrus.Application.Catalogs;
using Zirrus.Application.Identity;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Domain.Tests;

/// <summary>Asset read and metadata-edit use cases, and the reconciliation report, with in-memory ports.</summary>
public sealed class AssetUseCaseTests
{
    private const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationContext Organization = new(Guid.NewGuid());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly Catalog _press = Catalog.Create(Organization.Id, "Press", Now);
    private readonly Roles _roles = new();
    private readonly Assets _assets = new();
    private readonly Asset _active;
    private readonly Asset _pending;

    public AssetUseCaseTests()
    {
        _roles.Catalogs.Add(_press.Id);
        _roles.Grants[("erin", _press.Id)] = CatalogRole.Editor;
        _roles.Grants[("victor", _press.Id)] = CatalogRole.Viewer;
        _active = Asset.Start(_press, "a.jpg", "image/jpeg", "erin", Now);
        _active.MarkStored(Sha, 3, Now);
        _active.BeginProcessing(Now);
        _active.Activate(Now);
        _pending = Asset.Start(_press, "b.jpg", "image/jpeg", "erin", Now);
        _assets.Items.AddRange([_active, _pending]);
    }

    [Theory]
    [InlineData("victor", true, false)]
    [InlineData("erin", true, true)]
    [InlineData("admin", true, true)]
    [InlineData("oscar", false, false)]
    [InlineData(null, false, false)]
    public async Task Get_shows_active_assets_to_readers_and_pending_ones_to_editors(string? who, bool seesActive, bool seesPending)
    {
        var useCases = For(who);
        Assert.Equal(seesActive ? OperationStatus.Ok : OperationStatus.NotFound, (await useCases.GetAsync(_press.Id, _active.Id, Ct)).Status);
        Assert.Equal(seesPending ? OperationStatus.Ok : OperationStatus.NotFound, (await useCases.GetAsync(_press.Id, _pending.Id, Ct)).Status);
    }

    [Fact]
    public async Task Get_maps_the_asset()
    {
        var details = (await For("victor").GetAsync(_press.Id, _active.Id, Ct)).Value!;
        Assert.Equal((_active.Id, "active", Sha, 3L, _active.Revision), (details.Id, details.State, details.Sha256, details.SizeBytes, details.Revision));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, details.Metadata.ValueKind);
    }

    [Fact]
    public async Task Get_of_an_unknown_asset_is_not_found() =>
        Assert.Equal(OperationStatus.NotFound, (await For("erin").GetAsync(_press.Id, Guid.NewGuid(), Ct)).Status);

    [Fact]
    public async Task Editors_update_metadata_with_the_current_revision()
    {
        var result = await For("erin").UpdateMetadataAsync(_press.Id, _active.Id, """{"title":"Harbour"}""", _active.Revision, Ct);
        Assert.Equal(OperationStatus.Ok, result.Status);
        Assert.Equal("Harbour", result.Value!.Metadata.GetProperty("title").GetString());
        Assert.Equal(1, _assets.Saves);
    }

    [Fact]
    public async Task A_stale_revision_is_a_failed_precondition_and_nothing_is_saved()
    {
        var result = await For("erin").UpdateMetadataAsync(_press.Id, _active.Id, "{}", _active.Revision - 1, Ct);
        Assert.Equal(OperationStatus.PreconditionFailed, result.Status);
        Assert.Equal(0, _assets.Saves);
    }

    [Fact]
    public async Task A_concurrent_writer_detected_by_the_store_is_a_failed_precondition()
    {
        _assets.ConflictOnSave = true;
        var result = await For("erin").UpdateMetadataAsync(_press.Id, _active.Id, "{}", _active.Revision, Ct);
        Assert.Equal(OperationStatus.PreconditionFailed, result.Status);
    }

    [Fact]
    public async Task Viewers_cannot_edit_and_outsiders_do_not_learn_the_asset_exists()
    {
        Assert.Equal(OperationStatus.Forbidden, (await For("victor").UpdateMetadataAsync(_press.Id, _active.Id, "{}", _active.Revision, Ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await For("victor").UpdateMetadataAsync(_press.Id, _pending.Id, "{}", _pending.Revision, Ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await For("oscar").UpdateMetadataAsync(_press.Id, _active.Id, "{}", _active.Revision, Ct)).Status);
    }

    [Fact]
    public async Task Invalid_metadata_is_rejected() =>
        Assert.Equal(OperationStatus.Invalid, (await For("erin").UpdateMetadataAsync(_press.Id, _active.Id, "[1]", _active.Revision, Ct)).Status);

    [Fact]
    public async Task Assets_being_deleted_cannot_be_edited_by_anyone()
    {
        var deleting = Asset.Start(_press, "c.jpg", "image/jpeg", "erin", Now);
        deleting.Fail("upload: aborted", Now);
        deleting.MarkForDeletion(Now);
        _assets.Items.Add(deleting);
        Assert.Equal(OperationStatus.NotFound, (await For("erin").UpdateMetadataAsync(_press.Id, deleting.Id, "{}", deleting.Revision, Ct)).Status);
    }

    [Fact]
    public async Task Reconciliation_reports_every_category_and_caps_listed_items()
    {
        var ok = BlobKey.Parse("originals/ok");
        var gone = BlobKey.Parse("originals/gone");
        var resized = BlobKey.Parse("originals/resized");
        var inventory = new Inventory
        {
            Expected =
            [
                new(Guid.NewGuid(), ok, 10, "original", AssetState.Active),
                new(Guid.NewGuid(), gone, 10, "original", AssetState.Active),
                new(Guid.NewGuid(), BlobKey.Parse("originals/gone2"), null, "thumbnail", AssetState.Active),
                new(Guid.NewGuid(), resized, 10, "original", AssetState.Quarantined),
                new(Guid.NewGuid(), BlobKey.Parse("originals/unknown-size"), null, "original", AssetState.Processing),
            ],
            Stale = [new(Guid.NewGuid(), AssetState.Uploading, Now.AddDays(-1))],
            Leases = [new("job", "worker-1", Now.AddMinutes(-5))],
        };
        var store = new Blobs();
        store.Sizes[ok] = 10;
        store.Sizes[resized] = 11;
        store.Sizes[BlobKey.Parse("originals/unknown-size")] = 5;
        store.Temporaries.Add(new TemporaryBlob("old.part", 7, Now.AddDays(-2)));
        store.Temporaries.Add(new TemporaryBlob("fresh.part", 7, Now));

        var report = await new ReconcileStorage(inventory, store, new FixedTime(Now)).RunAsync(new ReconciliationOptions(TimeSpan.FromHours(1), MaxItemsPerCategory: 1), Ct);

        Assert.Equal(5, report.CheckedBlobs);
        Assert.Equal(2, report.MissingBlobs.Count);
        Assert.Equal([gone], report.MissingBlobs.Items.Select(b => b.Key));
        Assert.Equal([resized], report.SizeMismatches.Items.Select(b => b.Key));
        Assert.Equal(["old.part"], report.OrphanTemporaries.Items.Select(t => t.Name));
        Assert.Equal((1, 1), (report.StaleTransitions.Count, report.ExpiredLeases.Count));
        Assert.Equal(Now - TimeSpan.FromHours(1), inventory.StaleCutoff);
        Assert.False(report.IsClean);
        Assert.Equal(0, store.Deletes); // report only
    }

    [Fact]
    public async Task Reconciliation_of_a_consistent_store_is_clean()
    {
        var report = await new ReconcileStorage(new Inventory(), new Blobs(), new FixedTime(Now)).RunAsync(ReconciliationOptions.Default, Ct);
        Assert.True(report.IsClean);
        Assert.Equal(Now, report.StartedAt);
    }

    private AssetUseCases For(string? who)
    {
        CurrentUser? user = who switch
        {
            null => null,
            "admin" => new CurrentUser("alice", "Alice", IsOrganizationAdmin: true),
            _ => new CurrentUser(who, who, IsOrganizationAdmin: false),
        };
        return new AssetUseCases(_assets, new CatalogAccess(_roles, new StaticUser(user), Organization), new FixedTime(Now), new Blobs());
    }

    private sealed class Roles : ICatalogReader
    {
        public HashSet<Guid> Catalogs { get; } = [];

        public Dictionary<(string, Guid), CatalogRole> Grants { get; } = [];

        public Task<IReadOnlyList<(Guid Id, string Name, DateTimeOffset CreatedAt, CatalogRole? Role)>> ListAsync(Guid organizationId, string? subject, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogRole?> FindRoleAsync(Guid organizationId, Guid catalogId, string subject, CancellationToken cancellationToken) =>
            Task.FromResult(Grants.TryGetValue((subject, catalogId), out var role) ? role : (CatalogRole?)null);

        public Task<bool> ExistsAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
            Task.FromResult(Catalogs.Contains(catalogId));
    }

    private sealed class Assets : IAssetStore
    {
        public List<Asset> Items { get; } = [];

        public int Saves { get; private set; }

        public bool ConflictOnSave { get; set; }

        public Task<Asset?> FindAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(a => a.CatalogId == catalogId && a.Id == assetId));

        public Task<Rendition?> FindRenditionAsync(Guid assetId, string kind, CancellationToken cancellationToken) =>
            Task.FromResult<Rendition?>(null);

        public Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(!ConflictOnSave);
        }
    }

    private sealed class Inventory : IStorageInventory
    {
        public List<ExpectedBlob> Expected { get; init; } = [];

        public List<StaleAsset> Stale { get; init; } = [];

        public List<ExpiredLease> Leases { get; init; } = [];

        public DateTimeOffset StaleCutoff { get; private set; }

        public async IAsyncEnumerable<ExpectedBlob> ListExpectedBlobsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var blob in Expected)
            {
                yield return blob;
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<StaleAsset>> ListStaleAsync(DateTimeOffset changedBefore, int limit, CancellationToken cancellationToken)
        {
            StaleCutoff = changedBefore;
            return Task.FromResult<IReadOnlyList<StaleAsset>>(Stale.Take(limit).ToList());
        }

        public Task<IReadOnlyList<ExpiredLease>> ListExpiredLeasesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExpiredLease>>(Leases.Take(limit).ToList());
    }

    private sealed class Blobs : IBlobStore
    {
        public Dictionary<BlobKey, long> Sizes { get; } = [];

        public List<TemporaryBlob> Temporaries { get; } = [];

        public int Deletes { get; private set; }

        public Task<StoredBlob> PutAsync(BlobKey key, Stream content, string? expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BlobRead?> OpenAsync(BlobKey key, ByteRange? range, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BlobInfo?> StatAsync(BlobKey key, CancellationToken cancellationToken) =>
            Task.FromResult(Sizes.TryGetValue(key, out var size) ? new BlobInfo(key, size, Now) : null);

        public Task<bool> DeleteAsync(BlobKey key, CancellationToken cancellationToken)
        {
            Deletes++;
            return Task.FromResult(false);
        }

        public async IAsyncEnumerable<TemporaryBlob> ListTemporaryAsync(DateTimeOffset olderThan, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var t in Temporaries.Where(t => t.LastModified < olderThan))
            {
                yield return t;
            }

            await Task.CompletedTask;
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
}
