namespace Zirrus.Infrastructure.Identity;

/// <summary>
/// A server-side authentication session. The browser cookie carries only <see cref="Key"/>;
/// deleting the row ends the session for every copy of the cookie (security.md).
/// </summary>
public sealed class SessionRecord
{
    /// <summary>Random session key (hex), the only value in the session cookie.</summary>
    public required string Key { get; set; }

    /// <summary>Subject of the signed-in user, for "sign out everywhere" and audits.</summary>
    public required string Subject { get; set; }

    /// <summary>Authentication ticket, serialized and encrypted with ASP.NET Core Data Protection.</summary>
    public required byte[] ProtectedTicket { get; set; }

    /// <summary>Creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Absolute expiry (UTC); expired rows are rejected and deleted.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
