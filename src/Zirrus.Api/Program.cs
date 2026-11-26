using Zirrus.Api.Endpoints;
using Zirrus.Api.Security;
using Zirrus.Infrastructure;
using Zirrus.Infrastructure.Hosting;

if (await HostingExtensions.RunHealthProbeIfRequestedAsync(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddZirrusSecrets();
builder.Services.AddZirrusInfrastructure(builder.Configuration);
builder.Services.AddZirrusSessions(builder.Configuration, builder.Environment);
builder.AddZirrusAuthentication();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "ZIRRUS API";
    document.Info.Version = "v1";
    return Task.CompletedTask;
}));

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapZirrusHealthChecks();

// Every /api route requires a signed-in user and a CSRF token on unsafe methods (security.md).
var api = app.MapGroup("/api/v1")
    .RequireAuthorization()
    .AddEndpointFilter(AuthenticationSetup.ValidateAntiforgery);
app.MapAccountEndpoints(api);
api.MapCatalogEndpoints();
api.MapAssetEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point type, referenced by integration tests through WebApplicationFactory.</summary>
public partial class Program;
