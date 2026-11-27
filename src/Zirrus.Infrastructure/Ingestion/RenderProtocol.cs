using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Zirrus.Application.Ingestion;

namespace Zirrus.Infrastructure.Ingestion;

/// <summary>One encoded rendition on the wire between worker, renderer service and parser subprocess.</summary>
/// <param name="Kind">Rendition kind.</param>
/// <param name="MediaType">Output type.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Content">Encoded bytes (base64 in JSON).</param>
public sealed record RenderedImageDto(string Kind, string MediaType, int Width, int Height, byte[] Content);

/// <summary>Result of a render: images, or the reason the file is not supported, plus the parser's peak memory.</summary>
/// <param name="Images">Renditions in request order; empty when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the file was rejected (corrupt, unsupported, limits).</param>
/// <param name="PeakRssBytes">Peak resident memory of the parser process, when known.</param>
public sealed record RenderResponse(IReadOnlyList<RenderedImageDto> Images, string? Error, long? PeakRssBytes);

/// <summary>Serialization shared by the renderer service, its subprocess and the worker client.</summary>
public static class RenderProtocol
{
    /// <summary>Header carrying the JSON list of <see cref="RenditionSpec"/>.</summary>
    public const string SpecsHeader = "X-Zirrus-Renditions";

    /// <summary>JSON options (web defaults).</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Maps a response to the port's result, translating a rejection into <see cref="UnsupportedImageException"/>.</summary>
    /// <param name="response">Response.</param>
    /// <param name="specs">Requested specs, to restore each image's spec.</param>
    /// <returns>Rendered images.</returns>
    public static IReadOnlyList<RenderedImage> ToImages(RenderResponse response, IReadOnlyList<RenditionSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(specs);
        if (response.Error is not null)
        {
            throw new UnsupportedImageException(response.Error);
        }

        return response.Images.Select(i => new RenderedImage(
            specs.Single(s => s.Kind == i.Kind), i.Width, i.Height, i.Content)).ToList();
    }

    /// <summary>Maps port results to the wire format.</summary>
    /// <param name="images">Rendered images.</param>
    /// <param name="peakRssBytes">Peak memory, if measured.</param>
    /// <returns>The response.</returns>
    public static RenderResponse FromImages(IReadOnlyList<RenderedImage> images, long? peakRssBytes) =>
        new(images.Select(i => new RenderedImageDto(i.Spec.Kind, i.Spec.MediaType, i.Width, i.Height, i.Content.ToArray())).ToList(), null, peakRssBytes);
}

/// <summary>Renderer service settings (<c>Renderer</c> section of the worker).</summary>
public sealed class RendererClientOptions
{
    /// <summary>Base URL of the renderer service, e.g. <c>http://renderer:8080</c>. Empty renders in-process (tests, development).</summary>
    public string? Url { get; set; }

    /// <summary>Request timeout; above the renderer's own 60 s parser limit.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Worker-side <see cref="IImageRenderer"/> that sends originals to the isolated renderer service
/// (no secrets, no database or internet access). A 422 answer is a rejection; anything else
/// (unreachable, 5xx) is transient and retried by the queue.
/// </summary>
/// <param name="http">HTTP client with the renderer base address.</param>
public sealed class HttpImageRenderer(HttpClient http) : IImageRenderer
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RenderedImage>> RenderAsync(Stream source, IReadOnlyList<RenditionSpec> specs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(specs);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/render", UriKind.Relative))
        {
            Content = new StreamContent(source),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add(RenderProtocol.SpecsHeader, JsonSerializer.Serialize(specs, RenderProtocol.Json));

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity))
        {
            throw new HttpRequestException($"renderer answered {(int)response.StatusCode}", null, response.StatusCode);
        }

        var body = await response.Content.ReadFromJsonAsync<RenderResponse>(RenderProtocol.Json, cancellationToken)
            ?? throw new HttpRequestException("renderer sent an empty response");
        return RenderProtocol.ToImages(body, specs);
    }
}
