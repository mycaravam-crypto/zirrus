using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Zirrus.Application.Audit;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;
using Zirrus.Infrastructure.Audit;

namespace Zirrus.Api.Tests;

/// <summary>
/// W7 audit trail on PostgreSQL (security.md, Q10): actions are recorded with the change they
/// describe, the audit tables are append-only for every role, the application role cannot change
/// them, and signed digests detect tampering.
/// </summary>
/// <param name="fixture">Database and API.</param>
public sealed class AuditTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string AppPassword = "test-only-app-password";

    /// <summary>Writes through the API leave one audit record each, with actor, catalog and resource.</summary>
    [Fact]
    public async Task Changes_through_the_api_are_audited_and_rejected_ones_are_not()
    {
        using var admin = fixture.Api.ClientAs("alice,admin");
        using var created = await Api.SendAsync(admin, HttpMethod.Post, "/api/v1/catalogs", new { name = $"Audit {Guid.NewGuid():N}" });
        var catalogId = (await created.Content.ReadFromJsonAsync<JsonElement>(Api.Ct)).GetProperty("id").GetGuid();
        using (var granted = await Api.SendAsync(admin, HttpMethod.Put, $"/api/v1/catalogs/{catalogId}/members/erin", new { role = "editor" }))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }

        Catalog catalog;
        await using (var db = fixture.CreateContext())
        {
            catalog = await db.Catalogs.SingleAsync(c => c.Id == catalogId, Api.Ct);
        }

        var asset = await Seed.AssetAsync(fixture, catalog);
        using var erin = fixture.Api.ClientAs("erin");
        var path = $"/api/v1/catalogs/{catalogId}/assets/{asset.Id}";
        using (var updated = await SendWithIfMatch(erin, HttpMethod.Put, $"{path}/metadata", """{"title":"Audited"}""", $"\"{asset.Revision}\""))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        using (var stale = await SendWithIfMatch(erin, HttpMethod.Put, $"{path}/metadata", """{"title":"Lost"}""", $"\"{asset.Revision}\""))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode); // nothing changed: nothing recorded
        }

        using var category = await Api.SendAsync(admin, HttpMethod.Post, $"/api/v1/catalogs/{catalogId}/categories", new { name = "Audited" });
        var categoryId = (await category.Content.ReadFromJsonAsync<JsonElement>(Api.Ct)).GetProperty("id").GetGuid();
        (await Api.SendAsync(erin, HttpMethod.Put, $"{path}/categories/{categoryId}")).Dispose();
        (await Api.SendAsync(erin, HttpMethod.Delete, $"{path}/categories/{categoryId}")).Dispose();
        (await SendWithIfMatch(erin, HttpMethod.Delete, path, null, $"\\"{asset.Revision + 1}\\"")).Dispose();

        using (var details = await erin.GetAsync(new Uri(path, UriKind.Relative), Api.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        }

        using (var original = await erin.GetAsync(new Uri($"{path}/content", UriKind.Relative), Api.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, original.StatusCode);
        }

        using (var rendition = await erin.GetAsync(new Uri($"{path}/renditions/thumbnail", UriKind.Relative), Api.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, rendition.StatusCode);
        }

        using (var headOriginal = await erin.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri($"{path}/content", UriKind.Relative)), Api.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, headOriginal.StatusCode);
        }

        using (var headRendition = await erin.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri($"{path}/renditions/thumbnail", UriKind.Relative)), Api.Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, headRendition.StatusCode);
        }

        using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"{path}/content", UriKind.Relative)))
        {
            rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var ranged = await erin.SendAsync(rangeRequest, Api.Ct);
            Assert.Equal(HttpStatusCode.NotFound, ranged.StatusCode);
        }

        using (var list = await erin.GetAsync(new Uri($"/api/v1/catalogs/{catalogId}/assets", UriKind.Relative), Api.Ct))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.DoesNotContain(asset.Id.ToString(), await list.Content.ReadAsStringAsync(Api.Ct), StringComparison.OrdinalIgnoreCase);
        }

        await using var check = fixture.CreateContext();
        var events = await check.Set<AuditEvent>().Where(e => e.CatalogId == catalogId).OrderBy(e => e.Seq).ToListAsync(Api.Ct);
        Assert.Equal(
            [
                (AuditActions.CatalogCreate, "alice", (Guid?)catalogId),
                (AuditActions.MemberGrant, "alice", null),
                (AuditActions.MetadataUpdate, "erin", asset.Id),
                (AuditActions.CategoryCreate, "alice", categoryId),
                (AuditActions.CategoryAssign, "erin", asset.Id),
                (AuditActions.CategoryUnassign, "erin", asset.Id),
                (AuditActions.SoftDelete, "erin", asset.Id),
            ],
            events.Select(e => (e.Action, e.Actor, e.ResourceId)));
        Assert.All(events, e =>
        {
            Assert.Equal((PostgresFixture.OrganizationId, "success"), (e.OrganizationId, e.Outcome));
            Assert.False(string.IsNullOrEmpty(e.CorrelationId));
        });
        Assert.Equal("subject=erin; role=editor", events[1].Detail);
    }

    /// <summary>Not even the table owner can change or remove audit records; only the retention purge deletes.</summary>
    [Fact]
    public async Task Audit_records_are_append_only_for_the_owner_and_only_the_purge_deletes()
    {
        var old = await InsertEventAsync(DateTimeOffset.UtcNow.AddDays(-400));
        var recent = await InsertEventAsync(DateTimeOffset.UtcNow);
        await using var owner = new NpgsqlConnection(fixture.ConnectionString);
        await owner.OpenAsync(Api.Ct);
        foreach (var sql in new[]
        {
            $"UPDATE audit_events SET actor = 'mallory' WHERE id = '{recent}'",
            $"DELETE FROM audit_events WHERE id = '{recent}'",
            "TRUNCATE audit_events",
            "TRUNCATE audit_digests",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(owner, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        var purged = await Scalar<long>(owner, $"SELECT audit_purge(now() - interval '365 days')");
        Assert.True(purged >= 1);
        Assert.Equal(0L, await Scalar<long>(owner, $"SELECT count(*) FROM audit_events WHERE id = '{old}'"));
        Assert.Equal(1L, await Scalar<long>(owner, $"SELECT count(*) FROM audit_events WHERE id = '{recent}'"));
    }

    /// <summary>Q10: the application identity may insert and read audit records but never change or purge them.</summary>
    [Fact]
    public async Task The_application_role_cannot_change_audit_records_but_can_use_application_tables()
    {
        var recent = await InsertEventAsync(DateTimeOffset.UtcNow);
        var catalog = await Seed.CatalogAsync(fixture);
        var asset = await Seed.AssetAsync(fixture, catalog);
        await using (var owner = new NpgsqlConnection(fixture.ConnectionString))
        {
            await owner.OpenAsync(Api.Ct);
            await Execute(owner, $"ALTER ROLE zirrus_app WITH LOGIN PASSWORD '{AppPassword}'");
        }

        var appConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = "zirrus_app", Password = AppPassword }.ConnectionString;
        await using var app = new NpgsqlConnection(appConnection);
        await app.OpenAsync(Api.Ct);

        await Execute(app, $"""
            INSERT INTO audit_events (id, organization_id, actor, action, outcome, occurred_at, correlation_id)
            VALUES ('{Guid.NewGuid()}', '{PostgresFixture.OrganizationId}', 'app', 'test.insert', 'success', now(), 'c')
            """);
        Assert.True(await Scalar<long>(app, "SELECT count(*) FROM audit_events") >= 2);
        Assert.Equal(1, await ExecuteCount(app, $"UPDATE assets SET updated_at = now() WHERE id = '{asset.Id}'"));

        foreach (var sql in new[]
        {
            $"UPDATE audit_events SET actor = 'mallory' WHERE id = '{recent}'",
            $"DELETE FROM audit_events WHERE id = '{recent}'",
            "TRUNCATE audit_events",
            "DELETE FROM audit_digests",
            "SELECT audit_purge(now())",
            "DELETE FROM \"__EFMigrationsHistory\"",
            "DROP TABLE audit_events",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(app, sql));
            Assert.Contains(error.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, "42501" });
        }
    }

    private Task<Guid> InsertEventAsync(DateTimeOffset occurredAt) => InsertEventAsync(fixture, occurredAt);

    internal static async Task<Guid> InsertEventAsync(PostgresFixture fixture, DateTimeOffset occurredAt)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Api.Ct);
        await using var command = new NpgsqlCommand(
            "INSERT INTO audit_events (id, organization_id, actor, action, outcome, occurred_at, correlation_id, detail) VALUES (@id, @org, 'tester', 'test.event', 'success', @at, 'corr', 'd')",
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("org", PostgresFixture.OrganizationId);
        command.Parameters.AddWithValue("at", occurredAt);
        await command.ExecuteNonQueryAsync(Api.Ct);
        return id;
    }

    private static async Task<HttpResponseMessage> SendWithIfMatch(HttpClient client, HttpMethod method, string path, string? json, string ifMatch)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        request.Headers.Add("X-CSRF-TOKEN", await Api.CsrfAsync(client));
        return await client.SendAsync(request, Api.Ct);
    }

    internal static async Task Execute(NpgsqlConnection connection, string sql) => await ExecuteCount(connection, sql);

    private static async Task<int> ExecuteCount(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(Api.Ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Api.Ct))!;
    }

    internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Q10 digests, in their own database so other tests' events and purges do not interfere.</summary>
