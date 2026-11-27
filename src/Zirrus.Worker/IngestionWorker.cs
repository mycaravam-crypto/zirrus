using Zirrus.Application.Ingestion;

namespace Zirrus.Worker;

/// <summary>Job consumer settings (<c>Ingestion</c> section).</summary>
internal sealed class IngestionSettings
{
    /// <summary>Jobs processed in parallel per worker process.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>Lease per claim; renewed while the stage runs.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Delay before polling again when the queue is empty.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Claims jobs from the PostgreSQL queue and runs <see cref="IngestionPipeline"/> stages (ADR 0002).
/// The lease is renewed while a stage runs; if the process dies, the lease expires and another
/// worker retries the job, which is safe because stages are idempotent.
/// </summary>
/// <param name="services">Root provider; each job gets its own scope.</param>
/// <param name="queue">Job queue.</param>
/// <param name="settings">Settings.</param>
/// <param name="logger">Logger.</param>
internal sealed partial class IngestionWorker(
    IServiceProvider services,
    IJobQueue queue,
    Microsoft.Extensions.Options.IOptions<IngestionSettings> settings,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, settings.Value.Concurrency)).Select(i => LoopAsync($"{_workerId}:{i}", stoppingToken)));

    private async Task LoopAsync(string workerId, CancellationToken stoppingToken)
    {
        var options = settings.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await queue.ClaimAsync(workerId, options.Lease, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(options.IdleDelay, stoppingToken);
                    continue;
                }

                await RunWithRenewalAsync(job, options.Lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                LogLoopError(logger, e);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunWithRenewalAsync(ClaimedJob job, TimeSpan lease, CancellationToken stoppingToken)
    {
        using var stageCancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewAsync(job, lease, stageCancel);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<IngestionPipeline>().RunAsync(job, stageCancel.Token);
            LogOutcome(logger, job.JobKey, job.Attempt, outcome);
        }
        finally
        {
            await stageCancel.CancelAsync();
            await renewal;
        }
    }

    private async Task RenewAsync(ClaimedJob job, TimeSpan lease, CancellationTokenSource stageCancel)
    {
        try
        {
            using var timer = new PeriodicTimer(lease / 3);
            while (await timer.WaitForNextTickAsync(stageCancel.Token))
            {
                if (!await queue.RenewAsync(job, lease, stageCancel.Token))
                {
                    // Another worker took over: stop the stage; its commit would be refused anyway.
                    LogLeaseLost(logger, job.JobKey);
                    await stageCancel.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobKey} attempt {Attempt}: {Outcome}")]
    private static partial void LogOutcome(ILogger logger, string jobKey, int attempt, StageOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease lost for job {JobKey}; stopping the stage")]
    private static partial void LogLeaseLost(ILogger logger, string jobKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job loop error; backing off")]
    private static partial void LogLoopError(ILogger logger, Exception exception);
}
