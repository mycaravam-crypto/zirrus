using System.Text.Json;
using Npgsql;
using Zirrus.Spike.Application;
using Zirrus.Spike.Infrastructure;

// Spike worker: claims render jobs, renews the lease while working, writes the effect
// idempotently and completes the job. The harness kills it with SIGKILL at random points.
var workerId = args.Length > 0 ? args[0] : $"worker-{Environment.ProcessId}";
var lease = TimeSpan.FromSeconds(3);

await using var dataSource = SpikeDb.CreateDataSource();
IJobQueue queue = new NpgsqlJobQueue(dataSource);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

Console.WriteLine($"{workerId} started (pid {Environment.ProcessId})");
while (!stop.IsCancellationRequested)
{
    var job = await queue.ClaimAsync(workerId, lease, stop.Token);
    if (job is null)
    {
        await Task.Delay(100, stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        continue;
    }

    await RecordAttemptAsync(dataSource, job, workerId, stop.Token);
    using var payload = JsonDocument.Parse(job.Payload);
    var root = payload.RootElement;
    var workMs = root.GetProperty("ms").GetInt32();
    var fail = root.TryGetProperty("fail", out var f) ? f.GetString() : null;

    if (fail == "permanent")
    {
        await queue.FailAsync(job, "unsupported-format: simulated permanent failure", permanent: true, stop.Token);
        continue;
    }

    if (fail == "transient-twice" && job.Attempt <= 2)
    {
        await queue.FailAsync(job, $"scanner-unavailable: simulated transient failure {job.Attempt}", permanent: false, stop.Token);
        continue;
    }

    // Simulated rendering work, with lease renewal every second.
    var deadline = DateTime.UtcNow.AddMilliseconds(workMs);
    var leaseLost = false;
    while (DateTime.UtcNow < deadline && !leaseLost)
    {
        await Task.Delay(Math.Min(1000, Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds)), stop.Token);
        leaseLost = !await queue.RenewAsync(job, lease, stop.Token);
    }

    if (leaseLost)
    {
        Console.WriteLine($"{workerId} lost lease on {job.JobKey}; abandoning");
        continue;
    }

    // Idempotent effect: the unique job key makes a repeated execution a no-op.
    await using (var effect = dataSource.CreateCommand(
        """
        INSERT INTO job_effects ("JobKey", "ProducedBy", "ProducedAt") VALUES (@key, @worker, now())
        ON CONFLICT ("JobKey") DO NOTHING
        """))
    {
        effect.Parameters.AddWithValue("key", job.JobKey);
        effect.Parameters.AddWithValue("worker", workerId);
        await effect.ExecuteNonQueryAsync(stop.Token);
    }

    if (!await queue.CompleteAsync(job, stop.Token))
    {
        Console.WriteLine($"{workerId} could not complete {job.JobKey}: lease lost");
    }
}

static async Task RecordAttemptAsync(NpgsqlDataSource dataSource, ClaimedJob job, string workerId, CancellationToken ct)
{
    await using var command = dataSource.CreateCommand(
        """INSERT INTO job_attempts ("JobKey", "Worker", "StartedAt") VALUES (@key, @worker, now())""");
    command.Parameters.AddWithValue("key", job.JobKey);
    command.Parameters.AddWithValue("worker", workerId);
    await command.ExecuteNonQueryAsync(ct);
}
