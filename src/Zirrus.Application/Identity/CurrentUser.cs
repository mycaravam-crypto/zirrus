namespace Zirrus.Application.Identity;

/// <summary>The signed-in user as seen by use cases.</summary>
/// <param name="Subject">Stable identity-provider subject (OIDC <c>sub</c>).</param>
/// <param name="DisplayName">Name for display; may be empty.</param>
/// <param name="IsOrganizationAdmin">True when the identity provider grants the organization <c>admin</c> role.</param>
public sealed record CurrentUser(string Subject, string DisplayName, bool IsOrganizationAdmin);

/// <summary>Port that resolves the user of the current request; implemented by the API host.</summary>
public interface ICurrentUserAccessor
{
    /// <summary>The signed-in user, or null for anonymous requests.</summary>
    CurrentUser? User { get; }
}

/// <summary>The single organization of the MVP (ADR 0007); its ID is kept on all data for future isolation.</summary>
/// <param name="Id">Organization identifier from configuration.</param>
public sealed record OrganizationContext(Guid Id);
