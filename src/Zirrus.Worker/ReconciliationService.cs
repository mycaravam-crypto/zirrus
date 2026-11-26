using System.Text.Json;
using Zirrus.Application.Storage;

namespace Zirrus.Worker;

/// <summary>Reconciliation schedule (<c>Reconciliation</c> section).</summary>
internal sealed class ReconciliationSettings
{
    /// <summary>Time between runs; the first run starts one minute after startup.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Age after which transitional states and temporary writes are reported.</summary>
    public TimeSpan StaleAfter { get; set; } = ReconciliationOptions.Default.StaleAfter;

    /// <summary>Set to false to disable the job.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Runs <see cref="ReconcileStorage"/> periodically and logs the report (ADR 0006). Report only:
/// it never deletes or repairs. A run that fails is logged and retried at the next interval.
/// </summary>
/// <param name="services">Root service provider.</param>
/// <param name="settings">Schedule.</param>
/// <param name="logger">Logger.</param>
internal sealed partial class ReconciliationService(IServiceProvider services, Microsoft.Extensions.Options.IOptions<ReconciliationSettings> settings, ILogger<ReconciliationService> logger)
    : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = settings.Value;
        if (!options.Enabled)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            await RunOnceAsync(options, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(ReconciliationSettings options, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var reconcile = scope.ServiceProvider.GetRequiredService<ReconcileStorage>();
            var report = await reconcile.RunAsync(new ReconciliationOptions(options.StaleAfter), stoppingToken);
            var json = JsonSerializer.Serialize(report, Json);
            if (report.IsClean)
            {
                LogClean(logger, report.CheckedBlobs);
            }
            else
            {
                LogFindings(logger, report.MissingBlobs.Count, report.SizeMismatches.Count, report.OrphanTemporaries.Count,
                    report.StaleTransitions.Count, report.ExpiredLeases.Count, json);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation clean: {CheckedBlobs} blobs checked")]
    private static partial void LogClean(ILogger logger, int checkedBlobs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation findings: {Missing} missing, {SizeMismatches} size mismatches, {Orphans} orphan temporary files, {Stale} stale transitions, {ExpiredLeases} expired leases. Report: {Report}")]
    private static partial void LogFindings(ILogger logger, int missing, int sizeMismatches, int orphans, int stale, int expiredLeases, string report);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation run failed; retrying at the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
