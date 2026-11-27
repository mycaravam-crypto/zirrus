extern alias renderer;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Zirrus.Application.Ingestion;
using Zirrus.Infrastructure.Ingestion;
using ParserLimits = renderer::Zirrus.Renderer.ParserLimits;
using RenderEndpoint = renderer::Zirrus.Renderer.RenderEndpoint;
using SubprocessRenderer = renderer::Zirrus.Renderer.SubprocessRenderer;

namespace Zirrus.Api.Tests;

/// <summary>
/// W4 parser isolation: every file is decoded in a fresh child process with time and memory limits,
/// and the worker reaches it only through the renderer's HTTP endpoint.
/// </summary>
public sealed class RendererServiceTests
{
    private static readonly string ChildAssembly = typeof(RenderEndpoint).Assembly.Location;

    [Fact]
    public async Task A_child_process_renders_a_50_megapixel_jpeg_within_the_memory_limit()
    {
        var limits = new ParserLimits();
        var jpeg = Images.Encode(8640, 5760, SKEncodedImageFormat.Jpeg, quality: 85); // 49.8 MP, the spike's worst case
        var response = await new SubprocessRenderer(limits, ChildAssembly).RenderAsync(new MemoryStream(jpeg), RenditionSpec.Default, Api.Ct);

        Assert.Null(response.Error);
        Assert.Equal([("thumbnail", 320, 213), ("preview", 1600, 1067)], response.Images.Select(i => (i.Kind, i.Width, i.Height)));
        Assert.NotNull(response.PeakRssBytes);
        Assert.InRange(response.PeakRssBytes!.Value, 1, limits.MaxMemoryBytes); // acceptance: peak parser RSS under 512 MiB
    }

    [Fact]
    public async Task Corrupt_and_oversize_files_are_rejected_by_the_child()
    {
        var renderer = new SubprocessRenderer(new ParserLimits(), ChildAssembly);
        var jpeg = Images.Encode(800, 600, SKEncodedImageFormat.Jpeg);
        var truncated = await renderer.RenderAsync(new MemoryStream(jpeg[..(jpeg.Length / 3)]), RenditionSpec.Default, Api.Ct);
        Assert.StartsWith("decode failed", truncated.Error, StringComparison.Ordinal);
        var huge = await renderer.RenderAsync(new MemoryStream(Images.HugePngHeader()), RenditionSpec.Default, Api.Ct);
        Assert.Contains("exceeds 50000000 pixels", huge.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_watchdog_rejects_files_that_exceed_the_time_or_memory_limit()
    {
        var jpeg = Images.Encode(400, 300, SKEncodedImageFormat.Jpeg);

        var slow = await new SubprocessRenderer(new ParserLimits { Timeout = TimeSpan.FromMilliseconds(1) }, ChildAssembly)
            .RenderAsync(new MemoryStream(jpeg), RenditionSpec.Default, Api.Ct);
        Assert.Equal("parser time limit of 0 s exceeded", slow.Error);

        // A .NET process needs far more than 8 MiB, so the first sample trips the memory limit.
        var greedy = await new SubprocessRenderer(new ParserLimits { MaxMemoryBytes = 8L * 1024 * 1024, SampleInterval = TimeSpan.FromMilliseconds(5) }, ChildAssembly)
            .RenderAsync(new MemoryStream(jpeg), RenditionSpec.Default, Api.Ct);
        Assert.Equal("parser memory limit of 8 MiB exceeded", greedy.Error);
    }

    [Fact]
    public async Task The_worker_client_round_trips_through_the_endpoint_and_maps_rejections()
    {
        await using var app = await StartRendererAsync();
        using var http = app.GetTestClient();
        var client = new HttpImageRenderer(http);

        var images = await client.RenderAsync(new MemoryStream(Images.Encode(1000, 500, SKEncodedImageFormat.Png)), RenditionSpec.Default, Api.Ct);
        Assert.Equal([(320, 160), (1000, 500)], images.Select(i => (i.Width, i.Height)));
        Assert.Equal("image/webp", MediaFormat(images[0]));

        await Assert.ThrowsAsync<UnsupportedImageException>(() => client.RenderAsync(new MemoryStream([0xFF, 0xD8, 0xFF, 1, 2]), RenditionSpec.Default, Api.Ct));
    }

    [Fact]
    public async Task The_endpoint_requires_rendition_specs_and_unreachable_renderers_are_transient()
    {
        await using var app = await StartRendererAsync();
        using var http = app.GetTestClient();
        using var noSpecs = await http.PostAsync(new Uri("/render", UriKind.Relative), new ByteArrayContent([1]), Api.Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, noSpecs.StatusCode);

        using var down = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9") };
        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpImageRenderer(down).RenderAsync(new MemoryStream([1]), RenditionSpec.Default, Api.Ct));
    }

    private static string? MediaFormat(RenderedImage image) => Zirrus.Domain.Assets.MediaFormat.Sniff(image.Content.Span);

    private static async Task<WebApplication> StartRendererAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new SubprocessRenderer(new ParserLimits(), ChildAssembly));
        builder.Services.AddSingleton(new SemaphoreSlim(1));
        var app = builder.Build();
        var renderer = app.Services.GetRequiredService<SubprocessRenderer>();
        var slots = app.Services.GetRequiredService<SemaphoreSlim>();
        app.Run(async context =>
        {
            var result = await RenderEndpoint.HandleAsync(context, renderer, slots, context.RequestAborted);
            await result.ExecuteAsync(context);
        });
        await app.StartAsync(Api.Ct);
        return app;
    }
}
