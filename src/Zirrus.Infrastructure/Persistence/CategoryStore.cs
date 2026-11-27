using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>PostgreSQL category and assignment persistence.</summary>
/// <param name="db">Database context.</param>
internal sealed class CategoryStore(ZirrusDbContext db) : ICategoryStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Category>> ListAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
        await db.Set<Category>().AsNoTracking().Where(c => c.OrganizationId == organizationId && c.CatalogId == catalogId)
            .OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Category?> FindCategoryAsync(Guid organizationId, Guid catalogId, Guid categoryId, CancellationToken cancellationToken) =>
        db.Set<Category>().AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == organizationId && c.CatalogId == catalogId && c.Id == categoryId, cancellationToken);

    /// <inheritdoc />
    public Task<Catalog?> FindCatalogAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
        db.Catalogs.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == organizationId && c.Id == catalogId, cancellationToken);

    /// <inheritdoc />
    public Task<Asset?> FindAssetAsync(Guid organizationId, Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
        db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.OrganizationId == organizationId && a.CatalogId == catalogId && a.Id == assetId, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(Category category, CancellationToken cancellationToken)
    {
        db.Set<Category>().Add(category);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> AssignAsync(AssetCategory assignment, CancellationToken cancellationToken)
    {
        // Lock the active asset until commit: a concurrent deletion cannot pass the state check.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var active = await db.Assets.FromSqlInterpolated($"SELECT * FROM assets WHERE catalog_id = {assignment.CatalogId} AND id = {assignment.AssetId} AND state = 'Active' FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken);
        if (active.Count == 0)
        {
            return false;
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO asset_categories (catalog_id, asset_id, category_id, assigned_at) VALUES ({assignment.CatalogId}, {assignment.AssetId}, {assignment.CategoryId}, {assignment.AssignedAt}) ON CONFLICT (asset_id, category_id) DO NOTHING",
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken); // the staged audit entry commits with the assignment
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Category>> ListForAssetAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking()
            .Where(c => c.CatalogId == catalogId && db.AssetCategories.Any(a => a.CatalogId == catalogId && a.AssetId == assetId && a.CategoryId == c.Id))
            .OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task UnassignAsync(Guid catalogId, Guid assetId, Guid categoryId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.AssetCategories.Where(a => a.CatalogId == catalogId && a.AssetId == assetId && a.CategoryId == categoryId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken); // the staged audit entry commits with the removal
        await transaction.CommitAsync(cancellationToken);
    }
}
