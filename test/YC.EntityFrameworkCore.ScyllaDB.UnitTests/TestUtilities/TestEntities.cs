using YC.EntityFrameworkCore.ScyllaDB;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;

/// <summary>
///     A time-series shape: readings partitioned by sensor, ordered newest first within a partition.
/// </summary>
public class Reading
{
    public Guid SensorId { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public double Value { get; set; }

    public string? Status { get; set; }
}

/// <summary>
///     The same shape declared with attributes, used to prove the two configuration styles agree.
/// </summary>
[ScyllaTable("readings")]
public class AttributedReading
{
    [PartitionKey]
    public Guid SensorId { get; set; }

    [ClusteringKey(order: 0, direction: ScyllaClusteringOrder.Descending)]
    public DateTimeOffset RecordedAt { get; set; }

    public double Value { get; set; }
}

/// <summary>
///     A composite partition key with two clustering columns, plus a static column that belongs to the whole
///     partition rather than to any one row.
/// </summary>
public class Event
{
    public string TenantId { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public Guid EventId { get; set; }

    public string TenantName { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;
}

public class PageView
{
    public string Path { get; set; } = string.Empty;

    public long Views { get; set; }
}
