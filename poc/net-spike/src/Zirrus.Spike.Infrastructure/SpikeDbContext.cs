using Microsoft.EntityFrameworkCore;
using Zirrus.Spike.Domain;

namespace Zirrus.Spike.Infrastructure;

/// <summary>A queued job row; the job intent written in the asset transaction.</summary>
public sealed class JobRecord
{
    /// <summary>Database identifier.</summary>
    public long Id { get; set; }

    /// <summary>Deterministic, unique key: asset ID + pipeline version + stage.</summary>
    public required string JobKey { get; set; }

    /// <summary>Stage payload as JSON text (stored as JSONB).</summary>
    public required string Payload { get; set; }

    /// <summary>ready, running, done or dead.</summary>
    public string State { get; set; } = "ready";

    /// <summary>Number of claims so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Attempts allowed before the job is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Earliest time the job may be claimed (used for backoff).</summary>
    public DateTimeOffset RunAfter { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>End of the current lease; an expired lease makes the job claimable again.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Token identifying the current lease holder.</summary>
    public Guid? LeaseToken { get; set; }

    /// <summary>Worker that holds the lease, for diagnostics.</summary>
    public string? LockedBy { get; set; }

    /// <summary>Last structured error.</summary>
    public string? LastError { get; set; }

    /// <summary>Completion time.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>The logical result of a job stage; unique per job key, so retries cannot duplicate it.</summary>
public sealed class JobEffect
{
    /// <summary>Job key the effect belongs to.</summary>
    public required string JobKey { get; set; }

    /// <summary>Worker that produced the effect.</summary>
    public required string ProducedBy { get; set; }

    /// <summary>Time the effect was produced.</summary>
    public DateTimeOffset ProducedAt { get; set; }
}

/// <summary>Every handler execution, including ones interrupted by a crash (at-least-once evidence).</summary>
public sealed class JobAttemptLog
{
    /// <summary>Database identifier.</summary>
    public long Id { get; set; }

    /// <summary>Job key the attempt belongs to.</summary>
    public required string JobKey { get; set; }

    /// <summary>Worker that ran the attempt.</summary>
    public required string Worker { get; set; }

    /// <summary>Attempt start time.</summary>
    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>EF Core model for the spike: assets with JSONB metadata, the job queue and its evidence tables.</summary>
public sealed class SpikeDbContext(DbContextOptions<SpikeDbContext> options) : DbContext(options)
{
    /// <summary>Assets.</summary>
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>Job queue.</summary>
    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    /// <summary>Job effects.</summary>
    public DbSet<JobEffect> JobEffects => Set<JobEffect>();

    /// <summary>Attempt log.</summary>
    public DbSet<JobAttemptLog> JobAttempts => Set<JobAttemptLog>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.HasKey(a => a.Id);
            e.Property(a => a.State).HasConversion<string>();
            e.Property(a => a.Metadata).HasColumnType("jsonb");
            e.Property(a => a.Revision).IsConcurrencyToken();
            e.HasIndex(a => a.Metadata).HasMethod("gin").HasOperators("jsonb_path_ops");
            e.HasIndex(a => new { a.CatalogId, a.State });
        });

        modelBuilder.Entity<JobRecord>(e =>
        {
            e.ToTable("jobs");
            e.HasIndex(j => j.JobKey).IsUnique();
            e.Property(j => j.Payload).HasColumnType("jsonb");
            e.HasIndex(j => new { j.State, j.RunAfter }).HasFilter("\"State\" in ('ready', 'running')");
        });

        modelBuilder.Entity<JobEffect>(e =>
        {
            e.ToTable("job_effects");
            e.HasKey(x => x.JobKey);
        });

        modelBuilder.Entity<JobAttemptLog>(e => e.ToTable("job_attempts"));
    }
}
