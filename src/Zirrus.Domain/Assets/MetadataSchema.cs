namespace Zirrus.Domain.Assets;

/// <summary>Value type of a metadata field.</summary>
public enum MetadataFieldType
{
    /// <summary>Free text.</summary>
    Text = 0,

    /// <summary>Short exact-match value, e.g. a tag.</summary>
    Keyword = 1,

    /// <summary>Number.</summary>
    Number = 2,

    /// <summary>Date or date-time (ISO 8601).</summary>
    Date = 3,

    /// <summary>True or false.</summary>
    Boolean = 4,
}

/// <summary>A typed field of a catalog's metadata schema.</summary>
public sealed class MetadataField
{
    /// <summary>Maximum length of a field key.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>Maximum length of a field label.</summary>
    public const int MaxLabelLength = 200;

    private MetadataField(Guid schemaId, string key, string label, MetadataFieldType type, bool required, bool multiple, int position)
    {
        SchemaId = schemaId;
        Key = key;
        Label = label;
        Type = type;
        Required = required;
        Multiple = multiple;
        Position = position;
    }

    /// <summary>Schema the field belongs to.</summary>
    public Guid SchemaId { get; private set; }

    /// <summary>Stable key used in asset metadata JSON.</summary>
    public string Key { get; private set; }

    /// <summary>Display label.</summary>
    public string Label { get; private set; }

    /// <summary>Value type.</summary>
    public MetadataFieldType Type { get; private set; }

    /// <summary>A value is required before an asset can be saved.</summary>
    public bool Required { get; private set; }

    /// <summary>The field holds a list of values.</summary>
    public bool Multiple { get; private set; }

    /// <summary>Display order.</summary>
    public int Position { get; private set; }

    /// <summary>True for a lowercase letter followed by up to 63 lowercase letters, digits or '_'.</summary>
    /// <param name="key">Candidate key.</param>
    /// <returns>Whether the key is valid.</returns>
    public static bool IsValidKey(string? key) =>
        key is { Length: > 0 and <= MaxKeyLength }
        && key[0] is >= 'a' and <= 'z'
        && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    internal static MetadataField Create(Guid schemaId, MetadataFieldDefinition definition, int position)
    {
        if (!IsValidKey(definition.Key))
        {
            throw new ArgumentException($"Invalid field key '{definition.Key}'.", nameof(definition));
        }

        var label = definition.Label?.Trim() ?? string.Empty;
        if (label.Length == 0 || label.Length > MaxLabelLength)
        {
            throw new ArgumentException($"Field label must be 1–{MaxLabelLength} characters.", nameof(definition));
        }

        if (!Enum.IsDefined(definition.Type))
        {
            throw new ArgumentException("Unknown field type.", nameof(definition));
        }

        return new MetadataField(schemaId, definition.Key, label, definition.Type, definition.Required, definition.Multiple, position);
    }
}

/// <summary>Input for one field of a new schema version.</summary>
/// <param name="Key">Stable key.</param>
/// <param name="Label">Display label.</param>
/// <param name="Type">Value type.</param>
/// <param name="Required">Whether a value is required.</param>
/// <param name="Multiple">Whether the field holds a list.</param>
public sealed record MetadataFieldDefinition(string Key, string Label, MetadataFieldType Type, bool Required = false, bool Multiple = false);

/// <summary>
/// A versioned set of typed fields for a catalog. Versions are immutable; a change creates the next
/// version, so schema migrations are explicit (data-storage-search.md).
/// </summary>
public sealed class MetadataSchema
{
    /// <summary>Maximum number of fields per schema.</summary>
    public const int MaxFields = 100;

    private readonly List<MetadataField> _fields = [];

    private MetadataSchema(Guid id, Guid organizationId, Guid catalogId, int version, DateTimeOffset createdAt)
    {
        Id = id;
        OrganizationId = organizationId;
        CatalogId = catalogId;
        Version = version;
        CreatedAt = createdAt;
    }

    /// <summary>Opaque identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning organization (composite reference to the catalog).</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Owning catalog.</summary>
    public Guid CatalogId { get; private set; }

    /// <summary>Version number, starting at 1 and unique per catalog.</summary>
    public int Version { get; private set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Fields in display order.</summary>
    public IReadOnlyList<MetadataField> Fields => _fields;

    /// <summary>Builds an immutable schema version from field definitions, validating keys, labels and types.</summary>
    /// <param name="catalog">Owning catalog.</param>
    /// <param name="version">Version number (≥ 1).</param>
    /// <param name="fields">Field definitions; keys must be unique.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The schema.</returns>
    /// <exception cref="ArgumentException">A field is invalid, keys repeat or there are too many fields.</exception>
    public static MetadataSchema Create(Catalogs.Catalog catalog, int version, IReadOnlyList<MetadataFieldDefinition> fields, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (fields.Count > MaxFields)
        {
            throw new ArgumentException($"A schema has at most {MaxFields} fields.", nameof(fields));
        }

        if (fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count() != fields.Count)
        {
            throw new ArgumentException("Field keys must be unique.", nameof(fields));
        }

        var schema = new MetadataSchema(Guid.NewGuid(), catalog.OrganizationId, catalog.Id, version, now);
        for (var i = 0; i < fields.Count; i++)
        {
            schema._fields.Add(MetadataField.Create(schema.Id, fields[i], i));
        }

        return schema;
    }
}
