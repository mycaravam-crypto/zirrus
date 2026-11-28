using Zirrus.Infrastructure;
using Zirrus.Infrastructure.Hosting;
using Zirrus.Worker;

if (await HostingExtensions.RunHealthProbeIfRequestedAsync(args) is { } probeExitCode)
{
    return probeExitCode;
}

// The worker hosts background processing; it exposes only health endpoints, on the private network.
var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddZirrusSecrets();
builder.Services.AddZirrusInfrastructure(builder.Configuration);
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["Renderer:Url"]))
{
    // Parsers handle untrusted files: outside Development they run only in the isolated renderer service.
    throw new InvalidOperationException("Renderer:Url is required outside Development (isolated parser, ingestion-api-ui.md).");
}

builder.Services.AddZirrusIngestion(builder.Configuration);
builder.Services.Configure<IngestionSettings>(builder.Configuration.GetSection("Ingestion"));
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.Configure<ReconciliationSettings>(builder.Configuration.GetSection("Reconciliation"));
builder.Services.AddHostedService<ReconciliationService>();
builder.Services.Configure<AuditDigestSettings>(builder.Configuration.GetSection("Audit"));
builder.Services.AddHostedService<AuditDigestService>();
builder.Services.Configure<AssetPurgeSettings>(builder.Configuration.GetSection("Retention"));
builder.Services.AddHostedService<AssetPurgeService>();

var app = builder.Build();
app.MapZirrusHealthChecks();
await app.RunAsync();
return 0;
