using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>PostgreSQL-backed schema persistence.</summary>
/// <param name="db">Database context.</param>
internal sealed class SchemaStore(ZirrusDbContext db) : ISchemaStore
{
    /// <inheritdoc />
    public Task<Catalog?> FindCatalogAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
        db.Catalogs.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == organizationId && c.Id == catalogId, cancellationToken);

    /// <inheritdoc />
    public Task<MetadataSchema?> FindLatestAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
        db.Set<MetadataSchema>().AsNoTracking().Include(s => s.Fields)
            .Where(s => s.OrganizationId == organizationId && s.CatalogId == catalogId)
            .OrderByDescending(s => s.Version).FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(MetadataSchema schema, CancellationToken cancellationToken)
    {
        db.Set<MetadataSchema>().Add(schema);
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
}
