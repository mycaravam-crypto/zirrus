using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Domain.Tests;

/// <summary>Typed metadata rules for W5.</summary>
public sealed class MetadataValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Catalog Catalog = Zirrus.Domain.Catalogs.Catalog.Create(Guid.NewGuid(), "Press", Now);
    private static readonly MetadataSchema Schema = MetadataSchema.Create(Catalog, 1,
    [
        new("title", "Title", MetadataFieldType.Text, Required: true),
        new("tags", "Tags", MetadataFieldType.Keyword, Multiple: true),
        new("rating", "Rating", MetadataFieldType.Number),
        new("published", "Published", MetadataFieldType.Boolean),
        new("captured", "Captured", MetadataFieldType.Date),
        new("creators", "Creators", MetadataFieldType.Text, Required: true, Multiple: true),
    ], Now);

    [Theory]
    [InlineData("""{"title":"Harbour","creators":["Ada"]}""")]
    [InlineData("""{"title":"Harbour","creators":["Ada"],"tags":[],"rating":2.5,"published":false,"captured":"2026-11-26"}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada","Lin"],"captured":"2026-11-26T10:30:00+01:00"}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"captured":"2026-11-26T10:30:00Z"}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"captured":"2026-11-26T10:30:00-05:00"}""")]
    public void Valid_values_pass(string json) =>
        MetadataValidation.Validate(AssetMetadata.Parse(json), Schema);

    [Theory]
    [InlineData("""{"creators":["Ada"]}""")]
    [InlineData("""{"title":" ","creators":["Ada"]}""")]
    [InlineData("""{"title":null,"creators":["Ada"]}""")]
    [InlineData("""{"title":"Hafen","creators":[]}""")]
    [InlineData("""{"title":"Hafen","creators":[""]}""")]
    [InlineData("""{"title":"Hafen","creators":"Ada"}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"unknown":true}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"tags":["sea",7]}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"rating":"2"}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"rating":1e400}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"published":1}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"captured":"2026-02-30"}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"captured":"2026-11-26T10:30:00"}""")]
    [InlineData("""{"title":["Hafen"],"creators":["Ada"]}""")]
    [InlineData("""{"title":"Hafen","creators":["Ada"],"tags":null}""")]
    public void Invalid_values_fail(string json) =>
        Assert.Throws<ArgumentException>(() => MetadataValidation.Validate(AssetMetadata.Parse(json), Schema));
}
