using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

/// <summary>
///     Exposes the CQL a query would run, without running it.
/// </summary>
public interface IScyllaQueryingEnumerable
{
    string ToCqlString();
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
        var rowSet = await clientWrapper.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);

        _queryContext.InitializeStateManager(_standAloneStateManager);

        var isAggregate = _select.Aggregate is not null;
        var properties = _select.ProjectedProperties;

        foreach (var row in rowSet)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return _shaper(_queryContext, isAggregate ? AggregateBuffer(row) : EntityBuffer(row, properties));
        }

        _ = _contextType;
        _ = _resultCardinality;
    }

    private static ValueBuffer AggregateBuffer(Row row)
        => new([row.GetValue<long>(0)]);

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
            var value = row.GetValue(
                property.GetTypeMapping().Converter?.ProviderClrType ?? property.ClrType,
                property.GetColumnName());

            values[property.GetIndex()] = property.GetTypeMapping().Converter is { } converter
                ? converter.ConvertFromProvider(value)
                : value;
        }

        return new ValueBuffer(values);
    }
}
