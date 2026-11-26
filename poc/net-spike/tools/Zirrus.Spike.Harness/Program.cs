using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zirrus.Spike.Domain;
using Zirrus.Spike.Infrastructure;

// Spike harness for ADR 0002 (job queue) and ADR 0005 (EF Core + JSONB).
// Commands: kill-test | atomicity-test | jsonb-test
var command = args.Length > 0 ? args[0] : "kill-test";
var results = command switch
{
    "kill-test" => await KillTestAsync(),
    "atomicity-test" => await AtomicityTestAsync(),
    "jsonb-test" => await JsonbTestAsync(),
    _ => throw new ArgumentException($"unknown command {command}"),
};
var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
return results.TryGetValue("pass", out var pass) && pass is true ? 0 : 1;

static async Task ResetAsync()
{
    await using var db = SpikeDb.CreateContext();
    await db.Database.ExecuteSqlRawAsync("TRUNCATE assets, jobs, job_effects, job_attempts RESTART IDENTITY");
}

static async Task<Dictionary<string, object>> KillTestAsync()
{
    const int jobCount = 200, transientJobs = 10, permanentJobs = 5, workerCount = 3, killTarget = 20;
    await ResetAsync();
    var random = new Random(42);
    var catalog = Guid.NewGuid();
    for (var i = 0; i < jobCount; i++)
    {
        await using var db = SpikeDb.CreateContext();
        object payload = i < transientJobs ? new { ms = random.Next(300, 1200), fail = "transient-twice" }
            : i < transientJobs + permanentJobs ? new { ms = 100, fail = "permanent" }
            : new { ms = random.Next(300, 1200) };
        await new AssetImporter(db).ImportAsync(catalog, $"sha-{i:D4}", payload, CancellationToken.None);
    }

    var workerDll = Environment.GetEnvironmentVariable("WORKER_DLL")
        ?? throw new InvalidOperationException("set WORKER_DLL to the Zirrus.Spike.Worker.dll path");
    var workers = new Dictionary<string, Process>();
    var generation = 0;
    Process Start(string slot)
    {
        var id = $"{slot}-g{generation++}";
        var psi = new ProcessStartInfo("dotnet", $"{workerDll} {id}") { RedirectStandardOutput = true, UseShellExecute = false };
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        workers[id] = p;
        return p;
    }

    for (var w = 0; w < workerCount; w++)
    {
        Start($"w{w}");
    }

    await using var dataSource = SpikeDb.CreateDataSource();
    var stopwatch = Stopwatch.StartNew();
    var kills = new List<Kill>();
    while (kills.Count < killTarget && stopwatch.Elapsed < TimeSpan.FromMinutes(5))
    {
        await Task.Delay(random.Next(400, 1500));

        // Kill only a worker that currently holds a live lease, i.e. is mid-job.
        await using var holders = dataSource.CreateCommand(
            """SELECT "LockedBy", "JobKey" FROM jobs WHERE "State" = 'running' AND "LeaseUntil" > now()""");
        var running = new List<(string Worker, string Key)>();
        await using (var reader = await holders.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                running.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var victim = running.Where(r => workers.ContainsKey(r.Worker) && !workers[r.Worker].HasExited).ToList();
        if (victim.Count == 0)
        {
            continue;
        }

        var (worker, key) = victim[random.Next(victim.Count)];
        workers[worker].Kill(entireProcessTree: true);
        await workers[worker].WaitForExitAsync();
        workers.Remove(worker);
        kills.Add(new Kill(worker, key, Math.Round(stopwatch.Elapsed.TotalSeconds, 1)));
        Start(worker.Split('-')[0]);
    }

    // Drain: wait until nothing is ready or running (expired leases are reclaimed by live workers).
    while (stopwatch.Elapsed < TimeSpan.FromMinutes(10))
    {
        await using var open = dataSource.CreateCommand("""SELECT count(*) FROM jobs WHERE "State" IN ('ready', 'running')""");
        if ((long)(await open.ExecuteScalarAsync())! == 0)
        {
            break;
        }

        await Task.Delay(500);
    }

    foreach (var p in workers.Values.Where(p => !p.HasExited))
    {
        p.Kill(entireProcessTree: true);
    }

    async Task<long> Scalar(string sql)
    {
        await using var c = dataSource.CreateCommand(sql);
        return Convert.ToInt64(await c.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    var done = await Scalar("""SELECT count(*) FROM jobs WHERE "State" = 'done'""");
    var dead = await Scalar("""SELECT count(*) FROM jobs WHERE "State" = 'dead'""");
    var open2 = await Scalar("""SELECT count(*) FROM jobs WHERE "State" IN ('ready', 'running')""");
    var effects = await Scalar("SELECT count(*) FROM job_effects");
    var doneWithoutEffect = await Scalar(
        """SELECT count(*) FROM jobs j WHERE j."State" = 'done' AND NOT EXISTS (SELECT 1 FROM job_effects e WHERE e."JobKey" = j."JobKey")""");
    var deadWithEffect = await Scalar(
        """SELECT count(*) FROM jobs j WHERE j."State" = 'dead' AND EXISTS (SELECT 1 FROM job_effects e WHERE e."JobKey" = j."JobKey")""");
    var attempts = await Scalar("SELECT count(*) FROM job_attempts");
    var killedJobsDone = await Scalar(
        $"""SELECT count(DISTINCT "JobKey") FROM jobs WHERE "State" = 'done' AND "JobKey" IN ({string.Join(",", kills.Select(k => $"'{k.JobKey}'"))})""");
    var killedJobsDistinct = kills.Select(k => k.JobKey).Distinct().Count();
    var transientDone = await Scalar(
        """SELECT count(*) FROM jobs WHERE "State" = 'done' AND "Payload"->>'fail' = 'transient-twice' AND "Attempts" >= 3""");
    var permanentDead = await Scalar(
        """SELECT count(*) FROM jobs WHERE "State" = 'dead' AND "Payload"->>'fail' = 'permanent' AND "Attempts" = 1""");

    var expectedDone = jobCount - permanentJobs;
    var pass = kills.Count == killTarget && killedJobsDone == killedJobsDistinct && open2 == 0 && done == expectedDone && dead == permanentJobs
        && effects == done && doneWithoutEffect == 0 && deadWithEffect == 0
        && transientDone == transientJobs && permanentDead == permanentJobs;

    return new Dictionary<string, object>
    {
        ["test"] = "kill-test",
        ["jobs"] = jobCount,
        ["workers"] = workerCount,
        ["killsMidJob"] = kills.Count,
        ["killedJobsDistinct"] = killedJobsDistinct,
        ["killedJobsCompleted"] = killedJobsDone,
        ["done"] = done,
        ["dead"] = dead,
        ["stillOpen"] = open2,
        ["effects"] = effects,
        ["doneWithoutEffect"] = doneWithoutEffect,
        ["deadWithEffect"] = deadWithEffect,
        ["handlerExecutions"] = attempts,
        ["transientRetriedThenDone"] = transientDone,
        ["permanentDeadLetteredFirstAttempt"] = permanentDead,
        ["elapsedSeconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
        ["kills"] = kills,
        ["pass"] = pass,
    };
}

static async Task<Dictionary<string, object>> AtomicityTestAsync()
{
    await ResetAsync();
    await using (var db = SpikeDb.CreateContext())
    {
        // Pre-existing job with a colliding key forces the second SaveChanges to fail.
        db.Jobs.Add(new JobRecord { JobKey = "collide", Payload = "{}" });
        await db.SaveChangesAsync();
    }

    var failed = false;
    await using (var db = SpikeDb.CreateContext())
    {
        db.Assets.Add(new Asset { CatalogId = Guid.NewGuid(), Sha256 = "atomic" });
        db.Jobs.Add(new JobRecord { JobKey = "collide", Payload = "{}" });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            failed = true;
        }
    }

    await using var check = SpikeDb.CreateContext();
    var orphanAssets = await check.Assets.CountAsync(a => a.Sha256 == "atomic");
    return new Dictionary<string, object>
    {
        ["test"] = "atomicity-test",
        ["saveFailed"] = failed,
        ["assetRowsAfterFailedSave"] = orphanAssets,
        ["pass"] = failed && orphanAssets == 0,
    };
}

static async Task<Dictionary<string, object>> JsonbTestAsync()
{
    const int rows = 100_000;
    await ResetAsync();
    await using var dataSource = SpikeDb.CreateDataSource();
    var keywords = new[] { "alps", "harbor", "portrait", "archive", "product", "event", "night", "aerial" };
    var catalogs = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
    var seed = Stopwatch.StartNew();
    await using (var connection = await dataSource.OpenConnectionAsync())
    await using (var writer = await connection.BeginBinaryImportAsync(
        """COPY assets ("Id", "CatalogId", "State", "Sha256", "Metadata", "Revision") FROM STDIN (FORMAT BINARY)"""))
    {
        var random = new Random(7);
        for (var i = 0; i < rows; i++)
        {
            var meta = JsonSerializer.Serialize(new
            {
                title = $"Asset {i}",
                keywords = new[] { keywords[random.Next(keywords.Length)], keywords[random.Next(keywords.Length)] },
                creator = $"photographer-{random.Next(50)}",
                rating = random.Next(1, 6),
            });
            await writer.StartRowAsync();
            await writer.WriteAsync(Guid.NewGuid());
            await writer.WriteAsync(catalogs[i % 3]);
            await writer.WriteAsync("Active");
            await writer.WriteAsync($"sha-{i}");
            await writer.WriteAsync(meta, NpgsqlTypes.NpgsqlDbType.Jsonb);
            await writer.WriteAsync(0);
        }

        await writer.CompleteAsync();
    }

    seed.Stop();
    await using (var analyze = dataSource.CreateCommand("ANALYZE assets"))
    {
        await analyze.ExecuteNonQueryAsync();
    }

    // EF Core LINQ with a JSONB containment filter, scoped to one catalog.
    await using var db = SpikeDb.CreateContext();
    var catalog = catalogs[0];
    var filter = JsonDocument.Parse("""{"creator":"photographer-7"}""");
    var query = db.Assets.Where(a => a.CatalogId == catalog && a.State == AssetState.Active
                                     && EF.Functions.JsonContains(a.Metadata, filter));
    var sql = query.ToQueryString();
    var timer = Stopwatch.StartNew();
    var count = await query.CountAsync();
    timer.Stop();

    // Plan check: the same predicate EF Core generates (JSONB containment, @>), with bound parameters.
    var efUsesContainment = sql.Contains("@>", StringComparison.Ordinal);
    var plan = new List<string>();
    await using (var explain = dataSource.CreateCommand(
        """
        EXPLAIN (ANALYZE, BUFFERS) SELECT count(*) FROM assets
        WHERE "CatalogId" = @catalog AND "State" = 'Active' AND "Metadata" @> @filter
        """))
    {
        explain.Parameters.AddWithValue("catalog", catalog);
        explain.Parameters.AddWithValue("filter", NpgsqlTypes.NpgsqlDbType.Jsonb, """{"creator":"photographer-7"}""");
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(0));
        }
    }

    var usesGin = efUsesContainment && plan.Any(l => l.Contains("IX_assets_Metadata", StringComparison.Ordinal));

    // Optimistic concurrency: two contexts edit the same asset; the second save must fail.
    var id = await db.Assets.Where(a => a.CatalogId == catalog).Select(a => a.Id).FirstAsync();
    await using var first = SpikeDb.CreateContext();
    await using var second = SpikeDb.CreateContext();
    var a1 = await first.Assets.SingleAsync(a => a.Id == id);
    var a2 = await second.Assets.SingleAsync(a => a.Id == id);
    a1.Metadata = JsonDocument.Parse("""{"title":"edited by first"}""");
    a1.Revision++;
    await first.SaveChangesAsync();
    a2.Metadata = JsonDocument.Parse("""{"title":"edited by second"}""");
    a2.Revision++;
    var conflict = false;
    try
    {
        await second.SaveChangesAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
        conflict = true;
    }

    return new Dictionary<string, object>
    {
        ["test"] = "jsonb-test",
        ["rows"] = rows,
        ["seedSeconds"] = Math.Round(seed.Elapsed.TotalSeconds, 1),
        ["matches"] = count,
        ["queryMs"] = Math.Round(timer.Elapsed.TotalMilliseconds, 1),
        ["usesGinIndex"] = usesGin,
        ["sql"] = sql,
        ["plan"] = plan,
        ["staleUpdateRejected"] = conflict,
        ["pass"] = usesGin && conflict && count > 0,
    };
}

/// <summary>One SIGKILL of a worker that held a live lease.</summary>
/// <param name="Worker">Killed worker ID.</param>
/// <param name="JobKey">Job the worker was processing.</param>
/// <param name="AtSeconds">Seconds since the test started.</param>
internal sealed record Kill(string Worker, string JobKey, double AtSeconds);
