using Microsoft.EntityFrameworkCore;
using Zirrus.Application.Ingestion;
using Zirrus.Domain.Assets;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Infrastructure.Ingestion;

/// <summary>
/// EF Core implementation of <see cref="IIngestionStore"/>. Job intents are inserted with
/// <c>ON CONFLICT (job_key) DO NOTHING</c>, so re-dispatching a stage never duplicates its job.
/// </summary>
/// <param name="db">Database context (one per job or request).</param>
internal sealed class IngestionStore(ZirrusDbContext db) : IIngestionStore
{
    private readonly List<JobIntent> _jobs = [];
    private readonly List<Rendition> _renditions = [];

    /// <inheritdoc />
    public Task<Asset?> FindAssetAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
        db.Assets.SingleOrDefaultAsync(a => a.CatalogId == catalogId && a.Id == assetId, cancellationToken);

    /// <inheritdoc />
    public void Add(Asset asset) => db.Assets.Add(asset);

    /// <inheritdoc />
    public void Add(JobIntent job) => _jobs.Add(job);

    /// <inheritdoc />
    public void AddOrReplace(Rendition rendition) => _renditions.Add(rendition);

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await FlushAsync(cancellationToken);
        var completed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE jobs SET state = 'Done', completed_at = now(), lease_token = NULL, lease_until = NULL
            WHERE id = {job.Id} AND lease_token = {job.LeaseToken} AND state = 'Running'
            """,
            cancellationToken);
        if (completed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> FindDuplicatesAsync(Guid catalogId, string sha256, Guid exceptAssetId, CancellationToken cancellationToken) =>
        await db.Assets.AsNoTracking()
            .Where(a => a.CatalogId == catalogId && a.Sha256 == sha256 && a.Id != exceptAssetId
                        && a.State != AssetState.Deleting && a.State != AssetState.Deleted)
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.Id)
            .Take(20)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> RequeueDeadJobAsync(string jobKey, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE jobs SET state = 'Ready', attempts = 0, run_after = now(), last_error = NULL, locked_by = NULL
            WHERE job_key = {jobKey} AND state = 'Dead'
            """,
            cancellationToken) == 1;

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var rendition in _renditions)
        {
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM renditions WHERE asset_id = {rendition.AssetId} AND kind = {rendition.Kind}", cancellationToken);
            db.Renditions.Add(rendition);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var job in _jobs)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO jobs (job_key, stage, catalog_id, asset_id, payload, state, attempts, max_attempts, run_after, created_at)
                VALUES ({job.JobKey}, {job.Stage}, {job.CatalogId}, {job.AssetId}, {job.Payload}::jsonb, 'Ready', 0, {job.MaxAttempts}, {job.RunAfter}, {job.CreatedAt})
                ON CONFLICT (job_key) DO NOTHING
                """,
                cancellationToken);
        }

        _jobs.Clear();
        _renditions.Clear();
    }
}
