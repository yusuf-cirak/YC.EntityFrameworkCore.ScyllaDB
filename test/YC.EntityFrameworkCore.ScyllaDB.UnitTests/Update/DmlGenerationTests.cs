using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;
using YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;
using YC.EntityFrameworkCore.ScyllaDB.Update.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Update;

/// <summary>
///     Asserts the CQL each tracked change produces, with no driver involved.
/// </summary>
public class DmlGenerationTests
{
    private static readonly Guid SensorId = new("11111111-1111-1111-1111-111111111111");

    private static DbContext CreateContext()
        => ScyllaModelFixture.CreateContext(
            b =>
            {
                b.Entity<Reading>(e =>
                {
                    e.ToScyllaTable("readings");
                    e.HasPartitionKey(r => r.SensorId);
                    e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
                });

                b.Entity<PageView>(e =>
                {
                    e.ToScyllaTable("page_views");
                    e.HasPartitionKey(p => p.Path);
                    e.Property(p => p.Views).IsCounter();
                });
            });

    private static ScyllaWriteOperation OperationFor(DbContext context, object entity)
        => ScyllaDmlGenerator.Create(
            (IUpdateEntry)((IInfrastructure<InternalEntityEntry>)context.Entry(entity)).Instance);

    [Fact]
    public void Added_entity_produces_an_insert_with_every_column()
    {
        using var context = CreateContext();
        var reading = new Reading { SensorId = SensorId, RecordedAt = DateTimeOffset.UnixEpoch, Value = 1.5 };
        context.Add(reading);

        var operation = OperationFor(context, reading);

        Assert.StartsWith(
            "INSERT INTO \"test_keyspace\".\"readings\"", operation.Cql, StringComparison.Ordinal);
        Assert.Contains("\"SensorId\"", operation.Cql, StringComparison.Ordinal);
        Assert.Contains("\"RecordedAt\"", operation.Cql, StringComparison.Ordinal);
        Assert.Contains("\"Value\"", operation.Cql, StringComparison.Ordinal);
        Assert.Contains("VALUES (?, ?, ?, ?)", operation.Cql, StringComparison.Ordinal);
        Assert.Equal(4, operation.Values.Length);
        Assert.False(operation.IsCounter);
    }

    /// <summary>
    ///     Sending only what changed matters more here than in SQL: a CQL update writes a cell per column, and
    ///     rewriting untouched columns creates needless tombstone and compaction pressure.
    /// </summary>
    [Fact]
    public void Modified_entity_updates_only_the_changed_columns()
    {
        using var context = CreateContext();
        var reading = new Reading { SensorId = SensorId, RecordedAt = DateTimeOffset.UnixEpoch, Value = 1.5 };
        context.Attach(reading);
        reading.Value = 2.5;

        var operation = OperationFor(context, reading);

        Assert.StartsWith("UPDATE \"test_keyspace\".\"readings\" SET ", operation.Cql, StringComparison.Ordinal);
        Assert.Contains("\"Value\" = ?", operation.Cql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\" = ?", operation.Cql, StringComparison.Ordinal);
        Assert.Contains(
            "WHERE \"SensorId\" = ? AND \"RecordedAt\" = ?", operation.Cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleted_entity_produces_a_delete_restricted_by_the_full_primary_key()
    {
        using var context = CreateContext();
        var reading = new Reading { SensorId = SensorId, RecordedAt = DateTimeOffset.UnixEpoch };
        context.Attach(reading);
        context.Remove(reading);

        var operation = OperationFor(context, reading);

        Assert.Equal(
            "DELETE FROM \"test_keyspace\".\"readings\" WHERE \"SensorId\" = ? AND \"RecordedAt\" = ?",
            operation.Cql);
        Assert.Equal(2, operation.Values.Length);
    }

    /// <summary>
    ///     A counter row cannot be inserted, only incremented into existence, so a newly added entity becomes
    ///     an increment from zero.
    /// </summary>
    [Fact]
    public void Added_counter_row_becomes_an_increment_from_zero()
    {
        using var context = CreateContext();
        var pageView = new PageView { Path = "/home", Views = 3 };
        context.Add(pageView);

        var operation = OperationFor(context, pageView);

        Assert.Equal(
            "UPDATE \"test_keyspace\".\"page_views\" SET \"Views\" = \"Views\" + ? WHERE \"Path\" = ?",
            operation.Cql);
        Assert.Equal(3L, operation.Values[0]);
        Assert.True(operation.IsCounter);
    }

    /// <summary>
    ///     CQL has no read-modify-write, so the increment has to be the difference against EF Core's
    ///     original-value snapshot — which is what makes ordinary <c>+=</c> assignment work.
    /// </summary>
    [Fact]
    public void Modified_counter_sends_the_difference_not_the_new_value()
    {
        using var context = CreateContext();
        var pageView = new PageView { Path = "/home", Views = 10 };
        context.Attach(pageView);
        pageView.Views = 14;

        var operation = OperationFor(context, pageView);

        Assert.Contains("\"Views\" = \"Views\" + ?", operation.Cql, StringComparison.Ordinal);
        Assert.Equal(4L, operation.Values[0]);
    }

    [Fact]
    public void Decreasing_a_counter_subtracts_rather_than_adding_a_negative()
    {
        using var context = CreateContext();
        var pageView = new PageView { Path = "/home", Views = 10 };
        context.Attach(pageView);
        pageView.Views = 4;

        var operation = OperationFor(context, pageView);

        Assert.Contains("\"Views\" = \"Views\" - ?", operation.Cql, StringComparison.Ordinal);
        Assert.Equal(6L, operation.Values[0]);
    }

    [Fact]
    public void Counter_marked_modified_but_unchanged_is_rejected()
    {
        using var context = CreateContext();
        var pageView = new PageView { Path = "/home", Views = 10 };
        context.Attach(pageView);
        context.Entry(pageView).State = EntityState.Modified;

        var exception = Assert.Throws<InvalidOperationException>(() => OperationFor(context, pageView));

        Assert.Contains("no counter moved", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Grouping for batching is by partition, so two rows in the same partition must compare equal and two
    ///     in different partitions must not.
    /// </summary>
    [Fact]
    public void Partition_key_identity_follows_the_partition_key_values()
    {
        using var context = CreateContext();

        var first = new Reading { SensorId = SensorId, RecordedAt = DateTimeOffset.UnixEpoch };
        var samePartition = new Reading { SensorId = SensorId, RecordedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        var otherPartition = new Reading { SensorId = Guid.NewGuid(), RecordedAt = DateTimeOffset.UnixEpoch };

        context.AddRange(first, samePartition, otherPartition);

        Assert.Equal(OperationFor(context, first).PartitionKey, OperationFor(context, samePartition).PartitionKey);
        Assert.NotEqual(
            OperationFor(context, first).PartitionKey, OperationFor(context, otherPartition).PartitionKey);
    }
}
