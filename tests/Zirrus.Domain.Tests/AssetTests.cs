using Zirrus.Domain.Assets;
using Zirrus.Domain.Catalogs;

namespace Zirrus.Domain.Tests;

/// <summary>Asset lifecycle, metadata and storage-key rules (ADR 0006, data-storage-search.md).</summary>
public sealed class AssetTests
{
    private const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Catalog Press = Catalog.Create(Guid.NewGuid(), "Press", Now);

    [Fact]
    public void Start_creates_an_uploading_asset_with_an_opaque_key_independent_of_the_file_name()
    {
        var asset = Asset.Start(Press, @"..\..\etc/passwd photo.JPG", "IMAGE/JPEG", "erin", Now);
        Assert.Equal(AssetState.Uploading, asset.State);
        Assert.Equal("passwd photo.JPG", asset.FileName);
        Assert.Equal("image/jpeg", asset.MediaType);
        Assert.Equal($"originals/{Press.Id:N}/{asset.Id:N}", asset.OriginalKey);
        Assert.Equal((Press.OrganizationId, Press.Id, 1), (asset.OrganizationId, asset.CatalogId, asset.Revision));
        Assert.Equal("{}", asset.Metadata);
        Assert.Null(asset.Sha256);
    }

    [Theory]
    [InlineData("", "image/jpeg", "erin")]
    [InlineData("a/", "image/jpeg", "erin")]
    [InlineData("bad\u0001name", "image/jpeg", "erin")]
    [InlineData("ok.jpg", "jpeg", "erin")]
    [InlineData("ok.jpg", "image/ jpeg", "erin")]
    [InlineData("ok.jpg", "image/jpeg", "")]
    public void Start_rejects_invalid_input(string fileName, string mediaType, string createdBy) =>
        Assert.Throws<ArgumentException>(() => Asset.Start(Press, fileName, mediaType, createdBy, Now));

    [Fact]
    public void Happy_path_moves_through_quarantine_and_processing_to_active()
    {
        var asset = Asset.Start(Press, "a.jpg", "image/jpeg", "erin", Now);
        asset.MarkStored(Sha, 42, Now.AddSeconds(1));
        Assert.Equal((AssetState.Quarantined, Sha, 42L), (asset.State, asset.Sha256, asset.SizeBytes));
        asset.BeginProcessing(Now.AddSeconds(2));
        asset.Activate(Now.AddSeconds(3));
        Assert.Equal(AssetState.Active, asset.State);
        Assert.Equal(Now.AddSeconds(3), asset.StateChangedAt);
        Assert.Equal(4, asset.Revision);
    }

    [Fact]
    public void Failed_processing_can_be_retried_and_rejection_records_a_reason()
    {
        var asset = Stored();
        asset.BeginProcessing(Now);
        asset.Fail("render: timeout", Now);
        Assert.Equal((AssetState.Failed, "render: timeout"), (asset.State, asset.StatusReason));
        asset.BeginProcessing(Now);
        Assert.Null(asset.StatusReason);
        asset.Reject(new string('x', 600), Now);
        Assert.Equal(Asset.MaxReasonLength, asset.StatusReason!.Length);
        asset.MarkForDeletion(Now);
        asset.MarkDeleted(Now);
        Assert.Equal(AssetState.Deleted, asset.State);
    }

    [Fact]
    public void Invalid_transitions_are_refused()
    {
        var asset = Asset.Start(Press, "a.jpg", "image/jpeg", "erin", Now);
        Assert.Throws<InvalidOperationException>(() => asset.Activate(Now));
        Assert.Throws<InvalidOperationException>(() => asset.MarkForDeletion(Now));
        asset.Fail("upload: aborted", Now);
        Assert.Throws<InvalidOperationException>(() => asset.MarkStored(Sha, 1, Now));
        Assert.Throws<ArgumentException>(() => asset.Fail(" ", Now));
        Assert.False(AssetLifecycle.CanTransition(AssetState.Deleted, AssetState.Active));
        Assert.False(AssetLifecycle.CanTransition((AssetState)99, AssetState.Active));
    }

    [Theory]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    [InlineData("abc")]
    [InlineData(null)]
    public void MarkStored_requires_a_lowercase_sha256(string? sha) =>
        Assert.Throws<ArgumentException>(() => Asset.Start(Press, "a.jpg", "image/jpeg", "erin", Now).MarkStored(sha!, 1, Now));

