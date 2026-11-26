using Zirrus.Domain.Assets;

namespace Zirrus.Domain.Catalogs;

/// <summary>
/// A node in a catalog's category tree. The parent must be in the same catalog, enforced by a
/// composite foreign key; cycle prevention on moves follows with category management (W5).
/// </summary>
public sealed class Category
{
    /// <summary>Maximum length of a category name.</summary>
    public const int MaxNameLength = 200;

    private Category(Guid id, Guid organizationId, Guid catalogId, Guid? parentId, string name, DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        CatalogId = catalogId;
        ParentId = parentId;
        Name = name;
        CreatedAt = createdAt;
    }

    /// <summary>Opaque identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning organization (composite reference to the catalog).</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Owning catalog.</summary>
    public Guid CatalogId { get; private set; }

    /// <summary>Parent category in the same catalog; null for a root.</summary>
    public Guid? ParentId { get; private set; }

    /// <summary>Display name, unique among siblings.</summary>
    public string Name { get; private set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Creates a root category.</summary>
    /// <param name="catalog">Owning catalog.</param>
    /// <param name="name">Name; trimmed, 1–200 characters.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The category.</returns>
    public static Category CreateRoot(Catalog catalog, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new Category(Guid.NewGuid(), catalog.OrganizationId, catalog.Id, null, ValidateName(name), now);
    }

    /// <summary>Creates a child category under a parent of the same catalog.</summary>
    /// <param name="parent">Parent category.</param>
    /// <param name="name">Name; trimmed, 1–200 characters.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The category.</returns>
    public static Category CreateChild(Category parent, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new Category(Guid.NewGuid(), parent.OrganizationId, parent.CatalogId, parent.Id, ValidateName(name), now);
    }

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Category name must be 1–{MaxNameLength} characters.", nameof(name));
        }

        return trimmed;
    }
}

/// <summary>Assignment of an asset to a category of the same catalog (composite foreign keys).</summary>
public sealed class AssetCategory
{
    private AssetCategory(Guid catalogId, Guid assetId, Guid categoryId, DateTimeOffset assignedAt)
    {
        CatalogId = catalogId;
        AssetId = assetId;
        CategoryId = categoryId;
        AssignedAt = assignedAt;
    }

    /// <summary>Catalog of both the asset and the category.</summary>
    public Guid CatalogId { get; private set; }

    /// <summary>Assigned asset.</summary>
    public Guid AssetId { get; private set; }

    /// <summary>Category.</summary>
    public Guid CategoryId { get; private set; }

    /// <summary>Assignment time (UTC).</summary>
    public DateTimeOffset AssignedAt { get; private set; }

    /// <summary>Assigns an asset to a category.</summary>
    /// <param name="asset">Asset.</param>
    /// <param name="category">Category.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The assignment.</returns>
    /// <exception cref="ArgumentException">The asset and category belong to different catalogs.</exception>
    public static AssetCategory Assign(Asset asset, Category category, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(category);
        if (asset.CatalogId != category.CatalogId)
        {
            throw new ArgumentException("Asset and category must belong to the same catalog.", nameof(category));
        }

        return new AssetCategory(asset.CatalogId, asset.Id, category.Id, now);
    }
}
