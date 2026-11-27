using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;
using Zirrus.Infrastructure.Identity;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>EF Core model for ZIRRUS (ADR 0005). Domain types carry no EF Core references; mapping lives here.</summary>
/// <param name="options">Context options.</param>
public sealed class ZirrusDbContext(DbContextOptions<ZirrusDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>Organizations.</summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    /// <summary>Assets.</summary>
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>Renditions of assets.</summary>
    public DbSet<Rendition> Renditions => Set<Rendition>();

    /// <summary>Category trees.</summary>
    public DbSet<Category> Categories => Set<Category>();

    /// <summary>Asset-category assignments.</summary>
    public DbSet<AssetCategory> AssetCategories => Set<AssetCategory>();

    /// <summary>Metadata schema versions.</summary>
    public DbSet<MetadataSchema> MetadataSchemas => Set<MetadataSchema>();

    /// <summary>Durable job intent (outbox and queue, ADR 0002).</summary>
    public DbSet<JobIntent> Jobs => Set<JobIntent>();

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
        MapAudit(modelBuilder);

        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.HasKey(o => o.Id);
            e.Property(o => o.Name).HasMaxLength(Organization.MaxNameLength);
            SnakeCaseColumns(e);
        });

        modelBuilder.Entity<Catalog>(e =>
        {
            e.ToTable("catalogs");
            e.HasOne<Organization>().WithMany().HasForeignKey(c => c.OrganizationId).OnDelete(DeleteBehavior.Restrict);
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

        MapAssets(modelBuilder);
        MapCategoriesAndSchemas(modelBuilder);
        MapJobs(modelBuilder);

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

    private static void MapAssets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Asset>(e =>
        {
            e.ToTable("assets", t => t.HasCheckConstraint("ck_assets_sha256", "sha256 IS NULL OR sha256 ~ '^[0-9a-f]{64}$'"));
            e.HasKey(a => a.Id);
            e.Property(a => a.OriginalKey).HasMaxLength(BlobKey.MaxLength);
            e.Property(a => a.FileName).HasMaxLength(Asset.MaxFileNameLength);
            e.Property(a => a.MediaType).HasMaxLength(Asset.MaxMediaTypeLength);
            e.Property(a => a.Sha256).HasMaxLength(64).IsFixedLength();
            e.Property(a => a.State).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.StatusReason).HasMaxLength(Asset.MaxReasonLength);
            e.Property(a => a.CreatedBy).HasMaxLength(CatalogMembership.MaxSubjectLength);
            e.Property(a => a.Metadata).HasColumnType("jsonb");
            e.Property(a => a.EmbeddedMetadata).HasColumnType("jsonb");

            // Optimistic concurrency (ADR 0005): UPDATE ... WHERE revision = <loaded>; the API maps a miss to 412.
            e.Property(a => a.Revision).IsConcurrencyToken();
            SnakeCaseColumns(e);

            e.HasOne<Catalog>().WithMany()
                .HasForeignKey(a => new { a.OrganizationId, a.CatalogId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);

            // Renditions, category assignments and jobs reference (catalog_id, id): never another catalog's asset.
            e.HasAlternateKey(a => new { a.CatalogId, a.Id });
            e.HasIndex(a => a.OriginalKey).IsUnique();
            e.HasIndex(a => new { a.CatalogId, a.State, a.UpdatedAt });
            e.HasIndex(a => new { a.CatalogId, a.Sha256 });
            e.HasIndex(a => new { a.State, a.StateChangedAt });
            e.HasIndex(a => a.Metadata).HasMethod("gin").HasOperators("jsonb_path_ops");
        });

        modelBuilder.Entity<Rendition>(e =>
        {
            e.ToTable("renditions");
            e.HasKey(r => new { r.AssetId, r.Kind });
            e.Property(r => r.Kind).HasMaxLength(Rendition.MaxKindLength);
            e.Property(r => r.StorageKey).HasMaxLength(BlobKey.MaxLength);
            e.Property(r => r.MediaType).HasMaxLength(Asset.MaxMediaTypeLength);
            e.Property(r => r.Sha256).HasMaxLength(64).IsFixedLength();
            SnakeCaseColumns(e);
            e.HasOne<Asset>().WithMany()
                .HasForeignKey(r => new { r.CatalogId, r.AssetId })
                .HasPrincipalKey(a => new { a.CatalogId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => r.StorageKey).IsUnique();
        });
    }

    private static void MapCategoriesAndSchemas(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(Category.MaxNameLength);
            SnakeCaseColumns(e);
            e.HasOne<Catalog>().WithMany()
                .HasForeignKey(c => new { c.OrganizationId, c.CatalogId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasAlternateKey(c => new { c.CatalogId, c.Id });

            // The parent must be in the same catalog: (catalog_id, parent_id) references (catalog_id, id).
            e.HasOne<Category>().WithMany()
                .HasForeignKey(c => new { c.CatalogId, c.ParentId })
                .HasPrincipalKey(c => new { c.CatalogId, c.Id })
                .OnDelete(DeleteBehavior.Restrict);

            // Sibling names are unique; NULLS NOT DISTINCT makes this hold for roots too (PostgreSQL 15+).
            e.HasIndex(c => new { c.CatalogId, c.ParentId, c.Name }).IsUnique().AreNullsDistinct(false);
        });

        modelBuilder.Entity<AssetCategory>(e =>
        {
            e.ToTable("asset_categories");
            e.HasKey(x => new { x.AssetId, x.CategoryId });
            SnakeCaseColumns(e);
            e.HasOne<Asset>().WithMany()
                .HasForeignKey(x => new { x.CatalogId, x.AssetId })
                .HasPrincipalKey(a => new { a.CatalogId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Category>().WithMany()
                .HasForeignKey(x => new { x.CatalogId, x.CategoryId })
                .HasPrincipalKey(c => new { c.CatalogId, c.Id })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CatalogId, x.CategoryId });
        });

        modelBuilder.Entity<MetadataSchema>(e =>
        {
            e.ToTable("metadata_schemas");
            e.HasKey(s => s.Id);
            SnakeCaseColumns(e);
            e.HasOne<Catalog>().WithMany()
                .HasForeignKey(s => new { s.OrganizationId, s.CatalogId })
                .HasPrincipalKey(c => new { c.OrganizationId, c.Id })
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.CatalogId, s.Version }).IsUnique();
            e.HasMany(s => s.Fields).WithOne().HasForeignKey(f => f.SchemaId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(s => s.Fields).HasField("_fields").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<MetadataField>(e =>
        {
            e.ToTable("metadata_fields");
            e.HasKey(f => new { f.SchemaId, f.Key });
            e.Property(f => f.Key).HasMaxLength(MetadataField.MaxKeyLength);
            e.Property(f => f.Label).HasMaxLength(MetadataField.MaxLabelLength);
            e.Property(f => f.Type).HasConversion<string>().HasMaxLength(16);
            SnakeCaseColumns(e);
        });
    }

    private static void MapJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobIntent>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Id).UseIdentityAlwaysColumn();
            e.Property(j => j.JobKey).HasMaxLength(128);
            e.Property(j => j.Stage).HasMaxLength(JobIntent.MaxStageLength);
            e.Property(j => j.Payload).HasColumnType("jsonb");
            e.Property(j => j.State).HasConversion<string>().HasMaxLength(16);
            e.Property(j => j.LockedBy).HasMaxLength(128);
            e.Property(j => j.LastError).HasMaxLength(2000);
            SnakeCaseColumns(e);
            e.HasIndex(j => j.JobKey).IsUnique();
            e.HasOne<Asset>().WithMany()
                .HasForeignKey(j => new { j.CatalogId, j.AssetId })
                .HasPrincipalKey(a => new { a.CatalogId, a.Id })
                .OnDelete(DeleteBehavior.Cascade);

            // Claim path of the queue (FOR UPDATE SKIP LOCKED): only ready and running rows.
            e.HasIndex(j => new { j.State, j.RunAfter }).HasFilter("state IN ('Ready', 'Running')");
        });
    }

    private static void SnakeCaseColumns<T>(EntityTypeBuilder<T> entity)
        where T : class
    {
        foreach (var property in entity.Metadata.GetDeclaredProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && !char.IsUpper(name[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    private static void MapAudit(ModelBuilder modelBuilder)
    {
        // SQL-managed append-only tables (AuditHardening migration): mapped for inserts and reads only.
        modelBuilder.Entity<Zirrus.Infrastructure.Audit.AuditEvent>(e =>
        {
            e.ToTable("audit_events", t => t.ExcludeFromMigrations());
            e.HasKey(a => a.Id);
            e.Property(a => a.Seq).ValueGeneratedOnAdd();
            SnakeCaseColumns(e);
        });
        modelBuilder.Entity<Zirrus.Infrastructure.Audit.AuditDigest>(e =>
        {
            e.ToTable("audit_digests", t => t.ExcludeFromMigrations());
            e.HasKey(a => a.Id);
            SnakeCaseColumns(e);
        });
    }
}
