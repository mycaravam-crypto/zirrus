using System.Diagnostics;
using Zirrus.Application.Audit;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Infrastructure.Audit;

/// <summary>
/// A row of the append-only <c>audit_events</c> table. The table is SQL-managed (AuditHardening
/// migration): the application role may insert and read but never update or delete, and a trigger
/// rejects changes from every other role too, except the retention purge.
/// </summary>
public sealed class AuditEvent
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Insertion order; digests cover contiguous ranges of it.</summary>
    public long Seq { get; init; }

    /// <summary>Organization.</summary>
    public Guid OrganizationId { get; init; }

    /// <summary>Catalog, when the action concerns one.</summary>
    public Guid? CatalogId { get; init; }

    /// <summary>Affected resource, when there is one.</summary>
    public Guid? ResourceId { get; init; }

    /// <summary>Authenticated subject.</summary>
    public required string Actor { get; init; }

    /// <summary>Action name (<see cref="AuditActions"/>).</summary>
    public required string Action { get; init; }

    /// <summary>Outcome.</summary>
    public required string Outcome { get; init; }

    /// <summary>Time of the action (UTC).</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Request correlation (trace) ID.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Short non-secret context.</summary>
    public string? Detail { get; init; }
}

/// <summary>A sealed, signed digest of a contiguous range of audit events (<c>audit_digests</c>, append-only).</summary>
public sealed class AuditDigest
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>First event sequence number covered.</summary>
    public long FromSeq { get; init; }

    /// <summary>Last event sequence number covered.</summary>
    public long ToSeq { get; init; }

    /// <summary>Number of events covered.</summary>
    public int EventCount { get; init; }

    /// <summary>SHA-256 (hex) over the canonical event lines, chained to the previous digest.</summary>
    public required string Sha256 { get; init; }

    /// <summary>HMAC-SHA256 (hex) of <see cref="Sha256"/> with the digest key.</summary>
    public required string Hmac { get; init; }

    /// <summary>SHA-256 of the previous digest, or 64 zeros for the first.</summary>
    public required string PreviousSha256 { get; init; }

    /// <summary>Identifies the key (first 8 hex digits of its SHA-256), never the key itself.</summary>
    public required string KeyId { get; init; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// <see cref="IAuditLog"/> on the request's <see cref="ZirrusDbContext"/>: entries are added to the
/// change tracker and committed by the same SaveChanges as the change they describe.
/// </summary>
/// <param name="db">The request's database context.</param>
/// <param name="time">Time source.</param>
internal sealed class AuditLog(ZirrusDbContext db, TimeProvider time) : IAuditLog
{
    /// <inheritdoc />
    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = entry.OrganizationId,
            CatalogId = entry.CatalogId,
            ResourceId = entry.ResourceId,
            Actor = Truncate(entry.Actor, 255),
            Action = entry.Action,
            Outcome = entry.Outcome,
            OccurredAt = time.GetUtcNow(),
            CorrelationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            Detail = entry.Detail is null ? null : Truncate(entry.Detail, 500),
        });
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>
/// Records sign-in and sign-out, which have no other change to commit with: records and saves at once.
/// </summary>
/// <param name="audit">Audit log.</param>
/// <param name="db">Database context.</param>
/// <param name="organization">Organization context.</param>
public sealed class SessionAudit(IAuditLog audit, ZirrusDbContext db, Zirrus.Application.Identity.OrganizationContext organization)
{
    /// <summary>Records a session event for a subject.</summary>
    /// <param name="action"><see cref="AuditActions.SignIn"/> or <see cref="AuditActions.SignOut"/>.</param>
    /// <param name="subject">Authenticated subject.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    public async Task RecordAsync(string action, string subject, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry(action, subject, organization.Id));
        await db.SaveChangesAsync(cancellationToken);
    }
}
