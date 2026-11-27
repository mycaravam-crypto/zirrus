using Zirrus.Application;
using Zirrus.Application.Catalogs;
using Zirrus.Application.Identity;
using Zirrus.Domain.Catalogs;
using Zirrus.Domain.Assets;

namespace Zirrus.Domain.Tests;

/// <summary>Unit tests for the catalog permission rules (security.md matrix), with in-memory ports.</summary>
public sealed class CatalogAccessTests
{
    private static readonly OrganizationContext Organization = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly CurrentUser Admin = new("admin-1", "Admin", IsOrganizationAdmin: true);
    private static readonly CurrentUser Editor = new("editor-1", "Editor", IsOrganizationAdmin: false);
    private static readonly CurrentUser Viewer = new("viewer-1", "Viewer", IsOrganizationAdmin: false);
    private static readonly CurrentUser Outsider = new("outsider-1", "Outsider", IsOrganizationAdmin: false);

    private readonly InMemoryCatalogs _catalogs = new();
    private readonly Catalog _press;
    private readonly Catalog _archive;

    public CatalogAccessTests()
    {
        _press = Catalog.Create(Organization.Id, "Press", Now);
        _archive = Catalog.Create(Organization.Id, "Archive", Now);
        _catalogs.Add(_press);
        _catalogs.Add(_archive);
        _catalogs.Add(CatalogMembership.Grant(_press, Editor.Subject, CatalogRole.Editor, Now));
        _catalogs.Add(CatalogMembership.Grant(_press, Viewer.Subject, CatalogRole.Viewer, Now));
    }

    [Fact]
    public async Task Admin_lists_all_catalogs_of_the_organization()
    {
        var result = await List(Admin);
        Assert.Equal(["Archive", "Press"], result.Select(c => c.Name));
        Assert.All(result, c => Assert.Equal("admin", c.Access));
    }

    [Fact]
    public async Task Members_list_only_granted_catalogs_with_their_role()
    {
        Assert.Equal([("Press", "editor")], (await List(Editor)).Select(c => (c.Name, c.Access)));
        Assert.Equal([("Press", "viewer")], (await List(Viewer)).Select(c => (c.Name, c.Access)));
        Assert.Empty(await List(Outsider));
    }

    [Fact]
    public async Task Anonymous_callers_see_nothing() => Assert.Empty(await List(null));

