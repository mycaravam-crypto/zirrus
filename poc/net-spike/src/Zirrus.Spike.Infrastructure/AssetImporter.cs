using System.Text.Json;
using Zirrus.Spike.Domain;

namespace Zirrus.Spike.Infrastructure;

/// <summary>Import use case: stores the asset record and its job intent in one transaction.</summary>
/// <param name="db">EF Core context.</param>
public sealed class AssetImporter(SpikeDbContext db)
{
    /// <summary>Pipeline version, part of every deterministic job key.</summary>
    public const string PipelineVersion = "p1";

    /// <summary>Creates a quarantined asset and its "render" job intent atomically.</summary>
    /// <param name="catalogId">Owning catalog.</param>
    /// <param name="sha256">Checksum of the original.</param>
    /// <param name="jobPayload">Payload for the render stage.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The created asset.</returns>
    public async Task<Asset> ImportAsync(Guid catalogId, string sha256, object jobPayload, CancellationToken cancellationToken)
    {
        var asset = new Asset { CatalogId = catalogId, Sha256 = sha256 };
        db.Assets.Add(asset);
        db.Jobs.Add(new JobRecord
        {
            JobKey = $"{asset.Id}:{PipelineVersion}:render",
            Payload = JsonSerializer.Serialize(jobPayload),
        });

        // One SaveChanges = one transaction: either both rows exist or neither does.
        await db.SaveChangesAsync(cancellationToken);
        return asset;
    }
}
