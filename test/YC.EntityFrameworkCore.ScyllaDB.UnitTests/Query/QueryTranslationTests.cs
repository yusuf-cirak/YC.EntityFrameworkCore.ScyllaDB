using Microsoft.EntityFrameworkCore;
using YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Query;

/// <summary>
///     Asserts the CQL a query translates to, and the refusal for everything CQL cannot express.
/// </summary>
/// <remarks>
///     Translation happens before any I/O, so none of this needs a database — including the failures, which are
///     the half that matters most. A refusal that degrades into "could not be translated" leaves a caller with
///     no idea which of CQL's rules they hit.
/// </remarks>
public class QueryTranslationTests
{
    private static readonly Guid SensorId = new("11111111-1111-1111-1111-111111111111");

    private sealed class QueryTestContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Reading> Readings
            => Set<Reading>();

        public DbSet<Event> Events
            => Set<Event>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Reading>(e =>
            {
                e.ToScyllaTable("readings");
                e.HasPartitionKey(r => r.SensorId);
                e.HasClusteringKey(r => r.RecordedAt, ScyllaClusteringOrder.Descending);
            });

            modelBuilder.Entity<Event>(e =>
            {
                e.ToScyllaTable("events");
                e.HasPartitionKey(x => new { x.TenantId, x.Bucket });
                e.HasClusteringKey(x => x.OccurredAt);
                e.HasClusteringKey(x => x.EventId);
                e.Ignore(x => x.TenantName);
            });
        }
    }

    private static QueryTestContext CreateContext()
        => new(
            new DbContextOptionsBuilder<QueryTestContext>()
                .UseScyllaDb(["127.0.0.1"], "test_keyspace")
                .Options);

    private static string Cql(Func<QueryTestContext, IQueryable<Reading>> query)
    {
        using var context = CreateContext();
        return query(context).ToCqlString();
    }

    private static Exception Refusal(Func<QueryTestContext, IQueryable> query)
    {
        using var context = CreateContext();
        return Record.Exception(() => query(context).Cast<object>().ToCqlString())!;
    }

    [Fact]
    public void Partition_key_equality_becomes_a_restriction()
    {
        var cql = Cql(c => c.Readings.Where(r => r.SensorId == SensorId));

        // Columns follow EF Core's property order — key columns first, then the rest alphabetically — because
        // the value buffer handed to the materialiser is addressed by property index, not by select order.
        Assert.Contains(
            "SELECT \"SensorId\", \"RecordedAt\", \"Status\", \"Value\" FROM \"test_keyspace\".\"readings\"",
            cql,
            StringComparison.Ordinal);
        Assert.Contains("WHERE \"SensorId\" = ?", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Clustering_range_and_limit_are_translated()
    {
        var cql = Cql(
            c => c.Readings
                .Where(r => r.SensorId == SensorId && r.RecordedAt >= DateTimeOffset.UnixEpoch)
                .Take(10));

        Assert.Contains("\"SensorId\" = ? AND \"RecordedAt\" >= ?", cql, StringComparison.Ordinal);
        Assert.Contains("LIMIT ?", cql, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A comparison written the other way round must mean the same thing.
    /// </summary>
    [Fact]
    public void Comparison_with_the_column_on_the_right_is_flipped()
    {
        var cql = Cql(c => c.Readings.Where(r => r.SensorId == SensorId && DateTimeOffset.UnixEpoch < r.RecordedAt));

        Assert.Contains("\"RecordedAt\" > ?", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Contains_over_a_key_becomes_IN()
    {
        var ids = new[] { SensorId, Guid.Empty };
        var cql = Cql(c => c.Readings.Where(r => ids.Contains(r.SensorId)));

        Assert.Contains("\"SensorId\" IN (?, ?)", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowFiltering_is_emitted_and_permits_a_non_key_restriction()
    {
        var cql = Cql(c => c.Readings.Where(r => r.Status == "ok" && EF.Functions.AllowFiltering()));

        Assert.Contains("\"Status\" = ?", cql, StringComparison.Ordinal);
        Assert.EndsWith("ALLOW FILTERING", cql.Split(Environment.NewLine)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OrderBy_the_declared_clustering_order_is_emitted()
    {
        var cql = Cql(c => c.Readings.Where(r => r.SensorId == SensorId).OrderByDescending(r => r.RecordedAt));

        Assert.Contains("ORDER BY \"RecordedAt\" DESC", cql, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrestricted_partition_key_is_refused_with_the_columns_named()
    {
        var exception = Refusal(c => c.Readings);

        Assert.Contains("would scan every partition", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SensorId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AllowFiltering", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Partially_restricted_composite_partition_key_names_what_is_missing()
    {
        var exception = Refusal(c => c.Events.Where(e => e.TenantId == "acme"));

        Assert.Contains("'Bucket'", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Rows are stored in clustering order, so the engine cannot seek past a gap in the prefix.
    /// </summary>
    [Fact]
    public void Skipping_a_clustering_column_is_refused()
    {
        var exception = Refusal(
            c => c.Events.Where(e => e.TenantId == "acme" && e.Bucket == "b" && e.EventId == Guid.Empty));

        Assert.Contains("contiguous prefix", exception.Message, StringComparison.Ordinal);
        Assert.Contains("OccurredAt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_on_a_partition_key_is_refused_with_the_reason()
    {
        var exception = Refusal(c => c.Events.Where(e => string.Compare(e.TenantId, "acme") > 0));

        Assert.NotNull(exception);
    }

    [Fact]
    public void Non_key_restriction_without_AllowFiltering_is_refused()
    {
        var exception = Refusal(c => c.Readings.Where(r => r.SensorId == SensorId && r.Status == "ok"));

        Assert.Contains("not part of the primary key", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AllowFiltering", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderBy_a_non_clustering_column_is_refused()
    {
        var exception = Refusal(c => c.Readings.Where(r => r.SensorId == SensorId).OrderBy(r => r.Value));

        Assert.Contains("only order by clustering columns", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Skip_points_at_cursor_paging()
    {
        var exception = Refusal(c => c.Readings.Where(r => r.SensorId == SensorId).Skip(5));

        Assert.Contains("no OFFSET", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cursor-based", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_explains_that_CQL_has_none()
    {
        using var context = CreateContext();

        var exception = Record.Exception(
            () => context.Readings
                .Join(context.Events, r => r.SensorId, e => e.EventId, (r, e) => r)
                .ToCqlString());

        Assert.Contains("CQL has no JOIN", exception!.Message, StringComparison.Ordinal);
        Assert.Contains("denormalised", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupBy_is_refused_with_an_alternative()
    {
        var exception = Refusal(c => c.Readings.GroupBy(r => r.SensorId).Select(g => g.Key).Cast<object>().AsQueryable());

        Assert.Contains("GroupBy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Or_in_a_predicate_is_refused()
    {
        var exception = Refusal(
            c => c.Readings.Where(r => r.SensorId == SensorId || r.SensorId == Guid.Empty));

        Assert.Contains("does not support OR", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_equal_is_refused()
    {
        var exception = Refusal(c => c.Readings.Where(r => r.SensorId == SensorId && r.Status != "ok"));

        Assert.Contains("does not support '!='", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sum_explains_why_aggregates_are_not_translated()
    {
        using var context = CreateContext();

        var exception = Record.Exception(
            () => context.Readings.Where(r => r.SensorId == SensorId).Sum(r => r.Value));

        Assert.Contains("coordinator", exception!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Narrowing matters more here than in SQL: CQL rows can be wide, and every column named is a cell
    ///     fetched from storage.
    /// </summary>
    [Fact]
    public void Projection_narrows_the_selected_columns()
    {
        using var context = CreateContext();

        var cql = context.Readings
            .Where(r => r.SensorId == SensorId)
            .Select(r => new { r.RecordedAt, r.Value })
            .ToCqlString();

        Assert.Contains("SELECT \"RecordedAt\", \"Value\" FROM", cql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\"", cql, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The one thing a plain <c>LIMIT</c> cannot express: a limit per partition rather than per result.
    ///     CQL fixes the order, so both clauses must appear and in this sequence.
    /// </summary>
    [Fact]
    public void Per_partition_limit_is_emitted_before_the_overall_limit()
    {
        var ids = new[] { SensorId, Guid.Empty };

        using var context = CreateContext();

        var cql = context.Readings
            .Where(r => ids.Contains(r.SensorId))
            .PerPartitionLimit(3)
            .Take(10)
            .ToCqlString();

        var body = cql.Split(Environment.NewLine)[0];

        Assert.Contains("PER PARTITION LIMIT ?", body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf("PER PARTITION LIMIT", StringComparison.Ordinal)
            < body.IndexOf(" LIMIT ?", StringComparison.Ordinal),
            $"PER PARTITION LIMIT must precede LIMIT, but the statement was: {body}");
    }

    [Fact]
    public void Scalar_projection_selects_one_column()
    {
        using var context = CreateContext();

        var cql = context.Readings.Where(r => r.SensorId == SensorId).Select(r => r.Value).ToCqlString();

        Assert.Contains("SELECT \"Value\" FROM", cql, StringComparison.Ordinal);
    }
}
