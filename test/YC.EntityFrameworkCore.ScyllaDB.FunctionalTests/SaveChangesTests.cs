using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Writes through the provider and reads back with the driver.
/// </summary>
/// <remarks>
///     Reading through the provider would be circular — and the query pipeline does not exist yet — so every
///     assertion below goes to the database directly.
/// </remarks>
[Collection(ScyllaCollection.Name)]
public class SaveChangesTests(ScyllaContainerFixture fixture)
{
    private sealed class Reading
    {
        public Guid SensorId { get; set; }

        public DateTimeOffset RecordedAt { get; set; }

        public double Value { get; set; }

        public string? Status { get; set; }
    }

    private sealed class PageView
    {
        public string Path { get; set; } = string.Empty;

        public long Views { get; set; }
    }

    private sealed class WriteContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
            });

            modelBuilder.Entity<PageView>(e =>
            {
                e.ToScyllaTable("page_views");
                e.HasPartitionKey(p => p.Path);
                e.Property(p => p.Views).IsCounter();
            });
        }
    }

    private WriteContext CreateContext(string keyspace, Action<Infrastructure.ScyllaDbContextOptionsBuilder>? configure = null)
        => new(
            new DbContextOptionsBuilder<WriteContext>()
                .UseScyllaDb(
                    fixture.ContactPoints, keyspace, o =>
                    {
                        o.WithPort(fixture.Port);
                        configure?.Invoke(o);
                    })
                .Options);

    private static async Task<RowSet> QueryAsync(
        DbContext context,
        string cql,
        CancellationToken cancellationToken,
        params object[] values)
        => await context.GetService<IScyllaClientWrapper>()
            .ExecuteAsync(new SimpleStatement(cql, values), cancellationToken);

    [Fact]
    public async Task Added_entity_is_written_and_readable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("save_insert_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var sensorId = Guid.NewGuid();
        var recordedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        context.Add(new Reading { SensorId = sensorId, RecordedAt = recordedAt, Value = 21.5, Status = "ok" });
        var written = await context.SaveChangesAsync(cancellationToken);

        Assert.Equal(1, written);

        var rows = await QueryAsync(
            context,
            "SELECT \"Value\", \"Status\" FROM save_insert_tests.\"readings\" WHERE \"SensorId\" = ?",
            cancellationToken,
            sensorId);

        var row = Assert.Single(rows);
        Assert.Equal(21.5, row.GetValue<double>("Value"));
        Assert.Equal("ok", row.GetValue<string>("Status"));
    }

    [Fact]
    public async Task Modified_entity_leaves_untouched_columns_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("save_update_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var sensorId = Guid.NewGuid();
        var recordedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        context.Add(new Reading { SensorId = sensorId, RecordedAt = recordedAt, Value = 1, Status = "ok" });
        await context.SaveChangesAsync(cancellationToken);

        await using var updateContext = CreateContext("save_update_tests");
        var tracked = new Reading { SensorId = sensorId, RecordedAt = recordedAt, Value = 1, Status = "ok" };
        updateContext.Attach(tracked);
        tracked.Value = 99;
        await updateContext.SaveChangesAsync(cancellationToken);

        var rows = await QueryAsync(
            updateContext,
            "SELECT \"Value\", \"Status\" FROM save_update_tests.\"readings\" WHERE \"SensorId\" = ?",
            cancellationToken,
            sensorId);

        var row = Assert.Single(rows);
        Assert.Equal(99, row.GetValue<double>("Value"));
        Assert.Equal("ok", row.GetValue<string>("Status"));
    }

    [Fact]
    public async Task Deleted_entity_is_removed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("save_delete_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var sensorId = Guid.NewGuid();
        var recordedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var reading = new Reading { SensorId = sensorId, RecordedAt = recordedAt, Value = 1 };

        context.Add(reading);
        await context.SaveChangesAsync(cancellationToken);

        context.Remove(reading);
        await context.SaveChangesAsync(cancellationToken);

        var rows = await QueryAsync(
            context,
            "SELECT \"SensorId\" FROM save_delete_tests.\"readings\" WHERE \"SensorId\" = ?",
            cancellationToken,
            sensorId);

        Assert.Empty(rows);
    }

    /// <summary>
    ///     Rows sharing a partition go out as one unlogged batch, which ScyllaDB applies atomically.
    /// </summary>
    [Fact]
    public async Task Rows_in_one_partition_are_written_together()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("save_batch_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var sensorId = Guid.NewGuid();
        var baseTime = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            context.Add(new Reading { SensorId = sensorId, RecordedAt = baseTime.AddMinutes(i), Value = i });
        }

        Assert.Equal(5, await context.SaveChangesAsync(cancellationToken));

        var rows = await QueryAsync(
            context,
            "SELECT \"Value\" FROM save_batch_tests.\"readings\" WHERE \"SensorId\" = ?",
            cancellationToken,
            sensorId);

        Assert.Equal(5, rows.Count());
    }

    [Fact]
    public async Task Rows_across_partitions_are_all_written()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("save_multi_partition_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var recordedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var sensorIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();

        foreach (var sensorId in sensorIds)
        {
            context.Add(new Reading { SensorId = sensorId, RecordedAt = recordedAt, Value = 1 });
        }

        Assert.Equal(4, await context.SaveChangesAsync(cancellationToken));

        foreach (var sensorId in sensorIds)
        {
            var rows = await QueryAsync(
                context,
                "SELECT \"SensorId\" FROM save_multi_partition_tests.\"readings\" WHERE \"SensorId\" = ?",
                cancellationToken,
                sensorId);

            Assert.Single(rows);
        }
    }

    [Fact]
    public async Task Logged_batch_mode_still_writes_everything()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext(
            "save_logged_batch_tests", o => o.WithBatchMode(ScyllaBatchMode.Logged));
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var recordedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var sensorIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();

        foreach (var sensorId in sensorIds)
        {
            context.Add(new Reading { SensorId = sensorId, RecordedAt = recordedAt, Value = 1 });
        }

        Assert.Equal(3, await context.SaveChangesAsync(cancellationToken));

        var rows = await QueryAsync(
            context,
            "SELECT \"SensorId\" FROM save_logged_batch_tests.\"readings\"",
            cancellationToken);

        Assert.Equal(3, rows.Count());
    }

    /// <summary>
    ///     CQL has no read-modify-write for counters, so the provider sends the difference against EF Core's
    ///     snapshot. Two saves must therefore accumulate rather than overwrite.
    /// </summary>
    [Fact]
    public async Task Counter_accumulates_across_saves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("save_counter_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var pageView = new PageView { Path = "/home", Views = 5 };
        context.Add(pageView);
        await context.SaveChangesAsync(cancellationToken);

        pageView.Views += 7;
        await context.SaveChangesAsync(cancellationToken);

        var rows = await QueryAsync(
            context,
            "SELECT \"Views\" FROM save_counter_tests.\"page_views\" WHERE \"Path\" = ?",
            cancellationToken,
            "/home");

        Assert.Equal(12L, Assert.Single(rows).GetValue<long>("Views"));
    }
}
