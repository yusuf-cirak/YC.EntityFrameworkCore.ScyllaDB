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
    /// <param name="context">What the caller knows about the statement.</param>
    /// <param name="statement">The prepared statement, for the attributes only it can supply.</param>
    /// <param name="emitQueryText">
    ///     Whether <see cref="ScyllaOperationContext.QueryText" /> is written to the span as
    ///     <c>db.query.text</c>. False for <c>WithQueryTextInTelemetry(false)</c>. The operation name is still
    ///     derived from the query text either way — that option is about not emitting the CQL, not about
    ///     losing the low-cardinality verb the span is named for.
    /// </param>
    /// <remarks>
    ///     <see cref="ActivitySource.HasListeners" /> is checked before anything else is computed: deriving the
    ///     operation name and building the span name are not free, and the whole point of
    ///     <see cref="ActivitySource" /> is that a caller pays nothing when no listener is attached. Skipping
    ///     straight to <c>StartActivity</c> would still return null in that case, but only after doing the
    ///     work this guard avoids.
    /// </remarks>
    public static Activity? StartActivity(
        in ScyllaOperationContext context,
        IStatement? statement,
        bool emitQueryText = true)
    {
        if (!Source.HasListeners())
        {
            return null;
        }

        var operation = context.OperationName ?? DeriveOperationName(context.QueryText);

        // `context` is an `in` parameter and cannot be captured by the lambda below, so the span name is
        // built from it here first.
        var spanName = SpanName(operation, context);

        // A listener's `Sample` callback runs inside `StartActivity` and is another extensibility point that
        // can throw, before the caller's own try block has even started. Routed through the same isolation as
        // every other telemetry step, so a throwing sampler still leaves the caller with no activity rather
        // than an exception.
        var activity = Observe(() => Source.StartActivity(spanName, ActivityKind.Client));

        if (activity is null)
        {
            return null;
        }

        // `context` is an `in` parameter and cannot be captured by the lambda below, so the fields the
        // lambda needs are copied out here first.
        var keyspace = context.Keyspace;
        var table = context.Table;
        var queryText = emitQueryText ? context.QueryText : null;

        Observe(() =>
        {
            activity.SetTag("db.system.name", "cassandra");

            if (operation is not null)
            {
                activity.SetTag("db.operation.name", operation);
            }

            if (keyspace is { Length: > 0 })
            {
                activity.SetTag("db.namespace", keyspace);
            }

            if (table is { Length: > 0 })
            {
                activity.SetTag("db.collection.name", table);
            }

            if (queryText is not null)
            {
                activity.SetTag("db.query.text", queryText);
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
        });

        return activity;
    }

    /// <summary>
    ///     Stops an activity that <see cref="StartActivity" /> returned.
    /// </summary>
    /// <remarks>
    ///     Disposing an <see cref="Activity" /> stops it, and stopping it is what invokes every listener's
    ///     <c>ActivityStopped</c> callback — where an exporter or processor actually runs. A <c>using</c>
    ///     declaration compiles to the <em>outermost</em> <c>finally</c> in the method that declares it, outside
    ///     any <c>try</c>/<c>catch</c>/<c>finally</c> written below it, so letting the runtime dispose the
    ///     activity implicitly would run a listener's callback where nothing catches it: a listener that throws
    ///     on stop would turn a successful query into a faulted task, and on a failure path would replace the
    ///     genuine driver exception with its own. Calling this explicitly from inside the caller's own
    ///     <c>finally</c>, routed through the same isolation as every other telemetry step, keeps that
    ///     impossible.
    /// </remarks>
    public static void StopActivity(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        Observe(activity.Dispose);
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

        // Narrowed to a local: the null check above does not carry into the lambda's captured parameter.
        var span = activity;

        Observe(() =>
        {
            span.SetTag(
                "cassandra.consistency.level",
                rowSet.Info.AchievedConsistency.ToString().ToLowerInvariant());

            span.SetTag("db.response.returned_rows", rowSet.GetAvailableWithoutFetching());
        });
    }

    /// <summary>
    ///     Marks the span as failed and records the exception type.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The driver exposes no protocol error code on a common base type, so <c>db.response.status_code</c>
    ///         is not emitted; the exception type is what identifies the failure consistently.
    ///     </para>
    ///     <para>
    ///         The status description carries only the exception's type name, never <see cref="Exception.Message" />.
    ///         Driver exceptions — an unavailable-replica message quoting the statement, a Paxos conflict
    ///         echoing the row that won — can and do embed values bound into the failed statement, and this
    ///         provider never emits parameter values in telemetry. The type name alone identifies the failure
    ///         without that risk.
    ///     </para>
    /// </remarks>
    public static void RecordException(Activity? activity, Exception exception)
    {
        var exceptionType = exception.GetType().FullName;

        Observe(() =>
        {
            activity?.SetTag("error.type", exceptionType);
            activity?.SetStatus(ActivityStatusCode.Error, exceptionType);
        });
    }

    /// <summary>
    ///     Records how long an operation took, whether it succeeded or failed.
    /// </summary>
    /// <remarks>
    ///     Mirrors <see cref="StartActivity" />'s <see cref="ActivitySource.HasListeners" /> guard:
    ///     <c>OperationDuration.Enabled</c> is checked before anything else, so a statement executed with no
    ///     meter listener pays neither for deriving the operation name nor for the closure and
    ///     <see cref="Action" /> this method would otherwise allocate on every call.
    /// </remarks>
    public static void RecordDuration(TimeSpan elapsed, in ScyllaOperationContext context, string? errorType)
    {
        if (!OperationDuration.Enabled)
        {
            return;
        }

        // `context` is an `in` parameter and cannot be captured by the lambda below, so the fields the
        // lambda needs are copied out here first.
        var operation = context.OperationName ?? DeriveOperationName(context.QueryText);
        var keyspace = context.Keyspace;
        var table = context.Table;

        Observe(() =>
        {
            var tags = new TagList { { "db.system.name", "cassandra" } };

            if (operation is not null)
            {
                tags.Add("db.operation.name", operation);
            }

            if (keyspace is { Length: > 0 })
            {
                tags.Add("db.namespace", keyspace);
            }

            if (table is { Length: > 0 })
            {
                tags.Add("db.collection.name", table);
            }

            if (errorType is not null)
            {
                tags.Add("error.type", errorType);
            }

            OperationDuration.Record(elapsed.TotalSeconds, tags);
        });
    }

    /// <summary>
    ///     Runs a piece of telemetry-recording code so that nothing it does can affect the query it describes.
    /// </summary>
    /// <remarks>
    ///     Every caller above sits on the query's own success/failure path — <see cref="RecordResult" /> runs
    ///     where a successful result is about to be returned, <see cref="RecordDuration" /> runs in a
    ///     <c>finally</c> around both outcomes, and <see cref="StartActivity" />/<see cref="StopActivity" />
    ///     wrap the two points where a listener extends the runtime (<c>Sample</c> and <c>ActivityStopped</c>).
    ///     An exception escaping here would either replace a genuine driver exception or turn a successful query
    ///     into a reported failure. <see cref="Activity.SetTag" /> and <c>Histogram&lt;T&gt;.Record</c> are
    ///     documented not to throw in normal use, but that guarantee should hold because this method makes it
    ///     structural, not because the BCL happens to behave today. The catch is deliberately silent: a broken
    ///     exporter or listener must never fail a query. Cancellation and the exceptions that mean the process
    ///     itself is unwell are left to propagate untouched, since swallowing those would trade a real problem
    ///     for a quiet one.
    /// </remarks>
    /// <param name="action">The telemetry-recording step to run.</param>
    private static void Observe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException
            or StackOverflowException or AccessViolationException))
        {
            // Swallowed deliberately — see remarks above.
        }
    }

    /// <summary>
    ///     Runs a piece of telemetry-recording code that produces a value, under the same isolation as
    ///     <see cref="Observe(Action)" />.
    /// </summary>
    /// <param name="action">The telemetry-recording step to run.</param>
    /// <returns>What <paramref name="action" /> returned, or <see langword="default" /> if it threw.</returns>
    private static T? Observe<T>(Func<T?> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException
            or StackOverflowException or AccessViolationException))
        {
            // Swallowed deliberately — see remarks on Observe(Action) above.
            return default;
        }
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
