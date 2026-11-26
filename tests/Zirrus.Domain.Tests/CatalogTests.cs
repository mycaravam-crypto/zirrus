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

public sealed class CatalogMembershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Catalog Press = Catalog.Create(Guid.NewGuid(), "Press", Now);

    [Fact]
    public void Grant_copies_the_catalog_keys()
    {
        var membership = CatalogMembership.Grant(Press, "user-1", CatalogRole.Editor, Now);
        Assert.Equal((Press.OrganizationId, Press.Id, "user-1", CatalogRole.Editor), (membership.OrganizationId, membership.CatalogId, membership.Subject, membership.Role));
        Assert.True(membership.CanEdit);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" user")]
    public void Grant_rejects_invalid_subjects(string subject) =>
        Assert.Throws<ArgumentException>(() => CatalogMembership.Grant(Press, subject, CatalogRole.Viewer, Now));

    [Fact]
    public void Grant_rejects_a_too_long_subject() =>
        Assert.Throws<ArgumentException>(() => CatalogMembership.Grant(Press, new string('s', CatalogMembership.MaxSubjectLength + 1), CatalogRole.Viewer, Now));

    [Fact]
    public void Grant_and_ChangeRole_reject_undefined_roles()
    {
        Assert.Throws<ArgumentException>(() => CatalogMembership.Grant(Press, "u", (CatalogRole)9, Now));
        var membership = CatalogMembership.Grant(Press, "u", CatalogRole.Viewer, Now);
        Assert.Throws<ArgumentException>(() => membership.ChangeRole((CatalogRole)9, Now));
        membership.ChangeRole(CatalogRole.Editor, Now.AddDays(1));
        Assert.Equal((CatalogRole.Editor, Now.AddDays(1)), (membership.Role, membership.GrantedAt));
    }
}
