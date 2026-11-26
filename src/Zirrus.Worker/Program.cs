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
builder.Services.AddHostedService<HeartbeatService>();

var app = builder.Build();
app.MapZirrusHealthChecks();
await app.RunAsync();
return 0;
