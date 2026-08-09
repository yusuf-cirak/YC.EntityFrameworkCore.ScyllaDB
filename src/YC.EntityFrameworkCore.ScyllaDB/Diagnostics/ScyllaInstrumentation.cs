using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cassandra;

namespace YC.EntityFrameworkCore.ScyllaDB.Diagnostics;

/// <summary>
///     What a call site knows about the statement it is about to send.
/// </summary>
/// <param name="OperationName">
///     The CQL verb, for example <c>SELECT</c> or <c>CREATE TABLE</c>. Derived from
///     <paramref name="QueryText" /> when null.
/// </param>
/// <param name="Keyspace">The keyspace the statement targets.</param>
/// <param name="Table">The table, for statements that touch exactly one.</param>
/// <param name="QueryText">The CQL, with bind markers rather than values.</param>
/// <remarks>
///     The client wrapper receives an <see cref="IStatement" />, which does not carry the keyspace, table or
///     operation in a form worth parsing back out. The call sites do know them, so they pass them along rather
///     than the wrapper guessing.
/// </remarks>
public readonly record struct ScyllaOperationContext(
    string? OperationName = null,
    string? Keyspace = null,
    string? Table = null,
    string? QueryText = null);

/// <summary>
///     The provider's <see cref="ActivitySource" /> and <see cref="Meter" />.
/// </summary>
/// <remarks>
///     <para>
///         Both are named <see cref="Name" />, which is the string a consumer passes to
///         <c>AddSource</c> and <c>AddMeter</c>. That makes OpenTelemetry a listener rather than a dependency —
///         nothing here references it — and lets <c>dotnet-trace</c> and <c>dotnet-counters</c> work unchanged.
///     </para>
///     <para>
///         When nothing is listening, <see cref="ActivitySource.StartActivity(string, ActivityKind)" /> returns
///         null and no attribute is ever computed, so instrumentation is always on and costs nothing unobserved.
///     </para>
/// </remarks>
public static class ScyllaInstrumentation
{
    /// <summary>
    ///     The name of both the activity source and the meter. Part of the public contract: a consumer writes
    ///     this into their telemetry configuration.
    /// </summary>
    public const string Name = "YC.EntityFrameworkCore.ScyllaDB";

    private static readonly string Version =
        typeof(ScyllaInstrumentation).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static readonly ActivitySource Source = new(Name, Version);

    private static readonly Meter Meter = new(Name, Version);

    /// <summary>
    ///     Duration of database client operations, as defined by the database semantic conventions.
    /// </summary>
    /// <remarks>
    ///     One histogram answers rate, errors and duration together. Separate counters would be derivable from
    ///     it and would only add cardinality.
    /// </remarks>
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "db.client.operation.duration",
        unit: "s",
        description: "Duration of database client operations.");

    /// <summary>
    ///     Starts a client span for a statement, or returns null when nothing is listening.
    /// </summary>
    public static Activity? StartActivity(in ScyllaOperationContext context, IStatement? statement)
    {
        var operation = context.OperationName ?? DeriveOperationName(context.QueryText);
        var activity = Source.StartActivity(SpanName(operation, context), ActivityKind.Client);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("db.system.name", "cassandra");

        if (operation is not null)
        {
            activity.SetTag("db.operation.name", operation);
        }

        if (context.Keyspace is { Length: > 0 })
        {
            activity.SetTag("db.namespace", context.Keyspace);
        }

        if (context.Table is { Length: > 0 })
        {
            activity.SetTag("db.collection.name", context.Table);
        }

        if (statement is not null)
        {
            if (statement.PageSize > 0)
            {
                activity.SetTag("cassandra.page.size", statement.PageSize);
            }

            if (statement.IsIdempotent is { } idempotent)
            {
                activity.SetTag("cassandra.query.idempotent", idempotent);
            }

            if (statement is BatchStatement batch)
            {
                activity.SetTag("db.operation.batch.size", batch.Statements.Count());
            }
        }

        return activity;
    }

    /// <summary>
    ///     Records what the response tells us: the consistency actually achieved, and how many rows came back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The achieved consistency is not necessarily the one configured — the driver applies its own
    ///         default when none was set, and that default is what a reader needs to see.
    ///     </para>
    ///     <para>
    ///         <see cref="RowSet.GetAvailableWithoutFetching" /> counts the rows already buffered from this
    ///         response without advancing the enumerator or fetching another page. CQL is a paged protocol, so
    ///         "rows returned by the operation" is the page; counting the whole result set would mean draining
    ///         it, which is neither free nor what the attribute means.
    ///     </para>
    /// </remarks>
    public static void RecordResult(Activity? activity, RowSet rowSet)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(
            "cassandra.consistency.level",
            rowSet.Info.AchievedConsistency.ToString().ToLowerInvariant());

        activity.SetTag("db.response.returned_rows", rowSet.GetAvailableWithoutFetching());
    }

    /// <summary>
    ///     Marks the span as failed and records the exception type.
    /// </summary>
    /// <remarks>
    ///     The driver exposes no protocol error code on a common base type, so <c>db.response.status_code</c>
    ///     is not emitted; the exception type is what identifies the failure consistently.
    /// </remarks>
    public static void RecordException(Activity? activity, Exception exception)
    {
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    /// <summary>
    ///     Records how long an operation took, whether it succeeded or failed.
    /// </summary>
    public static void RecordDuration(TimeSpan elapsed, in ScyllaOperationContext context, string? errorType)
    {
        if (!OperationDuration.Enabled)
        {
            return;
        }

        var tags = new TagList { { "db.system.name", "cassandra" } };

        if ((context.OperationName ?? DeriveOperationName(context.QueryText)) is { } operation)
        {
            tags.Add("db.operation.name", operation);
        }

        if (context.Keyspace is { Length: > 0 })
        {
            tags.Add("db.namespace", context.Keyspace);
        }

        if (context.Table is { Length: > 0 })
        {
            tags.Add("db.collection.name", context.Table);
        }

        if (errorType is not null)
        {
            tags.Add("error.type", errorType);
        }

        OperationDuration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>
    ///     Builds a low-cardinality span name, dropping the parts that are unknown.
    /// </summary>
    private static string SpanName(string? operation, in ScyllaOperationContext context)
        => (operation, context.Keyspace, context.Table) switch
        {
            (null, _, _) => "CQL",
            ({ } op, { Length: > 0 } keyspace, { Length: > 0 } table) => $"{op} {keyspace}.{table}",
            ({ } op, { Length: > 0 } keyspace, _) => $"{op} {keyspace}",
            ({ } op, _, _) => op
        };

    /// <summary>
    ///     Reads the CQL verb off the front of a statement.
    /// </summary>
    /// <remarks>
    ///     DDL verbs are two words — <c>CREATE TABLE</c>, <c>DROP KEYSPACE</c> — and the distinction is worth
    ///     keeping, so those take a second token.
    /// </remarks>
    private static string? DeriveOperationName(string? cql)
    {
        if (cql is null)
        {
            return null;
        }

        var tokens = cql.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return null;
        }

        var first = tokens[0].ToUpperInvariant();

        return first is "CREATE" or "DROP" or "ALTER" && tokens.Length > 1
            ? $"{first} {tokens[1].ToUpperInvariant()}"
            : first;
    }
}
