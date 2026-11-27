using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Zirrus.Application.Assets;
using Zirrus.Application.Storage;
using Zirrus.Domain.Assets;

namespace Zirrus.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IAssetStore"/>.</summary>
/// <param name="db">Database context.</param>
internal sealed class AssetStore(ZirrusDbContext db) : IAssetStore
{
    /// <inheritdoc />
    public Task<Asset?> FindAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
        db.Assets.SingleOrDefaultAsync(a => a.CatalogId == catalogId && a.Id == assetId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetSummary>> ListAsync(Guid catalogId, bool activeOnly, AssetListPosition? after, int limit, CancellationToken cancellationToken, AssetSearchFilter? filter = null)
    {
        var query = Scoped(catalogId, activeOnly, filter);
        if (after is { } position)
        {
            // Keyset pagination on (created_at, id), newest first; stable while new uploads arrive.
            query = query.Where(a => a.CreatedAt < position.CreatedAt || (a.CreatedAt == position.CreatedAt && a.Id.CompareTo(position.Id) < 0));
        }

        var rows = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(limit)
            .Select(a => new { a.Id, a.FileName, a.MediaType, a.SizeBytes, a.State, a.StatusReason, a.CreatedAt, a.UpdatedAt })
            .ToListAsync(cancellationToken);
        return rows.Select(a => new AssetSummary(a.Id, a.FileName, a.MediaType, a.SizeBytes, a.State.ToString().ToLowerInvariant(), a.StatusReason, a.CreatedAt, a.UpdatedAt)).ToList();
    }

    /// <inheritdoc />
    public async Task<AssetFacets> FacetsAsync(Guid catalogId, AssetSearchFilter filter, CancellationToken cancellationToken)
    {
        // Same scope as the result list (catalog, active only, filters), so counts never include assets
        // the caller could not page to.
        var matching = Scoped(catalogId, activeOnly: true, filter);
        var total = await matching.CountAsync(cancellationToken);
        var counts = await db.AssetCategories.AsNoTracking()
            .Where(c => c.CatalogId == catalogId && matching.Any(a => a.Id == c.AssetId))
            .GroupBy(c => c.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).ThenBy(g => g.CategoryId).Take(AssetFacets.MaxCategories)
            .ToListAsync(cancellationToken);
        var ids = counts.Select(c => c.CategoryId).ToList();
        var names = await db.Categories.AsNoTracking()
            .Where(c => c.CatalogId == catalogId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var categories = counts.Where(c => names.ContainsKey(c.CategoryId))
            .Select(c => new CategoryFacet(c.CategoryId, names[c.CategoryId], c.Count))
            .OrderByDescending(f => f.Count).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        return new AssetFacets(total, categories);
    }

    /// <summary>Assets of one catalog visible in lists: never deleting or deleted; filters as given.</summary>
    /// <param name="catalogId">Catalog.</param>
    /// <param name="activeOnly">Only active assets (viewers and every filtered search).</param>
    /// <param name="filter">Optional search criteria.</param>
    /// <returns>The composable query.</returns>
    private IQueryable<Asset> Scoped(Guid catalogId, bool activeOnly, AssetSearchFilter? filter)
    {
        var query = db.Assets.AsNoTracking().Where(a => a.CatalogId == catalogId && a.State != AssetState.Deleting && a.State != AssetState.Deleted);
        if (activeOnly)
        {
            query = query.Where(a => a.State == AssetState.Active);
        }

        if (filter?.Text is { Length: > 0 } text)
        {
            // Fixed configurations and parameterized query text; no user-controlled SQL identifiers. File
            // names are split on . _ - first, so "harbour" finds harbour-at-dusk.jpg (the parser would keep
            // the whole name as one file token). The expressions must match the GIN indexes (SearchIndexes,
            // SearchFileNameWords migrations).
            var matchingIds = db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM assets
                WHERE catalog_id = {catalogId} AND state = 'Active'
                  AND (
                    (to_tsvector('english', regexp_replace(file_name, '[._-]+', ' ', 'g')) || jsonb_to_tsvector('english', metadata, '["string"]'::jsonb)) @@ plainto_tsquery('english', {text})
                    OR (to_tsvector('german', regexp_replace(file_name, '[._-]+', ' ', 'g')) || jsonb_to_tsvector('german', metadata, '["string"]'::jsonb)) @@ plainto_tsquery('german', {text})
                    OR (to_tsvector('simple', regexp_replace(file_name, '[._-]+', ' ', 'g')) || jsonb_to_tsvector('simple', metadata, '["string"]'::jsonb)) @@ plainto_tsquery('simple', {text})
                  )
                """);
            query = query.Where(a => matchingIds.Contains(a.Id));
        }

        if (filter?.CategoryId is { } categoryId)
        {
            query = query.Where(a => db.AssetCategories.Any(c => c.CatalogId == a.CatalogId && c.AssetId == a.Id && c.CategoryId == categoryId));
        }

        if (filter?.MetadataJson is { } json)
        {
            query = query.Where(a => EF.Functions.JsonContains(a.Metadata, json));
        }

        return query;
    }

    /// <inheritdoc />
    public Task<MetadataSchema?> FindMetadataSchemaAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
        db.Set<MetadataSchema>().AsNoTracking().Include(s => s.Fields)
            .Where(s => s.OrganizationId == organizationId && s.CatalogId == catalogId)
            .OrderByDescending(s => s.Version).FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Rendition?> FindRenditionAsync(Guid assetId, string kind, CancellationToken cancellationToken) =>
        db.Renditions.AsNoTracking().SingleOrDefaultAsync(r => r.AssetId == assetId && r.Kind == kind, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}

/// <summary>Reads asset, rendition and job rows for reconciliation (no tracking, batched scans).</summary>
/// <param name="db">Database context.</param>
internal sealed class StorageInventory(ZirrusDbContext db) : IStorageInventory
{
    private const int BatchSize = 500;

    /// <inheritdoc />
    public async IAsyncEnumerable<ExpectedBlob> ListExpectedBlobsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var states = AssetLifecycle.StatesWithOriginal.ToArray();
        Guid? after = null;
        while (true)
        {
            // Keyset pagination by id keeps memory flat however many assets exist.
            var query = db.Assets.AsNoTracking().Where(a => states.Contains(a.State));
            if (after is { } last)
            {
                query = query.Where(a => a.Id.CompareTo(last) > 0);
            }

            var batch = await query.OrderBy(a => a.Id)
                .Select(a => new { a.Id, a.OriginalKey, a.SizeBytes, a.State })
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                yield break;
            }

            var ids = batch.Select(a => a.Id).ToList();
            var renditions = await db.Renditions.AsNoTracking()
                .Where(r => ids.Contains(r.AssetId))
                .Select(r => new { r.AssetId, r.StorageKey, r.SizeBytes, r.Kind })
                .ToListAsync(cancellationToken);
            var byAsset = renditions.ToLookup(r => r.AssetId);

            foreach (var asset in batch)
            {
                yield return new ExpectedBlob(asset.Id, BlobKey.Parse(asset.OriginalKey), asset.SizeBytes, "original", asset.State);
                foreach (var rendition in byAsset[asset.Id])
                {
                    yield return new ExpectedBlob(asset.Id, BlobKey.Parse(rendition.StorageKey), rendition.SizeBytes, rendition.Kind, asset.State);
                }
            }

            after = batch[^1].Id;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StaleAsset>> ListStaleAsync(DateTimeOffset changedBefore, int limit, CancellationToken cancellationToken)
    {
        var states = AssetLifecycle.TransitionalStates.ToArray();
        var rows = await db.Assets.AsNoTracking()
            .Where(a => states.Contains(a.State) && a.StateChangedAt < changedBefore)
            .OrderBy(a => a.StateChangedAt)
            .Take(limit)
            .Select(a => new { a.Id, a.State, a.StateChangedAt })
            .ToListAsync(cancellationToken);
        return rows.Select(a => new StaleAsset(a.Id, a.State, a.StateChangedAt)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiredLease>> ListExpiredLeasesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Running && j.LeaseUntil < now)
            .OrderBy(j => j.LeaseUntil)
            .Take(limit)
            .Select(j => new { j.JobKey, j.LockedBy, j.LeaseUntil })
            .ToListAsync(cancellationToken);
        return rows.Select(j => new ExpiredLease(j.JobKey, j.LockedBy, j.LeaseUntil!.Value)).ToList();
    }
}
