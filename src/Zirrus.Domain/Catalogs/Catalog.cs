namespace Zirrus.Domain.Catalogs;

/// <summary>
/// A catalog groups assets and is the unit of permission: editors and viewers are granted per catalog.
/// </summary>
public sealed class Catalog
{
    /// <summary>Maximum length of a catalog name.</summary>
    public const int MaxNameLength = 200;

    private Catalog(Guid id, Guid organizationId, string name, DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
        CreatedAt = createdAt;
    }

    /// <summary>Opaque catalog identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning organization; kept on every catalog for future tenant isolation (ADR 0007).</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Display name, unique within the organization.</summary>
    public string Name { get; private set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Creates a catalog after validating its name.</summary>
    /// <param name="organizationId">Owning organization.</param>
    /// <param name="name">Display name; trimmed, 1–200 characters.</param>
    /// <param name="now">Current time, supplied by the caller for testability.</param>
    /// <returns>The new catalog.</returns>
    /// <exception cref="ArgumentException">The name is empty or too long.</exception>
    public static Catalog Create(Guid organizationId, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Catalog name must be 1–{MaxNameLength} characters.", nameof(name));
        }

        return new Catalog(Guid.NewGuid(), organizationId, trimmed, now);
    }
}
