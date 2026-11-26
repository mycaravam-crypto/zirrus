using System.Text.Json;

namespace Zirrus.Domain.Assets;

/// <summary>
/// User-editable descriptive metadata: a JSON object stored as JSONB. Field keys are stable
/// identifiers; type validation against the catalog's metadata schema follows in W5.
/// </summary>
public sealed record AssetMetadata
{
    /// <summary>Maximum serialized size in bytes.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Maximum JSON nesting depth.</summary>
    public const int MaxDepth = 8;

    private AssetMetadata(string json) => Json = json;

    /// <summary>The empty object.</summary>
    public static AssetMetadata Empty { get; } = new("{}");

    /// <summary>Compact JSON text of the object.</summary>
    public string Json { get; }

    /// <summary>Parses and normalizes metadata.</summary>
    /// <param name="json">JSON text.</param>
    /// <returns>The metadata.</returns>
    /// <exception cref="ArgumentException">The text is not a JSON object, is too large or deep, or has an invalid field key.</exception>
    public static AssetMetadata Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw new ArgumentException($"Metadata exceeds {MaxBytes} bytes.", nameof(json));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"Metadata is not valid JSON: {e.Message}", nameof(json), e);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Metadata must be a JSON object.", nameof(json));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!MetadataField.IsValidKey(property.Name))
                {
                    throw new ArgumentException($"Invalid field key '{property.Name}': use a lowercase letter, then lowercase letters, digits or '_' (max {MetadataField.MaxKeyLength}).", nameof(json));
                }

                if (!seen.Add(property.Name))
                {
                    throw new ArgumentException($"Duplicate field key '{property.Name}'.", nameof(json));
                }
            }

            return new AssetMetadata(JsonSerializer.Serialize(document.RootElement));
        }
    }
}
