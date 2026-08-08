using Microsoft.EntityFrameworkCore.Query;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

/// <summary>
///     Placeholder for the LINQ-to-CQL translator.
/// </summary>
/// <remarks>
///     EF Core resolves this factory while building a <c>DbContext</c>, but only calls
///     <see cref="Create" /> when a query is compiled. Registering the factory now therefore keeps contexts
///     constructible — so configuration, schema and write support can be finished and tested first — while any
///     attempt to actually run a query fails immediately and says why.
/// </remarks>
public class ScyllaQueryableMethodTranslatingExpressionVisitorFactory
    : IQueryableMethodTranslatingExpressionVisitorFactory
{
    public virtual QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => throw new NotImplementedException(
            ScyllaStrings.NotImplementedUntilPhase("Query translation", "the query pipeline milestone"));
}

/// <inheritdoc cref="ScyllaQueryableMethodTranslatingExpressionVisitorFactory" />
public class ScyllaShapedQueryCompilingExpressionVisitorFactory : IShapedQueryCompilingExpressionVisitorFactory
{
    public virtual ShapedQueryCompilingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => throw new NotImplementedException(
            ScyllaStrings.NotImplementedUntilPhase("Query materialization", "the query pipeline milestone"));
}
