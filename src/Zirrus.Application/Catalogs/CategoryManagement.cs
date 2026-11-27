using Zirrus.Application.Audit;
using Zirrus.Application.Identity;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Application.Catalogs;

/// <summary>Persistence port for catalog categories and asset assignments.</summary>
public interface ICategoryStore
{
    /// <summary>Lists categories within the specified organization and catalog.</summary>
    Task<IReadOnlyList<Category>> ListAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken);

    /// <summary>Loads one category within the organization and catalog.</summary>
    Task<Category?> FindCategoryAsync(Guid organizationId, Guid catalogId, Guid categoryId, CancellationToken cancellationToken);

    /// <summary>Loads a catalog within the organization.</summary>
    Task<Catalog?> FindCatalogAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken);

    /// <summary>Loads an asset within the organization and catalog.</summary>
    Task<Asset?> FindAssetAsync(Guid organizationId, Guid catalogId, Guid assetId, CancellationToken cancellationToken);

    /// <summary>Inserts a category, returning false on a duplicate sibling name.</summary>
    Task<bool> TryAddAsync(Category category, CancellationToken cancellationToken);

    /// <summary>Ensures an assignment exists without creating duplicates.</summary>
    Task<bool> AssignAsync(AssetCategory assignment, CancellationToken cancellationToken);

    /// <summary>Lists the categories an asset is assigned to, by name.</summary>
    Task<IReadOnlyList<Category>> ListForAssetAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken);

    /// <summary>Removes an assignment if it exists (idempotent).</summary>
    Task UnassignAsync(Guid catalogId, Guid assetId, Guid categoryId, CancellationToken cancellationToken);
}

/// <summary>Category tree node returned to clients.</summary>
/// <param name="Id">Category identifier.</param>
/// <param name="ParentId">Parent identifier, or null for a root.</param>
/// <param name="Name">Display name.</param>
public sealed record CategoryDetails(Guid Id, Guid? ParentId, string Name);

