using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.ScyllaDB;

namespace ScyllaDB.Sample;

/// <summary>
///     A sensor reading, partitioned by sensor and ordered newest first within each partition.
/// </summary>
/// <remarks>
///     The shape ScyllaDB is built for: writes append to the end of a partition, and the common read — "the
///     last N readings from this sensor" — is a single seek followed by a sequential scan.
/// </remarks>
public class Reading
{
    public Guid SensorId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public double Value { get; set; }

    public string Status { get; set; } = "ok";
}

/// <summary>
///     The same readings keyed by status instead of sensor.
/// </summary>
/// <remarks>
///     "Which readings are in alarm?" cannot be answered from <see cref="Reading" /> without scanning every
///     partition, because status is not part of its key. A materialized view stores the same rows under a key
///     that can answer it, and ScyllaDB keeps the two in step.
/// </remarks>
public class ReadingByStatus
{
    public Guid SensorId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public double Value { get; set; }

    public string Status { get; set; } = "ok";
}

/// <summary>
///     A session row, used to show time-to-live and lightweight transactions.
/// </summary>
public class Session
{
    public Guid Id { get; set; }

    public string User { get; set; } = string.Empty;

    public int Version { get; set; }
}

public class MetricsContext(DbContextOptions<MetricsContext> options) : DbContext(options)
{
    public DbSet<Reading> Readings
        => Set<Reading>();

    public DbSet<ReadingByStatus> ReadingsByStatus
        => Set<ReadingByStatus>();

    public DbSet<Session> Sessions
        => Set<Session>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultKeyspace("sample_metrics");

        modelBuilder.Entity<Reading>(e =>
        {
            e.ToScyllaTable("readings");
            e.HasPartitionKey(r => r.SensorId);
            e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
        });

        modelBuilder.Entity<ReadingByStatus>(e =>
        {
            e.ToMaterializedView("readings_by_status", baseTableName: "readings");
            e.HasPartitionKey(r => r.Status);
            e.HasClusteringKey(r => r.SensorId);
            e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToScyllaTable("sessions");
            e.HasPartitionKey(s => s.Id);

            // Makes updates and deletes conditional, which is the only optimistic concurrency CQL can offer:
            // an ordinary write is a blind upsert that reports no affected-row count.
            e.Property(s => s.Version).IsConcurrencyToken();
            e.UseInsertIfNotExists();
        });
    }
}