    [Theory]
    [InlineData("admin", true, true)]
    [InlineData("editor", true, true)]
    [InlineData("viewer", true, false)]
    [InlineData("outsider", false, false)]
    [InlineData("anonymous", false, false)]
    public async Task Catalog_access_follows_the_matrix(string who, bool canRead, bool canEdit)
    {
        var access = new CatalogAccess(_catalogs, Users(UserFor(who)), Organization);
        Assert.Equal(canRead, await access.CanReadAsync(_press.Id, CancellationToken.None));
        Assert.Equal(canEdit, await access.CanEditAsync(_press.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Admin_has_no_access_to_a_catalog_that_does_not_exist()
    {
        var access = new CatalogAccess(_catalogs, Users(Admin), Organization);
        Assert.False(await access.CanReadAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Only_admins_create_catalogs()
    {
        Assert.Equal(OperationStatus.Forbidden, (await Admin_(Editor).CreateCatalogAsync("New", CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.Forbidden, (await Admin_(null).CreateCatalogAsync("New", CancellationToken.None)).Status);

        var created = await Admin_(Admin).CreateCatalogAsync("  New  ", CancellationToken.None);
        Assert.Equal(OperationStatus.Ok, created.Status);
        Assert.Equal("New", created.Value!.Name);
    }

    [Fact]
    public async Task Catalog_creation_rejects_invalid_and_duplicate_names()
    {
        Assert.Equal(OperationStatus.Invalid, (await Admin_(Admin).CreateCatalogAsync(" ", CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.Conflict, (await Admin_(Admin).CreateCatalogAsync("Press", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Admin_grants_changes_and_removes_roles()
    {
        var administration = Admin_(Admin);
        var granted = await administration.SetMemberAsync(_archive.Id, Outsider.Subject, CatalogRole.Viewer, CancellationToken.None);
        Assert.Equal(("viewer", OperationStatus.Ok), (granted.Value!.Role, granted.Status));
        Assert.Equal([("Archive", "viewer")], (await List(Outsider)).Select(c => (c.Name, c.Access)));

        var changed = await administration.SetMemberAsync(_archive.Id, Outsider.Subject, CatalogRole.Editor, CancellationToken.None);
        Assert.Equal("editor", changed.Value!.Role);

        Assert.Equal(OperationStatus.Ok, (await administration.RemoveMemberAsync(_archive.Id, Outsider.Subject, CancellationToken.None)).Status);
        Assert.Empty(await List(Outsider));
        Assert.Equal(OperationStatus.NotFound, (await administration.RemoveMemberAsync(_archive.Id, Outsider.Subject, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Role_management_is_admin_only_and_checks_inputs()
    {
        Assert.Equal(OperationStatus.Forbidden, (await Admin_(Editor).SetMemberAsync(_press.Id, "x", CatalogRole.Viewer, CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.Forbidden, (await Admin_(Viewer).RemoveMemberAsync(_press.Id, Viewer.Subject, CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Admin_(Admin).SetMemberAsync(Guid.NewGuid(), "x", CatalogRole.Viewer, CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.Invalid, (await Admin_(Admin).SetMemberAsync(_press.Id, " padded ", CatalogRole.Viewer, CancellationToken.None)).Status);
        Assert.Equal(OperationStatus.Invalid, (await Admin_(Admin).SetMemberAsync(_press.Id, Editor.Subject, (CatalogRole)42, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Schema_administration_validates_access_input_and_conflicts()
    {
        var store = new SchemaMemory(_press);
        SchemaAdministration Service(CurrentUser? user) => new(store, new CatalogAccess(_catalogs, Users(user), Organization), Users(user), Organization, new FixedTime(Now));
        var ct = TestContext.Current.CancellationToken;
        var fields = new[] { new MetadataFieldDefinition("title", "Title", MetadataFieldType.Text) };
        Assert.Equal(OperationStatus.NotFound, (await Service(Outsider).GetAsync(_press.Id, ct)).Status);
        Assert.Equal(0, (await Service(Viewer).GetAsync(_press.Id, ct)).Value!.Version);
        Assert.Equal(OperationStatus.Forbidden, (await Service(Editor).CreateAsync(_press.Id, fields, ct)).Status);
        Assert.Equal(OperationStatus.Forbidden, (await Service(null).CreateAsync(_press.Id, fields, ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Service(Admin).CreateAsync(_archive.Id, fields, ct)).Status);
        Assert.Equal(OperationStatus.Invalid, (await Service(Admin).CreateAsync(_press.Id, null, ct)).Status);
        Assert.Equal(OperationStatus.Invalid, (await Service(Admin).CreateAsync(_press.Id, [null!], ct)).Status);
        Assert.Equal(OperationStatus.Invalid, (await Service(Admin).CreateAsync(_press.Id, [new("INVALID!", "Title", MetadataFieldType.Text)], ct)).Status);
        store.RejectInsert = true;
        Assert.Equal(OperationStatus.Conflict, (await Service(Admin).CreateAsync(_press.Id, fields, ct)).Status);
        store.RejectInsert = false;
        Assert.Equal(OperationStatus.Ok, (await Service(Admin).CreateAsync(_press.Id, fields, ct)).Status);
        Assert.Equal(1, (await Service(Viewer).GetAsync(_press.Id, ct)).Value!.Version);
        Assert.Equal(OperationStatus.Conflict, (await Service(Admin).CreateAsync(_press.Id, fields, ct)).Status);
    }

    [Fact]
    public async Task Category_management_validates_access_parent_and_assignment()
    {
        var store = new CategoryMemory(_press);
        CategoryManagement Service(CurrentUser? user) => new(store, new CatalogAccess(_catalogs, Users(user), Organization), Users(user), Organization, new FixedTime(Now));
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(OperationStatus.NotFound, (await Service(Outsider).ListAsync(_press.Id, ct)).Status);
        Assert.Empty((await Service(Viewer).ListAsync(_press.Id, ct)).Value!);
        Assert.Equal(OperationStatus.Forbidden, (await Service(Editor).CreateAsync(_press.Id, null, "Root", ct)).Status);
        Assert.Equal(OperationStatus.Forbidden, (await Service(null).CreateAsync(_press.Id, null, "Root", ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Service(Admin).CreateAsync(_archive.Id, null, "Root", ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Service(Admin).CreateAsync(_press.Id, Guid.NewGuid(), "Child", ct)).Status);
        Assert.Equal(OperationStatus.Invalid, (await Service(Admin).CreateAsync(_press.Id, null, " ", ct)).Status);
        var root = (await Service(Admin).CreateAsync(_press.Id, null, "Root", ct)).Value!;
        var child = (await Service(Admin).CreateAsync(_press.Id, root.Id, "Child", ct)).Value!;
        Assert.Equal(root.Id, child.ParentId);
        Assert.Equal(2, (await Service(Viewer).ListAsync(_press.Id, ct)).Value!.Count);
        Assert.Equal(OperationStatus.Conflict, (await Service(Admin).CreateAsync(_press.Id, null, "Root", ct)).Status);
        var asset = Asset.Start(_press, "photo.jpg", "image/jpeg", Editor.Subject, Now);
        store.Asset = asset;
        Assert.Equal(OperationStatus.NotFound, (await Service(Outsider).AssignAsync(_press.Id, asset.Id, root.Id, ct)).Status);
        Assert.Equal(OperationStatus.Forbidden, (await Service(Viewer).AssignAsync(_press.Id, asset.Id, root.Id, ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Service(Editor).AssignAsync(_press.Id, Guid.NewGuid(), root.Id, ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Service(Editor).AssignAsync(_press.Id, asset.Id, root.Id, ct)).Status);
        asset.MarkStored(new string('a', 64), 4, Now);
        asset.BeginProcessing(Now);
        asset.Activate(Now);
        Assert.Equal(OperationStatus.NotFound, (await Service(Editor).AssignAsync(_press.Id, asset.Id, Guid.NewGuid(), ct)).Status);
        store.RejectAssignment = true;
        Assert.Equal(OperationStatus.NotFound, (await Service(Editor).AssignAsync(_press.Id, asset.Id, root.Id, ct)).Status);
        store.RejectAssignment = false;
        Assert.Equal(OperationStatus.Ok, (await Service(Editor).AssignAsync(_press.Id, asset.Id, root.Id, ct)).Status);
        Assert.Equal(OperationStatus.Ok, (await Service(Editor).AssignAsync(_press.Id, asset.Id, child.Id, ct)).Status);

        // Listing: readers of the catalog see the active asset's categories; outsiders and unknown assets get 404.
        Assert.Equal(["Child", "Root"], (await Service(Viewer).ListForAssetAsync(_press.Id, asset.Id, ct)).Value!.Select(c => c.Name));
        Assert.Equal(OperationStatus.NotFound, (await Service(Outsider).ListForAssetAsync(_press.Id, asset.Id, ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Service(Viewer).ListForAssetAsync(_press.Id, Guid.NewGuid(), ct)).Status);

        // Removal: editors only; idempotent; returns what remains.
        Assert.Equal(OperationStatus.NotFound, (await Service(Outsider).UnassignAsync(_press.Id, asset.Id, root.Id, ct)).Status);
        Assert.Equal(OperationStatus.Forbidden, (await Service(Viewer).UnassignAsync(_press.Id, asset.Id, root.Id, ct)).Status);
        Assert.Equal(OperationStatus.NotFound, (await Service(Editor).UnassignAsync(_press.Id, Guid.NewGuid(), root.Id, ct)).Status);
        Assert.Equal(["Child"], (await Service(Editor).UnassignAsync(_press.Id, asset.Id, root.Id, ct)).Value!.Select(c => c.Name));
        Assert.Equal(["Child"], (await Service(Editor).UnassignAsync(_press.Id, asset.Id, root.Id, ct)).Value!.Select(c => c.Name));

        // A viewer cannot see the categories of an asset that is no longer active.
        asset.MarkForDeletion(Now);
        Assert.Equal(OperationStatus.NotFound, (await Service(Viewer).ListForAssetAsync(_press.Id, asset.Id, ct)).Status);
    }

    private sealed class SchemaMemory(Catalog catalog) : ISchemaStore
    {
        private MetadataSchema? _schema;
        public bool RejectInsert { get; set; }
        public Task<Catalog?> FindCatalogAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
            Task.FromResult<Catalog?>(catalog.Id == catalogId && catalog.OrganizationId == organizationId ? catalog : null);
        public Task<MetadataSchema?> FindLatestAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) => Task.FromResult(_schema);
        public Task<bool> TryAddAsync(MetadataSchema schema, CancellationToken cancellationToken)
        {
            if (RejectInsert) return Task.FromResult(false);
            _schema = schema;
            return Task.FromResult(true);
        }
    }

    private sealed class CategoryMemory(Catalog catalog) : ICategoryStore
    {
        private readonly List<Category> _nodes = [];
        public Asset? Asset { get; set; }
        public bool RejectAssignment { get; set; }
        public Task<IReadOnlyList<Category>> ListAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Category>>(_nodes);
        public Task<Category?> FindCategoryAsync(Guid organizationId, Guid catalogId, Guid categoryId, CancellationToken cancellationToken) =>
            Task.FromResult(_nodes.SingleOrDefault(c => c.Id == categoryId));
        public Task<Catalog?> FindCatalogAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
            Task.FromResult<Catalog?>(catalog.Id == catalogId && catalog.OrganizationId == organizationId ? catalog : null);
        public Task<Asset?> FindAssetAsync(Guid organizationId, Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(Asset?.Id == assetId ? Asset : null);
        public Task<bool> TryAddAsync(Category category, CancellationToken cancellationToken)
        {
            if (_nodes.Any(c => c.ParentId == category.ParentId && c.Name == category.Name)) return Task.FromResult(false);
            _nodes.Add(category);
            return Task.FromResult(true);
        }
        public HashSet<Guid> Assigned { get; } = [];
        public Task<bool> AssignAsync(AssetCategory assignment, CancellationToken cancellationToken)
        {
            if (RejectAssignment) return Task.FromResult(false);
            Assigned.Add(assignment.CategoryId);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<Category>> ListForAssetAsync(Guid catalogId, Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Category>>(_nodes.Where(c => Asset?.Id == assetId && Assigned.Contains(c.Id)).OrderBy(c => c.Name).ToList());
        public Task UnassignAsync(Guid catalogId, Guid assetId, Guid categoryId, CancellationToken cancellationToken)
        {
            Assigned.Remove(categoryId);
            return Task.CompletedTask;
        }
    }

    private Task<IReadOnlyList<CatalogSummary>> List(CurrentUser? user) =>
        new ListCatalogs(_catalogs, Users(user), Organization).ExecuteAsync(CancellationToken.None);

    private CatalogAdministration Admin_(CurrentUser? user) =>
        new(_catalogs, Users(user), Organization, new FixedTime(Now));

    private static CurrentUser? UserFor(string who) => who switch
    {
        "admin" => Admin,
        "editor" => Editor,
        "viewer" => Viewer,
        "outsider" => Outsider,
        _ => null,
    };

    private static ICurrentUserAccessor Users(CurrentUser? user) => new StaticUser(user);

    private sealed class StaticUser(CurrentUser? user) : ICurrentUserAccessor
    {
        public CurrentUser? User { get; } = user;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>In-memory implementation of both catalog ports.</summary>
    private sealed class InMemoryCatalogs : ICatalogReader, ICatalogStore
    {
        private readonly List<Catalog> _catalogs = [];
        private readonly List<CatalogMembership> _memberships = [];

        public Task<IReadOnlyList<(Guid Id, string Name, DateTimeOffset CreatedAt, CatalogRole? Role)>> ListAsync(
            Guid organizationId, string? subject, CancellationToken cancellationToken)
        {
            IReadOnlyList<(Guid, string, DateTimeOffset, CatalogRole?)> rows = _catalogs
                .Where(c => c.OrganizationId == organizationId)
                .Select(c => (c, role: _memberships.FirstOrDefault(m => m.CatalogId == c.Id && m.Subject == subject)?.Role))
                .Where(x => subject is null || x.role is not null)
                .OrderBy(x => x.c.Name, StringComparer.Ordinal)
                .Select(x => (x.c.Id, x.c.Name, x.c.CreatedAt, x.role))
                .ToList();
            return Task.FromResult(rows);
        }

        public Task<CatalogRole?> FindRoleAsync(Guid organizationId, Guid catalogId, string subject, CancellationToken cancellationToken) =>
            Task.FromResult(_memberships.FirstOrDefault(m => m.OrganizationId == organizationId && m.CatalogId == catalogId && m.Subject == subject)?.Role);

        public Task<bool> ExistsAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
            Task.FromResult(_catalogs.Any(c => c.OrganizationId == organizationId && c.Id == catalogId));

        public Task<Catalog?> FindAsync(Guid organizationId, Guid catalogId, CancellationToken cancellationToken) =>
            Task.FromResult(_catalogs.FirstOrDefault(c => c.OrganizationId == organizationId && c.Id == catalogId));

        public Task<bool> NameExistsAsync(Guid organizationId, string name, CancellationToken cancellationToken) =>
            Task.FromResult(_catalogs.Any(c => c.OrganizationId == organizationId && c.Name == name));

        public Task<CatalogMembership?> FindMembershipAsync(Catalog catalog, string subject, CancellationToken cancellationToken) =>
            Task.FromResult(_memberships.FirstOrDefault(m => m.CatalogId == catalog.Id && m.Subject == subject));

        public void Add(Catalog catalog) => _catalogs.Add(catalog);

        public void Add(CatalogMembership membership) => _memberships.Add(membership);

        public void Remove(CatalogMembership membership) => _memberships.Remove(membership);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
