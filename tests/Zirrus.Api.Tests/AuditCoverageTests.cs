using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Zirrus.Application.Audit;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;
using Zirrus.Infrastructure.Audit;

namespace Zirrus.Api.Tests;

/// <summary>
/// #48: the audited actions not covered by <see cref="AuditTests"/>' exact sequence (upload, retry,
/// schema creation, role revocation, sign-out) each leave exactly one event when they succeed, and
/// none when they are rejected. Sign-in is recorded by the OIDC handler, so it is checked in the
/// Compose end-to-end job against Keycloak, not here.
/// </summary>
/// <param name="fixture">Database and API.</param>
public sealed class AuditCoverageTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Each successful change records one event; rejected attempts record none of that action.</summary>
    [Fact]
    public async Task Each_audited_action_is_recorded_exactly_once_and_rejected_attempts_are_not()
    {
        var catalog = await Seed.CatalogAsync(fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer), ("ruth", CatalogRole.Viewer));
        using var admin = fixture.Api.ClientAs("alice,admin");
        using var erin = fixture.Api.ClientAs("erin");
        using var victor = fixture.Api.ClientAs("victor");
        var assets = $"/api/v1/catalogs/{catalog.Id}/assets";

        // Upload: one event for the editor's upload, none for the viewer's refused one.
        using (var refused = await UploadAsync(victor, catalog.Id))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Guid uploaded;
        using (var created = await UploadAsync(erin, catalog.Id))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            uploaded = (await created.Content.ReadFromJsonAsync<JsonElement>(Api.Ct)).GetProperty("asset").GetProperty("id").GetGuid();
        }

        // Schema: one event for the admin's creation, none for the editor's refusal or the duplicate.
        var schemaPath = $"/api/v1/catalogs/{catalog.Id}/metadata-schema";
        var fields = new[] { new MetadataFieldDefinition("title", "Title", MetadataFieldType.Text, Required: false) };
        Assert.Equal(HttpStatusCode.Forbidden, (await Api.SendAsync(erin, HttpMethod.Post, schemaPath, fields)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Api.SendAsync(admin, HttpMethod.Post, schemaPath, fields)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.SendAsync(admin, HttpMethod.Post, schemaPath, fields)).StatusCode);

        // Role revocation: one event; removing the same membership again is not found and records nothing.
        var memberPath = $"/api/v1/catalogs/{catalog.Id}/members/ruth";
        Assert.Equal(HttpStatusCode.Forbidden, (await Api.SendAsync(erin, HttpMethod.Delete, memberPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Api.SendAsync(admin, HttpMethod.Delete, memberPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.SendAsync(admin, HttpMethod.Delete, memberPath)).StatusCode);

        // Retry: one event when a dead stage is requeued; the repeated retry conflicts and records nothing.
        var failed = await FailedWithDeadJobAsync(catalog);
        var retryPath = $"{assets}/{failed.Id}/retry";
        Assert.Equal(HttpStatusCode.NotFound, (await Api.SendAsync(victor, HttpMethod.Post, retryPath)).StatusCode); // viewers do not see failed assets
        Assert.Equal(HttpStatusCode.OK, (await Api.SendAsync(erin, HttpMethod.Post, retryPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.SendAsync(erin, HttpMethod.Post, retryPath)).StatusCode);

        await using var db = fixture.CreateContext();
        var events = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.CatalogId == catalog.Id && e.Action != AuditActions.AccessDenied)
            .ToListAsync(Api.Ct);
        Assert.Equal(
            [
                (AuditActions.Retry, "erin"),
                (AuditActions.Upload, "erin"),
                (AuditActions.MemberRevoke, "alice"),
                (AuditActions.SchemaCreate, "alice"),
            ],
            events.Select(e => (e.Action, e.Actor)).Order());
        Assert.Equal(uploaded, events.Single(e => e.Action == AuditActions.Upload).ResourceId);
        Assert.Equal(failed.Id, events.Single(e => e.Action == AuditActions.Retry).ResourceId);
        Assert.All(events, e => Assert.Equal("success", e.Outcome));

        // The refused requests were authenticated 403 or 404 responses, so they appear as access.denied instead.
        Assert.True(await db.Set<AuditEvent>().CountAsync(e => e.CatalogId == catalog.Id && e.Action == AuditActions.AccessDenied, Api.Ct) >= 4);
    }

    /// <summary>Sign-out through the session cookie records one event for that user.</summary>
    [Fact]
    public async Task Sign_out_is_recorded_once()
    {
        var subject = $"sam-{Guid.NewGuid():N}";
        var api = fixture.CreateApi(headerAuthentication: false);
        using var browser = api.ClientAs(null);
        using (var signIn = await browser.GetAsync(new Uri($"{ApiFactory.SignInPath}?sub={subject}", UriKind.Relative), Api.Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
        }

        var token = await browser.GetFromJsonAsync<Api.CsrfResponse>(new Uri("/api/v1/antiforgery", UriKind.Relative), Api.Ct);
        using var form = new FormUrlEncodedContent([new KeyValuePair<string, string>(token!.FormFieldName, token.Token)]);
        using (var logout = await browser.PostAsync(new Uri("/logout", UriKind.Relative), form, Api.Ct))
        {
            Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        }

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Set<AuditEvent>().CountAsync(e => e.Action == AuditActions.SignOut && e.Actor == subject, Api.Ct));
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid catalogId)
    {
        using var file = new ByteArrayContent(Images.Encode(32, 32, SKEncodedImageFormat.Png));
        using var form = new MultipartFormDataContent { { file, "file", "audit.png" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/catalogs/{catalogId}/assets", UriKind.Relative)) { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", await Api.CsrfAsync(client));
        return await client.SendAsync(request, Api.Ct);
    }

    /// <summary>A failed asset whose processing job has used up its attempts, as left by the worker.</summary>
    private async Task<Asset> FailedWithDeadJobAsync(Catalog catalog)
    {
        var seeded = await Seed.AssetAsync(fixture, catalog, AssetState.Processing);
        await using var db = fixture.CreateContext();
        var asset = await db.Assets.SingleAsync(a => a.Id == seeded.Id, Api.Ct);
        asset.Fail("render: timeout", DateTimeOffset.UtcNow);
        var job = JobIntent.ForAsset(asset, PipelineStages.Process, PipelineStages.Version, DateTimeOffset.UtcNow);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(Api.Ct);
        await db.Jobs.Where(j => j.JobKey == job.JobKey).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, JobState.Dead), Api.Ct);
        return asset;
    }
}
