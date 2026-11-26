using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Zirrus.Domain.Catalogs;
using Zirrus.Infrastructure.Identity;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>EF Core model for ZIRRUS (ADR 0005). Domain types carry no EF Core references; mapping lives here.</summary>
/// <param name="options">Context options.</param>
public sealed class ZirrusDbContext(DbContextOptions<ZirrusDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>Catalogs.</summary>
    public DbSet<Catalog> Catalogs => Set<Catalog>();

    /// <summary>Catalog role assignments.</summary>
    public DbSet<CatalogMembership> CatalogMemberships => Set<CatalogMembership>();

    /// <summary>Server-side authentication sessions (cookie tickets).</summary>
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();

    /// <summary>ASP.NET Core Data Protection key ring, shared by all API and worker replicas.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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

            // Composite alternate key: memberships, assets and categories reference (organization_id, id),
            // so a row can never point at a catalog of another organization.
            e.HasAlternateKey(c => new { c.OrganizationId, c.Id });
        });

        modelBuilder.Entity<CatalogMembership>(e =>
        {
            e.ToTable("catalog_memberships");
            e.HasKey(m => new { m.CatalogId, m.Subject });
            e.Property(m => m.OrganizationId).HasColumnName("organization_id");
            e.Property(m => m.CatalogId).HasColumnName("catalog_id");
            e.Property(m => m.Subject).HasColumnName("subject").HasMaxLength(CatalogMembership.MaxSubjectLength);
            e.Property(m => m.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.GrantedAt).HasColumnName("granted_at");
            e.HasOne<Catalog>().WithMany()
                .HasForeignKey(m => new { m.OrganizationId, m.CatalogId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.OrganizationId, m.Subject });
            e.Ignore(m => m.CanEdit);
        });

        modelBuilder.Entity<SessionRecord>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasColumnName("key").HasMaxLength(64);
            e.Property(s => s.Subject).HasColumnName("subject").HasMaxLength(CatalogMembership.MaxSubjectLength);
            e.Property(s => s.ProtectedTicket).HasColumnName("protected_ticket");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            e.HasIndex(s => s.ExpiresAt);
            e.HasIndex(s => s.Subject);
        });

        modelBuilder.Entity<DataProtectionKey>(e => e.ToTable("data_protection_keys"));
    }
}
