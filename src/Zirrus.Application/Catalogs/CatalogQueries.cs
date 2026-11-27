using Zirrus.Application.Identity;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Application.Catalogs;

/// <summary>Read model returned by catalog queries.</summary>
/// <param name="Id">Catalog identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="Access">The caller's access: <c>admin</c>, <c>editor</c> or <c>viewer</c>.</param>
public sealed record CatalogSummary(Guid Id, string Name, DateTimeOffset CreatedAt, string Access);

/// <summary>Port for reading catalogs and memberships; implemented by the infrastructure layer.</summary>
public interface ICatalogReader
{
    /// <summary>Lists catalogs of an organization ordered by name, with the subject's role in each.</summary>
    /// <param name="organizationId">Organization.</param>
    /// <param name="subject">Only catalogs this subject is a member of; null lists all catalogs.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Catalogs with the subject's role, or null role when listing all.</returns>
    Task<IReadOnlyList<(Guid Id, string Name, DateTimeOffset CreatedAt, CatalogRole? Role)>> ListAsync(
        Guid organizationId, string? subject, CancellationToken cancellationToken);

    /// <summary>Gets the subject's role in a catalog of the organization.</summary>
    /// <param name="organizationId">Organization.</param>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="subject">Subject.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The role, or null when the subject has none or the catalog does not exist.</returns>
    Task<CatalogRole?> FindRoleAsync(Guid organizationId, Guid catalogId, string subject, CancellationToken cancellationToken);

    /// <summary>Checks whether a catalog exists in the organization.</summary>
    /// <param name="organizationId">Organization.</param>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>True when it exists.</returns>
    Task<bool> ExistsAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken);
}

/// <summary>
/// Use case: list the catalogs the caller may see. Organization administrators see all catalogs;
/// other users see only catalogs they are granted a role in (deny by default).
/// </summary>
/// <param name="reader">Catalog read port.</param>
/// <param name="users">Current user accessor.</param>
/// <param name="organization">Organization context.</param>
public sealed class ListCatalogs(ICatalogReader reader, ICurrentUserAccessor users, OrganizationContext organization)
{
    /// <summary>Runs the query.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Visible catalogs ordered by name; empty for anonymous callers.</returns>
    public async Task<IReadOnlyList<CatalogSummary>> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (users.User is not { } user)
        {
            return [];
        }

        var rows = await reader.ListAsync(organization.Id, user.IsOrganizationAdmin ? null : user.Subject, cancellationToken);
        return rows.Select(r => new CatalogSummary(r.Id, r.Name, r.CreatedAt, AccessName(user, r.Role))).ToList();
    }

    private static string AccessName(CurrentUser user, CatalogRole? role) =>
        user.IsOrganizationAdmin ? "admin" : role == CatalogRole.Editor ? "editor" : "viewer";
}

/// <summary>
/// Resource-based catalog permission checks, applied by every use case that touches catalog
/// content (security.md: authorization at use-case and query boundaries).
/// </summary>
/// <param name="reader">Catalog read port.</param>
/// <param name="users">Current user accessor.</param>
/// <param name="organization">Organization context.</param>
public sealed class CatalogAccess(ICatalogReader reader, ICurrentUserAccessor users, OrganizationContext organization)
{
    /// <summary>The organization every check is scoped to.</summary>
    public Guid OrganizationId => organization.Id;

    /// <summary>Subject of the caller, or null when anonymous.</summary>
    public string? Subject => users.User?.Subject;

    /// <summary>True when the caller may read the catalog (admin, editor or viewer).</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether reading is allowed.</returns>
    public async Task<bool> CanReadAsync(Guid catalogId, CancellationToken cancellationToken) =>
        await RoleAsync(catalogId, cancellationToken) is not null;

    /// <summary>True when the caller may change catalog content (admin or editor).</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether editing is allowed.</returns>
    public async Task<bool> CanEditAsync(Guid catalogId, CancellationToken cancellationToken) =>
        await RoleAsync(catalogId, cancellationToken) == CatalogRole.Editor;

    private async Task<CatalogRole?> RoleAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        if (users.User is not { } user)
        {
            return null;
        }

        if (user.IsOrganizationAdmin)
        {
            // Admins act as editors in every existing catalog of their organization.
            return await reader.ExistsAsync(organization.Id, catalogId, cancellationToken) ? CatalogRole.Editor : null;
        }

        return await reader.FindRoleAsync(organization.Id, catalogId, user.Subject, cancellationToken);
    }
}