/// <param name="fixture">Database.</param>
public sealed class AuditDigestTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>A committed digest whose export failed is exported on the next run without creating a duplicate.</summary>
    [Fact]
    public async Task Failed_digest_export_is_retried_before_sealing_new_events()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"zirrus-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var blockedPath = Path.Combine(root, "blocked");
        await File.WriteAllTextAsync(blockedPath, "not a directory", cancellationToken);
        var exportDirectory = blockedPath;
        var options = new AuditDigestOptions
        {
            DigestKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            ExportDirectory = exportDirectory,
            SafetyLag = TimeSpan.FromMinutes(5),
        };
        await using var context = fixture.CreateContext();
        var digests = new AuditDigests(
            context,
            Options.Create(options),
            new AuditTests.FixedTime(DateTimeOffset.UtcNow.AddHours(1)));

        try
        {
            var eventId = await AuditTests.InsertEventAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-30));
            await Assert.ThrowsAsync<IOException>(() => digests.SealAsync(cancellationToken));

            options.ExportDirectory = Path.Combine(root, "exports");
            await digests.SealAsync(cancellationToken);

            await using var db = fixture.CreateContext();
            var sequence = await db.Set<AuditEvent>().Where(e => e.Id == eventId).Select(e => e.Seq).SingleAsync(cancellationToken);
            var saved = await db.Set<AuditDigest>().SingleAsync(d => d.FromSeq <= sequence && d.ToSeq >= sequence, cancellationToken);
            var exportPath = Path.Combine(options.ExportDirectory, $"audit-digest-{saved.ToSeq:D12}.json");
            Assert.True(File.Exists(exportPath));
            Assert.Contains(saved.Sha256, await File.ReadAllTextAsync(exportPath, cancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Q10: signed digests verify, and a changed or removed sealed record is detected.</summary>
    [Fact]
    public async Task Digests_verify_and_detect_tampering_and_purges()
    {
        var digests = Digests(DateTimeOffset.UtcNow.AddHours(1));
        await AuditTests.InsertEventAsync(fixture, DateTimeOffset.UtcNow.AddDays(-400));
        await AuditTests.InsertEventAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-30));
        var first = await digests.SealAsync(Api.Ct);
        Assert.NotNull(first);
        var target = await AuditTests.InsertEventAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-20));
        await AuditTests.InsertEventAsync(fixture, DateTimeOffset.UtcNow.AddMinutes(-10));
        var second = await digests.SealAsync(Api.Ct);
        Assert.NotNull(second);
        Assert.Equal(first!.Sha256, second!.PreviousSha256);
        Assert.Null(await digests.SealAsync(Api.Ct)); // nothing new
        Assert.All(await digests.VerifyAsync(10, Api.Ct), r => Assert.Equal(DigestStatus.Valid, r.Status));

        // Tampering needs the owner to disable the trigger; the digest still catches it.
        await using var owner = new NpgsqlConnection(fixture.ConnectionString);
        await owner.OpenAsync(Api.Ct);
        await AuditTests.Execute(owner, $"""
            ALTER TABLE audit_events DISABLE TRIGGER audit_events_append_only;
            UPDATE audit_events SET actor = 'mallory' WHERE id = '{target}';
            ALTER TABLE audit_events ENABLE TRIGGER audit_events_append_only;
            """);
        var results = await digests.VerifyAsync(10, Api.Ct);
        Assert.Equal([DigestStatus.Valid, DigestStatus.Mismatch], results.Select(r => r.Status));

        // Another key cannot vouch for these digests.
        Assert.All(await Digests(DateTimeOffset.UtcNow, key: new string('B', 44)).VerifyAsync(10, Api.Ct), r => Assert.Equal(DigestStatus.UnknownKey, r.Status));

        // The retention purge removes the oldest events: reported as purged, not as tampering.
        await AuditTests.Execute(owner, "SELECT audit_purge(now() - interval '365 days')");
        Assert.Equal(DigestStatus.Purged, (await digests.VerifyAsync(10, Api.Ct))[0].Status);
    }

    private AuditDigests Digests(DateTimeOffset now, string? key = null) =>
        new(fixture.CreateContext(),
            Options.Create(new AuditDigestOptions { DigestKey = key ?? Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()), SafetyLag = TimeSpan.FromMinutes(5) }),
            new AuditTests.FixedTime(now));
}
