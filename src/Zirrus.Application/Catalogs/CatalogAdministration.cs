using Zirrus.Application.Audit;
using Zirrus.Application.Identity;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Application.Catalogs;

/// <summary>Port for changing catalogs and memberships; implemented by the infrastructure layer.</summary>
public interface ICatalogStore
{
    /// <summary>Loads a catalog scoped to the organization; another organization's catalog resolves to null.</summary>
    /// <param name="organizationId">Organization.</param>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The catalog, or null.</returns>
    Task<Catalog?> FindAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken);

    /// <summary>Checks whether a catalog name is taken in the organization.</summary>
    /// <param name="organizationId">Organization.</param>
    /// <param name="name">Trimmed name.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True when taken.</returns>
    Task<bool> NameExistsAsync(Guid organizationId, string name, CancellationToken cancellationToken);

    /// <summary>Finds a membership.</summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="subject">Subject.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The membership, or null.</returns>
    Task<CatalogMembership?> FindMembershipAsync(Catalog catalog, string subject, CancellationToken cancellationToken);

    /// <summary>Stages a new catalog.</summary>
    /// <param name="catalog">Catalog.</param>
    void Add(Catalog catalog);

    /// <summary>Stages a new membership.</summary>
    /// <param name="membership">Membership.</param>
    void Add(CatalogMembership membership);

    /// <summary>Stages removal of a membership.</summary>
    /// <param name="membership">Membership.</param>
    void Remove(CatalogMembership membership);

    /// <summary>Commits staged changes in one transaction.</summary>
    /// <param name="cancellationToken">Cancels the save.</param>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>A catalog member as returned to administrators.</summary>
/// <param name="Subject">Identity-provider subject.</param>
/// <param name="Role">Role name: <c>editor</c> or <c>viewer</c>.</param>
/// <param name="GrantedAt">When the role was granted (UTC).</param>
public sealed record CatalogMember(string Subject, string Role, DateTimeOffset GrantedAt);

/// <summary>Organization-administrator use cases: create catalogs and manage role assignments.</summary>
/// <param name="store">Catalog write port.</param>
/// <param name="users">Current user accessor.</param>
/// <param name="organization">Organization context.</param>
/// <param name="time">Time source.</param>
/// <param name="audit">Audit log; entries are committed with the change they describe.</param>
public sealed class CatalogAdministration(ICatalogStore store, ICurrentUserAccessor users, OrganizationContext organization, TimeProvider time, IAuditLog audit)
{
    /// <summary>Creates a catalog. Organization administrators only.</summary>
    /// <param name="name">Catalog name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The created catalog, or Forbidden, Invalid or Conflict.</returns>
    public async Task<OperationResult<CatalogSummary>> CreateCatalogAsync(string name, CancellationToken cancellationToken)
    {
        if (users.User is not { IsOrganizationAdmin: true })
        {
            return OperationResult.Fail<CatalogSummary>(OperationStatus.Forbidden, "Only organization administrators can create catalogs.");
        }

        Catalog catalog;
        try
        {
            catalog = Catalog.Create(organization.Id, name, time.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return OperationResult.Fail<CatalogSummary>(OperationStatus.Invalid, ex.Message);
        }

        if (await store.NameExistsAsync(organization.Id, catalog.Name, cancellationToken))
        {
            return OperationResult.Fail<CatalogSummary>(OperationStatus.Conflict, "A catalog with this name already exists.");
        }

        store.Add(catalog);
        audit.Record(new AuditEntry(AuditActions.CatalogCreate, users.User.Subject, organization.Id, catalog.Id, catalog.Id));
        await store.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok<CatalogSummary>(new CatalogSummary(catalog.Id, catalog.Name, catalog.CreatedAt, "admin"));
    }

    /// <summary>Grants or changes a user's role in a catalog. Organization administrators only.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="subject">Identity-provider subject of the user.</param>
    /// <param name="role">Role to grant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The membership, or Forbidden, NotFound or Invalid.</returns>
    public async Task<OperationResult<CatalogMember>> SetMemberAsync(Guid catalogId, string subject, CatalogRole role, CancellationToken cancellationToken)
    {
        if (users.User is not { IsOrganizationAdmin: true })
        {
            return OperationResult.Fail<CatalogMember>(OperationStatus.Forbidden, "Only organization administrators can manage roles.");
        }

        var catalog = await store.FindAsync(organization.Id, catalogId, cancellationToken);
        if (catalog is null)
        {
            return OperationResult.Fail<CatalogMember>(OperationStatus.NotFound, "Catalog not found.");
        }

        var now = time.GetUtcNow();
        var membership = await store.FindMembershipAsync(catalog, subject, cancellationToken);
        try
        {
            if (membership is null)
            {
                membership = CatalogMembership.Grant(catalog, subject, role, now);
                store.Add(membership);
            }
            else
            {
                membership.ChangeRole(role, now);
            }
        }
        catch (ArgumentException ex)
        {
            return OperationResult.Fail<CatalogMember>(OperationStatus.Invalid, ex.Message);
        }

        audit.Record(new AuditEntry(AuditActions.MemberGrant, users.User.Subject, organization.Id, catalog.Id, Detail: $"subject={subject}; role={ToMember(membership).Role}"));
        await store.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok<CatalogMember>(ToMember(membership));
    }

    /// <summary>Removes a user's role in a catalog. Organization administrators only.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="subject">Identity-provider subject of the user.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Ok when removed, or Forbidden or NotFound.</returns>
    public async Task<OperationResult<bool>> RemoveMemberAsync(Guid catalogId, string subject, CancellationToken cancellationToken)
    {
        if (users.User is not { IsOrganizationAdmin: true })
        {
            return OperationResult.Fail<bool>(OperationStatus.Forbidden, "Only organization administrators can manage roles.");
        }

        var catalog = await store.FindAsync(organization.Id, catalogId, cancellationToken);
        var membership = catalog is null ? null : await store.FindMembershipAsync(catalog, subject, cancellationToken);
        if (membership is null)
        {
            return OperationResult.Fail<bool>(OperationStatus.NotFound, "Membership not found.");
        }

        store.Remove(membership);
        audit.Record(new AuditEntry(AuditActions.MemberRevoke, users.User.Subject, organization.Id, catalogId, Detail: $"subject={subject}"));
        await store.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok<bool>(true);
    }

    private static CatalogMember ToMember(CatalogMembership membership) =>
        new(membership.Subject, membership.Role == CatalogRole.Editor ? "editor" : "viewer", membership.GrantedAt);
}
