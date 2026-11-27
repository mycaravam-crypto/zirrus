using Zirrus.Application.Audit;
using Zirrus.Application.Catalogs;
using Zirrus.Application.Identity;
using Zirrus.Domain.Assets;

namespace Zirrus.Application.Assets;

/// <summary>Persists a soft deletion and its audit event in the same transaction.</summary>
public interface IAssetDeletionStore
{
    /// <summary>Loads the tracked asset and atomically records its deletion.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="expectedRevision">Client revision.</param>
    /// <param name="actor">Authenticated subject.</param>
    /// <param name="correlationId">Request correlation identifier.</param>
    /// <param name="now">UTC time.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Outcome of the operation.</returns>
    Task<OperationStatus> DeleteAsync(Guid catalogId, Guid assetId, int expectedRevision, string actor,
        string correlationId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Authorizes soft deletion before changing lifecycle state.</summary>
/// <param name="store">Transactional persistence.</param>
/// <param name="access">Catalog authorization.</param>
/// <param name="users">Authenticated identity.</param>
/// <param name="time">Time source.</param>
/// <param name="audit">Audit log; entries are committed with the change they describe.</param>
public sealed class DeleteAsset(IAssetDeletionStore store, CatalogAccess access, ICurrentUserAccessor users, TimeProvider time, IAuditLog audit)
{
    /// <summary>Soft-deletes an asset using optimistic concurrency.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="expectedRevision">Client revision.</param>
    /// <param name="correlationId">Request correlation identifier.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Outcome; no physical blobs are removed.</returns>
    public async Task<OperationStatus> ExecuteAsync(Guid catalogId, Guid assetId, int expectedRevision,
        string correlationId, CancellationToken cancellationToken)
    {
        if (users.User is not { } user || !await access.CanReadAsync(catalogId, cancellationToken))
        {
            return OperationStatus.NotFound;
        }

        if (!await access.CanEditAsync(catalogId, cancellationToken))
        {
            return OperationStatus.Forbidden;
        }

        audit.Record(new AuditEntry(AuditActions.SoftDelete, user.Subject, access.OrganizationId, catalogId, assetId, $"revision={expectedRevision}"));
        return await store.DeleteAsync(catalogId, assetId, expectedRevision, user.Subject, correlationId,
            time.GetUtcNow(), cancellationToken);
    }
}
