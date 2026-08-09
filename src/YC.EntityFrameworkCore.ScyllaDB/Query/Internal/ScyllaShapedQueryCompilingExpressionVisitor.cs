using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

/// <summary>
///     Turns a translated query into the code that runs it and builds the results.
/// </summary>
public class ScyllaShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
{
    private readonly Type _contextType;

    public ScyllaShapedQueryCompilingExpressionVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext)
        => _contextType = queryCompilationContext.ContextType;

    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        ArgumentNullException.ThrowIfNull(shapedQueryExpression);

        var select = (CqlSelectExpression)shapedQueryExpression.QueryExpression;

        // Restrictions accumulate across several Where calls, so the CQL rules can only be judged now that the
        // whole query is in view.
        CqlQueryValidator.Validate(select);

        var valueBufferParameter = Expression.Parameter(typeof(ValueBuffer), "valueBuffer");

        var shaperBody = new ProjectionBindingRemover(valueBufferParameter).Visit(shapedQueryExpression.ShaperExpression);
        shaperBody = InjectStructuralTypeMaterializers(shaperBody);

        var shaperLambda = Expression.Lambda(
            shaperBody,
            QueryCompilationContext.QueryContextParameter,
            valueBufferParameter);

        return Expression.New(
            typeof(ScyllaQueryingEnumerable<>).MakeGenericType(shaperLambda.ReturnType).GetConstructors()[0],
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(select),
            Expression.Constant(shaperLambda.Compile()),
            Expression.Constant(_contextType),
            Expression.Constant(
                QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.NoTrackingWithIdentityResolution),
            Expression.Constant(shapedQueryExpression.ResultCardinality));
    }

    /// <summary>
    ///     Replaces projection bindings with reads from the row's value buffer.
    /// </summary>
    private sealed class ProjectionBindingRemover(ParameterExpression valueBuffer) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression extensionExpression)
        {
            if (extensionExpression is not ProjectionBindingExpression binding)
            {
                return base.VisitExtension(extensionExpression);
            }

            // An entity shaper wants the buffer itself; an aggregate wants one value out of it.
            return binding.Type == typeof(ValueBuffer)
                ? valueBuffer
                : Expression.Convert(
                    Expression.Call(
                        valueBuffer,
                        typeof(ValueBuffer).GetProperty("Item")!.GetMethod!,
                        Expression.Constant(binding.Index ?? 0)),
                    binding.Type);
        }
    }
}
