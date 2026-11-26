using Zirrus.Domain.Assets;

namespace Zirrus.Application.Storage;

/// <summary>A blob the database says must exist.</summary>
/// <param name="AssetId">Asset.</param>
/// <param name="Key">Storage key.</param>
/// <param name="ExpectedSizeBytes">Size recorded in the database, if known.</param>
/// <param name="Kind"><c>original</c> or the rendition kind.</param>
/// <param name="State">Asset state.</param>
public sealed record ExpectedBlob(Guid AssetId, BlobKey Key, long? ExpectedSizeBytes, string Kind, AssetState State);

/// <summary>An asset that has stayed in a transitional state too long.</summary>
/// <param name="AssetId">Asset.</param>
/// <param name="State">Transitional state.</param>
/// <param name="Since">When it entered the state.</param>
public sealed record StaleAsset(Guid AssetId, AssetState State, DateTimeOffset Since);

/// <summary>A running job whose lease has expired (crashed or stuck worker).</summary>
/// <param name="JobKey">Job key.</param>
/// <param name="LockedBy">Last lease holder.</param>
/// <param name="LeaseUntil">Lease end.</param>
public sealed record ExpiredLease(string JobKey, string? LockedBy, DateTimeOffset LeaseUntil);

/// <summary>Read port over asset, rendition and job rows for reconciliation.</summary>
public interface IStorageInventory
{
    /// <summary>Streams every blob that must exist: originals of assets in <see cref="AssetLifecycle.StatesWithOriginal"/> and their renditions.</summary>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Expected blobs.</returns>
    IAsyncEnumerable<ExpectedBlob> ListExpectedBlobsAsync(CancellationToken cancellationToken);

    /// <summary>Lists assets in a transitional state since before a time.</summary>
    /// <param name="changedBefore">Cut-off time.</param>
    /// <param name="limit">Maximum rows.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Stale assets, oldest first.</returns>
    Task<IReadOnlyList<StaleAsset>> ListStaleAsync(DateTimeOffset changedBefore, int limit, CancellationToken cancellationToken);

    /// <summary>Lists running jobs whose lease ended before a time.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="limit">Maximum rows.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Expired leases, oldest first.</returns>
    Task<IReadOnlyList<ExpiredLease>> ListExpiredLeasesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

/// <summary>Thresholds for a reconciliation run.</summary>
/// <param name="StaleAfter">Age after which a transitional state or temporary write counts as stale.</param>
/// <param name="MaxItemsPerCategory">Maximum findings listed per category; counts stay exact.</param>
public sealed record ReconciliationOptions(TimeSpan StaleAfter, int MaxItemsPerCategory = 100)
{
    /// <summary>One hour; uploads and renders normally finish within minutes.</summary>
    public static ReconciliationOptions Default { get; } = new(TimeSpan.FromHours(1));
}

/// <summary>Findings of one category: the exact count and the first items.</summary>
/// <typeparam name="T">Finding type.</typeparam>
/// <param name="Count">Number of findings.</param>
/// <param name="Items">The first findings, up to the configured maximum.</param>
public sealed record Findings<T>(int Count, IReadOnlyList<T> Items);

/// <summary>Result of a reconciliation run. Report only: nothing is changed or deleted.</summary>
/// <param name="StartedAt">Start time.</param>
/// <param name="FinishedAt">End time.</param>
/// <param name="CheckedBlobs">Expected blobs checked.</param>
/// <param name="MissingBlobs">Expected blobs absent from storage.</param>
/// <param name="SizeMismatches">Blobs whose stored size differs from the database.</param>
/// <param name="OrphanTemporaries">Temporary writes older than the threshold.</param>
/// <param name="StaleTransitions">Assets stuck in uploading, processing or deleting.</param>
/// <param name="ExpiredLeases">Running jobs with expired leases.</param>
public sealed record ReconciliationReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int CheckedBlobs,
    Findings<ExpectedBlob> MissingBlobs,
    Findings<ExpectedBlob> SizeMismatches,
    Findings<TemporaryBlob> OrphanTemporaries,
    Findings<StaleAsset> StaleTransitions,
    Findings<ExpiredLease> ExpiredLeases)
{
    /// <summary>True when nothing needs attention.</summary>
    public bool IsClean => MissingBlobs.Count + SizeMismatches.Count + OrphanTemporaries.Count + StaleTransitions.Count + ExpiredLeases.Count == 0;
}

/// <summary>
/// Use case: compare the database with storage and report missing blobs, size mismatches, orphan
/// temporary writes, stale transitions and expired leases (ADR 0006). It never repairs or deletes;
/// destructive cleanup needs a reviewed report first.
/// </summary>
/// <param name="inventory">Database read port.</param>
/// <param name="store">Blob store.</param>
/// <param name="time">Time source.</param>
public sealed class ReconcileStorage(IStorageInventory inventory, IBlobStore store, TimeProvider time)
{
    /// <summary>Runs one reconciliation pass.</summary>
    /// <param name="options">Thresholds.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The report.</returns>
    public async Task<ReconciliationReport> RunAsync(ReconciliationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = time.GetUtcNow();
        var cutoff = started - options.StaleAfter;
        var max = options.MaxItemsPerCategory;

        var checkedBlobs = 0;
        var missing = new Collector<ExpectedBlob>(max);
        var mismatched = new Collector<ExpectedBlob>(max);
        await foreach (var expected in inventory.ListExpectedBlobsAsync(cancellationToken))
        {
            checkedBlobs++;
            var info = await store.StatAsync(expected.Key, cancellationToken);
            if (info is null)
            {
                missing.Add(expected);
            }
            else if (expected.ExpectedSizeBytes is { } size && size != info.SizeBytes)
            {
                mismatched.Add(expected);
            }
        }

        var orphans = new Collector<TemporaryBlob>(max);
        await foreach (var temporary in store.ListTemporaryAsync(cutoff, cancellationToken))
        {
            orphans.Add(temporary);
        }

        var stale = await inventory.ListStaleAsync(cutoff, max + 1, cancellationToken);
        var leases = await inventory.ListExpiredLeasesAsync(started, max + 1, cancellationToken);

        return new ReconciliationReport(
            started,
            time.GetUtcNow(),
            checkedBlobs,
            missing.ToFindings(),
            mismatched.ToFindings(),
            orphans.ToFindings(),
            Truncate(stale, max),
            Truncate(leases, max));
    }

    // Counts beyond the limit are reported as "limit + 1" for database lists; callers see that more exist.
    private static Findings<T> Truncate<T>(IReadOnlyList<T> rows, int max) => new(rows.Count, rows.Take(max).ToList());

    private sealed class Collector<T>(int max)
    {
        private readonly List<T> _items = [];
        private int _count;

        public void Add(T item)
        {
            _count++;
            if (_items.Count < max)
            {
                _items.Add(item);
            }
        }

        public Findings<T> ToFindings() => new(_count, _items);
    }
}
