using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;
using YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;
using YC.EntityFrameworkCore.ScyllaDB.Update.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Update;

/// <summary>
///     Asserts the CQL produced for time-to-live, write timestamps, and lightweight transactions.
/// </summary>
public class WriteOptionsTests
{
    private sealed class Document
    {
        public Guid Id { get; set; }

        public string Body { get; set; } = string.Empty;

        public int Version { get; set; }
    }

    private static DbContext CreateContext(bool concurrencyToken = false, bool insertIfNotExists = false)
        => ScyllaModelFixture.CreateContext(
            b => b.Entity<Document>(e =>
            {
                e.ToScyllaTable("documents");
                e.HasPartitionKey(d => d.Id);

                if (concurrencyToken)
                {
                    e.Property(d => d.Version).IsConcurrencyToken();

                    // Required alongside a concurrency token: Paxos orders lightweight transactions only
                    // against each other, so a partition also written by ordinary inserts falls outside the
                    // guarantee. The model validator enforces the pairing.
                    e.UseInsertIfNotExists();
                }

                if (insertIfNotExists)
                {
                    e.UseInsertIfNotExists();
                }
            }));

    private static ScyllaWriteOperation OperationFor(
        DbContext context,
        object entity,
        ScyllaWriteOptions? options = null)
        => ScyllaDmlGenerator.Create(
            (IUpdateEntry)((IInfrastructure<InternalEntityEntry>)context.Entry(entity)).Instance, options);

    [Fact]
    public void Time_to_live_is_emitted_in_seconds()
    {
        using var context = CreateContext();
        var document = new Document { Id = Guid.NewGuid(), Body = "hello" };
        context.Add(document);

        var operation = OperationFor(context, document, new ScyllaWriteOptions(TimeToLive: TimeSpan.FromMinutes(5)));

        Assert.Contains("USING TTL ?", operation.Cql, StringComparison.Ordinal);
        Assert.Equal(300, operation.Values[^1]);
    }

    /// <summary>
    ///     CQL takes write timestamps in microseconds since the epoch, not milliseconds.
    /// </summary>
    [Fact]
    public void Write_timestamp_is_emitted_in_microseconds()
    {
        using var context = CreateContext();
        var document = new Document { Id = Guid.NewGuid(), Body = "hello" };
        context.Add(document);
        var timestamp = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        var operation = OperationFor(context, document, new ScyllaWriteOptions(Timestamp: timestamp));

        Assert.Contains("USING TIMESTAMP ?", operation.Cql, StringComparison.Ordinal);
        Assert.Equal(timestamp.ToUnixTimeMilliseconds() * 1000, operation.Values[^1]);
    }

    [Fact]
    public void Both_options_are_combined_in_one_using_clause()
    {
        using var context = CreateContext();
        var document = new Document { Id = Guid.NewGuid(), Body = "hello" };
        context.Add(document);

        var operation = OperationFor(
            context,
            document,
            new ScyllaWriteOptions(TimeSpan.FromSeconds(60), DateTimeOffset.UnixEpoch));

        Assert.Contains("USING TTL ? AND TIMESTAMP ?", operation.Cql, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A delete leaves nothing behind that could expire, so CQL rejects TTL there.
    /// </summary>
    [Fact]
    public void Delete_ignores_time_to_live_but_keeps_the_timestamp()
    {
        using var context = CreateContext();
        var document = new Document { Id = Guid.NewGuid() };
        context.Attach(document);
        context.Remove(document);

        var operation = OperationFor(
            context,
            document,
            new ScyllaWriteOptions(TimeSpan.FromSeconds(60), DateTimeOffset.UnixEpoch));

        Assert.DoesNotContain("TTL", operation.Cql, StringComparison.Ordinal);
        Assert.Contains("USING TIMESTAMP ?", operation.Cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_if_not_exists_becomes_a_conditional_write()
    {
        using var context = CreateContext(insertIfNotExists: true);
        var document = new Document { Id = Guid.NewGuid(), Body = "hello" };
        context.Add(document);

        var operation = OperationFor(context, document);

        Assert.Contains("IF NOT EXISTS", operation.Cql, StringComparison.Ordinal);
        Assert.True(operation.IsConditional);
    }

    /// <summary>
    ///     The condition compares against the original value, which is what makes it detect a competing write.
    /// </summary>
    [Fact]
    public void Concurrency_token_turns_an_update_into_a_lightweight_transaction()
    {
        using var context = CreateContext(concurrencyToken: true);
        var document = new Document { Id = Guid.NewGuid(), Body = "before", Version = 1 };
        context.Attach(document);
        document.Body = "after";
        document.Version = 2;

        var operation = OperationFor(context, document);

        Assert.Contains("IF \"Version\" = ?", operation.Cql, StringComparison.Ordinal);
        Assert.Equal(1, operation.Values[^1]);
        Assert.True(operation.IsConditional);
    }

    [Fact]
    public void Concurrency_token_also_guards_a_delete()
    {
        using var context = CreateContext(concurrencyToken: true);
        var document = new Document { Id = Guid.NewGuid(), Version = 7 };
        context.Attach(document);
        context.Remove(document);

        var operation = OperationFor(context, document);

        Assert.Contains("IF \"Version\" = ?", operation.Cql, StringComparison.Ordinal);
        Assert.True(operation.IsConditional);
    }

    /// <summary>
    ///     Paxos assigns its own timestamps to keep ballots ordered, so ScyllaDB refuses a custom one on a
    ///     conditional write. Saying so here beats surfacing the server's terser complaint.
    /// </summary>
    [Fact]
    public void Write_timestamp_on_a_conditional_write_is_refused()
    {
        using var context = CreateContext(concurrencyToken: true);
        var document = new Document { Id = Guid.NewGuid(), Body = "before", Version = 1 };
        context.Attach(document);
        document.Body = "after";

        var exception = Assert.Throws<InvalidOperationException>(
            () => OperationFor(context, document, new ScyllaWriteOptions(Timestamp: DateTimeOffset.UnixEpoch)));

        Assert.Contains("Paxos", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_options_scope_applies_then_stops_applying()
    {
        using var context = CreateContext();
        var accessor = context.GetService<IScyllaWriteOptionsAccessor>();

        Assert.Null(accessor.Current);

        using (context.UseScyllaWriteOptions(new ScyllaWriteOptions(TimeToLive: TimeSpan.FromMinutes(1))))
        {
            Assert.Equal(TimeSpan.FromMinutes(1), accessor.Current!.TimeToLive);
        }

        Assert.Null(accessor.Current);
    }
}
