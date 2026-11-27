using System.Globalization;
using System.Text.Json;

namespace Zirrus.Domain.Assets;

/// <summary>Validates descriptive metadata against a catalog's current immutable schema.</summary>
public static class MetadataValidation
{
    /// <summary>Checks known field keys, required values, types and cardinality.</summary>
    /// <param name="metadata">Normalized metadata object.</param>
    /// <param name="schema">Current catalog schema.</param>
    /// <exception cref="ArgumentException">A field is missing, unknown or invalid.</exception>
    public static void Validate(AssetMetadata metadata, MetadataSchema schema)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(schema);

        using var document = JsonDocument.Parse(metadata.Json);
        var fields = schema.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!fields.TryGetValue(property.Name, out var field))
            {
                throw new ArgumentException($"Unknown metadata field '{property.Name}'.", nameof(metadata));
            }

            var value = property.Value;
            if (field.Multiple)
            {
                if (value.ValueKind != JsonValueKind.Array
                    || (field.Required && value.GetArrayLength() == 0)
                    || value.EnumerateArray().Any(item => !Matches(field.Type, item)))
                {
                    throw new ArgumentException($"Field '{field.Key}' requires an array of valid values.", nameof(metadata));
                }
            }
            else if (!Matches(field.Type, value))
            {
                throw new ArgumentException($"Field '{field.Key}' has an invalid value.", nameof(metadata));
            }
        }

        foreach (var field in schema.Fields)
        {
            if (field.Required && !document.RootElement.TryGetProperty(field.Key, out _))
            {
                throw new ArgumentException($"Required metadata field '{field.Key}' is missing.", nameof(metadata));
            }
        }
    }

    private static bool Matches(MetadataFieldType type, JsonElement value) => type switch
    {
        MetadataFieldType.Text or MetadataFieldType.Keyword =>
            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()),
        MetadataFieldType.Number => value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number),
        MetadataFieldType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        MetadataFieldType.Date => value.ValueKind == JsonValueKind.String
            && (DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                || (value.TryGetDateTimeOffset(out _) && HasExplicitOffset(value.GetString()!))),
        _ => false,
    };

    private static bool HasExplicitOffset(string text) =>
        text.Contains('T', StringComparison.Ordinal)
        && (text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10);
}
