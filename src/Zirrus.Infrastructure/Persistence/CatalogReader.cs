using Microsoft.EntityFrameworkCore;
using Zirrus.Application.Catalogs;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="ICatalogReader"/>.</summary>
/// <param name="db">Database context.</param>
internal sealed class CatalogReader(ZirrusDbContext db) : ICatalogReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogSummary>> ListAsync(CancellationToken cancellationToken) =>
        await db.Catalogs
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CatalogSummary(c.Id, c.Name, c.CreatedAt))
            .ToListAsync(cancellationToken);
}
