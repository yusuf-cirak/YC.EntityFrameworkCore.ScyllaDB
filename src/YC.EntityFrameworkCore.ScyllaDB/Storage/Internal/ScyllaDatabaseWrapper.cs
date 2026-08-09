using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.Logging;
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Update.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Turns tracked changes into CQL writes.
/// </summary>
public class ScyllaDatabaseWrapper : Database, IResettableService
{
    private readonly IScyllaClientWrapper _clientWrapper;
    private readonly IScyllaSingletonOptions _options;
    private readonly IScyllaWriteOptionsAccessor _writeOptions;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Update> _updateLogger;

    public ScyllaDatabaseWrapper(
        DatabaseDependencies dependencies,
        IScyllaClientWrapper clientWrapper,
        IScyllaSingletonOptions options,
        IScyllaWriteOptionsAccessor writeOptions,
        IDiagnosticsLogger<DbLoggerCategory.Update> updateLogger)
        : base(dependencies)
    {
        ArgumentNullException.ThrowIfNull(clientWrapper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writeOptions);
        ArgumentNullException.ThrowIfNull(updateLogger);

        _clientWrapper = clientWrapper;
        _options = options;
        _writeOptions = writeOptions;
        _updateLogger = updateLogger;
    }

    public override int SaveChanges(IList<IUpdateEntry> entries)
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported(
                $"{nameof(DbContext)}.{nameof(DbContext.SaveChanges)}",
                $"{nameof(DbContext)}.{nameof(DbContext.SaveChangesAsync)}"));

    /// <summary>
    ///     Sends every tracked change, grouping by partition.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Writes that share a partition go out as one <c>UNLOGGED</c> batch, which ScyllaDB applies
    ///         atomically and at no more cost than the individual statements. Writes to different partitions go
    ///         out as independent statements, concurrently — a logged batch spanning partitions buys eventual
    ///         all-or-nothing at a steep price and still gives no isolation, so it stays opt-in.
    ///     </para>
    ///     <para>
    ///         The consequence, which callers need to know: a single <c>SaveChangesAsync</c> touching several
    ///         partitions is not atomic. That is CQL, not a shortcut taken here, and it is logged when it
    ///         happens.
    ///     </para>
    /// </remarks>
    public override async Task<int> SaveChangesAsync(
        IList<IUpdateEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return 0;
        }

        var writeOptions = _writeOptions.Current;
        var allOperations = entries.Select(e => ScyllaDmlGenerator.Create(e, writeOptions)).ToList();

        // Lightweight transactions run through Paxos, are confined to one partition, and report whether they
        // applied. They cannot share a batch with anything, so each goes on its own and its result is checked.
        foreach (var conditional in allOperations.Where(o => o.IsConditional))
        {
            await ExecuteConditionalAsync(conditional, cancellationToken).ConfigureAwait(false);
        }

        var operations = allOperations.Where(o => !o.IsConditional).ToList();

        if (operations.Count == 0)
        {
            return allOperations.Count;
        }

        var partitionGroups = operations
            .GroupBy(o => o.PartitionKey)
            .ToList();

        if (partitionGroups.Count > 1)
        {
            _updateLogger.Logger.LogWarning(
                "{Message}", ScyllaStrings.MultiPartitionSaveChanges(partitionGroups.Count));
        }

        if (_options.BatchMode == ScyllaBatchMode.Logged && operations.Count > 1)
        {
            await ExecuteBatchAsync(operations, BatchType.Logged, cancellationToken).ConfigureAwait(false);
            return allOperations.Count;
        }

        // Bounded so that a large SaveChangesAsync cannot flood the cluster's request queue.
        using var concurrency = new SemaphoreSlim(_options.MaxConcurrentWrites);

        await Task.WhenAll(
                partitionGroups.Select(group => ExecutePartitionAsync(group, concurrency, cancellationToken)))
            .ConfigureAwait(false);

        return allOperations.Count;
    }

    /// <summary>
    ///     Runs a lightweight transaction and turns a rejected write into a concurrency exception.
    /// </summary>
    /// <remarks>
    ///     This is the only optimistic concurrency ScyllaDB can offer. An ordinary CQL write is a blind upsert
    ///     that reports no affected-row count, so EF Core's usual "zero rows changed means someone else won"
    ///     detection has nothing to observe. A conditional write instead returns an <c>[applied]</c> column,
    ///     along with the row's current values when it did not apply.
    /// </remarks>
    private async Task ExecuteConditionalAsync(
        ScyllaWriteOperation operation,
        CancellationToken cancellationToken)
    {
        var statement = await _clientWrapper.BindAsync(operation.Cql, operation.Values, cancellationToken)
            .ConfigureAwait(false);

        // Paxos rounds must not be replayed blindly.
        statement.SetIdempotence(false);

        if (_options.SerialConsistencyLevel is { } serialConsistency)
        {
            statement.SetSerialConsistencyLevel(serialConsistency);
        }

        var rowSet = await _clientWrapper.ExecuteAsync(statement, ContextFor(operation), cancellationToken)
            .ConfigureAwait(false);
        var row = rowSet.FirstOrDefault();

        if (row is not null && row.GetValue<bool>("[applied]"))
        {
            return;
        }

        var entityType = operation.Entry?.EntityType.DisplayName() ?? "the table";

        // A rejected write comes back with the row that won. For a failed IF NOT EXISTS that is the row that
        // was already there; for a failed condition it is the primary key plus the columns the condition
        // named — not the whole row, which is why the message quotes only what the server returned.
        var current = row is null ? "unavailable" : DescribeRow(rowSet, row);

        throw new DbUpdateConcurrencyException(
            operation.Entry?.EntityState == EntityState.Added
                ? ScyllaStrings.InsertIfNotExistsFailed(entityType, current)
                : ScyllaStrings.ConcurrencyConditionFailed(entityType, current),
            operation.Entry is null ? [] : [operation.Entry]);
    }

    /// <summary>
    ///     Formats the row ScyllaDB returns alongside a rejected conditional write, which carries the values
    ///     that actually won.
    /// </summary>
    private static string DescribeRow(RowSet rowSet, Row row)
        => string.Join(
            ", ",
            rowSet.Columns
                .Where(c => !string.Equals(c.Name, "[applied]", StringComparison.Ordinal))
                .Select(c => $"{c.Name} = {row.GetValue<object>(c.Name) ?? "null"}"));

    private async Task ExecutePartitionAsync(
        IEnumerable<ScyllaWriteOperation> partition,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operations = partition.ToList();

            // Counters live in their own batch type and can never share one with ordinary writes. A counter
            // table holds nothing but counters, so in practice a partition is entirely one or the other.
            var counters = operations.Where(o => o.IsCounter).ToList();
            var regular = operations.Where(o => !o.IsCounter).ToList();

            if (_options.BatchMode == ScyllaBatchMode.Never)
            {
                foreach (var operation in operations)
                {
                    await ExecuteSingleAsync(operation, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await ExecuteGroupAsync(regular, BatchType.Unlogged, cancellationToken).ConfigureAwait(false);
            await ExecuteGroupAsync(counters, BatchType.Counter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task ExecuteGroupAsync(
        IReadOnlyList<ScyllaWriteOperation> operations,
        BatchType batchType,
        CancellationToken cancellationToken)
    {
        switch (operations.Count)
        {
            case 0:
                return;
            case 1:
                await ExecuteSingleAsync(operations[0], cancellationToken).ConfigureAwait(false);
                return;
            default:
                await ExecuteBatchAsync(operations, batchType, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task ExecuteSingleAsync(ScyllaWriteOperation operation, CancellationToken cancellationToken)
    {
        var statement = await _clientWrapper.BindAsync(operation.Cql, operation.Values, cancellationToken)
            .ConfigureAwait(false);

        // A counter increment is not idempotent: replaying it adds the delta twice. Everything else is a blind
        // upsert of the same values, so a retry is harmless and worth allowing.
        statement.SetIdempotence(!operation.IsCounter);

        await _clientWrapper.ExecuteAsync(statement, ContextFor(operation), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ExecuteBatchAsync(
        IReadOnlyList<ScyllaWriteOperation> operations,
        BatchType batchType,
        CancellationToken cancellationToken)
    {
        var batch = new BatchStatement().SetBatchType(batchType);

        foreach (var operation in operations)
        {
            batch.Add(
                await _clientWrapper.BindAsync(operation.Cql, operation.Values, cancellationToken)
                    .ConfigureAwait(false));
        }

        batch.SetIdempotence(batchType != BatchType.Counter);

        // ExecuteGroupAsync always hands in a single partition group, so every operation shares one keyspace
        // and table and operations[0] is representative. SaveChangesAsync's Logged path is different: it sends
        // every non-conditional write as one batch regardless of partition, so a batch can span tables. The
        // OpenTelemetry database conventions define db.collection.name as the target of a single-collection
        // operation, so when the operations disagree the honest span reports no table at all, rather than
        // naming one table while silently misattributing another table's writes and latency to it.
        var first = operations[0].PartitionKey;
        var keyspace = operations.All(o => string.Equals(o.PartitionKey.Keyspace, first.Keyspace, StringComparison.Ordinal))
            ? first.Keyspace
            : null;
        var table = operations.All(o => string.Equals(o.PartitionKey.Table, first.Table, StringComparison.Ordinal))
            ? first.Table
            : null;

        await _clientWrapper.ExecuteAsync(
                batch,
                new ScyllaOperationContext("BATCH", keyspace, table),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Describes a write for telemetry. A batch reports the table it targets but no single operation name,
    ///     since it is the batch itself that was executed.
    /// </summary>
    private static ScyllaOperationContext ContextFor(ScyllaWriteOperation operation)
        => new(
            null,
            operation.PartitionKey.Keyspace,
            operation.PartitionKey.Table,
            operation.Cql);

    public virtual void ResetState()
    {
    }

    public virtual Task ResetStateAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
