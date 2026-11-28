namespace Zirrus.Domain.Assets;

/// <summary>Lifecycle states of an asset (ADR 0006). Only <see cref="Active"/> assets are served to viewers.</summary>
public enum AssetState
{
    /// <summary>The original is being written to storage; no checksum yet.</summary>
    Uploading = 0,

    /// <summary>The original is stored and checksummed, waiting for scanning and processing.</summary>
    Quarantined = 1,

    /// <summary>Scanning, metadata extraction or rendering is running.</summary>
    Processing = 2,

    /// <summary>Processed and available to authorized users.</summary>
    Active = 3,

    /// <summary>Rejected by validation or malware scanning; never trusted or served.</summary>
    Rejected = 4,

    /// <summary>Processing failed; editors may retry. Never served.</summary>
    Failed = 5,

    /// <summary>Soft-deleted: access is denied immediately; blobs are purged later.</summary>
    Deleting = 6,

    /// <summary>Blobs are purged; the row remains for audit.</summary>
    Deleted = 7,
}

/// <summary>The allowed lifecycle transitions.</summary>
public static class AssetLifecycle
{
    private static readonly Dictionary<AssetState, AssetState[]> Allowed = new()
    {
        [AssetState.Uploading] = [AssetState.Quarantined, AssetState.Failed],
        [AssetState.Quarantined] = [AssetState.Processing, AssetState.Rejected, AssetState.Deleting],
        [AssetState.Processing] = [AssetState.Active, AssetState.Rejected, AssetState.Failed],
        [AssetState.Active] = [AssetState.Deleting],
        [AssetState.Rejected] = [AssetState.Deleting],
        [AssetState.Failed] = [AssetState.Processing, AssetState.Deleting],
        [AssetState.Deleting] = [AssetState.Deleted],
        [AssetState.Deleted] = [],
    };

    /// <summary>True when an asset may move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <param name="from">Current state.</param>
    /// <param name="to">Requested state.</param>
    /// <returns>Whether the transition is allowed.</returns>
    public static bool CanTransition(AssetState from, AssetState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>States in which the original blob must exist in storage (reconciliation).</summary>
    public static IReadOnlyList<AssetState> StatesWithOriginal { get; } =
        [AssetState.Quarantined, AssetState.Processing, AssetState.Active, AssetState.Rejected, AssetState.Failed];

    /// <summary>
    /// Transitional states that should not last long; older rows are reported as stale. Soft-deleted
    /// (<see cref="AssetState.Deleting"/>) assets are excluded: they stay for the retention period and the
    /// purge job reports the ones it cannot remove.
    /// </summary>
    public static IReadOnlyList<AssetState> TransitionalStates { get; } =
        [AssetState.Uploading, AssetState.Processing];
}
