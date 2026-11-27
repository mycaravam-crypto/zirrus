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

/// <summary>Request to create a category.</summary>
/// <param name="Name">Display name.</param>
/// <param name="ParentId">Parent category, or null for a root.</param>
public sealed record CreateCategoryRequest(string Name, Guid? ParentId = null);

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

        catalogs.MapGet("/{catalogId:guid}/metadata-schema", async (Guid catalogId, SchemaAdministration schemas, CancellationToken cancellationToken) =>
                ToHttp(await schemas.GetAsync(catalogId, cancellationToken)))
            .WithName("GetMetadataSchema")
            .WithSummary("Read the current catalog metadata schema")
            .Produces<SchemaDetails>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/{catalogId:guid}/metadata-schema", async (Guid catalogId, Zirrus.Domain.Assets.MetadataFieldDefinition[] fields,
                SchemaAdministration schemas, CancellationToken cancellationToken) =>
                ToHttp(await schemas.CreateAsync(catalogId, fields, cancellationToken), created: _ => $"/api/v1/catalogs/{catalogId}/metadata-schema"))
            .WithName("CreateMetadataSchema")
            .WithSummary("Create the initial metadata schema (organization administrators only)")
            .Produces<SchemaDetails>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        catalogs.MapGet("/{catalogId:guid}/categories", async (Guid catalogId, CategoryManagement categories, CancellationToken cancellationToken) =>
                ToHttp(await categories.ListAsync(catalogId, cancellationToken)))
            .WithName("ListCategories")
            .WithSummary("List the catalog category tree")
            .Produces<IReadOnlyList<CategoryDetails>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/{catalogId:guid}/categories", async (Guid catalogId, CreateCategoryRequest request,
                CategoryManagement categories, CancellationToken cancellationToken) =>
                ToHttp(await categories.CreateAsync(catalogId, request.ParentId, request.Name, cancellationToken),
                    created: _ => $"/api/v1/catalogs/{catalogId}/categories"))
            .WithName("CreateCategory")
            .WithSummary("Create a root or child category (administrators only)")
            .Produces<CategoryDetails>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        catalogs.MapPut("/{catalogId:guid}/assets/{assetId:guid}/categories/{categoryId:guid}",
                async (Guid catalogId, Guid assetId, Guid categoryId, CategoryManagement categories, CancellationToken cancellationToken) =>
                    ToHttp(await categories.AssignAsync(catalogId, assetId, categoryId, cancellationToken)))
            .WithName("AssignAssetCategory")
            .WithSummary("Assign an active asset to a category (editors)")
            .Produces<CategoryDetails>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        catalogs.MapGet("/{catalogId:guid}/assets/{assetId:guid}/categories",
                async (Guid catalogId, Guid assetId, CategoryManagement categories, CancellationToken cancellationToken) =>
                    ToHttp(await categories.ListForAssetAsync(catalogId, assetId, cancellationToken)))
            .WithName("ListAssetCategories")
            .WithSummary("List the categories an asset is assigned to")
            .Produces<IReadOnlyList<CategoryDetails>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        catalogs.MapDelete("/{catalogId:guid}/assets/{assetId:guid}/categories/{categoryId:guid}",
                async (Guid catalogId, Guid assetId, Guid categoryId, CategoryManagement categories, CancellationToken cancellationToken) =>
                    ToHttp(await categories.UnassignAsync(catalogId, assetId, categoryId, cancellationToken)))
            .WithName("UnassignAssetCategory")
            .WithSummary("Remove an asset from a category (editors); returns the remaining categories")
            .Produces<IReadOnlyList<CategoryDetails>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

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
