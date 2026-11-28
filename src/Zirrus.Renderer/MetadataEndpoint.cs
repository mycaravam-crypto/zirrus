using Microsoft.AspNetCore.Http.Features;
using Zirrus.Application.Ingestion;
using Zirrus.Domain.Assets;

namespace Zirrus.Renderer;

/// <summary>Isolated metadata extraction endpoint.</summary>
public static class MetadataEndpoint
{
    /// <summary>Argument that starts a one-file metadata extraction child.</summary>
    public const string ExtractMetadataArgument = "--extract-metadata";

    /// <summary>Extracts embedded metadata in a fresh, resource-limited parser process.</summary>
    public static async Task<IResult> HandleAsync(HttpContext http, SubprocessRenderer parser, CancellationToken cancellationToken)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MediaFormat.MaxUploadBytes;
        }

        try
        {
            var metadata = await parser.ExtractMetadataAsync(http.Request.Body, cancellationToken);
            return metadata is null
                ? Results.Json(JsonDocument.Parse("null").RootElement)
                : Results.Content(metadata.Json, "application/json");
        }
        catch (UnsupportedImageException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: e.Message);
        }
    }
}
