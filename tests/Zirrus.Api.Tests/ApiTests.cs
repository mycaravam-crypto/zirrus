using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Api.Tests;

/// <summary>Shared helpers: CSRF token handling and seeding.</summary>
internal static class Api
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fetches the CSRF token (also stores the CSRF cookie in the client) and returns it.</summary>
    public static async Task<string> CsrfAsync(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<CsrfResponse>(new Uri("/api/v1/antiforgery", UriKind.Relative), Ct);
        return token!.Token;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (csrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        }

        return await client.SendAsync(request, Ct);
    }

    public sealed record CsrfResponse(string Token, string FormFieldName);
}

public sealed class HealthAndContractTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_are_anonymous_and_healthy(string path)
    {
        using var client = fixture.Api.ClientAs(null);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Api.Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        using var client = fixture.Api.ClientAs(null);
        var body = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Api.Ct);
        Assert.Contains("\"ListCatalogs\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_catalog_name_in_one_organization_is_rejected_by_the_database()
    {
        await using var db = fixture.CreateContext();
        db.Catalogs.AddRange(
            Catalog.Create(PostgresFixture.OrganizationId, "Duplicate", DateTimeOffset.UtcNow),
            Catalog.Create(PostgresFixture.OrganizationId, "Duplicate", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Api.Ct));
    }

    [Fact]
    public async Task Membership_cannot_reference_a_catalog_of_another_organization()
    {
        await using var db = fixture.CreateContext();
        var other = Organization.Create(Guid.NewGuid(), "Other organization", DateTimeOffset.UtcNow);
        var foreign = Catalog.Create(other.Id, "Foreign org catalog", DateTimeOffset.UtcNow);
        db.Organizations.Add(other);
        db.Catalogs.Add(foreign);
        await db.SaveChangesAsync(Api.Ct);

        // Same catalog ID, different organization: the composite foreign key must refuse it.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM catalog_memberships WHERE catalog_id = {foreign.Id}", Api.Ct);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO catalog_memberships (catalog_id, subject, organization_id, role, granted_at) VALUES ({foreign.Id}, 'x', {PostgresFixture.OrganizationId}, 'Viewer', now())", Api.Ct));
        Assert.Equal("23503", ex.SqlState); // foreign_key_violation
    }
}

