using SkiaSharp;
using Zirrus.Application.Ingestion;
using Zirrus.Domain.Assets;

namespace Zirrus.Infrastructure.Ingestion;

/// <summary>
/// SkiaSharp renderer (spike §2: 77 MiB peak on a 50 MP JPEG thanks to scaled JPEG decoding).
/// Decodes once at the smallest scale that still covers the largest rendition, applies the EXIF
/// orientation and never upscales. Output is deterministic for a given input and SkiaSharp version.
/// </summary>
public sealed class SkiaImageRenderer : IImageRenderer
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RenderedImage>> RenderAsync(Stream source, IReadOnlyList<RenditionSpec> specs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(specs);
        cancellationToken.ThrowIfCancellationRequested();

        // SKCodec needs a seekable stream; renditions are produced from a local copy of the original.
        using var buffered = new MemoryStream();
        source.CopyTo(buffered);
        buffered.Position = 0;

        using var codec = SKCodec.Create(buffered, out var createResult)
            ?? throw new UnsupportedImageException($"unsupported or corrupt image ({createResult})");
        var info = codec.Info;
        if ((long)info.Width * info.Height > MediaFormat.MaxPixels)
        {
            throw new UnsupportedImageException($"decoded size {info.Width}x{info.Height} exceeds {MediaFormat.MaxPixels} pixels");
        }

        using var decoded = Decode(codec, specs.Max(s => s.LongestEdge));
        using var oriented = Orient(decoded, codec.EncodedOrigin);
        var results = new List<RenderedImage>(specs.Count);
        foreach (var spec in specs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(Encode(oriented, spec));
        }

        return Task.FromResult<IReadOnlyList<RenderedImage>>(results);
    }

    private static SKBitmap Decode(SKCodec codec, int largestEdge)
    {
        var info = codec.Info;
        var longest = Math.Max(info.Width, info.Height);
        var needed = Math.Min(longest, largestEdge);
        var size = info.Size;

        // JPEG decodes natively at k/8 scales; pick the smallest that still covers the largest rendition.
        for (var k = 1; k <= 8; k++)
        {
            var candidate = codec.GetScaledDimensions(k / 8f);
            if (Math.Max(candidate.Width, candidate.Height) >= needed)
            {
                size = candidate;
                break;
            }
        }

        var decodeInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(decodeInfo);
        var status = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (status != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new UnsupportedImageException($"decode failed: {status}");
        }

        return bitmap;
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;

        // Maps source pixels to the upright image: x' = ScaleX·x + SkewX·y + TransX, y' = SkewY·x + ScaleY·y + TransY.
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => null,
        };
        if (matrix is not { } m)
        {
            return source.Copy();
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(m);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest)); // exact pixel remap (90° steps and mirrors)
        return result;
    }

    private static RenderedImage Encode(SKBitmap image, RenditionSpec spec)
    {
        var scale = Math.Min(1.0, spec.LongestEdge / (double)Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        using var resized = scale < 1.0
            ? image.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
            : image.Copy();
        using var encoded = SKImage.FromBitmap(resized);
        var format = spec.MediaType switch
        {
            "image/webp" => SKEncodedImageFormat.Webp,
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            _ => throw new ArgumentException($"Unsupported rendition type {spec.MediaType}.", nameof(spec)),
        };
        using var data = encoded.Encode(format, spec.Quality);
        return new RenderedImage(spec, resized.Width, resized.Height, data.ToArray());
    }
}
