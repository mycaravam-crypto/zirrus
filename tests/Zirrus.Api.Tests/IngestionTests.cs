using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;
using Zirrus.Application.Ingestion;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;
using Zirrus.Infrastructure;
using Zirrus.Infrastructure.Ingestion;

namespace Zirrus.Api.Tests;

/// <summary>Test images generated with SkiaSharp, so no binary corpus is needed for the basics.</summary>
internal static class Images
{
    public static byte[] Encode(int width, int height, SKEncodedImageFormat format, int quality = 90)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.Navy, SKColors.Orange], SKShaderTileMode.Clamp) };
            canvas.DrawRect(0, 0, width, height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>A PNG whose header claims 10000×10000 pixels; no decodable pixel data follows.</summary>
    public static byte[] HugePngHeader()
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, 10_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 10_000);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // truecolor
        WriteChunk(stream, "IHDR", ihdr);
        WriteChunk(stream, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, System.IO.Hashing.Crc32.HashToUInt32(typeAndData));
        stream.Write(length);
    }
}

/// <summary>SkiaSharp renderer: sizes, formats, orientation, limits and determinism (spike §2).</summary>
public sealed class RendererTests
{
    private readonly SkiaImageRenderer _renderer = new();

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, 4000, 3000, 320, 240, 1600, 1200)]
    [InlineData(SKEncodedImageFormat.Png, 1000, 2000, 160, 320, 800, 1600)]
    [InlineData(SKEncodedImageFormat.Webp, 200, 100, 200, 100, 200, 100)]
    public async Task Renders_a_thumbnail_and_preview_without_upscaling(SKEncodedImageFormat format, int w, int h, int tw, int th, int pw, int ph)
    {
        var output = await _renderer.RenderAsync(new MemoryStream(Images.Encode(w, h, format)), RenditionSpec.Default, Api.Ct);
        Assert.Equal([("thumbnail", tw, th, "image/webp"), ("preview", pw, ph, "image/jpeg")],
            output.Select(o => (o.Spec.Kind, o.Width, o.Height, o.Spec.MediaType)));
        Assert.Equal("image/webp", MediaFormat.Sniff(output[0].Content.Span));
        Assert.Equal("image/jpeg", MediaFormat.Sniff(output[1].Content.Span));
    }

    [Fact]
    public async Task Applies_the_exif_orientation()
    {
        var output = await _renderer.RenderAsync(new MemoryStream(Images.Fixture("orientation-6-400x200.jpg")), RenditionSpec.Default, Api.Ct);
        Assert.Equal((200, 400), (output[1].Width, output[1].Height)); // 90° rotation swaps the edges
    }

    [Fact]
    public async Task Output_is_deterministic()
    {
        var source = Images.Encode(1200, 900, SKEncodedImageFormat.Jpeg);
        var first = await _renderer.RenderAsync(new MemoryStream(source), RenditionSpec.Default, Api.Ct);
        var second = await _renderer.RenderAsync(new MemoryStream(source), RenditionSpec.Default, Api.Ct);
        Assert.Equal(first.Select(o => o.Content.ToArray()), second.Select(o => o.Content.ToArray()));
    }

    [Fact]
    public async Task Truncated_corrupt_and_oversize_images_are_unsupported()
    {
        var jpeg = Images.Encode(800, 600, SKEncodedImageFormat.Jpeg);
        await Assert.ThrowsAsync<UnsupportedImageException>(() => _renderer.RenderAsync(new MemoryStream(jpeg[..(jpeg.Length / 3)]), RenditionSpec.Default, Api.Ct));
        await Assert.ThrowsAsync<UnsupportedImageException>(() => _renderer.RenderAsync(new MemoryStream([0xFF, 0xD8, 0xFF, 0, 1, 2, 3]), RenditionSpec.Default, Api.Ct));
        var huge = await Assert.ThrowsAsync<UnsupportedImageException>(() => _renderer.RenderAsync(new MemoryStream(Images.HugePngHeader()), RenditionSpec.Default, Api.Ct));
        Assert.Contains("exceeds", huge.Message, StringComparison.Ordinal);
    }
}

