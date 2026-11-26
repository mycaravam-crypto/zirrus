using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Zirrus.Application;
using Zirrus.Application.Assets;

namespace Zirrus.Api.Endpoints;

/// <summary>Asset endpoints. Optimistic concurrency uses the revision as a strong ETag (W3; RFC 9110 §13).</summary>
internal static class AssetEndpoints
{
    /// <summary>Maps asset endpoints on the authenticated <c>/api/v1</c> group.</summary>
    /// <param name="api">Route group.</param>
    public static void MapAssetEndpoints(this RouteGroupBuilder api)
    {
        var assets = api.MapGroup("/catalogs/{catalogId:guid}/assets").WithTags("Assets");

        // Returns an asset the caller may see, with its revision as ETag.
        assets.MapGet("/{assetId:guid}", async (Guid catalogId, Guid assetId, AssetUseCases useCases, HttpContext http, CancellationToken cancellationToken) =>
                ToHttp(await useCases.GetAsync(catalogId, assetId, cancellationToken), http))
            .WithName("GetAsset")
            .WithSummary("Read one asset with its state, checksum, metadata and ETag")
            .WithDescription("Viewers see active assets only; editors also see pending, rejected and failed ones. The ETag carries the revision for If-Match.")
            .Produces<AssetDetails>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Replaces the asset's user metadata if If-Match carries the current revision.
        assets.MapPut("/{assetId:guid}/metadata", async (Guid catalogId, Guid assetId, [FromHeader(Name = "If-Match")] string? ifMatch,
                JsonElement metadata, AssetUseCases useCases, HttpContext http, CancellationToken cancellationToken) =>
            {
                if (!TryParseRevision(ifMatch, out var revision))
                {
                    return Results.Problem(statusCode: StatusCodes.Status428PreconditionRequired,
                        title: "If-Match with the asset's ETag is required.");
                }

                return ToHttp(await useCases.UpdateMetadataAsync(catalogId, assetId, metadata.GetRawText(), revision, cancellationToken), http);
            })
            .WithName("ReplaceAssetMetadata")
            .WithSummary("Replace an asset's metadata (editors)")
            .WithDescription("Send the ETag from GET in If-Match. A stale ETag returns 412 and changes nothing.")
            .Produces<AssetDetails>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    /// <summary>Formats a revision as a strong entity tag.</summary>
    /// <param name="revision">Revision.</param>
    /// <returns>The quoted tag, e.g. <c>"3"</c>.</returns>
    public static string ETagFor(int revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Parses a single strong entity tag produced by <see cref="ETagFor"/>. Weak tags and <c>*</c> are not accepted.</summary>
    /// <param name="value">If-Match header value.</param>
    /// <param name="revision">Parsed revision.</param>
    /// <returns>Whether the header held one strong revision tag.</returns>
    public static bool TryParseRevision(string? value, out int revision)
    {
        revision = 0;
        var text = value?.Trim();
        return text is { Length: > 2 } && text[0] == '"' && text[^1] == '"'
            && int.TryParse(text.AsSpan(1, text.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    private static IResult ToHttp(OperationResult<AssetDetails> result, HttpContext http)
    {
        if (result.Status == OperationStatus.Ok)
        {
            http.Response.Headers.ETag = ETagFor(result.Value!.Revision);
            return Results.Ok(result.Value);
        }

        var status = result.Status switch
        {
            OperationStatus.Forbidden => StatusCodes.Status403Forbidden,
            OperationStatus.NotFound => StatusCodes.Status404NotFound,
            OperationStatus.Conflict => StatusCodes.Status409Conflict,
            OperationStatus.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Problem(statusCode: status, title: result.Error);
    }
}
