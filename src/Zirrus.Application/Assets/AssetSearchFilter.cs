using Zirrus.Domain.Assets;

namespace Zirrus.Application.Assets;

/// <summary>Bounded search criteria for the catalog asset list.</summary>
/// <param name="Text">Optional full-text search term, at most 200 characters.</param>
/// <param name="CategoryId">Optional exact category identifier.</param>
/// <param name="MetadataJson">Optional JSON object containment filter, at most 4096 characters.</param>
public sealed record AssetSearchFilter(string? Text, Guid? CategoryId, string? MetadataJson)
{
    /// <summary>Whether this is a search rather than an unfiltered status list.</summary>
    public bool HasCriteria => !string.IsNullOrEmpty(Text) || CategoryId is not null || MetadataJson is not null;

    /// <summary>Checks bounded inputs before the database query.</summary>
    /// <returns>True when every supplied criterion is valid.</returns>
    public bool IsValid()
    {
        if (Text is { Length: > 200 } || (Text is not null && (string.IsNullOrWhiteSpace(Text) || Text.Contains('\0')))
            || CategoryId == Guid.Empty || MetadataJson is { Length: > 4096 })
        {
            return false;
        }

        if (MetadataJson is null)
        {
            return true;
        }

        try
        {
            AssetMetadata.Parse(MetadataJson);
            return !MetadataJson.Contains('\0');
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
