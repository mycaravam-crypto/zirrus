namespace Zirrus.Domain.Catalogs;

/// <summary>
/// The organization that owns catalogs. The MVP runs one organization (ADR 0007); the row exists so
/// every catalog references a real owner and tenant isolation can be added without re-keying.
/// </summary>
public sealed class Organization
{
    /// <summary>Maximum length of an organization name.</summary>
    public const int MaxNameLength = 200;

    private Organization(Guid id, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }

    /// <summary>Opaque identifier (<c>Zirrus:OrganizationId</c>).</summary>
    public Guid Id { get; private set; }

    /// <summary>Display name.</summary>
    public string Name { get; private set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Creates an organization.</summary>
    /// <param name="id">Identifier.</param>
    /// <param name="name">Display name; trimmed, 1–200 characters.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The organization.</returns>
    public static Organization Create(Guid id, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (id == Guid.Empty || trimmed.Length == 0 || trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Organization needs an ID and a 1–{MaxNameLength} character name.", nameof(name));
        }

        return new Organization(id, trimmed, now);
    }
}
