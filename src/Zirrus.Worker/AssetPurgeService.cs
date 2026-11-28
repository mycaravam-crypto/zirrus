using Microsoft.Extensions.Options;
using Zirrus.Application.Assets;

namespace Zirrus.Worker;

/// <summary>Soft-deleted asset retention (<c>Retention</c> section).</summary>
internal sealed class AssetPurgeSettings
{
    /// <summary>
    /// How long soft-deleted assets are kept before their blobs are purged. No default: the period is a
    /// policy decision (proposed 30 days, security.md); while it is unset the purge does not run.
    /// </summary>
    public TimeSpan? SoftDeletedAssets { get; set; }

    /// <summary>Time between runs; the first run starts three minutes after startup.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum assets purged per run.</summary>
    public int PurgeBatchSize { get; set; } = 100;
}

/// <summary>
/// Runs <see cref="PurgeDeletedAssets"/> periodically (#49). Each purged asset is audited as
/// <c>asset.purge</c>. Assets whose blobs cannot be deleted stay soft-deleted and unreachable, are logged
/// as warnings and are retried at the next run. A run that fails is logged and retried at the next interval.
/// </summary>
/// <param name="services">Root service provider.</param>
/// <param name="settings">Retention and schedule.</param>
/// <param name="logger">Logger.</param>
internal sealed partial class AssetPurgeService(IServiceProvider services, IOptions<AssetPurgeSettings> settings, ILogger<AssetPurgeService> logger)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        if (options.SoftDeletedAssets is not { } retention || retention <= TimeSpan.Zero)
        {
            LogDisabled(logger);
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        using var timer = new PeriodicTimer(options.PurgeInterval);
        do
        {
            await RunOnceAsync(retention, Math.Max(1, options.PurgeBatchSize), stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(TimeSpan retention, int batchSize, CancellationToken stoppingToken)
    {
        try
        {
            // One scope per run keeps the change tracker small; each asset commits in its own transaction.
            await using var scope = services.CreateAsyncScope();
            var report = await scope.ServiceProvider.GetRequiredService<PurgeDeletedAssets>().RunAsync(retention, batchSize, stoppingToken);
            if (report.Purged > 0 || report.Failures.Count > 0)
            {
                LogRun(logger, report.Purged, report.Failures.Count, report.Cutoff, report.BatchFull);
            }

            foreach (var failure in report.Failures)
            {
                LogFailure(logger, failure.AssetId, failure.Error);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Asset purge disabled: Retention:SoftDeletedAssets is not configured")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Asset purge: {Purged} purged, {Failed} failed (soft-deleted before {Cutoff}); batch limit reached: {BatchFull}")]
    private static partial void LogRun(ILogger logger, int purged, int failed, DateTimeOffset cutoff, bool batchFull);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Asset {AssetId} could not be purged and stays soft-deleted; retrying at the next run: {Error}")]
    private static partial void LogFailure(ILogger logger, Guid assetId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Asset purge run failed; retrying at the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
