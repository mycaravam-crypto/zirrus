namespace Zirrus.Worker;

/// <summary>
/// Placeholder background service proving the worker host runs. The ingestion job consumer
/// (in-house Npgsql queue, ADR 0002) replaces it in W4 (#24).
/// </summary>
/// <param name="logger">Logger.</param>
internal sealed partial class HeartbeatService(ILogger<HeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            LogHeartbeat(logger);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker heartbeat")]
    private static partial void LogHeartbeat(ILogger logger);
}
