using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Diagnostics;
using YC.EntityFrameworkCore.ScyllaDB.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

/// <summary>
///     Exposes the CQL a query would run, and lets it be read one page at a time.
/// </summary>
public interface IScyllaQueryingEnumerable
{
    string ToCqlString();

    /// <summary>
    ///     Reads a single page, returning the cursor for the next one.
    /// </summary>
    Task<ScyllaPage<TResult>> ToPageAsync<TResult>(
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);
}

/// <summary>
///     Executes a translated query and yields shaped results.
/// </summary>
/// <remarks>
///     The synchronous side throws. The driver's I/O is natively asynchronous, so a blocking enumerator would
///     tie up a thread for no benefit and risk deadlocks — the same choice EF Core's Cosmos provider made.
/// </remarks>
public sealed class ScyllaQueryingEnumerable<T> : IAsyncEnumerable<T>, IEnumerable<T>, IScyllaQueryingEnumerable
{
    private readonly QueryContext _queryContext;
    private readonly CqlSelectExpression _select;
    private readonly Func<QueryContext, ValueBuffer, T> _shaper;
    private readonly Type _contextType;
    private readonly bool _standAloneStateManager;
    private readonly ResultCardinality _resultCardinality;

    public ScyllaQueryingEnumerable(
        QueryContext queryContext,
        CqlSelectExpression select,
        Func<QueryContext, ValueBuffer, T> shaper,
        Type contextType,
        bool standAloneStateManager,
        ResultCardinality resultCardinality)
    {
        _queryContext = queryContext;
        _select = select;
        _shaper = shaper;
        _contextType = contextType;
        _standAloneStateManager = standAloneStateManager;
        _resultCardinality = resultCardinality;
    }

    /// <summary>
    ///     Renders the statement with its bound values inlined, for logging and debugging.
    /// </summary>
    /// <remarks>
    ///     Never send this back to the server: the values are formatted for reading, not escaped for execution.
    /// </remarks>
    public string ToCqlString()
    {
        var (cql, values) = CqlQueryGenerator.Generate(_select, _queryContext.Parameters);

        return values.Length == 0
            ? cql
            : $"{cql}{Environment.NewLine}-- values: {string.Join(", ", values.Select(v => v ?? "null"))}";
    }

    /// <summary>
    ///     Reads one page of rows and returns the cursor for the next.
    /// </summary>
    /// <remarks>
    ///     This is what replaces <c>Skip</c>: CQL has no OFFSET, and cannot get one, because rows are found by
    ///     seeking rather than counted through. Auto-paging is turned off so the driver returns exactly one
    ///     page instead of transparently fetching the rest as the results are enumerated.
    /// </remarks>
    public async Task<ScyllaPage<TResult>> ToPageAsync<TResult>(
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var clientWrapper = _queryContext.Context.GetService<IScyllaClientWrapper>();
        var (cql, values) = CqlQueryGenerator.Generate(_select, _queryContext.Parameters);

        var statement = await clientWrapper.BindAsync(cql, values, cancellationToken).ConfigureAwait(false);

        statement.SetPageSize(pageSize);
        statement.SetAutoPage(false);

        if (continuationToken is not null)
        {
            statement.SetPagingState(Convert.FromBase64String(continuationToken));
        }

        var rowSet = await clientWrapper.ExecuteAsync(
                statement,
                new ScyllaOperationContext(
                    "SELECT",
                    _select.EntityType.GetKeyspace(),
                    _select.EntityType.GetScyllaTableName(),
                    cql),
                cancellationToken)
            .ConfigureAwait(false);

        _queryContext.InitializeStateManager(_standAloneStateManager);

        var values2 = new List<TResult>(pageSize);

        foreach (var row in rowSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values2.Add((TResult)(object)_shaper(_queryContext, BufferFor(row))!);
        }

        return new ScyllaPage<TResult>(
            values2,
            rowSet.PagingState is { Length: > 0 } state ? Convert.ToBase64String(state) : null);
    }

    public IEnumerator<T> GetEnumerator()
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported("a synchronous query", "ToListAsync, FirstOrDefaultAsync or similar"));

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var clientWrapper = _queryContext.Context.GetService<IScyllaClientWrapper>();
        var parameters = _queryContext.Parameters;

        var (cql, values) = CqlQueryGenerator.Generate(_select, parameters);

        var statement = await clientWrapper.BindAsync(cql, values, cancellationToken).ConfigureAwait(false);

        var rowSet = await clientWrapper.ExecuteAsync(
                statement,
                new ScyllaOperationContext(
                    "SELECT",
                    _select.EntityType.GetKeyspace(),
                    _select.EntityType.GetScyllaTableName(),
                    cql),
                cancellationToken)
            .ConfigureAwait(false);

        _queryContext.InitializeStateManager(_standAloneStateManager);

        foreach (var row in rowSet)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return _shaper(_queryContext, BufferFor(row));
        }

        _ = _contextType;
        _ = _resultCardinality;
    }

    private ValueBuffer BufferFor(Row row)
        => _select.Aggregate is not null
            ? new ValueBuffer([row.GetValue<long>(0)])
            : _select.Projection is { } projection
                ? ProjectionBuffer(row, projection)
                : EntityBuffer(row, _select.ProjectedProperties);

    /// <summary>
    ///     Reads a projection's columns positionally, matching the slots the projection binder handed out.
    /// </summary>
    private static ValueBuffer ProjectionBuffer(Row row, IReadOnlyList<IProperty> projection)
    {
        var values = new object?[projection.Count];

        for (var i = 0; i < projection.Count; i++)
        {
            values[i] = ReadColumn(row, projection[i]);
        }

        return new ValueBuffer(values);
    }

    /// <summary>
    ///     Reads a row into the value buffer layout EF Core's materialiser expects.
    /// </summary>
    /// <remarks>
    ///     The materialiser addresses values by each property's own index, which is not the order the columns
    ///     were selected in, so the buffer is filled by index rather than appended to.
    /// </remarks>
    private static ValueBuffer EntityBuffer(Row row, IReadOnlyList<IProperty> properties)
    {
        var values = new object?[properties.Max(p => p.GetIndex()) + 1];

        foreach (var property in properties)
        {
            values[property.GetIndex()] = ReadColumn(row, property);
        }

        return new ValueBuffer(values);
    }

    private static object? ReadColumn(Row row, IProperty property)
    {
        var mapping = property.GetTypeMapping();
        var value = row.GetValue(mapping.Converter?.ProviderClrType ?? property.ClrType, property.GetColumnName());

        return mapping.Converter is { } converter ? converter.ConvertFromProvider(value) : value;
    }
}
