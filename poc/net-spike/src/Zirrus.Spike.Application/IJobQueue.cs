namespace Zirrus.Spike.Application;

/// <summary>A job claimed by a worker, valid while its lease has not expired.</summary>
/// <param name="Id">Database identifier of the job.</param>
/// <param name="JobKey">Deterministic key: asset ID + pipeline version + stage.</param>
/// <param name="Payload">Stage-specific payload as JSON.</param>
/// <param name="Attempt">Attempt number, starting at 1.</param>
/// <param name="LeaseToken">Token that proves this worker still owns the lease.</param>
public sealed record ClaimedJob(long Id, string JobKey, string Payload, int Attempt, Guid LeaseToken);

/// <summary>
/// Durable job queue port. Job intent is written in the same database transaction as the
/// asset change; implementations dispatch at least once, so handlers must be idempotent.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "IJobQueue is the port name used in ADR 0002.")]
public interface IJobQueue
{
    /// <summary>Claims the next ready job, or returns null when none is ready.</summary>
    /// <param name="workerId">Identifier of the claiming worker, for diagnostics.</param>
    /// <param name="lease">How long the claim is valid without renewal.</param>
    /// <param name="cancellationToken">Cancels the claim attempt.</param>
    Task<ClaimedJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Extends the lease of a running job; returns false if the lease was lost.</summary>
    /// <param name="job">The claimed job.</param>
    /// <param name="lease">New lease duration from now.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    Task<bool> RenewAsync(ClaimedJob job, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Marks a job as done; returns false if the lease was lost first.</summary>
    /// <param name="job">The claimed job.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    Task<bool> CompleteAsync(ClaimedJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failure. Transient failures are retried with exponential backoff until the
    /// attempt limit; permanent failures and exhausted retries move the job to dead letter.
    /// </summary>
    /// <param name="job">The claimed job.</param>
    /// <param name="errorDetail">Structured error category and message.</param>
    /// <param name="permanent">True when retrying cannot succeed.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    Task FailAsync(ClaimedJob job, string errorDetail, bool permanent, CancellationToken cancellationToken);
}