    [Fact]
    public void MarkStored_rejects_negative_sizes() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Asset.Start(Press, "a.jpg", "image/jpeg", "erin", Now).MarkStored(Sha, -1, Now));

    [Fact]
    public void Metadata_update_needs_the_current_revision()
    {
        var asset = Active();
        var revision = asset.Revision;
        asset.UpdateMetadata(AssetMetadata.Parse("""{"title": "Harbour", "tags": ["sea"]}"""), revision, Now);
        Assert.Equal("""{"title":"Harbour","tags":["sea"]}""", asset.Metadata);
        Assert.Equal(revision + 1, asset.Revision);

        Assert.Throws<RevisionMismatchException>(() => asset.UpdateMetadata(AssetMetadata.Empty, revision, Now));
        Assert.Equal(revision + 1, asset.Revision);
    }

    [Fact]
    public void Deleted_assets_cannot_be_edited()
    {
        var asset = Active();
        asset.MarkForDeletion(Now);
        Assert.Throws<InvalidOperationException>(() => asset.UpdateMetadata(AssetMetadata.Empty, asset.Revision, Now));
    }

    [Fact]
    public void Embedded_metadata_is_kept_apart_from_user_metadata()
    {
        var asset = Active();
        asset.SetEmbeddedMetadata(AssetMetadata.Parse("""{"exif_make": "Canon"}"""), Now);
        Assert.Equal("""{"exif_make":"Canon"}""", asset.EmbeddedMetadata);
        Assert.Equal("{}", asset.Metadata);
    }

    [Theory]
    [InlineData(AssetState.Active, false, true)]
    [InlineData(AssetState.Quarantined, false, false)]
    [InlineData(AssetState.Quarantined, true, true)]
    [InlineData(AssetState.Deleting, true, false)]
    public void Visibility_depends_on_state_and_role(AssetState state, bool canEdit, bool visible)
    {
        var asset = Stored();
        if (state == AssetState.Active)
        {
            asset.BeginProcessing(Now);
            asset.Activate(Now);
        }
        else if (state == AssetState.Deleting)
        {
            asset.MarkForDeletion(Now);
        }

        Assert.Equal(visible, asset.IsVisibleTo(canEdit));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("""{"Title": 1}""")]
    [InlineData("""{"1st": 1}""")]
    [InlineData("""{"a": 1, "a": 2}""")]
    [InlineData("""{"a":[[[[[[[[[1]]]]]]]]]}""")]
    public void Metadata_must_be_an_object_with_valid_keys(string json) =>
        Assert.Throws<ArgumentException>(() => AssetMetadata.Parse(json));

    [Fact]
    public void Metadata_is_size_limited() =>
        Assert.Throws<ArgumentException>(() => AssetMetadata.Parse($$"""{"a":"{{new string('x', AssetMetadata.MaxBytes)}}"}"""));

    [Theory]
    [InlineData("originals/abc/def", true)]
    [InlineData("renditions/a-1/b", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("/abs", false)]
    [InlineData("a//b", false)]
    [InlineData("a/", false)]
    [InlineData("Upper", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Blob_keys_accept_only_the_generated_alphabet(string? value, bool valid)
    {
        Assert.Equal(valid, BlobKey.TryParse(value, out var key));
        if (valid)
        {
            Assert.Equal(value, BlobKey.Parse(value!).ToString());
            Assert.Equal(value, key.Value);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => BlobKey.Parse(value!));
        }
    }

    [Fact]
    public void Blob_keys_have_a_length_limit() =>
        Assert.False(BlobKey.TryParse(new string('a', BlobKey.MaxLength + 1), out _));

    [Fact]
    public void Renditions_get_versioned_keys_and_validate_their_data()
    {
        var asset = Active();
        var rendition = Rendition.Create(asset, "thumbnail", 2, "image/jpeg", 320, 200, 1234, Sha, Now);
        Assert.Equal($"renditions/{Press.Id:N}/{asset.Id:N}/thumbnail-v2", rendition.StorageKey);
        Assert.Equal((asset.CatalogId, asset.Id, 320, 200), (rendition.CatalogId, rendition.AssetId, rendition.Width, rendition.Height));

        Assert.Throws<ArgumentException>(() => Rendition.Create(asset, "Thumb nail", 1, "image/jpeg", 1, 1, 1, Sha, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rendition.Create(asset, "thumbnail", 0, "image/jpeg", 1, 1, 1, Sha, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rendition.Create(asset, "thumbnail", 1, "image/jpeg", 0, 1, 1, Sha, Now));
        Assert.Throws<ArgumentException>(() => Rendition.Create(asset, "thumbnail", 1, "image/jpeg", 1, 1, 1, "x", Now));
    }

    [Fact]
    public void Job_intents_have_deterministic_keys_per_asset_stage_and_pipeline_version()
    {
        var asset = Stored();
        var job = JobIntent.ForAsset(asset, "render", 3, Now);
        Assert.Equal($"{asset.Id:N}:render:v3", job.JobKey);
        Assert.Equal((JobState.Ready, 0, JobIntent.DefaultMaxAttempts, Now), (job.State, job.Attempts, job.MaxAttempts, job.RunAfter));
        Assert.Equal(job.JobKey, JobIntent.ForAsset(asset, "render", 3, Now.AddHours(1)).JobKey);
        Assert.NotEqual(job.JobKey, JobIntent.ForAsset(asset, "render", 4, Now).JobKey);
        Assert.Throws<ArgumentException>(() => JobIntent.ForAsset(asset, "Render!", 1, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobIntent.ForAsset(asset, "render", 0, Now));
    }

    [Fact]
    public void Categories_nest_within_one_catalog_and_assignments_cannot_cross_catalogs()
    {
        var root = Category.CreateRoot(Press, "  Events ", Now);
        var child = Category.CreateChild(root, "2026", Now);
        Assert.Equal(("Events", root.Id, Press.Id), (root.Name, child.ParentId, child.CatalogId));
        Assert.Null(root.ParentId);
        Assert.Throws<ArgumentException>(() => Category.CreateRoot(Press, " ", Now));

        var asset = Active();
        var assignment = AssetCategory.Assign(asset, child, Now);
        Assert.Equal((Press.Id, asset.Id, child.Id), (assignment.CatalogId, assignment.AssetId, assignment.CategoryId));

        var other = Category.CreateRoot(Catalog.Create(Press.OrganizationId, "Other", Now), "Elsewhere", Now);
        Assert.Throws<ArgumentException>(() => AssetCategory.Assign(asset, other, Now));
    }

    [Fact]
    public void Metadata_schemas_validate_fields()
    {
        var schema = MetadataSchema.Create(Press, 1,
        [
            new("title", "Title", MetadataFieldType.Text, Required: true),
            new("tags", "Tags", MetadataFieldType.Keyword, Multiple: true),
        ], Now);
        Assert.Equal(["title", "tags"], schema.Fields.Select(f => f.Key));
        Assert.Equal((0, 1), (schema.Fields[0].Position, schema.Fields[1].Position));
        Assert.True(schema.Fields[0].Required);
        Assert.All(schema.Fields, f => Assert.Equal(schema.Id, f.SchemaId));

        Assert.Throws<ArgumentException>(() => MetadataSchema.Create(Press, 1, [new("t", "A", MetadataFieldType.Text), new("t", "B", MetadataFieldType.Text)], Now));
        Assert.Throws<ArgumentException>(() => MetadataSchema.Create(Press, 1, [new("Bad", "A", MetadataFieldType.Text)], Now));
        Assert.Throws<ArgumentException>(() => MetadataSchema.Create(Press, 1, [new("ok", " ", MetadataFieldType.Text)], Now));
        Assert.Throws<ArgumentException>(() => MetadataSchema.Create(Press, 1, [new("ok", "A", (MetadataFieldType)9)], Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetadataSchema.Create(Press, 0, [], Now));
        Assert.Throws<ArgumentException>(() => MetadataSchema.Create(Press, 1,
            Enumerable.Range(0, MetadataSchema.MaxFields + 1).Select(i => new MetadataFieldDefinition($"f{i}", "F", MetadataFieldType.Text)).ToList(), Now));
    }

    [Fact]
    public void Organizations_need_an_id_and_a_name()
    {
        var id = Guid.NewGuid();
        Assert.Equal("Acme", Organization.Create(id, " Acme ", Now).Name);
        Assert.Throws<ArgumentException>(() => Organization.Create(Guid.Empty, "Acme", Now));
        Assert.Throws<ArgumentException>(() => Organization.Create(id, "", Now));
    }

    private static Asset Stored()
    {
        var asset = Asset.Start(Press, "a.jpg", "image/jpeg", "erin", Now);
        asset.MarkStored(Sha, 42, Now);
        return asset;
    }

    private static Asset Active()
    {
        var asset = Stored();
        asset.BeginProcessing(Now);
        asset.Activate(Now);
        return asset;
    }
}
