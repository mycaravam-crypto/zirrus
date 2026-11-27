using Zirrus.Application.Audit;

namespace Zirrus.Domain.Tests;

/// <summary>Collects staged audit entries for assertions.</summary>
internal sealed class RecordingAudit : IAuditLog
{
    /// <summary>Entries in the order they were recorded.</summary>
    public List<AuditEntry> Entries { get; } = [];

    /// <inheritdoc />
    public void Record(AuditEntry entry) => Entries.Add(entry);
}
