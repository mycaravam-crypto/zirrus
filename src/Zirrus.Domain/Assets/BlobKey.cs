using System.Diagnostics.CodeAnalysis;

namespace Zirrus.Domain.Assets;

/// <summary>
/// Opaque storage key (ADR 0006). Keys are generated from identifiers, never from user input or
/// file names, so they cannot traverse paths or collide across catalogs.
/// </summary>
public readonly record struct BlobKey
{
    /// <summary>Maximum key length.</summary>
    public const int MaxLength = 200;

    private BlobKey(string value) => Value = value;

    /// <summary>The key, e.g. <c>originals/{catalog}/{asset}</c>.</summary>
    public string Value { get; }

    /// <summary>Key of an asset's original file.</summary>
    /// <param name="catalogId">Owning catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <returns>The key.</returns>
    public static BlobKey ForOriginal(Guid catalogId, Guid assetId) => new($"originals/{catalogId:N}/{assetId:N}");

    /// <summary>Key of a rendition, unique per asset, kind and pipeline version.</summary>
    /// <param name="catalogId">Owning catalog.</param>
    /// <param name="assetId">Asset.</param>
    /// <param name="kind">Rendition kind, e.g. <c>thumbnail</c>.</param>
    /// <param name="pipelineVersion">Rendering pipeline version.</param>
    /// <returns>The key.</returns>
    public static BlobKey ForRendition(Guid catalogId, Guid assetId, string kind, int pipelineVersion)
    {
        Rendition.ValidateKind(kind);
        return new($"renditions/{catalogId:N}/{assetId:N}/{kind}-v{pipelineVersion}");
    }

    /// <summary>Parses a stored key, accepting only the generated alphabet.</summary>
    /// <param name="value">Key text.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentException">The key contains characters outside <c>[a-z0-9/-]</c>, empty segments or is too long.</exception>
    public static BlobKey Parse(string value)
    {
        if (!TryParse(value, out var key))
        {
            throw new ArgumentException("Invalid storage key.", nameof(value));
        }

        return key;
    }

    /// <summary>Parses a stored key without throwing.</summary>
    /// <param name="value">Key text.</param>
    /// <param name="key">The key when valid.</param>
    /// <returns>Whether the key is valid.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out BlobKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value[0] == '/' || value[^1] == '/' || value.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '/' or '-'))
            {
                return false;
            }
        }

        key = new BlobKey(value);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
