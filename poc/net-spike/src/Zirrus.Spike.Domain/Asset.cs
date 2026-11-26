using System.Text.Json;

namespace Zirrus.Spike.Domain;

/// <summary>Lifecycle states of an asset; only <see cref="Active"/> assets are served.</summary>
public enum AssetState
{
    /// <summary>Stored in quarantine, waiting for processing.</summary>
    Quarantined,
    /// <summary>Processed and available to authorized users.</summary>
    Active,
    /// <summary>Rejected by validation or malware scanning.</summary>
    Rejected,
}

/// <summary>A digital asset in a catalog, with its integrity data and typed metadata.</summary>
public sealed class Asset
{
    /// <summary>Opaque asset identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Catalog that owns the asset; part of every authorization check.</summary>
    public Guid CatalogId { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public AssetState State { get; set; } = AssetState.Quarantined;

    /// <summary>Hex-encoded SHA-256 of the original file.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Custom descriptive metadata, stored as JSONB.</summary>
    public JsonDocument Metadata { get; set; } = JsonDocument.Parse("{}");

    /// <summary>Optimistic concurrency revision, mapped to If-Match/ETag in the API.</summary>
    public int Revision { get; set; }
}
