using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;

// Media spike for ADR 0003 / #7: NetVips vs. SkiaSharp rendering and MetadataExtractor coverage.
// Commands:
//   gen <dir>                          create the test corpus
//   render <netvips|skia> <file> <out> validate + thumbnail (320) + preview (1600), report peak RSS
//   bench <netvips|skia> <file> <n>    n thumbnails from one source, report thumbnails/min
//   meta <file>                        MetadataExtractor fields as JSON
const int MaxPixels = 50_000_000;
var command = args[0];
var result = command switch
{
    "gen" => Generate(args[1]),
    "render" => Render(args[1], args[2], args[3]),
    "bench" => Bench(args[1], args[2], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)),
    "meta" => MetadataFields.Extract(args[1]),
    _ => throw new ArgumentException(command),
};
result["peakRssMiB"] = PeakRssMiB();
Console.WriteLine(JsonSerializer.Serialize(result));
return 0;

static Dictionary<string, object> Generate(string dir)
{
    Directory.CreateDirectory(dir);
    // 50 MP photo-like JPEG (8640 x 5760), plus smaller PNG/WebP, an oversize JPEG, a truncated JPEG and EICAR.
    using (var photo = NetVips.Image.Gaussnoise(8640, 5760, mean: 128, sigma: 40)
        .Bandjoin(NetVips.Image.Xyz(8640, 5760)[0].Linear(new[] { 255.0 / 8640 }, new[] { 0.0 }))
        .Bandjoin(NetVips.Image.Xyz(8640, 5760)[1].Linear(new[] { 255.0 / 5760 }, new[] { 0.0 }))
        .Cast(NetVips.Enums.BandFormat.Uchar)
        .Copy(interpretation: NetVips.Enums.Interpretation.Srgb))
    {
        photo.WriteToFile(Path.Combine(dir, "photo-50mp.jpg"), new NetVips.VOption { { "Q", 90 } });
    }

    using (var small = NetVips.Image.Xyz(2000, 1500).Bandjoin(NetVips.Image.Black(2000, 1500)).Cast(NetVips.Enums.BandFormat.Uchar)
        .Copy(interpretation: NetVips.Enums.Interpretation.Srgb))
    {
        small.WriteToFile(Path.Combine(dir, "graphic.png"));
        small.WriteToFile(Path.Combine(dir, "graphic.webp"), new NetVips.VOption { { "Q", 85 } });
    }

    using (var big = NetVips.Image.Black(12000, 9000, bands: 3).Copy(interpretation: NetVips.Enums.Interpretation.Srgb))
    {
        big.WriteToFile(Path.Combine(dir, "oversize-108mp.jpg"));
    }

    var jpeg = File.ReadAllBytes(Path.Combine(dir, "photo-50mp.jpg"));
    File.WriteAllBytes(Path.Combine(dir, "truncated.jpg"), jpeg[..(jpeg.Length / 3)]);
    File.WriteAllText(Path.Combine(dir, "eicar.jpg"), @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
    return new Dictionary<string, object> { ["generated"] = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray()! };
}

static Dictionary<string, object> Render(string engine, string file, string outDir)
{
    Directory.CreateDirectory(outDir);
    var timer = Stopwatch.StartNew();
    var result = new Dictionary<string, object> { ["engine"] = engine, ["file"] = Path.GetFileName(file) };
    var format = Sniff(file);
    if (format is null)
    {
        result["ok"] = false;
        result["error"] = "rejected: magic bytes are not JPEG, PNG or WebP";
        return result;
    }

    try
    {
        var (w, h, thumb, preview) = engine == "netvips"
            ? RenderNetVips(file, outDir)
            : RenderSkia(file, outDir);
        result["ok"] = true;
        result["source"] = $"{w}x{h}";
        result["thumbnail"] = thumb;
        result["preview"] = preview;
    }
    catch (Exception ex) when (ex is NetVips.VipsException or InvalidDataException)
    {
        result["ok"] = false;
        result["error"] = $"rejected: {ex.Message.Split('\n')[0]}";
    }

    result["ms"] = timer.ElapsedMilliseconds;
    return result;
}

static Dictionary<string, object> Bench(string engine, string file, int n)
{
    var outDir = Path.Combine(Path.GetTempPath(), $"bench-{engine}");
    Directory.CreateDirectory(outDir);
    var timer = Stopwatch.StartNew();
    for (var i = 0; i < n; i++)
    {
        _ = engine == "netvips" ? RenderNetVips(file, outDir) : RenderSkia(file, outDir);
    }

    var perMinute = n / timer.Elapsed.TotalMinutes;
    return new Dictionary<string, object>
    {
        ["engine"] = engine, ["file"] = Path.GetFileName(file), ["renders"] = n,
        ["seconds"] = Math.Round(timer.Elapsed.TotalSeconds, 1), ["thumbnailAndPreviewPerMinute"] = Math.Round(perMinute, 1),
    };
}

static string? Sniff(string file)
{
    Span<byte> head = stackalloc byte[12];
    using var stream = File.OpenRead(file);
    var read = stream.Read(head);
    if (read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
    {
        return "jpeg";
    }

    if (read >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
    {
        return "png";
    }

    return read >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8) ? "webp" : null;
}

static (int W, int H, string Thumb, string Preview) RenderNetVips(string file, string outDir)
{
    NetVips.Cache.Max = 0;
    using (var header = NetVips.Image.NewFromFile(file, access: NetVips.Enums.Access.Sequential))
    {
        if ((long)header.Width * header.Height > MaxPixels)
        {
            throw new InvalidDataException($"decoded size {header.Width}x{header.Height} exceeds {MaxPixels} pixels");
        }
    }

    // thumbnail uses shrink-on-load and applies EXIF orientation; fail_on=truncated rejects corrupt input.
    using var thumb = NetVips.Image.Thumbnail(file, 320, height: 320, failOn: NetVips.Enums.FailOn.Truncated);
    using var preview = NetVips.Image.Thumbnail(file, 1600, height: 1600, failOn: NetVips.Enums.FailOn.Truncated);
    var thumbPath = Path.Combine(outDir, "netvips-thumb.webp");
    thumb.WriteToFile(thumbPath, new NetVips.VOption { { "Q", 80 } });
    preview.WriteToFile(Path.Combine(outDir, "netvips-preview.jpg"), new NetVips.VOption { { "Q", 85 } });
    using var source = NetVips.Image.NewFromFile(file);
    return (source.Width, source.Height, $"{thumb.Width}x{thumb.Height}", $"{preview.Width}x{preview.Height}");
}

static (int W, int H, string Thumb, string Preview) RenderSkia(string file, string outDir)
{
    using var stream = File.OpenRead(file);
    using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("unsupported or corrupt image");
    var info = codec.Info;
    if ((long)info.Width * info.Height > MaxPixels)
    {
        throw new InvalidDataException($"decoded size {info.Width}x{info.Height} exceeds {MaxPixels} pixels");
    }

    // Decode once at the smallest codec-supported scale that still covers the 1600 px preview.
    // JPEG decoders support k/8 scales; GetScaledDimensions rounds to the nearest one, which can
    // undershoot, so pick the smallest k/8 whose long edge still reaches 1600 px.
    var needed = Math.Min(1f, 1600f / Math.Max(info.Width, info.Height));
    var decodeSize = info.Size;
    for (var k = 1; k <= 8; k++)
    {
        var candidate = codec.GetScaledDimensions(k / 8f);
        if (Math.Max(candidate.Width, candidate.Height) >= Math.Max(info.Width, info.Height) * needed - 0.5f)
        {
            decodeSize = candidate;
            break;
        }
    }

    var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var decoded = new SKBitmap(decodeInfo);
    var status = codec.GetPixels(decodeInfo, decoded.GetPixels());
    if (status is not (SKCodecResult.Success))
    {
        throw new InvalidDataException($"decode failed: {status}");
    }

    using var oriented = Orient(decoded, codec.EncodedOrigin);
    var thumb = SaveResized(oriented, 320, Path.Combine(outDir, "skia-thumb.webp"), SKEncodedImageFormat.Webp, 80);
    var preview = SaveResized(oriented, 1600, Path.Combine(outDir, "skia-preview.jpg"), SKEncodedImageFormat.Jpeg, 85);
    return (info.Width, info.Height, thumb, preview);
}

static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
{
    var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
    var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
    using var canvas = new SKCanvas(result);
    switch (origin)
    {
        case SKEncodedOrigin.RightTop: // 90° clockwise (EXIF 6)
            canvas.Translate(result.Width, 0);
            canvas.RotateDegrees(90);
            break;
        case SKEncodedOrigin.BottomRight: // 180° (EXIF 3)
            canvas.Translate(result.Width, result.Height);
            canvas.RotateDegrees(180);
            break;
        case SKEncodedOrigin.LeftBottom: // 270° (EXIF 8)
            canvas.Translate(0, result.Height);
            canvas.RotateDegrees(270);
            break;
        default:
            break; // mirrored orientations are out of MVP scope for the spike
    }

    using var image = SKImage.FromBitmap(source);
    canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
    return result;
}

static string SaveResized(SKBitmap source, int longEdge, string path, SKEncodedImageFormat format, int quality)
{
    var factor = Math.Min(1f, (float)longEdge / Math.Max(source.Width, source.Height));
    var size = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * factor)), Math.Max(1, (int)Math.Round(source.Height * factor)));
    using var resized = source.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell));
    using var image = SKImage.FromBitmap(resized);
    using var data = image.Encode(format, quality);
    using (var output = File.Create(path))
    {
        data.SaveTo(output);
    }

    return $"{size.Width}x{size.Height}";
}

