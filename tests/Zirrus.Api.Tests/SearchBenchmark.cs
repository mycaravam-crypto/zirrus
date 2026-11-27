using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Npgsql;

namespace Zirrus.Api.Tests;

/// <summary>
/// Q1 search benchmark through the API (authentication, authorization, EF Core and JSON), on the #12
/// reference workload: 100,000 assets in 3 catalogs, mixed grants, 10 concurrent users without think
/// time. Opt-in: set <c>ZIRRUS_BENCH=1</c> (optionally <c>ZIRRUS_BENCH_SECONDS</c>, default 60, and
/// <c>ZIRRUS_BENCH_OUT</c> for a Markdown report). See <c>docs/architecture/search-benchmark.md</c>.
/// </summary>
/// <param name="fixture">Database and API.</param>
public sealed class SearchBenchmark(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string SeedOrganization = "00000000-0000-4000-8000-0000000000aa";
    private const string Visible1 = "00000000-0000-4000-8000-0000000000c1";
    private const string Visible2 = "00000000-0000-4000-8000-0000000000c2";
    private const string Hidden = "00000000-0000-4000-8000-0000000000c3";
    private const int Users = 10;

    private static readonly string[] Terms = ["harbour", "Häfen", "München", "boat", "Photographer 17", "Pressekonferenz"];

    /// <summary>Keyword p95 ≤ 500 ms, filter p95 ≤ 750 ms, &lt; 1% errors, and no access to the ungranted catalog.</summary>
    [Fact]
    public async Task Search_meets_Q1_latency_targets_under_concurrent_load()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ZIRRUS_BENCH") == "1", "Set ZIRRUS_BENCH=1 to run the search benchmark.");
        var seconds = int.Parse(Environment.GetEnvironmentVariable("ZIRRUS_BENCH_SECONDS") ?? "60", CultureInfo.InvariantCulture);

        await SeedAsync();
        var categories = await CategoriesAsync();

        var samples = new ConcurrentBag<(string Kind, double Ms, bool Ok)>();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var workers = Enumerable.Range(0, Users).Select(worker => Task.Run(async () =>
        {
            using var client = fixture.Api.ClientAs("bench-viewer");
            var random = new Random(worker);
            for (var i = 0; DateTime.UtcNow < deadline; i++)
            {
                var catalog = random.Next(2) == 0 ? Visible1 : Visible2;
                var path = $"/api/v1/catalogs/{catalog}/assets";
                var roll = random.Next(10);
                var (kind, url, expected) = roll switch
                {
                    < 4 => ("keyword", $"{path}?q={Uri.EscapeDataString(Terms[random.Next(Terms.Length)])}", HttpStatusCode.OK),
                    < 7 => ("filter", $"{path}?categoryId={categories[catalog][random.Next(30)]}&metadata="
                        + Uri.EscapeDataString($$"""{"photographer":"Photographer {{random.Next(250)}}"}"""), HttpStatusCode.OK),
                    < 9 => ("facets", $"{path}/facets?q={Uri.EscapeDataString(Terms[random.Next(Terms.Length)])}", HttpStatusCode.OK),
                    _ => ("list", path, HttpStatusCode.OK),
                };

                // Every 20th request probes the ungranted catalog: it must always be 404 (Q3).
                if (i % 20 == 19)
                {
                    (kind, url, expected) = ("hidden", $"/api/v1/catalogs/{Hidden}/assets?q=harbour", HttpStatusCode.NotFound);
                }

                var watch = Stopwatch.StartNew();
                bool ok;
                try
                {
                    using var response = await client.GetAsync(new Uri(url, UriKind.Relative), CancellationToken.None);
                    await response.Content.ReadAsByteArrayAsync(CancellationToken.None);
                    ok = response.StatusCode == expected;
                }
                catch (HttpRequestException)
                {
                    ok = false;
                }

                samples.Add((kind, watch.Elapsed.TotalMilliseconds, ok));
            }
        })).ToList();
        await Task.WhenAll(workers);

        var report = Report(samples.ToList(), seconds);
        TestContext.Current.SendDiagnosticMessage(report);
        if (Environment.GetEnvironmentVariable("ZIRRUS_BENCH_OUT") is { Length: > 0 } output)
        {
            await File.WriteAllTextAsync(output, report, CancellationToken.None);
        }

        var all = samples.ToList();
        Assert.True(P95(all, "keyword") <= 500, $"keyword p95 {P95(all, "keyword"):F1} ms > 500 ms");
        Assert.True(P95(all, "filter") <= 750, $"filter p95 {P95(all, "filter"):F1} ms > 750 ms");
        Assert.True(all.Count(s => !s.Ok) < all.Count * 0.01, $"{all.Count(s => !s.Ok)} unexpected responses of {all.Count}");
        Assert.All(all.Where(s => s.Kind == "hidden"), s => Assert.True(s.Ok, "the ungranted catalog was reachable"));
    }

    private static double P95(List<(string Kind, double Ms, bool Ok)> samples, string kind)
    {
        var sorted = samples.Where(s => s.Kind == kind).Select(s => s.Ms).Order().ToList();
        return sorted.Count == 0 ? double.NaN : sorted[(int)(sorted.Count * 0.95)];
    }

    private static string Report(List<(string Kind, double Ms, bool Ok)> samples, int seconds)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Search benchmark: {Users} users, {seconds} s, {samples.Count} requests, {samples.Count(s => !s.Ok)} unexpected");
        text.AppendLine();
        text.AppendLine("| Request | n | p50 ms | p95 ms | p99 ms | max ms | unexpected |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var group in samples.GroupBy(s => s.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var sorted = group.Select(s => s.Ms).Order().ToList();
            double At(double q) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {sorted.Count} | {At(0.5):F1} | {At(0.95):F1} | {At(0.99):F1} | {sorted[^1]:F1} | {group.Count(s => !s.Ok)} |");
        }

        return text.ToString();
    }

    private async Task SeedAsync()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Zirrus.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repository root not found");
        }

        var sql = await File.ReadAllTextAsync(Path.Combine(root, "tools", "perf", "seed-search-workload.sql"), CancellationToken.None);
        sql = string.Join('\n', sql.Split('\n').Where(line => !line.StartsWith('\\')))
            .Replace(SeedOrganization, PostgresFixture.OrganizationId.ToString(), StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<Dictionary<string, List<Guid>>> CategoriesAsync()
    {
        var result = new Dictionary<string, List<Guid>> { [Visible1] = [], [Visible2] = [] };
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            $"SELECT catalog_id::text, id FROM categories WHERE catalog_id IN ('{Visible1}', '{Visible2}') ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            result[reader.GetString(0)].Add(reader.GetGuid(1));
        }

        return result;
    }
}
