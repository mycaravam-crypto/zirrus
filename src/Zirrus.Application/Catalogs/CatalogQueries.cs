namespace Zirrus.Application.Catalogs;

/// <summary>Read model returned by catalog queries.</summary>
/// <param name="Id">Catalog identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
public sealed record CatalogSummary(Guid Id, string Name, DateTimeOffset CreatedAt);

/// <summary>Port for reading catalogs; implemented by the infrastructure layer.</summary>
public interface ICatalogReader
{
    /// <summary>Lists catalogs ordered by name.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The catalogs visible to the caller.</returns>
    Task<IReadOnlyList<CatalogSummary>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Use case: list catalogs. Authorization filtering is added with catalog permissions (W2, #22).
/// </summary>
/// <param name="reader">Catalog read port.</param>
public sealed class ListCatalogs(ICatalogReader reader)
{
    /// <summary>Runs the query.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Catalog summaries ordered by name.</returns>
    public Task<IReadOnlyList<CatalogSummary>> ExecuteAsync(CancellationToken cancellationToken) =>
        reader.ListAsync(cancellationToken);
}
