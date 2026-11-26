using Microsoft.EntityFrameworkCore;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>EF Core model for ZIRRUS (ADR 0005). Domain types carry no EF Core references; mapping lives here.</summary>
/// <param name="options">Context options.</param>
public sealed class ZirrusDbContext(DbContextOptions<ZirrusDbContext> options) : DbContext(options)
{
    /// <summary>Catalogs.</summary>
    public DbSet<Catalog> Catalogs => Set<Catalog>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Catalog>(e =>
        {
            e.ToTable("catalogs");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id");
            e.Property(c => c.OrganizationId).HasColumnName("organization_id");
            e.Property(c => c.Name).HasColumnName("name").HasMaxLength(Catalog.MaxNameLength);
            e.Property(c => c.CreatedAt).HasColumnName("created_at");
            e.HasIndex(c => new { c.OrganizationId, c.Name }).IsUnique();

            // Composite alternate key so assets and categories can reference (organization_id, id) later (W3).
            e.HasAlternateKey(c => new { c.OrganizationId, c.Id });
        });
    }
}
