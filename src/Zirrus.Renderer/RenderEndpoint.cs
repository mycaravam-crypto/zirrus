using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Zirrus.Application.Ingestion;
using Zirrus.Domain.Assets;
using Zirrus.Infrastructure.Ingestion;

namespace Zirrus.Renderer;

/// <summary>The renderer's HTTP endpoint and child-process switch.</summary>
public static class RenderEndpoint
{
    /// <summary>Argument that makes the process render one file from stdin and exit.</summary>
    public const string RenderOneArgument = "--render-one";

    /// <summary>
    /// Renders the request body. 200 with images; 422 when the file is rejected (corrupt,
    /// unsupported, over limits); 500 when the parser crashed (the worker retries).
    /// </summary>
    /// <param name="http">Request context.</param>
    /// <param name="renderer">Subprocess renderer.</param>
    /// <param name="slots">Concurrency limit.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    /// <returns>The result.</returns>
    public static async Task<IResult> HandleAsync(HttpContext http, SubprocessRenderer renderer, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MediaFormat.MaxUploadBytes;
        }

        var specs = JsonSerializer.Deserialize<List<RenditionSpec>>(http.Request.Headers[RenderProtocol.SpecsHeader].ToString() is { Length: > 0 } header ? header : "[]", RenderProtocol.Json);
        if (specs is not { Count: > 0 and <= 8 })
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Send 1–8 renditions in {RenderProtocol.SpecsHeader}.");
        }

        await slots.WaitAsync(cancellationToken);
        try
        {
            var response = await renderer.RenderAsync(http.Request.Body, specs, cancellationToken);
            return Results.Json(response, RenderProtocol.Json, statusCode: response.Error is null ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
        }
        finally
        {
            slots.Release();
        }
    }
}
