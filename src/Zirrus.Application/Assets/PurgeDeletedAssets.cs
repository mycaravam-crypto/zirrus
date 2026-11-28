using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;

namespace Zirrus.Application.Assets;

/// <summary>A soft-deleted asset whose retention period has ended, with every blob it owns.</summary>
/// <param name="AssetId">Asset.</param>
/// <param name="CatalogId">Catalog.</param>
/// <param name="OriginalKey">Original blob.</param>
/// <param name="RenditionKeys">Rendition blobs.</param>
public sealed record PurgeCandidate(Guid AssetId, Guid CatalogId, BlobKey OriginalKey, IReadOnlyList<BlobKey> RenditionKeys);

/// <summary>An asset whose purge failed in this run; it stays soft-deleted and is retried next run.</summary>
/// <param name="AssetId">Asset.</param>
/// <param name="Error">Exception type and message, without stack trace.</param>
public sealed record PurgeFailure(Guid AssetId, string Error);

/// <summary>Result of one purge run.</summary>
/// <param name="Cutoff">Assets soft-deleted before this time were eligible.</param>
/// <param name="Purged">Assets purged.</param>
/// <param name="Failures">Assets whose blob deletion failed; still soft-deleted.</param>
/// <param name="BatchFull">True when the run hit its batch limit, so more eligible assets may remain.</param>
public sealed record PurgeReport(DateTimeOffset Cutoff, int Purged, IReadOnlyList<PurgeFailure> Failures, bool BatchFull);

/// <summary>Persistence for the retention purge of soft-deleted assets.</summary>
public interface IAssetPurgeStore
{
    /// <summary>Lists assets soft-deleted (state <see cref="AssetState.Deleting"/>) before a time, oldest first.</summary>
    /// <param name="deletedBefore">Retention cutoff.</param>
    /// <param name="limit">Maximum assets.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Candidates with their blob keys.</returns>
    Task<IReadOnlyList<PurgeCandidate>> ListExpiredAsync(DateTimeOffset deletedBefore, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// After the blobs are gone: in one transaction, re-checks that the asset is still soft-deleted
    /// before the cutoff, removes its rendition rows and category links, marks it
    /// <see cref="AssetState.Deleted"/> (the row stays for audit) and records the purge audit event.
    /// </summary>
    /// <param name="candidate">Asset.</param>
    /// <param name="deletedBefore">Retention cutoff, checked again under a row lock.</param>
    /// <param name="now">UTC time.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>False when the asset no longer qualifies; nothing is changed then.</returns>
    Task<bool> CompleteAsync(PurgeCandidate candidate, DateTimeOffset deletedBefore, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Physically removes soft-deleted assets after the retention period (#49, ADR 0006). Bounded: at most
/// <c>batchSize</c> assets per run. Idempotent and retryable: blobs are deleted first (a missing blob counts
/// as deleted) and the asset is marked deleted only afterwards, so a crash or a failed blob deletion leaves
/// it soft-deleted, still unreachable, and the next run repeats the work. It never touches an asset that
/// is not soft-deleted or whose retention has not ended.
/// </summary>
/// <param name="store">Purge persistence.</param>
/// <param name="blobs">Blob store.</param>
/// <param name="time">Time source.</param>
public sealed class PurgeDeletedAssets(IAssetPurgeStore store, IBlobStore blobs, TimeProvider time)
{
    /// <summary>Audit actor for purges.</summary>
    public const string Actor = "system:retention";

    /// <summary>Runs one purge pass.</summary>
    /// <param name="retention">How long soft-deleted assets are kept; must be positive.</param>
    /// <param name="batchSize">Maximum assets per run; must be positive.</param>
    /// <param name="cancellationToken">Cancels the run between assets.</param>
    /// <returns>The report.</returns>
    public async Task<PurgeReport> RunAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);
        var cutoff = time.GetUtcNow() - retention;
        var candidates = await store.ListExpiredAsync(cutoff, batchSize, cancellationToken);
        var purged = 0;
        var failures = new List<PurgeFailure>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var key in candidate.RenditionKeys.Append(candidate.OriginalKey))
                {
                    await blobs.DeleteAsync(key, cancellationToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures.Add(new PurgeFailure(candidate.AssetId, $"{e.GetType().Name}: {e.Message}"));
                continue;
            }

            if (await store.CompleteAsync(candidate, cutoff, time.GetUtcNow(), cancellationToken))
            {
                purged++;
            }
        }

        return new PurgeReport(cutoff, purged, failures, candidates.Count == batchSize);
    }
}
