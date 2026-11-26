using Npgsql;
using Zirrus.Spike.Application;

namespace Zirrus.Spike.Infrastructure;

/// <summary>
/// PostgreSQL job queue: jobs are claimed with FOR UPDATE SKIP LOCKED under a time-limited
/// lease. A crashed worker's lease expires and the job becomes claimable again.
/// </summary>
/// <param name="dataSource">Npgsql data source for the spike database.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Implements the IJobQueue port from ADR 0002.")]
public sealed class NpgsqlJobQueue(NpgsqlDataSource dataSource) : IJobQueue
{
    private static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public async Task<ClaimedJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Dead-letter jobs whose lease expired on their last allowed attempt.
        await using (var reap = new NpgsqlCommand(
            """
            UPDATE jobs SET "State" = 'dead', "LeaseToken" = NULL,
                   "LastError" = 'lease-expired: attempts exhausted'
            WHERE "State" = 'running' AND "LeaseUntil" < now() AND "Attempts" >= "MaxAttempts"
            """, connection))
        {
            await reap.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var claim = new NpgsqlCommand(
            """
            UPDATE jobs SET "State" = 'running', "Attempts" = "Attempts" + 1,
                   "LeaseUntil" = now() + @lease, "LeaseToken" = gen_random_uuid(), "LockedBy" = @worker
            WHERE "Id" = (
                SELECT "Id" FROM jobs
                WHERE ("State" = 'ready' AND "RunAfter" <= now())
                   OR ("State" = 'running' AND "LeaseUntil" < now())
                ORDER BY "RunAfter"
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING "Id", "JobKey", "Payload"::text, "Attempts", "LeaseToken"
            """, connection);
        claim.Parameters.AddWithValue("lease", lease);
        claim.Parameters.AddWithValue("worker", workerId);

        await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimedJob(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetGuid(4));
    }

    /// <inheritdoc />
    public async Task<bool> RenewAsync(ClaimedJob job, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var command = dataSource.CreateCommand(
            """
            UPDATE jobs SET "LeaseUntil" = now() + @lease
            WHERE "Id" = @id AND "LeaseToken" = @token AND "State" = 'running'
            """);
        command.Parameters.AddWithValue("lease", lease);
        command.Parameters.AddWithValue("id", job.Id);
        command.Parameters.AddWithValue("token", job.LeaseToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var command = dataSource.CreateCommand(
            """
            UPDATE jobs SET "State" = 'done', "CompletedAt" = now(), "LeaseToken" = NULL, "LeaseUntil" = NULL
            WHERE "Id" = @id AND "LeaseToken" = @token AND "State" = 'running'
            """);
        command.Parameters.AddWithValue("id", job.Id);
        command.Parameters.AddWithValue("token", job.LeaseToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <inheritdoc />
    public async Task FailAsync(ClaimedJob job, string errorDetail, bool permanent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var backoff = BackoffBase * Math.Pow(2, job.Attempt - 1);
        await using var command = dataSource.CreateCommand(
            """
            UPDATE jobs SET
                "State" = CASE WHEN @permanent OR "Attempts" >= "MaxAttempts" THEN 'dead' ELSE 'ready' END,
                "RunAfter" = now() + @backoff, "LeaseToken" = NULL, "LeaseUntil" = NULL, "LastError" = @error
            WHERE "Id" = @id AND "LeaseToken" = @token AND "State" = 'running'
            """);
        command.Parameters.AddWithValue("permanent", permanent);
        command.Parameters.AddWithValue("backoff", backoff);
        command.Parameters.AddWithValue("error", errorDetail);
        command.Parameters.AddWithValue("id", job.Id);
        command.Parameters.AddWithValue("token", job.LeaseToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
