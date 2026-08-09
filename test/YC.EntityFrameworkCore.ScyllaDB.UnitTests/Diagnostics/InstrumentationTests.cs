using System.Diagnostics;
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Diagnostics;

/// <summary>
///     The source name is a public contract: consumers write it into their OpenTelemetry configuration, so
///     changing it silently turns their telemetry off.
/// </summary>
public class InstrumentationTests
{
    private static (List<Activity> Captured, ActivityListener Listener) Listen()
    {
        var captured = new List<Activity>();

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ScyllaInstrumentation.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add
        };

        ActivitySource.AddActivityListener(listener);

        return (captured, listener);
    }

    [Fact]
    public void Source_is_named_for_the_package()
    {
        Assert.Equal("YC.EntityFrameworkCore.ScyllaDB", ScyllaInstrumentation.Name);
    }

    [Fact]
    public void Activity_carries_the_database_semantic_convention_attributes()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            using (var activity = ScyllaInstrumentation.StartActivity(
                       new ScyllaOperationContext("SELECT", "metrics", "readings", "SELECT * FROM x"),
                       statement: null))
            {
                Assert.NotNull(activity);
            }
        }

        var recorded = Assert.Single(captured);

        Assert.Equal("SELECT metrics.readings", recorded.DisplayName);
        Assert.Equal(ActivityKind.Client, recorded.Kind);
        Assert.Equal("cassandra", recorded.GetTagItem("db.system.name"));
        Assert.Equal("metrics", recorded.GetTagItem("db.namespace"));
        Assert.Equal("readings", recorded.GetTagItem("db.collection.name"));
        Assert.Equal("SELECT", recorded.GetTagItem("db.operation.name"));
    }

    /// <summary>
    ///     Span names must stay low-cardinality, so a missing table or keyspace drops out of the name rather
    ///     than appearing as a placeholder.
    /// </summary>
    [Fact]
    public void Activity_name_drops_the_parts_it_does_not_have()
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            ScyllaInstrumentation
                .StartActivity(new ScyllaOperationContext("CREATE KEYSPACE", "metrics", null, null), null)
                ?.Dispose();

            ScyllaInstrumentation
                .StartActivity(new ScyllaOperationContext("SELECT", null, null, null), null)
                ?.Dispose();
        }

        Assert.Equal("CREATE KEYSPACE metrics", captured[0].DisplayName);
        Assert.Equal("SELECT", captured[1].DisplayName);
    }

    /// <summary>
    ///     The operation name is what makes a span readable in a trace view, so it is derived from the CQL when
    ///     the call site did not supply one.
    /// </summary>
    [Theory]
    [InlineData("SELECT \"a\" FROM \"k\".\"t\"", "SELECT")]
    [InlineData("INSERT INTO \"k\".\"t\" (\"a\") VALUES (?)", "INSERT")]
    [InlineData("CREATE TABLE IF NOT EXISTS \"k\".\"t\" (a int)", "CREATE TABLE")]
    [InlineData("DROP KEYSPACE IF EXISTS \"k\"", "DROP KEYSPACE")]
    public void Operation_name_is_derived_from_the_statement_when_not_supplied(string cql, string expected)
    {
        var (captured, listener) = Listen();
        using (listener)
        {
            ScyllaInstrumentation
                .StartActivity(new ScyllaOperationContext(null, "k", null, cql), null)
                ?.Dispose();
        }

        Assert.Equal(expected, Assert.Single(captured).GetTagItem("db.operation.name"));
    }

    /// <summary>
    ///     Nothing listening means no activity is created at all, which is what makes always-on instrumentation
    ///     free.
    /// </summary>
    [Fact]
    public void No_activity_is_created_when_nothing_is_listening()
    {
        Assert.Null(
            ScyllaInstrumentation.StartActivity(
                new ScyllaOperationContext("SELECT", "metrics", "readings", null), statement: null));
    }
}