/// <summary>Catalog-scoped category reads, administrator-only creation and editor assignments.</summary>
/// <param name="store">Category persistence.</param>
/// <param name="access">Catalog authorization.</param>
/// <param name="users">Current user.</param>
/// <param name="organization">Organization context.</param>
/// <param name="time">Time source.</param>
/// <param name="audit">Audit log; entries are committed with the change they describe.</param>
public sealed class CategoryManagement(ICategoryStore store, CatalogAccess access, ICurrentUserAccessor users,
    OrganizationContext organization, TimeProvider time, IAuditLog audit)
{
    /// <summary>Reads the category tree as a flat list of parent-linked nodes.</summary>
    public async Task<OperationResult<IReadOnlyList<CategoryDetails>>> ListAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<IReadOnlyList<CategoryDetails>>(OperationStatus.NotFound, "Catalog not found.");
        }

        var categories = await store.ListAsync(organization.Id, catalogId, cancellationToken);
        return OperationResult.Ok<IReadOnlyList<CategoryDetails>>(categories.Select(ToDetails).ToList());
    }

    /// <summary>Creates a root or child category; existing parent links are immutable in this increment.</summary>
    public async Task<OperationResult<CategoryDetails>> CreateAsync(Guid catalogId, Guid? parentId, string name, CancellationToken cancellationToken)
    {
        if (users.User is not { IsOrganizationAdmin: true })
        {
            return OperationResult.Fail<CategoryDetails>(OperationStatus.Forbidden, "Only administrators can create categories.");
        }

        var catalog = await store.FindCatalogAsync(organization.Id, catalogId, cancellationToken);
        if (catalog is null)
        {
            return OperationResult.Fail<CategoryDetails>(OperationStatus.NotFound, "Catalog not found.");
        }

        Category category;
        try
        {
            if (parentId is { } id)
            {
                var parent = await store.FindCategoryAsync(organization.Id, catalogId, id, cancellationToken);
                if (parent is null)
                {
                    return OperationResult.Fail<CategoryDetails>(OperationStatus.NotFound, "Parent category not found.");
                }

                category = Category.CreateChild(parent, name, time.GetUtcNow());
            }
            else
            {
                category = Category.CreateRoot(catalog, name, time.GetUtcNow());
            }
        }
        catch (ArgumentException ex)
        {
            return OperationResult.Fail<CategoryDetails>(OperationStatus.Invalid, ex.Message);
        }

        audit.Record(new AuditEntry(AuditActions.CategoryCreate, users.User!.Subject, organization.Id, catalogId, category.Id));
        return await store.TryAddAsync(category, cancellationToken)
            ? OperationResult.Ok(ToDetails(category))
            : OperationResult.Fail<CategoryDetails>(OperationStatus.Conflict, "A sibling category with this name exists.");
    }

    /// <summary>Assigns an active asset to a category; repeated assignments are idempotent.</summary>
    public async Task<OperationResult<CategoryDetails>> AssignAsync(Guid catalogId, Guid assetId, Guid categoryId, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<CategoryDetails>(OperationStatus.NotFound, "Catalog not found.");
        }

        if (!await access.CanEditAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<CategoryDetails>(OperationStatus.Forbidden, "Editing requires the editor role.");
        }

        var asset = await store.FindAssetAsync(organization.Id, catalogId, assetId, cancellationToken);
        var category = await store.FindCategoryAsync(organization.Id, catalogId, categoryId, cancellationToken);
        if (asset is null || asset.State != AssetState.Active || category is null)
        {
            return OperationResult.Fail<CategoryDetails>(OperationStatus.NotFound, "Active asset or category not found.");
        }

        audit.Record(new AuditEntry(AuditActions.CategoryAssign, access.Subject!, organization.Id, catalogId, asset.Id, $"category={category.Id}"));
        return await store.AssignAsync(AssetCategory.Assign(asset, category, time.GetUtcNow()), cancellationToken)
            ? OperationResult.Ok(ToDetails(category))
            : OperationResult.Fail<CategoryDetails>(OperationStatus.NotFound, "Active asset or category not found.");
    }

    /// <summary>
    /// Lists an asset's categories. Readers see assignments of assets they may see: viewers only active
    /// assets, editors every non-deleted one; anything else is reported as not found.
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<CategoryDetails>>> ListForAssetAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<IReadOnlyList<CategoryDetails>>(OperationStatus.NotFound, "Catalog not found.");
        }

        var asset = await store.FindAssetAsync(organization.Id, catalogId, assetId, cancellationToken);
        if (asset is null || !asset.IsVisibleTo(await access.CanEditAsync(catalogId, cancellationToken)))
        {
            return OperationResult.Fail<IReadOnlyList<CategoryDetails>>(OperationStatus.NotFound, "Asset not found.");
        }

        var categories = await store.ListForAssetAsync(catalogId, assetId, cancellationToken);
        return OperationResult.Ok<IReadOnlyList<CategoryDetails>>(categories.Select(ToDetails).ToList());
    }

    /// <summary>Removes an asset from a category (editors); removing a missing assignment is not an error.</summary>
    /// <returns>The asset's remaining categories.</returns>
    public async Task<OperationResult<IReadOnlyList<CategoryDetails>>> UnassignAsync(Guid catalogId, Guid assetId, Guid categoryId, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<IReadOnlyList<CategoryDetails>>(OperationStatus.NotFound, "Catalog not found.");
        }

        if (!await access.CanEditAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<IReadOnlyList<CategoryDetails>>(OperationStatus.Forbidden, "Editing requires the editor role.");
        }

        var asset = await store.FindAssetAsync(organization.Id, catalogId, assetId, cancellationToken);
        if (asset is null || !asset.IsVisibleTo(true))
        {
            return OperationResult.Fail<IReadOnlyList<CategoryDetails>>(OperationStatus.NotFound, "Asset not found.");
        }

        audit.Record(new AuditEntry(AuditActions.CategoryUnassign, access.Subject!, organization.Id, catalogId, assetId, $"category={categoryId}"));
        await store.UnassignAsync(catalogId, assetId, categoryId, cancellationToken);
        var remaining = await store.ListForAssetAsync(catalogId, assetId, cancellationToken);
        return OperationResult.Ok<IReadOnlyList<CategoryDetails>>(remaining.Select(ToDetails).ToList());
    }

    private static CategoryDetails ToDetails(Category category) => new(category.Id, category.ParentId, category.Name);
}
