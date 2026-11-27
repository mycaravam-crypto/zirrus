using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Zirrus.Application.Ingestion;

namespace Zirrus.Infrastructure.Ingestion;

/// <summary>ClamAV daemon settings (<c>Scanner</c> section).</summary>
public sealed class ClamAvOptions
{
    /// <summary>clamd host name (the <c>clamav</c> service in Compose).</summary>
    public string Host { get; set; } = "clamav";

    /// <summary>clamd TCP port.</summary>
    public int Port { get; set; } = 3310;

    /// <summary>Time allowed for one scan, including connecting.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Scans with clamd's <c>INSTREAM</c> command over TCP: no shared filesystem with the scanner and
/// no extra client library. Any protocol or connection error is fail-closed (<see cref="ScannerUnavailableException"/>).
/// </summary>
/// <param name="options">Settings.</param>
public sealed class ClamAvScanner(ClamAvOptions options) : IMalwareScanner
{
    private const int ChunkSize = 64 * 1024;

    /// <inheritdoc />
    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        string reply;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);

            var buffer = new byte[4 + ChunkSize];
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(4, ChunkSize), timeout.Token)) > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)read);
                await stream.WriteAsync(buffer.AsMemory(0, 4 + read), timeout.Token);
            }

            await stream.WriteAsync(new byte[4], timeout.Token); // zero-length chunk ends the stream
            reply = await ReadReplyAsync(stream, timeout.Token);
        }
        catch (Exception e) when (e is SocketException or IOException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ScannerUnavailableException($"clamd at {options.Host}:{options.Port}: {e.Message}", e);
        }

        return Parse(reply);
    }

    /// <summary>Interprets a clamd reply such as <c>stream: OK</c> or <c>stream: Eicar-Signature FOUND</c>.</summary>
    /// <param name="reply">Reply without the terminator.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ScannerUnavailableException">clamd reported an error (e.g. size limit exceeded).</exception>
    internal static ScanResult Parse(string reply)
    {
        var text = reply.StartsWith("stream: ", StringComparison.Ordinal) ? reply["stream: ".Length..] : reply;
        if (text == "OK")
        {
            return new ScanResult(Clean: true);
        }

        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            return new ScanResult(Clean: false, text[..^" FOUND".Length]);
        }

        throw new ScannerUnavailableException($"clamd error: {text}");
    }

    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var reply = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cancellationToken) == 1 && one[0] != 0)
        {
            reply.Add(one[0]);
            if (reply.Count > 1024)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(reply.ToArray()).Trim();
    }
}
