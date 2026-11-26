namespace Zirrus.Infrastructure.Storage;

/// <summary>Blob storage settings (<c>Storage</c> section).</summary>
public sealed class StorageOptions
{
    /// <summary><c>filesystem</c> (default) or <c>s3</c>.</summary>
    public string Provider { get; set; } = "filesystem";

    /// <summary>Root directory of the filesystem store; a dedicated volume in Compose.</summary>
    public string Root { get; set; } = "/var/lib/zirrus/blobs";

    /// <summary>S3-compatible store settings.</summary>
    public S3StorageOptions S3 { get; set; } = new();
}
