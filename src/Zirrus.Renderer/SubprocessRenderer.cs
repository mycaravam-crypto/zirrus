using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Zirrus.Application.Ingestion;
using Zirrus.Infrastructure.Ingestion;
using Zirrus.Domain.Assets;

namespace Zirrus.Renderer;

/// <summary>Parser limits (<c>Parser</c> section; ingestion-api-ui.md isolation rules).</summary>
public sealed class ParserLimits
{
    /// <summary>Wall-clock limit per file.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Resident memory limit of the parser process.</summary>
    public long MaxMemoryBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How often the watchdog samples the parser's memory.</summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Renders each file in a fresh child process of this binary (<c>--render-one</c>): an exploit in a
/// decoder dies with its process and cannot touch later files. The child gets an empty environment
/// apart from runtime limits, a private temporary directory, the file on stdin and the result on
/// stdout. A watchdog kills it when it exceeds the time or memory limit; both count as rejections,
/// because a retry would hit the same limit.
/// </summary>
/// <param name="limits">Limits.</param>
/// <param name="childAssembly">Path of <c>Zirrus.Renderer.dll</c> for the child; null restarts the current application.</param>
public sealed class SubprocessRenderer(ParserLimits limits, string? childAssembly = null)
{
    /// <summary>Exit code of a child that rejected the file (the response carries the reason).</summary>
    public const int RejectedExitCode = 2;

