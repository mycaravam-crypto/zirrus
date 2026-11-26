namespace Zirrus.Domain.Catalogs;

/// <summary>Role a user holds in one catalog (security.md authorization matrix).</summary>
public enum CatalogRole
{
    /// <summary>Reads active assets, previews and search results in the catalog.</summary>
    Viewer = 0,

    /// <summary>Additionally uploads, edits metadata, assigns categories, deletes and retries.</summary>
    Editor = 1,
}

/// <summary>
/// Grants a user a role in a catalog. Organization administrators come from the identity
/// provider and need no membership; editors and viewers are granted per catalog.
/// </summary>
public sealed class CatalogMembership
{
    /// <summary>Maximum length of an identity-provider subject.</summary>
    public const int MaxSubjectLength = 255;

    private CatalogMembership(Guid organizationId, Guid catalogId, string subject, CatalogRole role, DateTimeOffset grantedAt)
    {
        OrganizationId = organizationId;
        CatalogId = catalogId;
        Subject = subject;
        Role = role;
        GrantedAt = grantedAt;
    }

    /// <summary>Owning organization; part of the composite reference to the catalog.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Catalog the role applies to.</summary>
    public Guid CatalogId { get; private set; }

    /// <summary>Stable user identifier from the identity provider (the OIDC <c>sub</c> claim).</summary>
    public string Subject { get; private set; }

    /// <summary>Granted role.</summary>
    public CatalogRole Role { get; private set; }

    /// <summary>When the role was granted or last changed (UTC).</summary>
    public DateTimeOffset GrantedAt { get; private set; }

    /// <summary>Creates the membership that lets a user act as viewer or editor in one catalog, keyed by the catalog's organization.</summary>
    /// <param name="catalog">Catalog the role applies to.</param>
    /// <param name="subject">Identity-provider subject; 1–255 characters.</param>
    /// <param name="role">Role to grant.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The membership.</returns>
    /// <exception cref="ArgumentException">The subject is empty or too long, or the role is undefined.</exception>
    public static CatalogMembership Grant(Catalog catalog, string subject, CatalogRole role, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Length == 0 || subject.Length > MaxSubjectLength || subject.Trim().Length != subject.Length)
        {
            throw new ArgumentException($"Subject must be 1–{MaxSubjectLength} characters without surrounding spaces.", nameof(subject));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentException("Unknown catalog role.", nameof(role));
        }

        return new CatalogMembership(catalog.OrganizationId, catalog.Id, subject, role, now);
    }

    /// <summary>Changes the role, for example from viewer to editor.</summary>
    /// <param name="role">New role.</param>
    /// <param name="now">Current time.</param>
    /// <exception cref="ArgumentException">The role is undefined.</exception>
    public void ChangeRole(CatalogRole role, DateTimeOffset now)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentException("Unknown catalog role.", nameof(role));
        }

        Role = role;
        GrantedAt = now;
    }

    /// <summary>True when the role allows changing catalog content.</summary>
    public bool CanEdit => Role == CatalogRole.Editor;
}
