using Zirrus.Domain.Catalogs;

namespace Zirrus.Domain.Tests;

public sealed class CatalogTests
{
    private static readonly Guid Organization = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_the_name_and_keeps_the_organization()
    {
        var catalog = Catalog.Create(Organization, "  Press photos  ", Now);

        Assert.Equal("Press photos", catalog.Name);
        Assert.Equal(Organization, catalog.OrganizationId);
        Assert.Equal(Now, catalog.CreatedAt);
        Assert.NotEqual(Guid.Empty, catalog.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_an_empty_name(string name) =>
        Assert.Throws<ArgumentException>(() => Catalog.Create(Organization, name, Now));

    [Fact]
    public void Create_rejects_a_name_over_the_limit() =>
        Assert.Throws<ArgumentException>(() => Catalog.Create(Organization, new string('x', Catalog.MaxNameLength + 1), Now));
}
