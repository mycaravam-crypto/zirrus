using Zirrus.Infrastructure.Hosting;
using Zirrus.Renderer;

// Isolated renderer service (W4, ingestion-api-ui.md): no secrets, no database, no internet; reachable
// only by the worker. Every file is parsed in a fresh child process with time and memory limits.
if (args is [RenderEndpoint.RenderOneArgument, var specs, ..])
{
    return await RenderOne.RunAsync(specs);
}

if (await HostingExtensions.RunHealthProbeIfRequestedAsync(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.Configure<ParserLimits>(builder.Configuration.GetSection("Parser"));
builder.Services.AddSingleton(sp => new SubprocessRenderer(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ParserLimits>>().Value));
builder.Services.AddSingleton(new SemaphoreSlim(builder.Configuration.GetValue("Parser:Concurrency", 2)));
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapZirrusHealthChecks();
app.MapPost("/render", RenderEndpoint.HandleAsync);
await app.RunAsync();
return 0;

