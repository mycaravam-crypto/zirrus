using Zirrus.Application.Identity;
using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Application.Catalogs;

/// <summary>Catalog schema persistence port.</summary>
public interface ISchemaStore
{
    /// <summary>Finds the catalog within the organization.</summary>
    Task<Catalog?> FindCatalogAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken);

    /// <summary>Reads the latest schema, including its fields.</summary>
    Task<MetadataSchema?> FindLatestAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken);

    /// <summary>Inserts a schema; returns false when its version already exists.</summary>
    Task<bool> TryAddAsync(MetadataSchema schema, CancellationToken cancellationToken);
}

/// <summary>Public schema representation.</summary>
/// <param name="Version">Immutable schema version.</param>
/// <param name="Fields">Fields in display order.</param>
public sealed record SchemaDetails(int Version, IReadOnlyList<MetadataFieldDefinition> Fields)
{
    /// <summary>Maps a persisted schema to a read model.</summary>
    public static SchemaDetails From(MetadataSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new(schema.Version, schema.Fields.OrderBy(f => f.Position)
            .Select(f => new MetadataFieldDefinition(f.Key, f.Label, f.Type, f.Required, f.Multiple)).ToList());
    }
}

/// <summary>Catalog-scoped schema reads and administrator-only initial schema creation.</summary>
/// <param name="store">Schema persistence.</param>
/// <param name="access">Catalog authorization.</param>
/// <param name="users">Current user.</param>
/// <param name="organization">Organization context.</param>
/// <param name="time">Time source.</param>
public sealed class SchemaAdministration(ISchemaStore store, CatalogAccess access, ICurrentUserAccessor users,
    OrganizationContext organization, TimeProvider time)
{
    /// <summary>Reads the current schema for a visible catalog; version zero denotes no schema.</summary>
    public async Task<OperationResult<SchemaDetails>> GetAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        if (!await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationResult.Fail<SchemaDetails>(OperationStatus.NotFound, "Catalog not found.");
        }

        var schema = await store.FindLatestAsync(organization.Id, catalogId, cancellationToken);
        return OperationResult.Ok(schema is null ? new SchemaDetails(0, []) : SchemaDetails.From(schema));
    }

    /// <summary>Creates the first schema. Subsequent revisions require an explicit migration policy.</summary>
    public async Task<OperationResult<SchemaDetails>> CreateAsync(Guid catalogId, IReadOnlyList<MetadataFieldDefinition>? fields,
        CancellationToken cancellationToken)
    {
        if (users.User is not { IsOrganizationAdmin: true })
        {
            return OperationResult.Fail<SchemaDetails>(OperationStatus.Forbidden, "Only organization administrators can manage schemas.");
        }

        var catalog = await store.FindCatalogAsync(organization.Id, catalogId, cancellationToken);
        if (catalog is null)
        {
            return OperationResult.Fail<SchemaDetails>(OperationStatus.NotFound, "Catalog not found.");
        }

        if (fields is null || fields.Any(f => f is null))
        {
            return OperationResult.Fail<SchemaDetails>(OperationStatus.Invalid, "Field definitions are required.");
        }

        if (await store.FindLatestAsync(organization.Id, catalogId, cancellationToken) is not null)
        {
            return OperationResult.Fail<SchemaDetails>(OperationStatus.Conflict, "A schema already exists; schema evolution requires migration.");
        }

        MetadataSchema schema;
        try
        {
            schema = MetadataSchema.Create(catalog, 1, fields, time.GetUtcNow());
        }
        catch (ArgumentException ex)
        {
            return OperationResult.Fail<SchemaDetails>(OperationStatus.Invalid, ex.Message);
        }

        return await store.TryAddAsync(schema, cancellationToken)
            ? OperationResult.Ok(SchemaDetails.From(schema))
            : OperationResult.Fail<SchemaDetails>(OperationStatus.Conflict, "A schema already exists.");
    }
}
