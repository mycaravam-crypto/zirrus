using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Infrastructure.Identity;

/// <summary>
/// Cookie session store in PostgreSQL. Tickets (claims and OIDC tokens) are encrypted with Data
/// Protection before storage. All replicas share the table, so sessions survive restarts and work
/// behind any API instance.
/// </summary>
/// <param name="scopes">Creates a scope per call; the cookie handler holds this store as a singleton.</param>
/// <param name="dataProtection">Data Protection provider for ticket encryption.</param>
/// <param name="time">Time source.</param>
public sealed class PostgresTicketStore(IServiceScopeFactory scopes, IDataProtectionProvider dataProtection, TimeProvider time) : ITicketStore
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(8);
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Zirrus.Sessions.v1");

    /// <inheritdoc />
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZirrusDbContext>();
        db.Sessions.Add(new SessionRecord
        {
            Key = key,
            Subject = SubjectOf(ticket),
            ProtectedTicket = Protect(ticket),
            CreatedAt = now,
            ExpiresAt = ticket.Properties.ExpiresUtc ?? now + DefaultLifetime,
        });
        await db.SaveChangesAsync();
        return key;
    }

    /// <inheritdoc />
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZirrusDbContext>();
        var session = await db.Sessions.FindAsync(key);
        if (session is null)
        {
            return; // ended elsewhere (logout); never resurrect it
        }

        session.ProtectedTicket = Protect(ticket);
        session.ExpiresAt = ticket.Properties.ExpiresUtc ?? time.GetUtcNow() + DefaultLifetime;
        await db.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZirrusDbContext>();
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        if (session is null)
        {
            return null;
        }

        if (session.ExpiresAt <= time.GetUtcNow())
        {
            await db.Sessions.Where(s => s.Key == key).ExecuteDeleteAsync();
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(session.ProtectedTicket));
        }
        catch (CryptographicException)
        {
            return null; // key ring lost or tampered row: treat as signed out
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ZirrusDbContext>();
        await db.Sessions.Where(s => s.Key == key).ExecuteDeleteAsync();
    }

    private byte[] Protect(AuthenticationTicket ticket) => _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    private static string SubjectOf(AuthenticationTicket ticket) =>
        ticket.Principal.FindFirst("sub")?.Value ?? ticket.Principal.Identity?.Name ?? CookieAuthenticationDefaults.AuthenticationScheme;
}
