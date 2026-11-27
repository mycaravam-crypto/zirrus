using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Api.Tests;

/// <summary>Seeds catalogs, categories and assets directly in the database.</summary>
internal static class Seed
{
    public const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    public static async Task<Catalog> CatalogAsync(PostgresFixture fixture, params (string Subject, CatalogRole Role)[] grants)
    {
        await using var db = fixture.CreateContext();
        var catalog = Catalog.Create(PostgresFixture.OrganizationId, $"Catalog {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        db.Catalogs.Add(catalog);
        db.CatalogMemberships.AddRange(grants.Select(g => CatalogMembership.Grant(catalog, g.Subject, g.Role, DateTimeOffset.UtcNow)));
        await db.SaveChangesAsync(Api.Ct);
        return catalog;
    }

    public static async Task<Asset> AssetAsync(PostgresFixture fixture, Catalog catalog, AssetState state = AssetState.Active)
    {
        await using var db = fixture.CreateContext();
        var asset = Asset.Start(catalog, "photo.jpg", "image/jpeg", "erin", DateTimeOffset.UtcNow);
        if (state != AssetState.Uploading)
        {
            asset.MarkStored(Sha, 4, DateTimeOffset.UtcNow);
        }

        if (state is AssetState.Processing or AssetState.Active)
        {
            asset.BeginProcessing(DateTimeOffset.UtcNow);
        }

        if (state == AssetState.Active)
        {
            asset.Activate(DateTimeOffset.UtcNow);
        }

        db.Assets.Add(asset);
        await db.SaveChangesAsync(Api.Ct);
        return asset;
    }
}

/// <summary>W3 schema: composite foreign keys, concurrency token, JSONB and job keys on PostgreSQL.</summary>
/// <param name="fixture">Database.</param>
public sealed class SchemaTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Renditions_cannot_reference_an_asset_of_another_catalog()
    {
        var press = await Seed.CatalogAsync(fixture);
        var archive = await Seed.CatalogAsync(fixture);
        var asset = await Seed.AssetAsync(fixture, press);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            $"""
            INSERT INTO renditions (asset_id, kind, catalog_id, pipeline_version, storage_key, media_type, width, height, size_bytes, sha256, created_at)
            VALUES ('{asset.Id}', 'thumbnail', '{archive.Id}', 1, 'renditions/x', 'image/jpeg', 1, 1, 1, '{Seed.Sha}', now())
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Assets_cannot_be_assigned_to_a_category_of_another_catalog()
    {
        var press = await Seed.CatalogAsync(fixture);
        var archive = await Seed.CatalogAsync(fixture);
        var asset = await Seed.AssetAsync(fixture, press);
        var foreignCategory = Category.CreateRoot(archive, "Elsewhere", DateTimeOffset.UtcNow);
        await using (var db = fixture.CreateContext())
        {
            db.Categories.Add(foreignCategory);
            await db.SaveChangesAsync(Api.Ct);
        }

        // Either catalog in the row breaks one of the two composite keys.
        foreach (var catalogId in new[] { press.Id, archive.Id })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(
                $"INSERT INTO asset_categories (asset_id, category_id, catalog_id, assigned_at) VALUES ('{asset.Id}', '{foreignCategory.Id}', '{catalogId}', now())"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task A_category_parent_must_be_in_the_same_catalog()
    {
        var press = await Seed.CatalogAsync(fixture);
        var archive = await Seed.CatalogAsync(fixture);
        var parent = Category.CreateRoot(press, "Events", DateTimeOffset.UtcNow);
        await using (var db = fixture.CreateContext())
        {
            db.Categories.AddRange(parent, Category.CreateChild(parent, "2026", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Api.Ct);
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            $"INSERT INTO categories (id, organization_id, catalog_id, parent_id, name, created_at) VALUES ('{Guid.NewGuid()}', '{PostgresFixture.OrganizationId}', '{archive.Id}', '{parent.Id}', 'Cross', now())"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Sibling_category_names_are_unique_including_roots()
    {
        var press = await Seed.CatalogAsync(fixture);
        await using var db = fixture.CreateContext();
        db.Categories.AddRange(Category.CreateRoot(press, "Events", DateTimeOffset.UtcNow), Category.CreateRoot(press, "Events", DateTimeOffset.UtcNow));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Api.Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Assets_cannot_reference_a_catalog_of_another_organization()
    {
        var press = await Seed.CatalogAsync(fixture);
        var asset = await Seed.AssetAsync(fixture, press);
        var other = Organization.Create(Guid.NewGuid(), "Other", DateTimeOffset.UtcNow);
        await using (var db = fixture.CreateContext())
        {
            db.Organizations.Add(other);
            await db.SaveChangesAsync(Api.Ct);
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute($"UPDATE assets SET organization_id = '{other.Id}' WHERE id = '{asset.Id}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Concurrent_writers_of_one_revision_lose_with_a_concurrency_exception()
    {
        var press = await Seed.CatalogAsync(fixture);
        var asset = await Seed.AssetAsync(fixture, press);
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var a = await first.Assets.SingleAsync(x => x.Id == asset.Id, Api.Ct);
        var b = await second.Assets.SingleAsync(x => x.Id == asset.Id, Api.Ct);

        a.UpdateMetadata(AssetMetadata.Parse("""{"title":"first"}"""), a.Revision, DateTimeOffset.UtcNow);
        b.UpdateMetadata(AssetMetadata.Parse("""{"title":"second"}"""), b.Revision, DateTimeOffset.UtcNow);
        await first.SaveChangesAsync(Api.Ct);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Api.Ct));

        await using var check = fixture.CreateContext();
        Assert.Equal("""{"title": "first"}""", (await check.Assets.SingleAsync(x => x.Id == asset.Id, Api.Ct)).Metadata);
    }

    [Fact]
    public async Task Jsonb_containment_queries_work_and_job_keys_are_unique()
    {
        var press = await Seed.CatalogAsync(fixture);
        var asset = await Seed.AssetAsync(fixture, press);
        await using var db = fixture.CreateContext();
        var tracked = await db.Assets.SingleAsync(x => x.Id == asset.Id, Api.Ct);
        tracked.UpdateMetadata(AssetMetadata.Parse("""{"tags":["sea","harbour"]}"""), tracked.Revision, DateTimeOffset.UtcNow);
        db.Jobs.Add(JobIntent.ForAsset(tracked, "render", 1, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Api.Ct);

        var found = await db.Assets.Where(x => x.CatalogId == press.Id && EF.Functions.JsonContains(x.Metadata, """{"tags":["sea"]}""")).Select(x => x.Id).ToListAsync(Api.Ct);
        Assert.Equal([asset.Id], found);

        db.Jobs.Add(JobIntent.ForAsset(tracked, "render", 1, DateTimeOffset.UtcNow));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Api.Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task The_sha256_column_rejects_malformed_checksums()
    {
        var press = await Seed.CatalogAsync(fixture);
        var asset = await Seed.AssetAsync(fixture, press);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute($"UPDATE assets SET sha256 = '{new string('Z', 64)}' WHERE id = '{asset.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Api.Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Api.Ct);
    }
}

/// <summary>Q9 (part): migrations apply on existing data, and a failing migration changes nothing.</summary>
/// <param name="fixture">Database server.</param>
public sealed class MigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string BrokenMigrationId = "29990101000000_BrokenOnPurpose";

    [Fact]
    public async Task Upgrading_a_database_with_catalogs_creates_their_organizations()
    {
        var connectionString = await fixture.CreateEmptyDatabaseAsync();
        var otherOrganization = Guid.NewGuid();
        await using (var db = Context(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261126194224_Identity", Api.Ct);
            await db.Database.ExecuteSqlAsync($"INSERT INTO catalogs (id, organization_id, name, created_at) VALUES ({Guid.NewGuid()}, {otherOrganization}, 'Legacy', now())", Api.Ct);
            await db.Database.MigrateAsync(Api.Ct);
        }

        await using var check = Context(connectionString);
        var organizations = await check.Organizations.Select(o => o.Id).ToListAsync(Api.Ct);
        Assert.Contains(otherOrganization, organizations);
        Assert.Contains(PostgresFixture.OrganizationId, organizations);
        Assert.Empty(await check.Database.GetPendingMigrationsAsync(Api.Ct));
    }

    [Fact]
    public async Task A_failing_migration_leaves_schema_and_data_intact()
    {
        var connectionString = await fixture.CreateEmptyDatabaseAsync();
        await using (var db = Context(connectionString))
        {
            await db.Database.MigrateAsync(Api.Ct);
            db.Catalogs.Add(Catalog.Create(PostgresFixture.OrganizationId, "Keep me", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Api.Ct);
        }

        await using (var broken = Context(connectionString, withBrokenMigration: true))
        {
            Assert.Equal([BrokenMigrationId], await broken.Database.GetPendingMigrationsAsync(Api.Ct));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => broken.Database.MigrateAsync(Api.Ct));
            Assert.Equal(PostgresErrorCodes.DivisionByZero, ex.SqlState);
        }

        await using var check = Context(connectionString);
        Assert.Equal(["Keep me"], await check.Catalogs.Select(c => c.Name).ToListAsync(Api.Ct));
        var brokenColumn = await check.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'catalogs' AND column_name = 'broken'").SingleAsync(Api.Ct);
        Assert.Equal(0, brokenColumn);
        Assert.DoesNotContain(BrokenMigrationId, await check.Database.GetAppliedMigrationsAsync(Api.Ct));
        Assert.Empty(await check.Database.GetPendingMigrationsAsync(Api.Ct));
    }

    private static ZirrusDbContext Context(string connectionString, bool withBrokenMigration = false)
    {
        var options = new DbContextOptionsBuilder<ZirrusDbContext>().UseNpgsql(connectionString);
        if (withBrokenMigration)
        {
            options.ReplaceService<IMigrationsAssembly, MigrationsWithBrokenOne>();
        }

        return new ZirrusDbContext(options.Options);
    }

    /// <summary>Adds a column, rewrites data, then fails: everything must roll back.</summary>
    [DbContext(typeof(ZirrusDbContext))]
    [Migration(BrokenMigrationId)]
    private sealed class BrokenOnPurpose : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>("broken", "catalogs", nullable: true);
            migrationBuilder.Sql("UPDATE catalogs SET name = 'overwritten'");
            migrationBuilder.Sql("SELECT 1 / 0");
        }
    }

#pragma warning disable EF1001 // MigrationsAssembly is internal API; used only to inject a test migration.
    private sealed class MigrationsWithBrokenOne(
        ICurrentDbContext currentContext,
        IDbContextOptions options,
        IMigrationsIdGenerator idGenerator,
        IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
        : MigrationsAssembly(currentContext, options, idGenerator, logger)
    {
        public override IReadOnlyDictionary<string, TypeInfo> Migrations
        {
            get
            {
                var all = new SortedList<string, TypeInfo>(base.Migrations.ToDictionary(), StringComparer.Ordinal)
                {
                    [BrokenMigrationId] = typeof(BrokenOnPurpose).GetTypeInfo(),
                };
                return all;
            }
        }
    }
#pragma warning restore EF1001
}

/// <summary>W3 acceptance: a stale metadata update returns 412 through the API.</summary>
/// <param name="fixture">Database and API.</param>
public sealed class AssetApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Metadata_edits_use_if_match_and_a_stale_etag_gets_412()
    {
        var press = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor));
        var asset = await Seed.AssetAsync(fixture, press);
        using var erin = fixture.Api.ClientAs("erin");
        var path = $"/api/v1/catalogs/{press.Id}/assets/{asset.Id}";

        using var get = await erin.GetAsync(new Uri(path, UriKind.Relative), Api.Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var etag = get.Headers.ETag!.Tag;
        Assert.Equal($"\"{asset.Revision}\"", etag);

        using var updated = await PutMetadata(erin, path, """{"title":"Harbour"}""", etag);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>(Api.Ct);
        Assert.Equal("Harbour", body.GetProperty("metadata").GetProperty("title").GetString());
        Assert.Equal($"\"{asset.Revision + 1}\"", updated.Headers.ETag!.Tag);

        using var stale = await PutMetadata(erin, path, """{"title":"Lost update"}""", etag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var after = await erin.GetFromJsonAsync<JsonElement>(new Uri(path, UriKind.Relative), Api.Ct);
        Assert.Equal("Harbour", after.GetProperty("metadata").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.PreconditionRequired)]
    [InlineData("*", HttpStatusCode.PreconditionRequired)]
    [InlineData("W/\"1\"", HttpStatusCode.PreconditionRequired)]
    public async Task Metadata_edits_require_a_strong_if_match(string? ifMatch, HttpStatusCode expected)
    {
        var press = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor));
        var asset = await Seed.AssetAsync(fixture, press);
        using var erin = fixture.Api.ClientAs("erin");
        using var response = await PutMetadata(erin, $"/api/v1/catalogs/{press.Id}/assets/{asset.Id}", "{}", ifMatch);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("victor", "{}", HttpStatusCode.Forbidden)]
    [InlineData("oscar", "{}", HttpStatusCode.NotFound)]
    [InlineData("erin", "[1]", HttpStatusCode.BadRequest)]
    public async Task Metadata_edits_follow_roles_and_validate_input(string user, string json, HttpStatusCode expected)
    {
        var press = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        var asset = await Seed.AssetAsync(fixture, press);
        using var client = fixture.Api.ClientAs(user);
        using var response = await PutMetadata(client, $"/api/v1/catalogs/{press.Id}/assets/{asset.Id}", json, $"\"{asset.Revision}\"");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task The_asset_list_is_scoped_by_role_and_paged()
    {
        var press = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        var active = await Seed.AssetAsync(fixture, press);
        await Seed.AssetAsync(fixture, press);
        await Seed.AssetAsync(fixture, press, AssetState.Quarantined);
        var path = $"/api/v1/catalogs/{press.Id}/assets";

        using var victor = fixture.Api.ClientAs("victor");
        var viewerPage = await victor.GetFromJsonAsync<JsonElement>(new Uri(path, UriKind.Relative), Api.Ct);
        Assert.Equal(2, viewerPage.GetProperty("items").GetArrayLength());

        using var erin = fixture.Api.ClientAs("erin");
        var first = await erin.GetFromJsonAsync<JsonElement>(new Uri($"{path}?limit=2", UriKind.Relative), Api.Ct);
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await erin.GetFromJsonAsync<JsonElement>(new Uri($"{path}?limit=2&cursor={cursor}", UriKind.Relative), Api.Ct);
        var ids = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Contains(active.Id, ids);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        using var oscar = fixture.Api.ClientAs("oscar");
        Assert.Equal(HttpStatusCode.NotFound, (await oscar.GetAsync(new Uri(path, UriKind.Relative), Api.Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await erin.GetAsync(new Uri($"{path}?cursor=zzz", UriKind.Relative), Api.Ct)).StatusCode);
    }

    [Fact]
    public async Task Search_combines_metadata_category_and_language_filters_without_leaking_assets()
    {
        var press = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        var archive = await Seed.CatalogAsync(fixture);
        var matching = await Seed.AssetAsync(fixture, press);
        var other = await Seed.AssetAsync(fixture, press);
        var foreign = await Seed.AssetAsync(fixture, archive);
        await Seed.AssetAsync(fixture, press, AssetState.Quarantined);
        var category = Category.CreateRoot(press, "Travel", DateTimeOffset.UtcNow);
        await using (var db = fixture.CreateContext())
        {
            foreach (var id in new[] { matching.Id, other.Id, foreign.Id })
            {
                var asset = await db.Assets.SingleAsync(a => a.Id == id, Api.Ct);
                asset.UpdateMetadata(AssetMetadata.Parse("""{"title":"Häuser running","rating":5}"""), asset.Revision, DateTimeOffset.UtcNow);
            }

            db.Categories.Add(category);
            db.AssetCategories.Add(AssetCategory.Assign(matching, category, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Api.Ct);
        }

        using var editor = fixture.Api.ClientAs("erin");
        using var viewer = fixture.Api.ClientAs("victor");
        using var outsider = fixture.Api.ClientAs("oscar");
        var path = $"/api/v1/catalogs/{press.Id}/assets";
        var filter = Uri.EscapeDataString("""{"rating":5}""");
        foreach (var term in new[] { "run", "Haus" })
        {
            var url = new Uri($"{path}?q={term}&categoryId={category.Id}&metadata={filter}", UriKind.Relative);
            foreach (var client in new[] { editor, viewer })
            {
                var page = await client.GetFromJsonAsync<JsonElement>(url, Api.Ct);
                Assert.Equal(matching.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
            }

            using var denied = await outsider.GetAsync(url, Api.Ct);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }

        using var invalid = await editor.GetAsync(new Uri($"{path}?metadata=%5B1%5D", UriKind.Relative), Api.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Viewers_do_not_see_assets_that_are_not_active()
    {
        var press = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        var quarantined = await Seed.AssetAsync(fixture, press, AssetState.Quarantined);
        var path = new Uri($"/api/v1/catalogs/{press.Id}/assets/{quarantined.Id}", UriKind.Relative);
        using var victor = fixture.Api.ClientAs("victor");
        using var erin = fixture.Api.ClientAs("erin");
        Assert.Equal(HttpStatusCode.NotFound, (await victor.GetAsync(path, Api.Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await erin.GetAsync(path, Api.Ct)).StatusCode);
    }

    [Fact]
    public async Task Metadata_edits_need_a_csrf_token()
    {
        var press = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor));
        var asset = await Seed.AssetAsync(fixture, press);
        using var erin = fixture.Api.ClientAs("erin");
        using var response = await PutMetadata(erin, $"/api/v1/catalogs/{press.Id}/assets/{asset.Id}", "{}", $"\"{asset.Revision}\"", csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PutMetadata(HttpClient client, string path, string json, string? ifMatch, bool csrf = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"{path}/metadata", UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (csrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", await Api.CsrfAsync(client));
        }

        return await client.SendAsync(request, Api.Ct);
    }
}

/// <summary>Reconciliation against the real database and filesystem store (report only).</summary>
/// <param name="fixture">Database and API host services.</param>
public sealed class ReconciliationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Reports_missing_and_resized_blobs_stale_uploads_and_expired_leases_without_changing_anything()
    {
        var press = await Seed.CatalogAsync(fixture);
        var present = await Seed.AssetAsync(fixture, press);
        var missing = await Seed.AssetAsync(fixture, press);
        var resized = await Seed.AssetAsync(fixture, press);
        var uploading = await Seed.AssetAsync(fixture, press, AssetState.Uploading);
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE assets SET state_changed_at = now() - interval '2 hours' WHERE id = {uploading.Id}", Api.Ct);
            db.Jobs.Add(JobIntent.ForAsset(present, "render", 1, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Api.Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE jobs SET state = 'Running', lease_until = now() - interval '1 minute', locked_by = 'worker-9'", Api.Ct);
        }

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        await store.PutAsync(BlobKey.Parse(present.OriginalKey), new MemoryStream(new byte[4]), null, Api.Ct);
        await store.PutAsync(BlobKey.Parse(resized.OriginalKey), new MemoryStream(new byte[5]), null, Api.Ct);

        var report = await scope.ServiceProvider.GetRequiredService<ReconcileStorage>().RunAsync(ReconciliationOptions.Default, Api.Ct);

        Assert.Equal(3, report.CheckedBlobs);
        Assert.Equal([missing.Id], report.MissingBlobs.Items.Select(b => b.AssetId));
        Assert.Equal([resized.Id], report.SizeMismatches.Items.Select(b => b.AssetId));
        Assert.Equal([(uploading.Id, AssetState.Uploading)], report.StaleTransitions.Items.Select(s => (s.AssetId, s.State)));
        Assert.Equal(["worker-9"], report.ExpiredLeases.Items.Select(l => l.LockedBy));
        Assert.NotNull(await store.StatAsync(BlobKey.Parse(resized.OriginalKey), Api.Ct));
    }
}
