using Zirrus.Api.Security;
using Zirrus.Application;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Api.Endpoints;

/// <summary>Request to create a catalog.</summary>
/// <param name="Name">Catalog name, 1–200 characters, unique in the organization.</param>
public sealed record CreateCatalogRequest(string Name);

/// <summary>Request to grant or change a catalog role.</summary>
/// <param name="Role">Role: <c>editor</c> or <c>viewer</c>.</param>
public sealed record SetCatalogMemberRequest(string Role);

/// <summary>Catalog and role-assignment endpoints.</summary>
internal static class CatalogEndpoints
{
    /// <summary>Maps catalog endpoints on the authenticated <c>/api/v1</c> group.</summary>
    /// <param name="api">Route group.</param>
    public static void MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        var catalogs = api.MapGroup("/catalogs").WithTags("Catalogs");

        // Lists the catalogs the caller may see: all for administrators, granted ones otherwise.
        catalogs.MapGet("/", async (ListCatalogs listCatalogs, CancellationToken cancellationToken) =>
                TypedResults.Ok(await listCatalogs.ExecuteAsync(cancellationToken)))
            .WithName("ListCatalogs")
            .WithSummary("List the catalogs the caller may see, with their access level")
            .WithDescription("Organization administrators see every catalog; other users see the catalogs they hold a role in.");

        var admin = catalogs.MapGroup("/").RequireAuthorization(AuthenticationSetup.OrganizationAdminPolicy);

        // Creates a catalog; organization administrators only.
        admin.MapPost("/", async (CreateCatalogRequest request, CatalogAdministration administration, CancellationToken cancellationToken) =>
                ToHttp(await administration.CreateCatalogAsync(request.Name, cancellationToken), created: c => $"/api/v1/catalogs/{c.Id}"))
            .WithName("CreateCatalog")
            .WithSummary("Create a catalog (organization administrators only)")
            .Produces<CatalogSummary>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Grants or changes a user's role in a catalog; organization administrators only.
        admin.MapPut("/{catalogId:guid}/members/{subject}", async (Guid catalogId, string subject, SetCatalogMemberRequest request,
                CatalogAdministration administration, CancellationToken cancellationToken) =>
            {
                if (!TryParseRole(request.Role, out var role))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Role must be 'editor' or 'viewer'.");
                }

                return ToHttp(await administration.SetMemberAsync(catalogId, subject, role, cancellationToken));
            })
            .WithName("SetCatalogMember")
            .WithSummary("Grant or change a user's role in a catalog (organization administrators only)")
            .Produces<CatalogMember>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Removes a user's role in a catalog; organization administrators only.
        admin.MapDelete("/{catalogId:guid}/members/{subject}", async (Guid catalogId, string subject,
                CatalogAdministration administration, CancellationToken cancellationToken) =>
            {
                var result = await administration.RemoveMemberAsync(catalogId, subject, cancellationToken);
                return result.Status == OperationStatus.Ok ? Results.NoContent() : ToHttp(result);
            })
            .WithName("RemoveCatalogMember")
            .WithSummary("Remove a user's role in a catalog (organization administrators only)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static bool TryParseRole(string? value, out CatalogRole role)
    {
        role = CatalogRole.Viewer;
        switch (value?.ToUpperInvariant())
        {
            case "EDITOR":
                role = CatalogRole.Editor;
                return true;
            case "VIEWER":
                return true;
            default:
                return false;
        }
    }

    private static IResult ToHttp<T>(OperationResult<T> result, Func<T, string>? created = null) => result.Status switch
    {
        OperationStatus.Ok when created is not null => Results.Created(created(result.Value!), result.Value),
        OperationStatus.Ok => Results.Ok(result.Value),
        OperationStatus.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: result.Error),
        OperationStatus.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: result.Error),
        OperationStatus.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Error),
        _ => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error),
    };
}
