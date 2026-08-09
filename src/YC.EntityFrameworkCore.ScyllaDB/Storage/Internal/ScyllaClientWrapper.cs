using Cassandra;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     The per-<c>DbContext</c> face of the driver. Holds no connection of its own; it borrows the shared
///     session and applies the context's options to each statement.
/// </summary>
public interface IScyllaClientWrapper
{
    Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default);

    Task<RowSet> ExecuteAsync(string cql, CancellationToken cancellationToken = default);

    Task<RowSet> ExecuteAsync(IStatement statement, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Prepares the statement (reusing an earlier preparation of the same CQL) and binds the values.
    /// </summary>
    /// <remarks>
    ///     Preparing is what lets the driver compute the routing key from the bound partition key, which is how
    ///     a statement reaches the node — and the shard — that owns the data instead of taking an extra hop.
    /// </remarks>
    Task<BoundStatement> BindAsync(string cql, object?[] values, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies the configured consistency, page size, execution profile and tracing to a statement.
    /// </summary>
    TStatement ApplyDefaults<TStatement>(TStatement statement)
        where TStatement : IStatement;
}

/// <inheritdoc cref="IScyllaClientWrapper" />
public class ScyllaClientWrapper : IScyllaClientWrapper, IResettableService
{
    private readonly IScyllaSingletonClientWrapper _singletonWrapper;
    private readonly IScyllaSingletonOptions _options;

    public ScyllaClientWrapper(
        IScyllaSingletonClientWrapper singletonWrapper,
        IScyllaSingletonOptions options)
    {
        ArgumentNullException.ThrowIfNull(singletonWrapper);
        ArgumentNullException.ThrowIfNull(options);

        _singletonWrapper = singletonWrapper;
        _options = options;
    }

    public virtual Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default)
        => _singletonWrapper.GetSessionAsync(cancellationToken);

    public virtual async Task<RowSet> ExecuteAsync(string cql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cql);

        return await ExecuteAsync(new SimpleStatement(cql), cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task<RowSet> ExecuteAsync(
        IStatement statement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var prepared = ApplyDefaults(statement);

        // The execution profile is not a property of the statement; it is chosen per execution.
        return _options.ExecutionProfile is { } executionProfile
            ? await session.ExecuteAsync(prepared, executionProfile).ConfigureAwait(false)
            : await session.ExecuteAsync(prepared).ConfigureAwait(false);
    }

    public virtual async Task<BoundStatement> BindAsync(
        string cql,
        object?[] values,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cql);
        ArgumentNullException.ThrowIfNull(values);

        var prepared = await _singletonWrapper.PrepareAsync(cql, cancellationToken).ConfigureAwait(false);

        return prepared.Bind(values);
    }

    public virtual TStatement ApplyDefaults<TStatement>(TStatement statement)
        where TStatement : IStatement
    {
        ArgumentNullException.ThrowIfNull(statement);

        // Only set what was explicitly configured, so anything left alone keeps the driver's own default —
        // notably LOCAL_ONE consistency, which the provider deliberately does not override.
        if (_options.ConsistencyLevel is { } consistencyLevel)
        {
            statement.SetConsistencyLevel(consistencyLevel);
        }

        if (_options.SerialConsistencyLevel is { } serialConsistencyLevel)
        {
            statement.SetSerialConsistencyLevel(serialConsistencyLevel);
        }

        if (_options.PageSize is { } pageSize)
        {
            statement.SetPageSize(pageSize);
        }

        if (_options.QueryTimeout is { } queryTimeout)
        {
            statement.SetReadTimeoutMillis((int)queryTimeout.TotalMilliseconds);
        }

        if (_options.EnableTracing)
        {
            statement.EnableTracing(true);
        }

        return statement;
    }

    public virtual void ResetState()
    {
    }

    public virtual Task ResetStateAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
