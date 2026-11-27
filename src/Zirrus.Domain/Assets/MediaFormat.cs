namespace Zirrus.Domain.Assets;

/// <summary>
/// Image formats accepted by the MVP pipeline (ingestion-api-ui.md), detected from magic bytes.
/// The client's declared content type and file extension are never trusted.
/// </summary>
public static class MediaFormat
{
    /// <summary>Bytes needed to identify every supported format.</summary>
    public const int HeaderLength = 12;

    /// <summary>Maximum accepted upload size: 100 MiB.</summary>
    public const long MaxUploadBytes = 100L * 1024 * 1024;

    /// <summary>Maximum decoded size: 50 megapixels.</summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>Identifies JPEG, PNG or WebP from the first bytes of a file.</summary>
    /// <param name="header">At least the first <see cref="HeaderLength"/> bytes, or the whole file if shorter.</param>
    /// <returns><c>image/jpeg</c>, <c>image/png</c>, <c>image/webp</c>, or null for anything else.</returns>
    public static string? Sniff(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (header.Length >= 8 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        return header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
            ? "image/webp"
            : null;
    }
}

/// <summary>Pipeline stages and their versions (ADR 0002 deterministic job keys).</summary>
public static class PipelineStages
{
    /// <summary>Malware scan of the quarantined original.</summary>
    public const string Scan = "scan";

    /// <summary>Metadata extraction and rendering, then activation.</summary>
    public const string Process = "process";

    /// <summary>Pipeline version; bump it to reprocess assets with new job keys.</summary>
    public const int Version = 1;
}