static double PeakRssMiB()
{
    var line = File.ReadLines("/proc/self/status").First(l => l.StartsWith("VmHWM:", StringComparison.Ordinal));
    var kb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture);
    return Math.Round(kb / 1024.0, 1);
}

/// <summary>Extracts the MVP metadata field set with MetadataExtractor.</summary>
internal static class MetadataFields
{
    /// <summary>Reads title, creator, keywords, copyright, capture date and GPS from a file.</summary>
    /// <param name="file">Image path.</param>
    /// <returns>Field values found, keyed by MVP field name.</returns>
    public static Dictionary<string, object> Extract(string file)
    {
        var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(file);
        string? Tag(string directory, string tag) => directories
            .Where(d => d.Name == directory)
            .SelectMany(d => d.Tags)
            .FirstOrDefault(t => t.Name == tag)?.Description;

        var xmp = directories.OfType<MetadataExtractor.Formats.Xmp.XmpDirectory>().FirstOrDefault();
        var xmpValues = xmp?.GetXmpProperties() ?? new Dictionary<string, string>();
        string? Xmp(string prefix) => xmpValues.FirstOrDefault(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).Value;
        var xmpKeywords = xmpValues.Where(p => p.Key.StartsWith("dc:subject[", StringComparison.Ordinal)).Select(p => p.Value).ToArray();

        var gps = directories.OfType<MetadataExtractor.Formats.Exif.GpsDirectory>().FirstOrDefault()?.GetGeoLocation();
        var fields = new Dictionary<string, object?>
        {
            ["title"] = Tag("IPTC", "Object Name") ?? Xmp("dc:title[1]"),
            ["creator"] = Tag("IPTC", "By-line") ?? Xmp("dc:creator[1]") ?? Tag("Exif IFD0", "Artist"),
            ["keywords"] = Tag("IPTC", "Keywords") ?? (xmpKeywords.Length > 0 ? string.Join(";", xmpKeywords) : null),
            ["copyright"] = Tag("IPTC", "Copyright Notice") ?? Xmp("dc:rights[1]") ?? Tag("Exif IFD0", "Copyright"),
            ["captureDate"] = Tag("Exif SubIFD", "Date/Time Original"),
            ["gps"] = gps is { } g ? $"{g.Latitude:F5},{g.Longitude:F5}" : null,
            ["orientation"] = Tag("Exif IFD0", "Orientation"),
        };
        return new Dictionary<string, object>
        {
            ["file"] = Path.GetFileName(file),
            ["fields"] = fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value!),
            ["missing"] = fields.Where(f => f.Value is null && f.Key != "orientation").Select(f => f.Key).ToArray(),
        };
    }
}
