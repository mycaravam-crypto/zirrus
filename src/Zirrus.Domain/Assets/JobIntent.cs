namespace Zirrus.Domain.Assets;

/// <summary>Queue state of a job (ADR 0002).</summary>
public enum JobState
{
    /// <summary>Waiting to be claimed (possibly after a backoff).</summary>
    Ready = 0,

    /// <summary>Claimed by a worker under a lease.</summary>
    Running = 1,

    /// <summary>Completed.</summary>
    Done = 2,

    /// <summary>Dead-lettered: permanent failure or attempts exhausted; visible to operators.</summary>
    Dead = 3,
}

/// <summary>
/// Durable job intent, written in the same transaction as the asset change that requires it
/// (outbox, ADR 0002). The deterministic key makes re-dispatch idempotent.
/// </summary>
public sealed class JobIntent
{
    /// <summary>Maximum length of a stage name.</summary>
    public const int MaxStageLength = 32;

    /// <summary>Default attempts before dead-lettering.</summary>
    public const int DefaultMaxAttempts = 5;

    // Materialization constructor for the persistence layer.
    private JobIntent()
    {
        JobKey = Stage = Payload = string.Empty;
    }

    private JobIntent(string jobKey, string stage, Guid? catalogId, Guid? assetId, string payload, DateTimeOffset now)
    {
        JobKey = jobKey;
        Stage = stage;
        CatalogId = catalogId;
        AssetId = assetId;
        Payload = payload;
        State = JobState.Ready;
        MaxAttempts = DefaultMaxAttempts;
        RunAfter = now;
        CreatedAt = now;
    }

    /// <summary>Database identifier (sequence).</summary>
    public long Id { get; private set; }

    /// <summary>Deterministic, unique key: <c>{asset}:{stage}:v{pipeline}</c>.</summary>
    public string JobKey { get; private set; }

    /// <summary>Stage, e.g. <c>scan</c>, <c>extract</c>, <c>render</c>.</summary>
    public string Stage { get; private set; }

    /// <summary>Catalog of the asset, when the job concerns one.</summary>
    public Guid? CatalogId { get; private set; }

    /// <summary>Asset the job concerns, if any.</summary>
    public Guid? AssetId { get; private set; }

    /// <summary>Stage payload (JSONB object).</summary>
    public string Payload { get; private set; }

    /// <summary>Queue state.</summary>
    public JobState State { get; private set; }

    /// <summary>Claims so far.</summary>
    public int Attempts { get; private set; }

    /// <summary>Attempts allowed before dead-lettering.</summary>
    public int MaxAttempts { get; private set; }

    /// <summary>Earliest claim time (backoff).</summary>
    public DateTimeOffset RunAfter { get; private set; }

    /// <summary>End of the current lease.</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }

    /// <summary>Token of the current lease holder.</summary>
    public Guid? LeaseToken { get; private set; }

    /// <summary>Worker holding the lease, for diagnostics.</summary>
    public string? LockedBy { get; private set; }

    /// <summary>Last structured error.</summary>
    public string? LastError { get; private set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Completion time (UTC).</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Creates the intent to run a pipeline stage for an asset.</summary>
    /// <param name="asset">Asset to process.</param>
    /// <param name="stage">Stage: lowercase letters, digits and '-'.</param>
    /// <param name="pipelineVersion">Pipeline version (≥ 1); a new version yields a new key.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The job intent.</returns>
    public static JobIntent ForAsset(Asset asset, string stage, int pipelineVersion, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentOutOfRangeException.ThrowIfLessThan(pipelineVersion, 1);
        if (stage.Length == 0 || stage.Length > MaxStageLength || !stage.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            throw new ArgumentException($"Stage must be 1–{MaxStageLength} characters of [a-z0-9-].", nameof(stage));
        }

        return new JobIntent($"{asset.Id:N}:{stage}:v{pipelineVersion}", stage, asset.CatalogId, asset.Id, "{}", now);
    }
}
