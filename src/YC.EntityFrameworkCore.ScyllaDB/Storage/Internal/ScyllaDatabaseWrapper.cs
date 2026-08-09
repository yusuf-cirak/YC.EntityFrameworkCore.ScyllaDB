using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.Logging;
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
    private readonly IDiagnosticsLogger<DbLoggerCategory.Update> _updateLogger;

    public ScyllaDatabaseWrapper(
        DatabaseDependencies dependencies,
        IScyllaClientWrapper clientWrapper,
        IScyllaSingletonOptions options,
        IDiagnosticsLogger<DbLoggerCategory.Update> updateLogger)
        : base(dependencies)
    {
        ArgumentNullException.ThrowIfNull(clientWrapper);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(updateLogger);

        _clientWrapper = clientWrapper;
        _options = options;
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

        var operations = entries.Select(ScyllaDmlGenerator.Create).ToList();

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
            return operations.Count;
        }

        // Bounded so that a large SaveChangesAsync cannot flood the cluster's request queue.
        using var concurrency = new SemaphoreSlim(_options.MaxConcurrentWrites);

        await Task.WhenAll(
                partitionGroups.Select(group => ExecutePartitionAsync(group, concurrency, cancellationToken)))
            .ConfigureAwait(false);

        return operations.Count;
    }

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

        await _clientWrapper.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
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

        await _clientWrapper.ExecuteAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    public virtual void ResetState()
    {
    }

    public virtual Task ResetStateAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
