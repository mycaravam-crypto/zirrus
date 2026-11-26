using Microsoft.EntityFrameworkCore;
using Zirrus.Application.Catalogs;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="ICatalogReader"/>.</summary>
/// <param name="db">Database context.</param>
internal sealed class CatalogReader(ZirrusDbContext db) : ICatalogReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid Id, string Name, DateTimeOffset CreatedAt, CatalogRole? Role)>> ListAsync(
        Guid organizationId, string? subject, CancellationToken cancellationToken)
    {
        if (subject is null)
        {
            var all = await db.Catalogs.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId)
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name, c.CreatedAt })
                .ToListAsync(cancellationToken);
            return all.Select(c => (c.Id, c.Name, c.CreatedAt, (CatalogRole?)null)).ToList();
        }

        // Permission scope is applied in the query itself, before ordering and materialization.
        var granted = await (
                from c in db.Catalogs.AsNoTracking()
                join m in db.CatalogMemberships.AsNoTracking()
                    on new { c.OrganizationId, CatalogId = c.Id } equals new { m.OrganizationId, m.CatalogId }
                where c.OrganizationId == organizationId && m.Subject == subject
                orderby c.Name
                select new { c.Id, c.Name, c.CreatedAt, m.Role })
            .ToListAsync(cancellationToken);
        return granted.Select(c => (c.Id, c.Name, c.CreatedAt, (CatalogRole?)c.Role)).ToList();
    }

    /// <inheritdoc />
    public async Task<CatalogRole?> FindRoleAsync(Guid organizationId, Guid catalogId, string subject, CancellationToken cancellationToken) =>
        await db.CatalogMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.CatalogId == catalogId && m.Subject == subject)
            .Select(m => (CatalogRole?)m.Role)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
        db.Catalogs.AnyAsync(c => c.OrganizationId == organizationId && c.Id == catalogId, cancellationToken);
}

/// <summary>EF Core implementation of <see cref="ICatalogStore"/>.</summary>
/// <param name="db">Database context.</param>
internal sealed class CatalogStore(ZirrusDbContext db) : ICatalogStore
{
    /// <inheritdoc />
    public Task<Catalog?> FindAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
        db.Catalogs.FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Id == catalogId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> NameExistsAsync(Guid organizationId, string name, CancellationToken cancellationToken) =>
        db.Catalogs.AnyAsync(c => c.OrganizationId == organizationId && c.Name == name, cancellationToken);

    /// <inheritdoc />
    public Task<CatalogMembership?> FindMembershipAsync(Catalog catalog, string subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return db.CatalogMemberships.FirstOrDefaultAsync(
            m => m.OrganizationId == catalog.OrganizationId && m.CatalogId == catalog.Id && m.Subject == subject, cancellationToken);
    }

    /// <inheritdoc />
    public void Add(Catalog catalog) => db.Catalogs.Add(catalog);

    /// <inheritdoc />
    public void Add(CatalogMembership membership) => db.CatalogMemberships.Add(membership);

    /// <inheritdoc />
    public void Remove(CatalogMembership membership) => db.CatalogMemberships.Remove(membership);

    /// <inheritdoc />
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