/// <summary>clamd INSTREAM client against a scripted fake daemon.</summary>
public sealed class ClamAvScannerTests
{
    [Theory]
    [InlineData("stream: OK", true, null)]
    [InlineData("stream: Eicar-Test-Signature FOUND", false, "Eicar-Test-Signature")]
    public async Task Interprets_clamd_verdicts_and_streams_the_content(string reply, bool clean, string? signature)
    {
        await using var daemon = new FakeClamd(reply);
        var content = Encoding.ASCII.GetBytes(new string('z', 200_000));
        var result = await new ClamAvScanner(daemon.Options).ScanAsync(new MemoryStream(content), Api.Ct);
        Assert.Equal((clean, signature), (result.Clean, result.Signature));
        Assert.Equal("zINSTREAM", daemon.Command);
        Assert.Equal(content, daemon.Received);
    }

    [Fact]
    public async Task Errors_and_unreachable_daemons_fail_closed()
    {
        await using (var daemon = new FakeClamd("INSTREAM size limit exceeded. ERROR"))
        {
            await Assert.ThrowsAsync<ScannerUnavailableException>(() => new ClamAvScanner(daemon.Options).ScanAsync(new MemoryStream([1]), Api.Ct));
        }

        var closedPort = new ClamAvOptions { Host = "127.0.0.1", Port = FakeClamd.FreePort(), Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAsync<ScannerUnavailableException>(() => new ClamAvScanner(closedPort).ScanAsync(new MemoryStream([1]), Api.Ct));
    }

    /// <summary>Accepts one INSTREAM session and answers with a fixed reply.</summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _session;

        public FakeClamd(string reply)
        {
            _listener.Start();
            _session = ServeAsync(reply);
        }

        public ClamAvOptions Options => new() { Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Timeout = TimeSpan.FromSeconds(10) };

        public string Command { get; private set; } = string.Empty;

        public byte[] Received { get; private set; } = [];

        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            await _session;
            _listener.Stop();
            _listener.Dispose();
        }

        private async Task ServeAsync(string reply)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var command = new List<byte>();
            var one = new byte[1];
            while (await stream.ReadAsync(one) == 1 && one[0] != 0)
            {
                command.Add(one[0]);
            }

            Command = Encoding.ASCII.GetString([.. command]);
            using var received = new MemoryStream();
            var length = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(length);
                var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(length);
                if (size == 0)
                {
                    break;
                }

                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk);
                received.Write(chunk);
            }

            Received = received.ToArray();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0"));
        }
    }
}

