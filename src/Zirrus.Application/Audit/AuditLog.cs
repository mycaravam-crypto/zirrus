namespace Zirrus.Application.Audit;

/// <summary>
/// One audit record (security.md, W7): who did what to which resource, with what outcome. Time and
/// correlation ID are added by the log.
/// </summary>
/// <param name="Action">Stable action name, e.g. <c>asset.upload</c> (see <see cref="AuditActions"/>).</param>
/// <param name="Actor">Authenticated subject.</param>
/// <param name="OrganizationId">Organization.</param>
/// <param name="CatalogId">Catalog, when the action concerns one.</param>
/// <param name="ResourceId">Affected resource (asset, category, schema …), when there is one.</param>
/// <param name="Detail">Short non-secret context, e.g. a granted role; never metadata values or tokens.</param>
/// <param name="Outcome">Outcome; <c>success</c> unless stated.</param>
public sealed record AuditEntry(string Action, string Actor, Guid OrganizationId, Guid? CatalogId = null, Guid? ResourceId = null, string? Detail = null, string Outcome = "success");

/// <summary>
/// Append-only audit log. <see cref="Record"/> stages the entry in the current unit of work, so it is
/// committed in the same transaction as the change it describes, or not at all.
/// </summary>
public interface IAuditLog
{
    /// <summary>Stages an entry; the caller's next save commits it together with the change.</summary>
    /// <param name="entry">The entry.</param>
    void Record(AuditEntry entry);
}

/// <summary>Action names recorded in the audit log.</summary>
public static class AuditActions
{
    /// <summary>A user signed in.</summary>
    public const string SignIn = "session.sign_in";

    /// <summary>A user signed out.</summary>
    public const string SignOut = "session.sign_out";

    /// <summary>An authenticated request was denied access to an API resource.</summary>
    public const string AccessDenied = "access.denied";

    /// <summary>A file was uploaded into quarantine.</summary>
    public const string Upload = "asset.upload";

    /// <summary>An asset's metadata was replaced.</summary>
    public const string MetadataUpdate = "asset.metadata_update";

    /// <summary>A failed or stuck asset was requeued.</summary>
    public const string Retry = "asset.retry";

    /// <summary>An asset was soft-deleted.</summary>
    public const string SoftDelete = "asset.soft_delete";

    /// <summary>A soft-deleted asset was purged after its retention period: blobs, renditions and category links removed.</summary>
    public const string Purge = "asset.purge";

    /// <summary>A catalog was created.</summary>
    public const string CatalogCreate = "catalog.create";

    /// <summary>A role was granted or changed in a catalog.</summary>
    public const string MemberGrant = "catalog.member_grant";

    /// <summary>A role was removed from a catalog.</summary>
    public const string MemberRevoke = "catalog.member_revoke";

    /// <summary>A metadata schema version was created.</summary>
    public const string SchemaCreate = "schema.create";

    /// <summary>A category was created.</summary>
    public const string CategoryCreate = "category.create";

    /// <summary>An asset was assigned to a category.</summary>
    public const string CategoryAssign = "category.assign";

    /// <summary>An asset was removed from a category.</summary>
    public const string CategoryUnassign = "category.unassign";
}
