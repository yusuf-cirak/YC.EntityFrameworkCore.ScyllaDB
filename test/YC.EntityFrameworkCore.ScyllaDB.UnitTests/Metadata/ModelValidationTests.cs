using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Metadata;

/// <summary>
///     A model CQL cannot express should fail while the model is being built, not on the first query. Each
///     message must also say how to fix the problem — these assert that, not just that something threw.
/// </summary>
public class ModelValidationTests
{
    private sealed class Author
    {
        public Guid Id { get; set; }

        public List<Book> Books { get; set; } = [];
    }

    private sealed class Book
    {
        public Guid Id { get; set; }

        public Author? Author { get; set; }
    }

    private sealed class WithUntypedCollection
    {
        public Guid Id { get; set; }

        public IEnumerable<string> Tags { get; set; } = [];
    }

    [Fact]
    public void Entity_type_without_a_partition_key_is_rejected()
    {
        var exception = ScyllaModelFixture.CaptureModelError(b => b.Entity<Reading>());

        Assert.NotNull(exception);
        Assert.Contains("has no partition key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("HasPartitionKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Navigations_are_rejected_with_an_explanation()
    {
        var exception = ScyllaModelFixture.CaptureModelError(
            b =>
            {
                b.Entity<Author>().HasPartitionKey(a => a.Id);
                b.Entity<Book>().HasPartitionKey(x => x.Id);
            });

        Assert.NotNull(exception);
        Assert.Contains("CQL has no joins", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Denormalise", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A list keeps order and duplicates; a set does not. Guessing between them would silently pick storage
    ///     semantics the model never asked for.
    /// </summary>
    [Fact]
    public void Ambiguous_collection_asks_which_CQL_collection_was_meant()
    {
        var exception = ScyllaModelFixture.CaptureModelError(
            b => b.Entity<WithUntypedCollection>().HasPartitionKey(x => x.Id));

        Assert.NotNull(exception);
        Assert.Contains("either a CQL list or a CQL set", exception.Message, StringComparison.Ordinal);
        Assert.Contains("IsList()", exception.Message, StringComparison.Ordinal);
        Assert.Contains("IsSet()", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicitly_marking_the_collection_kind_resolves_the_ambiguity()
    {
        var exception = ScyllaModelFixture.CaptureModelError(
            b => b.Entity<WithUntypedCollection>(e =>
            {
                e.HasPartitionKey(x => x.Id);
                e.Property(x => x.Tags).IsSet();
            }));

        Assert.Null(exception);
    }

    [Fact]
    public void Static_column_without_a_clustering_key_is_rejected()
    {
        var exception = ScyllaModelFixture.CaptureModelError(
            b => b.Entity<Event>(e =>
            {
                e.HasPartitionKey(x => x.TenantId);
                e.Property(x => x.TenantName).IsStaticColumn();
            }));

        Assert.NotNull(exception);
        Assert.Contains("one value per partition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Counter_mixed_with_a_regular_column_is_rejected()
    {
        var exception = ScyllaModelFixture.CaptureModelError(
            b => b.Entity<Reading>(e =>
            {
                e.HasPartitionKey(r => r.SensorId);
                e.Property(r => r.Value).IsCounter();
            }));

        Assert.NotNull(exception);
        Assert.Contains("all counters plus its primary key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Counter_table_with_only_counters_is_accepted()
    {
        var exception = ScyllaModelFixture.CaptureModelError(
            b => b.Entity<PageView>(e =>
            {
                e.HasPartitionKey(p => p.Path);
                e.Property(p => p.Views).IsCounter();
            }));

        Assert.Null(exception);
    }

    [Fact]
    public void Counter_table_cannot_declare_a_default_time_to_live()
    {
        var exception = ScyllaModelFixture.CaptureModelError(
            b => b.Entity<PageView>(e =>
            {
                e.HasPartitionKey(p => p.Path);
                e.Property(p => p.Views).IsCounter();
                e.HasDefaultTimeToLive(TimeSpan.FromMinutes(5));
            }));

        Assert.NotNull(exception);
        Assert.Contains("Counters have no expiry", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     In CQL the primary key is the physical layout, not an identity constraint: partition columns first in
    ///     declaration order, then clustering columns in theirs.
    /// </summary>
    [Fact]
    public void Primary_key_is_the_partition_key_followed_by_the_clustering_keys()
    {
        var model = ScyllaModelFixture.BuildModel(
            b => b.Entity<Event>(e =>
            {
                e.HasPartitionKey(x => new { x.TenantId, x.Bucket });
                e.HasClusteringKey(x => x.OccurredAt, ScyllaClusteringOrder.Descending);
                e.HasClusteringKey(x => x.EventId);
                e.Ignore(x => x.TenantName);
            }));

        var key = model.FindEntityType(typeof(Event))!.FindPrimaryKey()!;

        Assert.Equal(
            [nameof(Event.TenantId), nameof(Event.Bucket), nameof(Event.OccurredAt), nameof(Event.EventId)],
            key.Properties.Select(p => p.Name));
    }

    /// <summary>
    ///     The keyspace was already named when configuring the connection; requiring it again in
    ///     OnModelCreating would be a redundancy that is easy to get wrong.
    /// </summary>
    [Fact]
    public void Keyspace_from_the_connection_options_becomes_the_model_default()
    {
        var model = ScyllaModelFixture.BuildModel(
            b => b.Entity<Reading>().HasPartitionKey(r => r.SensorId),
            keyspace: "configured_keyspace");

        Assert.Equal("configured_keyspace", model.GetDefaultKeyspace());
        Assert.Equal("configured_keyspace", model.FindEntityType(typeof(Reading))!.GetKeyspace());
    }

    [Fact]
    public void Explicit_model_keyspace_wins_over_the_connection_default()
    {
        var model = ScyllaModelFixture.BuildModel(
            b =>
            {
                b.HasDefaultKeyspace("model_keyspace");
                b.Entity<Reading>().HasPartitionKey(r => r.SensorId);
            },
            keyspace: "configured_keyspace");

        Assert.Equal("model_keyspace", model.GetDefaultKeyspace());
    }
}
