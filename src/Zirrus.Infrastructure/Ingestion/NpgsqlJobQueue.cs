using Npgsql;
using Zirrus.Application.Ingestion;

namespace Zirrus.Infrastructure.Ingestion;

/// <summary>
/// PostgreSQL job queue (ADR 0002), validated in the spike with 20 worker kills. Jobs are claimed
/// with <c>FOR UPDATE SKIP LOCKED</c> under a lease; a crashed worker's lease expires and the job
/// becomes claimable again. Exhausted leases are dead-lettered.
/// </summary>
/// <param name="dataSource">Npgsql data source.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Implements the IJobQueue port from ADR 0002.")]
internal sealed class NpgsqlJobQueue(NpgsqlDataSource dataSource) : IJobQueue
{
    /// <summary>First retry delay; doubles per attempt.</summary>
    internal static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<ClaimedJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var reap = new NpgsqlCommand(
            """
            UPDATE jobs SET state = 'Dead', lease_token = NULL, lease_until = NULL,
                   last_error = 'lease expired on the last attempt (worker crashed or stalled)'
            WHERE state = 'Running' AND lease_until < now() AND attempts >= max_attempts
            """, connection))
        {
            await reap.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var claim = new NpgsqlCommand(
            """
            UPDATE jobs SET state = 'Running', attempts = attempts + 1, lease_until = now() + @lease,
                   lease_token = gen_random_uuid(), locked_by = @worker
            WHERE id = (
                SELECT id FROM jobs
                WHERE (state = 'Ready' AND run_after <= now()) OR (state = 'Running' AND lease_until < now())
                ORDER BY run_after
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING id, job_key, stage, catalog_id, asset_id, attempts, max_attempts, lease_token
            """, connection);
        claim.Parameters.AddWithValue("lease", lease);
        claim.Parameters.AddWithValue("worker", workerId);
        await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimedJob(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetGuid(7));
    }

    /// <inheritdoc />
    public async Task<bool> RenewAsync(ClaimedJob job, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var command = dataSource.CreateCommand(
            "UPDATE jobs SET lease_until = now() + @lease WHERE id = @id AND lease_token = @token AND state = 'Running'");
        command.Parameters.AddWithValue("lease", lease);
        command.Parameters.AddWithValue("id", job.Id);
        command.Parameters.AddWithValue("token", job.LeaseToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <inheritdoc />
    public async Task FailAsync(ClaimedJob job, string errorDetail, bool permanent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(errorDetail);
        await using var command = dataSource.CreateCommand(
            """
            UPDATE jobs SET
                state = CASE WHEN @permanent OR attempts >= max_attempts THEN 'Dead' ELSE 'Ready' END,
                run_after = now() + @backoff, lease_token = NULL, lease_until = NULL, last_error = @error
            WHERE id = @id AND lease_token = @token AND state = 'Running'
            """);
        command.Parameters.AddWithValue("permanent", permanent);
        command.Parameters.AddWithValue("backoff", BackoffBase * Math.Pow(2, Math.Max(0, job.Attempt - 1)));
        command.Parameters.AddWithValue("error", errorDetail.Length > 2000 ? errorDetail[..2000] : errorDetail);
        command.Parameters.AddWithValue("id", job.Id);
        command.Parameters.AddWithValue("token", job.LeaseToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
