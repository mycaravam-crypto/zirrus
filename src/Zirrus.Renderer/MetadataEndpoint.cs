using Microsoft.AspNetCore.Http.Features;
using Zirrus.Application.Ingestion;
using Zirrus.Domain.Assets;

namespace Zirrus.Renderer;

/// <summary>Isolated metadata extraction endpoint.</summary>
internal static class MetadataEndpoint
{
    /// <summary>Argument that starts a one-file metadata extraction child.</summary>
    public const string ExtractMetadataArgument = "--extract-metadata";

    /// <summary>Extracts embedded metadata in a fresh, resource-limited parser process.</summary>
    /// <param name="http">HTTP request.</param>
    /// <param name="parser">Isolated parser process host.</param>
    /// <param name="slots">Shared parser concurrency limit.</param>
    /// <param name="cancellationToken">Cancels the request and child process.</param>
    /// <returns>Extracted metadata, or a problem response for rejected input.</returns>
    internal static async Task<IResult> HandleAsync(HttpContext http, SubprocessRenderer parser, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MediaFormat.MaxUploadBytes;
        }

        await slots.WaitAsync(cancellationToken);
        try
        {
            var metadata = await parser.ExtractMetadataAsync(http.Request.Body, cancellationToken);
            return Results.Content(metadata?.Json ?? "null", "application/json");
        }
        catch (UnsupportedImageException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: e.Message);
        }
        finally
        {
            slots.Release();
        }
    }
}
