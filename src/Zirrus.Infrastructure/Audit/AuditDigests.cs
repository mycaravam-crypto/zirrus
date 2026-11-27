using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zirrus.Infrastructure.Persistence;

namespace Zirrus.Infrastructure.Audit;

/// <summary>Audit digest settings (<c>Audit</c> section; the key comes from the <c>Audit__DigestKey</c> secret).</summary>
public sealed class AuditDigestOptions
{
    /// <summary>HMAC key, base64, at least 32 bytes. Without it no digests are sealed.</summary>
    public string? DigestKey { get; set; }

    /// <summary>Directory the digests are also written to (a restricted location); none when empty.</summary>
    public string? ExportDirectory { get; set; }

    /// <summary>Events younger than this are left for the next digest, so late commits are not skipped.</summary>
    public TimeSpan SafetyLag { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Result of verifying one digest.</summary>
/// <param name="DigestId">Digest.</param>
/// <param name="FromSeq">First event covered.</param>
/// <param name="ToSeq">Last event covered.</param>
/// <param name="Status">Outcome.</param>
public sealed record DigestVerification(Guid DigestId, long FromSeq, long ToSeq, DigestStatus Status);

/// <summary>Outcome of verifying a digest.</summary>
public enum DigestStatus
{
    /// <summary>Events, chain and signature match.</summary>
    Valid,

    /// <summary>Events were changed, added or removed, the chain is broken, or the signature is wrong.</summary>
    Mismatch,

    /// <summary>The covered events were removed by the retention purge; nothing to compare.</summary>
    Purged,

    /// <summary>Signed with a different key than the configured one.</summary>
    UnknownKey,
}

/// <summary>
/// Seals contiguous ranges of audit events into signed digests and verifies them (security.md, Q10).
/// A digest is SHA-256 over the previous digest and the canonical JSON line of every event in the
/// range, signed with HMAC-SHA256. Any change to a covered event, its removal outside the purge, or
/// an insert into a sealed range changes the hash, so verification reports a mismatch.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="options">Key, export directory and safety lag.</param>
/// <param name="time">Time source.</param>
public sealed class AuditDigests(ZirrusDbContext db, IOptions<AuditDigestOptions> options, TimeProvider time)
{
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private const long SealLock = 0x5A1A0D17; // pg_advisory_xact_lock key: one sealer at a time
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Whether a valid key is configured.</summary>
    public bool IsConfigured => Key() is not null;

    /// <summary>Seals the events written since the last digest (up to the safety lag).</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The new digest, or null when there is nothing to seal or no key.</returns>
    public async Task<AuditDigest?> SealAsync(CancellationToken cancellationToken)
    {
        if (Key() is not { } key)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SealLock})", cancellationToken);
        var previous = await db.Set<AuditDigest>().AsNoTracking().OrderByDescending(d => d.ToSeq).FirstOrDefaultAsync(cancellationToken);
        var fromSeq = (previous?.ToSeq ?? 0) + 1;
        var cutoff = time.GetUtcNow() - options.Value.SafetyLag;
        var events = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Seq >= fromSeq && e.OccurredAt < cutoff)
            .OrderBy(e => e.Seq).ToListAsync(cancellationToken);
        if (events.Count == 0)
        {
            return null;
        }

        var previousSha = previous?.Sha256 ?? Genesis;
        var sha = Hash(previousSha, events);
        var digest = new AuditDigest
        {
            Id = Guid.CreateVersion7(),
            FromSeq = fromSeq,
            ToSeq = events[^1].Seq,
            EventCount = events.Count,
            Sha256 = sha,
            Hmac = Sign(key, sha),
            PreviousSha256 = previousSha,
            KeyId = KeyId(key),
            CreatedAt = time.GetUtcNow(),
        };
        db.Set<AuditDigest>().Add(digest);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ExportAsync(digest, cancellationToken);
        return digest;
    }

    /// <summary>Verifies the most recent digests against the current events.</summary>
    /// <param name="count">How many digests, newest first.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One result per digest, oldest first.</returns>
    public async Task<IReadOnlyList<DigestVerification>> VerifyAsync(int count, CancellationToken cancellationToken)
    {
        var key = Key();
        var digests = await db.Set<AuditDigest>().AsNoTracking().OrderByDescending(d => d.ToSeq).Take(count + 1).ToListAsync(cancellationToken);
        digests.Reverse();
        var results = new List<DigestVerification>();
        for (var i = digests.Count > count ? 1 : 0; i < digests.Count; i++)
        {
            var digest = digests[i];
            var expectedPrevious = i == 0 ? (digest.FromSeq == 1 ? Genesis : digest.PreviousSha256) : digests[i - 1].Sha256;
            var events = await db.Set<AuditEvent>().AsNoTracking()
                .Where(e => e.Seq >= digest.FromSeq && e.Seq <= digest.ToSeq).OrderBy(e => e.Seq).ToListAsync(cancellationToken);
            var status = Status(digest, events, expectedPrevious, key);
            results.Add(new DigestVerification(digest.Id, digest.FromSeq, digest.ToSeq, status));
        }

        return results;
    }

    private static DigestStatus Status(AuditDigest digest, List<AuditEvent> events, string expectedPrevious, byte[]? key)
    {
        // The retention purge removes the oldest events: a missing leading part (or all) of the range is
        // purged, not tampered with. Gaps elsewhere still count as a mismatch.
        if (events.Count < digest.EventCount && (events.Count == 0 || events[0].Seq > digest.FromSeq)
            && events.Select((e, i) => e.Seq - i).Distinct().Count() <= 1 && (events.Count == 0 || events[^1].Seq == digest.ToSeq))
        {
            return DigestStatus.Purged;
        }

        if (key is null || KeyId(key) != digest.KeyId)
        {
            return DigestStatus.UnknownKey;
        }

        var intact = events.Count == digest.EventCount
            && digest.PreviousSha256 == expectedPrevious
            && Hash(digest.PreviousSha256, events) == digest.Sha256
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(key, digest.Sha256)), Encoding.ASCII.GetBytes(digest.Hmac));
        return intact ? DigestStatus.Valid : DigestStatus.Mismatch;
    }

    /// <summary>SHA-256 (hex) over the previous digest and one canonical JSON line per event.</summary>
    /// <param name="previousSha">Previous digest hash.</param>
    /// <param name="events">Events in sequence order.</param>
    /// <returns>The hash.</returns>
    internal static string Hash(string previousSha, IEnumerable<AuditEvent> events)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(previousSha + "\n"));
        foreach (var e in events)
        {
            var line = JsonSerializer.Serialize(new object?[]
            {
                e.Seq, e.Id, e.OrganizationId, e.CatalogId, e.ResourceId, e.Actor, e.Action, e.Outcome,
                e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), e.CorrelationId, e.Detail,
            });
            hash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Sign(byte[] key, string sha) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(sha)));

    private static string KeyId(byte[] key) => Convert.ToHexStringLower(SHA256.HashData(key))[..8];

    private byte[]? Key()
    {
        if (string.IsNullOrWhiteSpace(options.Value.DigestKey))
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(options.Value.DigestKey.Trim());
            return key.Length >= 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task ExportAsync(AuditDigest digest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ExportDirectory))
        {
            return;
        }

        Directory.CreateDirectory(options.Value.ExportDirectory);
        var path = Path.Combine(options.Value.ExportDirectory, $"audit-digest-{digest.ToSeq:D12}.json");
        var json = JsonSerializer.Serialize(digest, ExportJson);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }
}
