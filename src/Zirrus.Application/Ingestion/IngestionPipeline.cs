using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;

namespace Zirrus.Application.Ingestion;

/// <summary>What happened to a claimed job.</summary>
public enum StageOutcome
{
    /// <summary>The stage's effects and the job completion were committed together.</summary>
    Completed,

    /// <summary>A transient failure was recorded; the queue retries with backoff or dead-letters.</summary>
    Retrying,

    /// <summary>Another worker took over the job; nothing was committed.</summary>
    LeaseLost,
}

/// <summary>
/// Runs pipeline stages for claimed jobs (ingestion-api-ui.md steps 2–4). Each stage checks the
/// asset state first, so a redelivered job is a no-op, and commits its effects together with the job
/// completion; nothing is published before the asset is active.
/// </summary>
/// <param name="store">Transactional writes.</param>
/// <param name="blobs">Blob store.</param>
/// <param name="scanner">Malware scanner.</param>
/// <param name="renderer">Renderer.</param>
/// <param name="extractor">Embedded-metadata extractor.</param>
/// <param name="queue">Job queue (failures).</param>
/// <param name="time">Time source.</param>
public sealed class IngestionPipeline(
    IIngestionStore store,
    IBlobStore blobs,
    IMalwareScanner scanner,
    IImageRenderer renderer,
    IMetadataExtractor extractor,
    IJobQueue queue,
    TimeProvider time)
{
    /// <summary>Runs the stage of a claimed job.</summary>
    /// <param name="job">Claimed job.</param>
    /// <param name="cancellationToken">Cancels the stage (worker shutdown); the lease then expires and another worker retries.</param>
    /// <returns>The outcome.</returns>
    public async Task<StageOutcome> RunAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var asset = job is { CatalogId: { } catalogId, AssetId: { } assetId }
            ? await store.FindAssetAsync(catalogId, assetId, cancellationToken)
            : null;
        if (asset is null)
        {
            return await CompleteAsync(job, cancellationToken); // asset gone: nothing left to do
        }

        try
        {
            return job.Stage switch
            {
                PipelineStages.Scan => await ScanAsync(job, asset, cancellationToken),
                PipelineStages.Process => await ProcessAsync(job, asset, cancellationToken),
                _ => await FailAsync(job, asset, $"pipeline: unknown stage '{job.Stage}'", permanent: true, cancellationToken),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return await FailAsync(job, asset, $"{job.Stage}: {e.GetType().Name}: {e.Message}", permanent: false, cancellationToken);
        }
    }

    private async Task<StageOutcome> ScanAsync(ClaimedJob job, Asset asset, CancellationToken cancellationToken)
    {
        if (asset.State != AssetState.Quarantined)
        {
            return await CompleteAsync(job, cancellationToken); // already scanned by an earlier delivery
        }

        ScanResult result;
        await using (var original = await OpenOriginalAsync(asset, cancellationToken))
        {
            try
            {
                result = await scanner.ScanAsync(original.Content, cancellationToken);
            }
            catch (ScannerUnavailableException e)
            {
                // Fail closed: the asset stays quarantined; the job retries and is dead-lettered for operators.
                await queue.FailAsync(job, $"scan: scanner unavailable: {e.Message}", permanent: false, cancellationToken);
                return StageOutcome.Retrying;
            }
        }

        var now = time.GetUtcNow();
        if (result.Clean)
        {
            asset.BeginProcessing(now);
            store.Add(JobIntent.ForAsset(asset, PipelineStages.Process, PipelineStages.Version, now));
        }
        else
        {
            asset.Reject($"malware: {result.Signature ?? "threat detected"}", now);
        }

        return await CompleteAsync(job, cancellationToken);
    }

    private async Task<StageOutcome> ProcessAsync(ClaimedJob job, Asset asset, CancellationToken cancellationToken)
    {
        if (asset.State != AssetState.Processing)
        {
            return await CompleteAsync(job, cancellationToken);
        }

        AssetMetadata? embedded;
        IReadOnlyList<RenderedImage> images;
        try
        {
            await using (var original = await OpenOriginalAsync(asset, cancellationToken))
            {
                embedded = await extractor.ExtractAsync(original.Content, cancellationToken);
            }

            await using (var original = await OpenOriginalAsync(asset, cancellationToken))
            {
                images = await renderer.RenderAsync(original.Content, RenditionSpec.Default, cancellationToken);
            }
        }
        catch (UnsupportedImageException e)
        {
            asset.Reject($"decode: {e.Message}", time.GetUtcNow());
            return await CompleteAsync(job, cancellationToken);
        }

        var now = time.GetUtcNow();
        foreach (var image in images)
        {
            var key = BlobKey.ForRendition(asset.CatalogId, asset.Id, image.Spec.Kind, PipelineStages.Version);
            StoredBlob stored;
            try
            {
                stored = await blobs.PutAsync(key, new MemoryStream(image.Content.ToArray(), writable: false), null, cancellationToken);
            }
            catch (BlobConflictException)
            {
                // An earlier, interrupted attempt left a different encoding; renditions are derived, so replace it.
                await blobs.DeleteAsync(key, cancellationToken);
                stored = await blobs.PutAsync(key, new MemoryStream(image.Content.ToArray(), writable: false), null, cancellationToken);
            }

            store.AddOrReplace(Rendition.Create(asset, image.Spec.Kind, PipelineStages.Version, image.Spec.MediaType,
                image.Width, image.Height, stored.SizeBytes, stored.Sha256, now));
        }

        if (embedded is not null)
        {
            asset.SetEmbeddedMetadata(embedded, now);
        }

        asset.Activate(now);
        return await CompleteAsync(job, cancellationToken);
    }

    private async Task<BlobRead> OpenOriginalAsync(Asset asset, CancellationToken cancellationToken) =>
        await blobs.OpenAsync(BlobKey.Parse(asset.OriginalKey), null, cancellationToken)
        ?? throw new FileNotFoundException($"Original of asset {asset.Id} is missing from storage.");

    private async Task<StageOutcome> FailAsync(ClaimedJob job, Asset asset, string error, bool permanent, CancellationToken cancellationToken)
    {
        // Processing that will not be retried leaves the asset failed so editors see it and can retry.
        if ((permanent || job.IsLastAttempt) && asset.State == AssetState.Processing)
        {
            asset.Fail(error, time.GetUtcNow());
            await store.SaveChangesAsync(cancellationToken);
        }

        await queue.FailAsync(job, error, permanent, cancellationToken);
        return StageOutcome.Retrying;
    }

    private async Task<StageOutcome> CompleteAsync(ClaimedJob job, CancellationToken cancellationToken) =>
        await store.CompleteAsync(job, cancellationToken) ? StageOutcome.Completed : StageOutcome.LeaseLost;
}
