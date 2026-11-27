using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Zirrus.Application;
using Zirrus.Application.Assets;
using Zirrus.Application.Ingestion;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;

namespace Zirrus.Api.Endpoints;

/// <summary>Asset endpoints. Optimistic concurrency uses the revision as a strong ETag (W3; RFC 9110 §13).</summary>
internal static class AssetEndpoints
{
    /// <summary>Maps asset endpoints on the authenticated <c>/api/v1</c> group.</summary>
    /// <param name="api">Route group.</param>
    public static void MapAssetEndpoints(this RouteGroupBuilder api)
    {
        var assets = api.MapGroup("/catalogs/{catalogId:guid}/assets").WithTags("Assets");

        // Lists the catalog's assets the caller may see, newest first, with cursor pagination.
        assets.MapGet("/", async (Guid catalogId, string? cursor, int? limit, AssetUseCases useCases, CancellationToken cancellationToken) =>
            {
                var result = await useCases.ListAsync(catalogId, cursor, limit, cancellationToken);
                return result.Status == OperationStatus.Ok ? Results.Ok(result.Value) : Problem(result.Status, result.Error);
            })
            .WithName("ListAssets")
            .WithSummary("List a catalog's assets, newest first (viewers see active assets only)")
            .WithDescription("Cursor pagination: pass nextCursor from the previous page; limit 1–200, default 50. Search and filters follow in W5.")
            .Produces<AssetPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Streams one multipart file into quarantine; scanning and rendering run in the worker.
        assets.MapPost("/", UploadAsync)
            .WithName("UploadAsset")
            .WithSummary("Upload a JPEG, PNG or WebP image into quarantine (editors)")
            .WithDescription("multipart/form-data with one part named 'file'; send the CSRF token in X-CSRF-TOKEN. The format is detected from the content. The response lists duplicates in the catalog by SHA-256.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<UploadResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        // Requeues a failed asset, or a quarantined one whose scan was dead-lettered.
        assets.MapPost("/{assetId:guid}/retry", async (Guid catalogId, Guid assetId, RetryAsset retry, HttpContext http, CancellationToken cancellationToken) =>
                ToHttp(await retry.ExecuteAsync(catalogId, assetId, cancellationToken), http))
            .WithName("RetryAsset")
            .WithSummary("Retry processing of a failed or stalled asset (editors)")
            .Produces<AssetDetails>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Streams the original of an active asset; supports a single HTTP byte range.
        assets.MapGet("/{assetId:guid}/content", DownloadAsync)
            .WithName("DownloadAsset")
            .WithSummary("Download the original of an active asset, optionally a byte range")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status206PartialContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status416RangeNotSatisfiable);

        // Streams a thumbnail or preview of an active asset.
        assets.MapGet("/{assetId:guid}/renditions/{kind}", async (Guid catalogId, Guid assetId, string kind, AssetUseCases useCases, HttpContext http, CancellationToken cancellationToken) =>
            {
                var result = await useCases.OpenRenditionAsync(catalogId, assetId, kind, cancellationToken);
                return result.Status == OperationStatus.Ok
                    ? Stream(http, result.Value!, inline: true)
                    : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: result.Error);
            })
            .WithName("GetRendition")
            .WithSummary("Download the thumbnail or preview of an active asset")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound);

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

    /// <summary>Reads the multipart body section by section (no buffering) and hands the first <c>file</c> part to <see cref="UploadAsset"/>.</summary>
    /// <param name="catalogId">Target catalog.</param>
    /// <param name="http">Request context.</param>
    /// <param name="upload">Upload use case.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    /// <returns>201 with the quarantined asset, or problem details.</returns>
    private static async Task<IResult> UploadAsync(Guid catalogId, HttpContext http, UploadAsset upload, CancellationToken cancellationToken)
    {
        // Allow the upload limit plus multipart overhead; the use case enforces the exact file limit.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MediaFormat.MaxUploadBytes + (1024 * 1024);
        }

        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 and <= 200 } boundary)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Send multipart/form-data with one part named 'file'.");
        }

        var reader = new MultipartReader(boundary, http.Request.Body);
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !disposition.IsFileDisposition()
                || !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, "file", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "upload";
            var result = await upload.ExecuteAsync(catalogId, fileName, section.Body, cancellationToken);
            return result.Status == OperationStatus.Ok
                ? Results.Created($"/api/v1/catalogs/{catalogId}/assets/{result.Value!.Asset.Id}", result.Value)
                : Problem(result.Status, result.Error);
        }

        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No part named 'file' was found.");
    }

    /// <summary>Streams an active original, honouring one byte range (206) and answering 416 with the size for unsatisfiable ranges.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="useCases">Asset use cases.</param>
    /// <param name="http">Request context.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    /// <returns>The stream, 416, or 404.</returns>
    private static async Task<IResult> DownloadAsync(Guid catalogId, Guid assetId, AssetUseCases useCases, HttpContext http, CancellationToken cancellationToken)
    {
        var range = ParseRange(http.Request.Headers.Range);
        OperationResult<AssetContent> result;
        try
        {
            result = await useCases.OpenOriginalAsync(catalogId, assetId, range, cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            var total = (await useCases.OpenOriginalAsync(catalogId, assetId, null, cancellationToken)).Value;
            if (total is not null)
            {
                await total.Read.DisposeAsync();
                http.Response.Headers.ContentRange = $"bytes */{total.Read.TotalLength}";
            }

            return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        return result.Status == OperationStatus.Ok
            ? Stream(http, result.Value!, inline: false, partial: range is not null)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: result.Error);
    }

    private static IResult Stream(HttpContext http, AssetContent content, bool inline, bool partial = false)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = "private, no-store"; // revoked access must not survive in caches
        headers.XContentTypeOptions = "nosniff";
        headers.AcceptRanges = "bytes";
        headers.ContentDisposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment") { FileNameStar = content.FileName }.ToString();
        if (partial)
        {
            var read = content.Read;
            headers.ContentRange = $"bytes {read.Offset}-{read.Offset + read.Length - 1}/{read.TotalLength}";
            http.Response.StatusCode = StatusCodes.Status206PartialContent;
        }

        http.Response.RegisterForDisposeAsync(content.Read);
        return Results.Stream(content.Read.Content, content.MediaType);
    }

    /// <summary>Parses a single <c>bytes=</c> range (<c>a-b</c>, <c>a-</c>; suffix ranges are not supported). Anything else means "whole file".</summary>
    /// <param name="header">Range header value.</param>
    /// <returns>The range, or null.</returns>
    internal static ByteRange? ParseRange(string? header)
    {
        if (header is null || !header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }

        var spec = header["bytes=".Length..].Split('-', 2);
        if (spec.Length != 2 || !long.TryParse(spec[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return null;
        }

        if (spec[1].Length == 0)
        {
            return new ByteRange(start);
        }

        return long.TryParse(spec[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end) && end >= start
            ? new ByteRange(start, end - start + 1)
            : null;
    }

    private static IResult Problem(OperationStatus status, string? error) => Results.Problem(statusCode: status switch
    {
        OperationStatus.Forbidden => StatusCodes.Status403Forbidden,
        OperationStatus.NotFound => StatusCodes.Status404NotFound,
        OperationStatus.Conflict => StatusCodes.Status409Conflict,
        OperationStatus.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        OperationStatus.TooLarge => StatusCodes.Status413PayloadTooLarge,
        OperationStatus.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        _ => StatusCodes.Status400BadRequest,
    }, title: error);

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

        return Problem(result.Status, result.Error);
    }
}