/// <summary>
/// W4 pipeline through the real API, PostgreSQL queue, filesystem store and SkiaSharp, with a
/// scripted scanner: upload → scan → process → active, plus rejection, fail-closed and retry paths.
/// </summary>
public sealed class IngestionPipelineTests : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly ServiceProvider _worker;
    private readonly ScriptedScanner _scanner = new();

    public IngestionPipelineTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Zirrus"] = fixture.ConnectionString,
            ["Storage:Root"] = fixture.BlobRoot,
            ["Zirrus:OrganizationId"] = PostgresFixture.OrganizationId.ToString(),
        }).Build();
        var services = new ServiceCollection().AddLogging(logging => logging.Services.Configure<Microsoft.Extensions.Logging.LoggerFilterOptions>(o => o.MinLevel = Microsoft.Extensions.Logging.LogLevel.Warning));
        services.AddZirrusInfrastructure(configuration).AddZirrusIngestion(configuration);
        services.Replace(ServiceDescriptor.Singleton<IMalwareScanner>(_scanner));
        _worker = services.BuildServiceProvider();

        // Tests share the class fixture's queue and run one after another: retire jobs left by earlier tests.
        using var db = fixture.CreateContext();
        db.Database.ExecuteSql($"UPDATE jobs SET state = 'Done' WHERE state IN ('Ready', 'Running')");
    }

    [Fact]
    public async Task An_upload_is_scanned_rendered_and_published_with_downloads()
    {
        var press = await Seed.CatalogAsync(_fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        using var erin = _fixture.Api.ClientAs("erin");
        using var victor = _fixture.Api.ClientAs("victor");
        var jpeg = Images.Encode(2400, 1800, SKEncodedImageFormat.Jpeg);

        using var uploaded = await Upload(erin, press.Id, jpeg, "harbour.jpg");
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var body = await uploaded.Content.ReadFromJsonAsync<JsonElement>(Api.Ct);
        var asset = body.GetProperty("asset");
        var assetId = asset.GetProperty("id").GetGuid();
        Assert.Equal(("quarantined", "image/jpeg", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(jpeg))),
            (asset.GetProperty("state").GetString(), asset.GetProperty("mediaType").GetString(), asset.GetProperty("sha256").GetString()));
        var path = $"/api/v1/catalogs/{press.Id}/assets/{assetId}";

        // Nothing is published before processing completes.
        Assert.Equal(HttpStatusCode.NotFound, (await victor.GetAsync(new Uri(path, UriKind.Relative), Api.Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await erin.GetAsync(new Uri($"{path}/content", UriKind.Relative), Api.Ct)).StatusCode);

        Assert.Equal(StageOutcome.Completed, await RunNextAsync()); // scan
        Assert.Equal(StageOutcome.Completed, await RunNextAsync()); // process
        Assert.Null(await ClaimAsync());

        var details = await victor.GetFromJsonAsync<JsonElement>(new Uri(path, UriKind.Relative), Api.Ct);
        Assert.Equal("active", details.GetProperty("state").GetString());

        using var thumbnail = await victor.GetAsync(new Uri($"{path}/renditions/thumbnail", UriKind.Relative), Api.Ct);
        Assert.Equal("image/webp", thumbnail.Content.Headers.ContentType!.MediaType);
        Assert.True(thumbnail.Headers.CacheControl is { Private: true, NoStore: true });
        using (var decoded = SKBitmap.Decode(await thumbnail.Content.ReadAsByteArrayAsync(Api.Ct)))
        {
            Assert.Equal((320, 240), (decoded.Width, decoded.Height));
        }

        var original = await victor.GetByteArrayAsync(new Uri($"{path}/content", UriKind.Relative), Api.Ct);
        Assert.Equal(jpeg, original);

        using var range = new HttpRequestMessage(HttpMethod.Get, new Uri($"{path}/content", UriKind.Relative));
        range.Headers.Range = new RangeHeaderValue(10, 19);
        using var partial = await victor.SendAsync(range, Api.Ct);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal($"bytes 10-19/{jpeg.Length}", partial.Content.Headers.ContentRange!.ToString());
        Assert.Equal(jpeg[10..20], await partial.Content.ReadAsByteArrayAsync(Api.Ct));

        using var beyond = new HttpRequestMessage(HttpMethod.Get, new Uri($"{path}/content", UriKind.Relative));
        beyond.Headers.Range = new RangeHeaderValue(jpeg.Length, null);
        using var unsatisfiable = await victor.SendAsync(beyond, Api.Ct);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);

        // A second upload of the same bytes is accepted and reports the duplicate.
        using var again = await Upload(erin, press.Id, jpeg, "copy.jpg");
        var duplicates = (await again.Content.ReadFromJsonAsync<JsonElement>(Api.Ct)).GetProperty("duplicates");
        Assert.Equal([assetId], duplicates.EnumerateArray().Select(d => d.GetGuid()));
    }

    [Fact]
    public async Task Infected_files_are_rejected_and_never_processed()
    {
        var press = await Seed.CatalogAsync(_fixture, ("erin", CatalogRole.Editor));
        using var erin = _fixture.Api.ClientAs("erin");
        var assetId = await UploadIdAsync(erin, press.Id, Images.Encode(64, 64, SKEncodedImageFormat.Png));

        _scanner.Next = new ScanResult(false, "Eicar-Test-Signature");
        Assert.Equal(StageOutcome.Completed, await RunNextAsync());
        Assert.Null(await ClaimAsync());
        var asset = await LoadAsync(press.Id, assetId);
        Assert.Equal((AssetState.Rejected, "malware: Eicar-Test-Signature"), (asset.State, asset.StatusReason));
    }

    [Fact]
    public async Task An_unavailable_scanner_keeps_the_asset_quarantined_until_an_editor_retries()
    {
        var press = await Seed.CatalogAsync(_fixture, ("erin", CatalogRole.Editor), ("victor", CatalogRole.Viewer));
        using var erin = _fixture.Api.ClientAs("erin");
        var assetId = await UploadIdAsync(erin, press.Id, Images.Encode(64, 64, SKEncodedImageFormat.Webp));
        _scanner.Unavailable = true;

        for (var attempt = 1; attempt <= JobIntent.DefaultMaxAttempts; attempt++)
        {
            Assert.Equal(StageOutcome.Retrying, await RunNextAsync());
            await MakeJobsDueAsync();
        }

        Assert.Null(await ClaimAsync());
        Assert.Equal(AssetState.Quarantined, (await LoadAsync(press.Id, assetId)).State);
        Assert.Equal(("Dead", JobIntent.DefaultMaxAttempts), await JobAsync(assetId, PipelineStages.Scan));

        var retryPath = $"/api/v1/catalogs/{press.Id}/assets/{assetId}/retry";
        using var victor = _fixture.Api.ClientAs("victor");
        Assert.Equal(HttpStatusCode.NotFound, (await Api.SendAsync(victor, HttpMethod.Post, retryPath)).StatusCode); // viewers do not see it
        Assert.Equal(HttpStatusCode.OK, (await Api.SendAsync(erin, HttpMethod.Post, retryPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.SendAsync(erin, HttpMethod.Post, retryPath)).StatusCode); // already queued

        _scanner.Unavailable = false;
        Assert.Equal(StageOutcome.Completed, await RunNextAsync());
        Assert.Equal(StageOutcome.Completed, await RunNextAsync());
        Assert.Equal(AssetState.Active, (await LoadAsync(press.Id, assetId)).State);
    }

    [Fact]
    public async Task Undecodable_images_are_rejected_after_the_scan()
    {
        var press = await Seed.CatalogAsync(_fixture, ("erin", CatalogRole.Editor));
        using var erin = _fixture.Api.ClientAs("erin");
        var jpeg = Images.Encode(800, 600, SKEncodedImageFormat.Jpeg);
        var assetId = await UploadIdAsync(erin, press.Id, jpeg[..(jpeg.Length / 3)]);

        Assert.Equal(StageOutcome.Completed, await RunNextAsync());
        Assert.Equal(StageOutcome.Completed, await RunNextAsync());
        var asset = await LoadAsync(press.Id, assetId);
        Assert.Equal(AssetState.Rejected, asset.State);
        Assert.StartsWith("decode:", asset.StatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redelivered_jobs_are_idempotent_and_a_lost_lease_commits_nothing()
    {
        var press = await Seed.CatalogAsync(_fixture, ("erin", CatalogRole.Editor));
        using var erin = _fixture.Api.ClientAs("erin");
        var assetId = await UploadIdAsync(erin, press.Id, Images.Encode(300, 200, SKEncodedImageFormat.Jpeg));

        // A worker that claimed the scan job but lost its lease cannot commit its result.
        var stale = (await ClaimAsync())!;
        await using (var db = _fixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE jobs SET lease_until = now() - interval '1 second' WHERE id = {stale.Id}", Api.Ct);
        }

        var current = (await ClaimAsync())!;
        Assert.Equal((stale.Id, 2), (current.Id, current.Attempt));
        Assert.Equal(StageOutcome.LeaseLost, await RunAsync(stale));
        Assert.Equal(AssetState.Quarantined, (await LoadAsync(press.Id, assetId)).State);

        Assert.Equal(StageOutcome.Completed, await RunAsync(current));
        Assert.Equal(StageOutcome.Completed, await RunNextAsync());

        // Re-running a finished stage changes nothing and creates no second job.
        await using (var db = _fixture.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE jobs SET state = 'Ready', run_after = now() WHERE asset_id = {assetId} AND stage = 'scan'", Api.Ct);
        }

        Assert.Equal(StageOutcome.Completed, await RunNextAsync());
        await using var check = _fixture.CreateContext();
        Assert.Equal(2, await check.Jobs.CountAsync(j => j.AssetId == assetId, Api.Ct));
        Assert.Equal(2, await check.Renditions.CountAsync(r => r.AssetId == assetId, Api.Ct));
        Assert.Equal(AssetState.Active, (await LoadAsync(press.Id, assetId)).State);
    }

    [Theory]
    [InlineData("victor", HttpStatusCode.Forbidden)]
    [InlineData("oscar", HttpStatusCode.NotFound)]
    public async Task Only_editors_upload(string user, HttpStatusCode expected)
    {
        var press = await Seed.CatalogAsync(_fixture, ("victor", CatalogRole.Viewer));
        using var client = _fixture.Api.ClientAs(user);
        using var response = await Upload(client, press.Id, Images.Encode(10, 10, SKEncodedImageFormat.Png));
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Uploads_are_validated_by_content_size_and_csrf()
    {
        var press = await Seed.CatalogAsync(_fixture, ("erin", CatalogRole.Editor));
        using var erin = _fixture.Api.ClientAs("erin");

        using var text = await Upload(erin, press.Id, Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"), "eicar.jpg");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);

        using var noCsrf = await Upload(erin, press.Id, Images.Encode(10, 10, SKEncodedImageFormat.Png), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);

        using var tooLarge = await Upload(erin, press.Id, new OversizeJpeg(MediaFormat.MaxUploadBytes + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        await using var db = _fixture.CreateContext();
        var failed = await db.Assets.SingleAsync(a => a.CatalogId == press.Id, Api.Ct);
        Assert.Equal(AssetState.Failed, failed.State);
        Assert.Null(await _worker.GetRequiredService<Zirrus.Application.Storage.IBlobStore>().StatAsync(BlobKey.Parse(failed.OriginalKey), Api.Ct));
    }

    public async ValueTask DisposeAsync() => await _worker.DisposeAsync();

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid catalogId, byte[] content, string fileName = "image.bin", bool csrf = true) =>
        await Upload(client, catalogId, new ByteArrayContent(content), fileName, csrf);

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid catalogId, HttpContent file, string fileName = "image.bin", bool csrf = true)
    {
        using var form = new MultipartFormDataContent { { file, "file", fileName } };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/catalogs/{catalogId}/assets", UriKind.Relative)) { Content = form };
        if (csrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", await Api.CsrfAsync(client));
        }

        return await client.SendAsync(request, Api.Ct);
    }

    private static async Task<Guid> UploadIdAsync(HttpClient client, Guid catalogId, byte[] content)
    {
        using var response = await Upload(client, catalogId, content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Api.Ct)).GetProperty("asset").GetProperty("id").GetGuid();
    }

    private Task<ClaimedJob?> ClaimAsync() => _worker.GetRequiredService<IJobQueue>().ClaimAsync("test-worker", TimeSpan.FromMinutes(1), Api.Ct);

    private async Task<StageOutcome> RunNextAsync() =>
        await RunAsync(await ClaimAsync() ?? throw new InvalidOperationException("No job was ready."));

    private async Task<StageOutcome> RunAsync(ClaimedJob job)
    {
        await using var scope = _worker.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IngestionPipeline>().RunAsync(job, Api.Ct);
    }

    private async Task MakeJobsDueAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlAsync($"UPDATE jobs SET run_after = now() WHERE state = 'Ready'", Api.Ct);
    }

    private async Task<Asset> LoadAsync(Guid catalogId, Guid assetId)
    {
        await using var db = _fixture.CreateContext();
        return await db.Assets.AsNoTracking().SingleAsync(a => a.CatalogId == catalogId && a.Id == assetId, Api.Ct);
    }

    private async Task<(string State, int Attempts)> JobAsync(Guid assetId, string stage)
    {
        await using var db = _fixture.CreateContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.AssetId == assetId && j.Stage == stage, Api.Ct);
        return (job.State.ToString(), job.Attempts);
    }

    /// <summary>Scanner whose next verdict the test controls.</summary>
    private sealed class ScriptedScanner : IMalwareScanner
    {
        public ScanResult Next { get; set; } = new(true);

        public bool Unavailable { get; set; }

        public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            await content.CopyToAsync(Stream.Null, cancellationToken);
            if (Unavailable)
            {
                throw new ScannerUnavailableException("scripted outage");
            }

            var result = Next;
            Next = new ScanResult(true);
            return result;
        }
    }

    /// <summary>A JPEG header followed by zeros, streamed without buffering.</summary>
    /// <param name="length">Total length.</param>
    private sealed class OversizeJpeg(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
            var zeros = new byte[1024 * 1024];
            for (long written = 4; written < length; written += zeros.Length)
            {
                await stream.WriteAsync(zeros.AsMemory(0, (int)Math.Min(zeros.Length, length - written)));
            }
        }

        protected override bool TryComputeLength(out long length2)
        {
            length2 = length;
            return true;
        }
    }
}
