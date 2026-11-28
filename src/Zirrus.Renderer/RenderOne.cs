using System.Text.Json;
using Zirrus.Application.Ingestion;
using Zirrus.Infrastructure.Ingestion;

namespace Zirrus.Renderer;

/// <summary>
/// Child-process entry point: reads one image from stdin, renders it with SkiaSharp and writes a
/// <see cref="RenderResponse"/> to stdout. Exits 0 on success and <see cref="SubprocessRenderer.RejectedExitCode"/>
/// when the file is rejected; any other exit is a crash.
/// </summary>
internal static class RenderOne
{
    /// <summary>Runs the child.</summary>
    /// <param name="specsJson">Requested renditions as JSON.</param>
    /// <returns>Exit code.</returns>
    public static async Task<int> RunAsync(string specsJson)
    {
        var specs = JsonSerializer.Deserialize<List<RenditionSpec>>(specsJson, RenderProtocol.Json) ?? [];
        await using var stdin = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        RenderResponse response;
        int exitCode;
        try
        {
            var images = await new SkiaImageRenderer().RenderAsync(stdin, specs, CancellationToken.None);
            response = RenderProtocol.FromImages(images, PeakRssBytes());
            exitCode = 0;
        }
        catch (UnsupportedImageException e)
        {
            response = new RenderResponse([], e.Message, PeakRssBytes());
            exitCode = SubprocessRenderer.RejectedExitCode;
        }

        await JsonSerializer.SerializeAsync(stdout, response, RenderProtocol.Json);
        return exitCode;
    }

    /// <summary>Extracts metadata from one original supplied on stdin and writes JSON to stdout.</summary>
    /// <returns>Zero on success, or the renderer rejection exit code.</returns>
    public static async Task<int> ExtractMetadataAsync()
    {
        try
        {
            await using var stdin = Console.OpenStandardInput();
            var metadata = await new EmbeddedMetadataReader().ExtractAsync(stdin, CancellationToken.None);
            await Console.Out.WriteAsync(metadata?.Json ?? "null");
            return 0;
        }
        catch (UnsupportedImageException e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return SubprocessRenderer.RejectedExitCode;
        }
    }

    /// <summary>Peak resident set size (VmHWM) of this process on Linux; null elsewhere.</summary>
    /// <returns>Bytes, or null.</returns>
    internal static long? PeakRssBytes()
    {
        const string status = "/proc/self/status";
        if (!File.Exists(status))
        {
            return null;
        }

        var line = File.ReadLines(status).FirstOrDefault(l => l.StartsWith("VmHWM:", StringComparison.Ordinal));
        var kib = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        return long.TryParse(kib, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value * 1024 : null;
    }
}
