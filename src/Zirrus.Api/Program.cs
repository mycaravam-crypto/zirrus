using Zirrus.Application.Catalogs;
using Zirrus.Infrastructure;
using Zirrus.Infrastructure.Hosting;

if (await HostingExtensions.RunHealthProbeIfRequestedAsync(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddZirrusSecrets();
builder.Services.AddZirrusInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "ZIRRUS API";
    document.Info.Version = "v1";
    return Task.CompletedTask;
}));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi("/openapi/{documentName}.json");
app.MapZirrusHealthChecks();

var v1 = app.MapGroup("/api/v1").WithTags("Catalogs");

// Lists catalogs ordered by name. Becomes permission-filtered with catalog roles in W2 (#22).
v1.MapGet("/catalogs", async (ListCatalogs listCatalogs, CancellationToken cancellationToken) =>
        TypedResults.Ok(await listCatalogs.ExecuteAsync(cancellationToken)))
    .WithName("ListCatalogs")
    .WithSummary("List the catalogs assets are organized in")
    .WithDescription("Returns all catalogs ordered by name. Until catalog permissions arrive (W2), every catalog is listed.");

await app.RunAsync();
return 0;

/// <summary>Entry point type, referenced by integration tests through WebApplicationFactory.</summary>
public partial class Program;