    /// <summary>Renders in a child process.</summary>
    /// <param name="source">Original image.</param>
    /// <param name="specs">Renditions to produce.</param>
    /// <param name="cancellationToken">Cancels the render and kills the child.</param>
    /// <returns>The child's response; <see cref="RenderResponse.Error"/> is set for rejections.</returns>
    /// <exception cref="InvalidOperationException">The child crashed (transient for the caller).</exception>
    public async Task<RenderResponse> RenderAsync(Stream source, IReadOnlyList<RenditionSpec> specs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var workDir = Directory.CreateTempSubdirectory("zirrus-parse-");
        try
        {
            using var child = Process.Start(StartInfo(specs, workDir.FullName))
                ?? throw new InvalidOperationException("parser process did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limits.Timeout);

            var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
            var watchdog = WatchMemoryAsync(child, timeout);
            try
            {
                await using (var stdin = child.StandardInput.BaseStream)
                {
                    await source.CopyToAsync(stdin, timeout.Token);
                }

                await child.WaitForExitAsync(timeout.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                Kill(child);
                cancellationToken.ThrowIfCancellationRequested();
                var reason = await watchdog ?? (e is IOException ? null : $"time limit of {limits.Timeout.TotalSeconds:0} s exceeded");
                return reason is not null
                    ? new RenderResponse([], $"parser {reason}", null)
                    : throw new InvalidOperationException("parser closed its input early", e);
            }

            // The child has exited, so its pipes reach end-of-file: finish reading them before stopping the
            // timeout token they share, or a still-pending read would be cancelled under load.
            var output = await stdout;
            var errors = await stderr;
            await timeout.CancelAsync();
            if (await watchdog is { } exceeded)
            {
                return new RenderResponse([], $"parser {exceeded}", null);
            }

            if (child.ExitCode is not (0 or RejectedExitCode))
            {
                throw new InvalidOperationException($"parser exited with code {child.ExitCode}: {Truncate(errors)}");
            }

            return JsonSerializer.Deserialize<RenderResponse>(output, RenderProtocol.Json)
                ?? throw new InvalidOperationException("parser wrote no result");
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    /// <summary>Extracts embedded metadata in a fresh child process under the parser limits.</summary>
    /// <param name="source">Original image stream.</param>
    /// <param name="cancellationToken">Cancels extraction and kills the child.</param>
    /// <returns>Extracted metadata, or null when no supported fields are present.</returns>
    /// <exception cref="UnsupportedImageException">Metadata is malformed or parser limits are exceeded.</exception>
    /// <exception cref="InvalidOperationException">The child process crashed.</exception>
    public async Task<AssetMetadata?> ExtractMetadataAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var workDir = Directory.CreateTempSubdirectory("zirrus-metadata-");
        try
        {
            using var child = Process.Start(StartMetadataInfo(workDir.FullName))
                ?? throw new InvalidOperationException("metadata parser process did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limits.Timeout);
            var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
            var watchdog = WatchMemoryAsync(child, timeout);
            try
            {
                await using (var stdin = child.StandardInput.BaseStream)
                {
                    await source.CopyToAsync(stdin, timeout.Token);
                }
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                Kill(child);
                cancellationToken.ThrowIfCancellationRequested();
                var reason = await watchdog ?? (e is IOException ? null : $"time limit of {limits.Timeout.TotalSeconds:0} s exceeded");
                if (reason is not null)
                {
                    throw new UnsupportedImageException($"metadata parser {reason}");
                }

                throw new InvalidOperationException("metadata parser closed its input early", e);
            }

            var output = await stdout;
            var errors = await stderr;
            await timeout.CancelAsync();
            if (await watchdog is { } exceeded) throw new UnsupportedImageException($"metadata parser {exceeded}");
            if (child.ExitCode == RejectedExitCode) throw new UnsupportedImageException($"metadata: {Truncate(errors)}");
            if (child.ExitCode != 0) throw new InvalidOperationException($"metadata parser exited with code {child.ExitCode}: {Truncate(errors)}");
            return output.Trim() == "null" ? null : AssetMetadata.Parse(output);
        }
        finally
        {
            workDir.Delete(recursive: true);
        }
    }

    private ProcessStartInfo StartMetadataInfo(string workDir)
    {
        var (fileName, prefix) = SelfCommand();
        var info = CreateStartInfo(fileName, prefix, workDir);
        info.ArgumentList.Add(MetadataEndpoint.ExtractMetadataArgument);
        return info;
    }

    private ProcessStartInfo StartInfo(IReadOnlyList<RenditionSpec> specs, string workDir)
    {
        var (fileName, prefix) = SelfCommand();
        var info = CreateStartInfo(fileName, prefix, workDir);
        info.ArgumentList.Add(RenderEndpoint.RenderOneArgument);
        info.ArgumentList.Add(JsonSerializer.Serialize(specs, RenderProtocol.Json));
        return info;
    }

    private ProcessStartInfo CreateStartInfo(string fileName, string[] prefix, string workDir)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };
        foreach (var arg in prefix)
        {
            info.ArgumentList.Add(arg);
        }

        // No inherited configuration, secrets or proxy settings: only what the runtime needs.
        info.Environment.Clear();
        info.Environment["HOME"] = workDir;
        info.Environment["TMPDIR"] = workDir;
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_gcServer"] = "0";
        info.Environment["DOTNET_GCHeapHardLimit"] = $"0x{limits.MaxMemoryBytes / 2:X}"; // managed share; the watchdog covers native memory
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } dotnetRoot)
        {
            info.Environment["DOTNET_ROOT"] = dotnetRoot;
        }

        return info;
    }

    /// <summary>The command that starts the renderer again: the apphost, or <c>dotnet Zirrus.Renderer.dll</c>.</summary>
    private (string FileName, string[] Prefix) SelfCommand()
    {
        if (childAssembly is not null)
        {
            // <dotnet root>/shared/Microsoft.NETCore.App/<version>/ → <dotnet root>/dotnet
            var root = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            return (Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), [childAssembly]);
        }

        var process = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path.");
        var assembly = Assembly.GetEntryAssembly()?.Location;
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && assembly is { Length: > 0 }
            ? (process, [assembly])
            : (process, []);
    }

    /// <summary>Samples resident memory; returns the reason after killing an oversized child, or null.</summary>
    private async Task<string?> WatchMemoryAsync(Process child, CancellationTokenSource stop)
    {
        try
        {
            using var timer = new PeriodicTimer(limits.SampleInterval);
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                child.Refresh();
                if (child.HasExited)
                {
                    return null;
                }

                if (child.WorkingSet64 > limits.MaxMemoryBytes)
                {
                    Kill(child);
                    await stop.CancelAsync();
                    return $"memory limit of {limits.MaxMemoryBytes / (1024 * 1024)} MiB exceeded";
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
        }

        return null;
    }

    private static void Kill(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }

    private static string Truncate(string text) => text.Length > 500 ? text[..500] : text;
}
