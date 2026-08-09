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
    private readonly QueryableMethodTranslatingExpressionVisitorDependencies _dependencies;

    public ScyllaQueryableMethodTranslatingExpressionVisitorFactory(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        _dependencies = dependencies;
    }

    public virtual QueryableMethodTranslatingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new ScyllaQueryableMethodTranslatingExpressionVisitor(_dependencies, queryCompilationContext);
}

/// <inheritdoc cref="ScyllaQueryableMethodTranslatingExpressionVisitorFactory" />
public class ScyllaShapedQueryCompilingExpressionVisitorFactory : IShapedQueryCompilingExpressionVisitorFactory
{
    private readonly ShapedQueryCompilingExpressionVisitorDependencies _dependencies;

    public ScyllaShapedQueryCompilingExpressionVisitorFactory(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        _dependencies = dependencies;
    }

    public virtual ShapedQueryCompilingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new ScyllaShapedQueryCompilingExpressionVisitor(_dependencies, queryCompilationContext);
}
