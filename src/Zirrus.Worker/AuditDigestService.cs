using Microsoft.Extensions.Options;
using Zirrus.Infrastructure.Audit;

namespace Zirrus.Worker;

/// <summary>Audit digest schedule (<c>Audit</c> section).</summary>
internal sealed class AuditDigestSettings
{
    /// <summary>Time between runs; the first run starts two minutes after startup.</summary>
    public TimeSpan DigestInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many recent digests each run re-verifies.</summary>
    public int VerifyRecent { get; set; } = 48;
}

/// <summary>
/// Seals new audit events into a signed digest and re-verifies recent digests (security.md, Q10).
/// A mismatch is logged as critical: audit records were changed outside the application.
/// Without a configured key the job logs a warning and does nothing.
/// </summary>
/// <param name="services">Root service provider.</param>
/// <param name="settings">Schedule.</param>
/// <param name="logger">Logger.</param>
internal sealed partial class AuditDigestService(IServiceProvider services, IOptions<AuditDigestSettings> settings, ILogger<AuditDigestService> logger)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        using var timer = new PeriodicTimer(settings.Value.DigestInterval);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var digests = scope.ServiceProvider.GetRequiredService<AuditDigests>();
            if (!digests.IsConfigured)
            {
                LogNoKey(logger);
                return;
            }

            if (await digests.SealAsync(stoppingToken) is { } sealedDigest)
            {
                LogSealed(logger, sealedDigest.EventCount, sealedDigest.FromSeq, sealedDigest.ToSeq);
            }

            foreach (var result in await digests.VerifyAsync(settings.Value.VerifyRecent, stoppingToken))
            {
                if (result.Status is DigestStatus.Mismatch)
                {
                    LogMismatch(logger, result.DigestId, result.FromSeq, result.ToSeq);
                }
                else if (result.Status is DigestStatus.UnknownKey)
                {
                    LogUnknownKey(logger, result.DigestId);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit digests disabled: no valid Audit:DigestKey (base64, at least 32 bytes) is configured")]
    private static partial void LogNoKey(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit digest sealed: {Count} events, seq {From}–{To}")]
    private static partial void LogSealed(ILogger logger, int count, long from, long to);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Audit digest {DigestId} does not match its events (seq {From}–{To}): audit records were changed outside the application")]
    private static partial void LogMismatch(ILogger logger, Guid digestId, long from, long to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit digest {DigestId} was signed with a different key; it cannot be verified with the configured key")]
    private static partial void LogUnknownKey(ILogger logger, Guid digestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit digest run failed; retrying at the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