/// <summary>Q3: the security.md role matrix through the real API, database and policies.</summary>
public sealed class AuthorizationMatrixTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Admin = "alice,admin";
    private const string Editor = "erin";
    private const string Viewer = "victor";
    private const string Outsider = "oscar";
    private readonly PostgresFixture _fixture;
    private Catalog _press = null!;
    private Catalog _archive = null!;

    public AuthorizationMatrixTests(PostgresFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        _press = Catalog.Create(PostgresFixture.OrganizationId, $"Press {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        _archive = Catalog.Create(PostgresFixture.OrganizationId, $"Archive {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        db.Catalogs.AddRange(_press, _archive);
        db.CatalogMemberships.AddRange(
            CatalogMembership.Grant(_press, Editor, CatalogRole.Editor, DateTimeOffset.UtcNow),
            CatalogMembership.Grant(_press, Viewer, CatalogRole.Viewer, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Api.Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData("/api/v1/catalogs")]
    [InlineData("/api/v1/me")]
    [InlineData("/api/v1/antiforgery")]
    public async Task Anonymous_requests_get_401_not_a_redirect(string path)
    {
        using var client = _fixture.Api.ClientAs(null);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Api.Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_reports_the_user_and_admin_flag()
    {
        using var admin = _fixture.Api.ClientAs(Admin);
        var me = await admin.GetFromJsonAsync<MeResponse>(new Uri("/api/v1/me", UriKind.Relative), Api.Ct);
        Assert.Equal(("alice", true), (me!.Subject, me.IsOrganizationAdmin));

        using var viewer = _fixture.Api.ClientAs(Viewer);
        me = await viewer.GetFromJsonAsync<MeResponse>(new Uri("/api/v1/me", UriKind.Relative), Api.Ct);
        Assert.Equal(("victor", false), (me!.Subject, me.IsOrganizationAdmin));
    }

    [Theory]
    [InlineData(Admin, "admin", 2)]
    [InlineData(Editor, "editor", 1)]
    [InlineData(Viewer, "viewer", 1)]
    [InlineData(Outsider, null, 0)]
    public async Task Catalog_list_is_scoped_to_granted_catalogs(string user, string? access, int visible)
    {
        using var client = _fixture.Api.ClientAs(user);
        var catalogs = (await client.GetFromJsonAsync<List<CatalogSummary>>(new Uri("/api/v1/catalogs", UriKind.Relative), Api.Ct))!
            .Where(c => c.Id == _press.Id || c.Id == _archive.Id)
            .ToList();
        Assert.Equal(visible, catalogs.Count);
        Assert.All(catalogs, c => Assert.Equal(access, c.Access));
        if (visible == 1)
        {
            Assert.Equal(_press.Id, catalogs[0].Id);
        }
    }

    [Theory]
    [InlineData(Editor)]
    [InlineData(Viewer)]
    [InlineData(Outsider)]
    public async Task Only_admins_create_catalogs(string user)
    {
        using var client = _fixture.Api.ClientAs(user);
        using var response = await Api.SendAsync(client, HttpMethod.Post, "/api/v1/catalogs", new { name = $"Nope {Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_creates_a_catalog_and_duplicates_conflict()
    {
        using var client = _fixture.Api.ClientAs(Admin);
        var name = $"Brand {Guid.NewGuid():N}";
        using var created = await Api.SendAsync(client, HttpMethod.Post, "/api/v1/catalogs", new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);

        using var duplicate = await Api.SendAsync(client, HttpMethod.Post, "/api/v1/catalogs", new { name });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Unsafe_requests_without_a_csrf_token_are_rejected()
    {
        using var client = _fixture.Api.ClientAs(Admin);
        using var response = await Api.SendAsync(client, HttpMethod.Post, "/api/v1/catalogs", new { name = "No token" }, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_csrf_token_of_another_user_is_rejected()
    {
        using var victim = _fixture.Api.ClientAs(Admin);
        using var attacker = _fixture.Api.ClientAs(Outsider);
        var attackerToken = await Api.CsrfAsync(attacker);
        await Api.CsrfAsync(victim); // victim has its own CSRF cookie

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/catalogs", UriKind.Relative))
        {
            Content = JsonContent.Create(new { name = "Forged" }),
        };
        request.Headers.Add("X-CSRF-TOKEN", attackerToken);
        using var response = await victim.SendAsync(request, Api.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admin_manages_roles_and_access_follows()
    {
        using var admin = _fixture.Api.ClientAs(Admin);
        using var outsider = _fixture.Api.ClientAs(Outsider);
        var path = $"/api/v1/catalogs/{_archive.Id}/members/{Outsider}";

        using (var granted = await Api.SendAsync(admin, HttpMethod.Put, path, new { role = "viewer" }))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }

        var visible = await outsider.GetFromJsonAsync<List<CatalogSummary>>(new Uri("/api/v1/catalogs", UriKind.Relative), Api.Ct);
        Assert.Contains(visible!, c => c.Id == _archive.Id && c.Access == "viewer");

        using (var removed = await Api.SendAsync(admin, HttpMethod.Delete, path))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        visible = await outsider.GetFromJsonAsync<List<CatalogSummary>>(new Uri("/api/v1/catalogs", UriKind.Relative), Api.Ct);
        Assert.DoesNotContain(visible!, c => c.Id == _archive.Id);
    }

    [Theory]
    [InlineData(Editor, "viewer", HttpStatusCode.Forbidden)]
    [InlineData(Viewer, "editor", HttpStatusCode.Forbidden)]
    [InlineData(Admin, "owner", HttpStatusCode.BadRequest)]
    public async Task Role_assignment_rejects_non_admins_and_unknown_roles(string user, string role, HttpStatusCode expected)
    {
        using var client = _fixture.Api.ClientAs(user);
        using var response = await Api.SendAsync(client, HttpMethod.Put, $"/api/v1/catalogs/{_press.Id}/members/someone", new { role });
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Role_assignment_on_an_unknown_catalog_is_404()
    {
        using var client = _fixture.Api.ClientAs(Admin);
        using var response = await Api.SendAsync(client, HttpMethod.Put, $"/api/v1/catalogs/{Guid.NewGuid()}/members/someone", new { role = "viewer" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_schema_creation_is_admin_only_and_reads_are_catalog_scoped()
    {
        using var admin = _fixture.Api.ClientAs(Admin);
        using var editor = _fixture.Api.ClientAs(Editor);
        using var viewer = _fixture.Api.ClientAs(Viewer);
        using var outsider = _fixture.Api.ClientAs(Outsider);
        var path = $"/api/v1/catalogs/{_press.Id}/metadata-schema";
        var fields = new[] { new Zirrus.Domain.Assets.MetadataFieldDefinition("title", "Title", Zirrus.Domain.Assets.MetadataFieldType.Text, Required: true) };

        using var denied = await Api.SendAsync(editor, HttpMethod.Post, path, fields);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var created = await Api.SendAsync(admin, HttpMethod.Post, path, fields);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var schema = await viewer.GetFromJsonAsync<SchemaDetails>(new Uri(path, UriKind.Relative), Api.Ct);
        Assert.Equal(1, schema!.Version);
        Assert.True(Assert.Single(schema.Fields).Required);
        using var hidden = await outsider.GetAsync(new Uri(path, UriKind.Relative), Api.Ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var duplicate = await Api.SendAsync(admin, HttpMethod.Post, path, fields);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    private sealed record MeResponse(string Subject, string Name, bool IsOrganizationAdmin);
}

/// <summary>Server-side sessions and the shared, encrypted key ring (security.md, W2).</summary>
public sealed class SessionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Logout_ends_the_session_for_every_copy_of_the_cookie()
    {
        var api = fixture.CreateApi(headerAuthentication: false);
        using var browser = api.ClientAs(null);
        var cookie = await SignInAsync(browser, "sam");

        using var copy = CookieClient(api, cookie);
        Assert.Equal(HttpStatusCode.OK, (await copy.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Api.Ct)).StatusCode);

        // Logout is a form POST with the antiforgery field (OIDC is not configured in tests).
        var token = await browser.GetFromJsonAsync<Api.CsrfResponse>(new Uri("/api/v1/antiforgery", UriKind.Relative), Api.Ct);
        using var form = new FormUrlEncodedContent([new KeyValuePair<string, string>(token!.FormFieldName, token.Token)]);
        using var logout = await browser.PostAsync(new Uri("/logout", UriKind.Relative), form, Api.Ct);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await copy.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Api.Ct)).StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.Sessions.AnyAsync(s => s.Subject == "sam", Api.Ct));
    }

    [Fact]
    public async Task Logout_without_a_csrf_token_is_rejected()
    {
        var api = fixture.CreateApi(headerAuthentication: false);
        using var browser = api.ClientAs(null);
        await SignInAsync(browser, "tina");
        using var logout = await browser.PostAsync(new Uri("/logout", UriKind.Relative), new FormUrlEncodedContent([]), Api.Ct);
        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
    }

    [Fact]
    public async Task Cookie_holds_only_a_session_key_and_the_ticket_is_encrypted()
    {
        var api = fixture.CreateApi(headerAuthentication: false);
        using var browser = api.ClientAs(null);
        var cookie = await SignInAsync(browser, "uma");
        Assert.True(cookie.Length < 400, $"session cookie is {cookie.Length} characters");

        await using var db = fixture.CreateContext();
        var session = await db.Sessions.SingleAsync(s => s.Subject == "uma", Api.Ct);
        Assert.DoesNotContain("uma", System.Text.Encoding.UTF8.GetString(session.ProtectedTicket), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessions_and_keys_work_across_two_replicas_and_the_key_ring_is_encrypted_at_rest()
    {
        var replicaA = fixture.CreateApi(headerAuthentication: false);
        var replicaB = fixture.CreateApi(headerAuthentication: false);
        using var browser = replicaA.ClientAs(null);
        var cookie = await SignInAsync(browser, "vera");

        using var viaB = CookieClient(replicaB, cookie);
        Assert.Equal(HttpStatusCode.OK, (await viaB.GetAsync(new Uri("/api/v1/me", UriKind.Relative), Api.Ct)).StatusCode);

        await using var db = fixture.CreateContext();
        var keys = await db.DataProtectionKeys.Select(k => k.Xml!).ToListAsync(Api.Ct);
        Assert.NotEmpty(keys);
        Assert.All(keys, xml =>
        {
            Assert.Contains("encryptedKey", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Production_refuses_to_start_without_the_key_ring_key()
    {
        var api = fixture.CreateApi(headerAuthentication: false, dataProtectionKey: string.Empty);
        var ex = Assert.ThrowsAny<Exception>(() => api.ClientAs(null));
        Assert.Contains("DataProtection:Key", ex.ToString(), StringComparison.Ordinal);
    }

    private static async Task<string> SignInAsync(HttpClient browser, string subject)
    {
        using var response = await browser.GetAsync(new Uri($"{ApiFactory.SignInPath}?sub={subject}", UriKind.Relative), Api.Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("zirrus.session=", StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        return setCookie.Split(';')[0]["zirrus.session=".Length..];
    }

    private static HttpClient CookieClient(ApiFactory api, string cookie)
    {
        var client = api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"zirrus.session={cookie}");
        return client;
    }
}
