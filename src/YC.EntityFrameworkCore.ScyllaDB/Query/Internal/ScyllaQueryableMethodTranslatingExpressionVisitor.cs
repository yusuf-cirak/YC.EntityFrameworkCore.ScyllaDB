using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

/// <summary>
///     Translates LINQ into a <see cref="CqlSelectExpression" />.
/// </summary>
/// <remarks>
///     Everything CQL cannot express throws here, while the LINQ operator is still in view and the message can
///     name it. Returning null instead would surface EF Core's generic "could not be translated", which tells a
///     caller nothing about why ScyllaDB refuses.
/// </remarks>
public class ScyllaQueryableMethodTranslatingExpressionVisitor : QueryableMethodTranslatingExpressionVisitor
{
    public ScyllaQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext, subquery: false)
    {
    }

    protected override ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType)
    {
        var select = new CqlSelectExpression(entityType);

        return new ShapedQueryExpression(
            select,
            new StructuralTypeShaperExpression(
                entityType,
                new ProjectionBindingExpression(select, new ProjectionMember(), typeof(ValueBuffer)),
                nullable: false));
    }

    // CQL has no subqueries at all, so there is nothing for a nested visitor to translate.
    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor()
        => throw new InvalidOperationException(ScyllaStrings.SubqueriesNotSupported());

    private static CqlSelectExpression SelectOf(ShapedQueryExpression source)
        => (CqlSelectExpression)source.QueryExpression;

    protected override ShapedQueryExpression TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        var select = ScyllaPredicateTranslator.Apply(SelectOf(source), predicate);

        return source.UpdateQueryExpression(select);
    }

    /// <summary>
    ///     Only an identity projection is translated; the whole entity is read either way.
    /// </summary>
    /// <remarks>
    ///     Narrowing the column list is a real optimisation, but it changes what the shaper has to build, so it
    ///     is left until projections are supported properly rather than half-done.
    /// </remarks>
    protected override ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        return selector.Body == selector.Parameters[0]
            ? source
            : throw new InvalidOperationException(ScyllaStrings.ProjectionsNotSupported());
    }

    protected override ShapedQueryExpression TranslateOrderBy(
        ShapedQueryExpression source,
        LambdaExpression keySelector,
        bool ascending)
        => ApplyOrdering(source, keySelector, ascending, replace: true);

    protected override ShapedQueryExpression TranslateThenBy(
        ShapedQueryExpression source,
        LambdaExpression keySelector,
        bool ascending)
        => ApplyOrdering(source, keySelector, ascending, replace: false);

    private static ShapedQueryExpression ApplyOrdering(
        ShapedQueryExpression source,
        LambdaExpression keySelector,
        bool ascending,
        bool replace)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);

        var select = SelectOf(source);
        var property = ScyllaPredicateTranslator.ResolveProperty(select.EntityType, keySelector.Body)
                       ?? throw new InvalidOperationException(
                           ScyllaStrings.NotTranslatable($"OrderBy({keySelector})"));

        return source.UpdateQueryExpression(select.AddOrdering(new CqlOrdering(property, ascending), replace));
    }

    protected override ShapedQueryExpression TranslateTake(ShapedQueryExpression source, Expression count)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ScyllaPredicateTranslator.ResolveValue(count) is not { } limit)
        {
            throw new InvalidOperationException(ScyllaStrings.NotTranslatable("Take"));
        }

        return source.UpdateQueryExpression(SelectOf(source).WithLimit(limit));
    }

    protected override ShapedQueryExpression TranslateFirstOrDefault(
        ShapedQueryExpression source,
        LambdaExpression? predicate,
        Type returnType,
        bool returnDefault)
    {
        ArgumentNullException.ThrowIfNull(source);

        var filtered = predicate is null ? source : TranslateWhere(source, predicate);

        return filtered
            .UpdateQueryExpression(SelectOf(filtered).WithLimit(CqlValue.FromConstant(1)))
            .UpdateResultCardinality(returnDefault ? ResultCardinality.SingleOrDefault : ResultCardinality.Single);
    }

    /// <summary>
    ///     Reads two rows so that a second one can be detected and reported, as <c>Single</c> promises.
    /// </summary>
    protected override ShapedQueryExpression TranslateSingleOrDefault(
        ShapedQueryExpression source,
        LambdaExpression? predicate,
        Type returnType,
        bool returnDefault)
    {
        ArgumentNullException.ThrowIfNull(source);

        var filtered = predicate is null ? source : TranslateWhere(source, predicate);

        return filtered
            .UpdateQueryExpression(SelectOf(filtered).WithLimit(CqlValue.FromConstant(2)))
            .UpdateResultCardinality(returnDefault ? ResultCardinality.SingleOrDefault : ResultCardinality.Single);
    }

    protected override ShapedQueryExpression TranslateAny(ShapedQueryExpression source, LambdaExpression? predicate)
    {
        ArgumentNullException.ThrowIfNull(source);

        var filtered = predicate is null ? source : TranslateWhere(source, predicate);
        var select = SelectOf(filtered).WithAggregate("COUNT(*)").WithLimit(CqlValue.FromConstant(1));

        return new ShapedQueryExpression(
                select,
                Expression.GreaterThan(
                    Expression.Convert(
                        new ProjectionBindingExpression(select, 0, typeof(long)), typeof(long)),
                    Expression.Constant(0L)))
            .UpdateResultCardinality(ResultCardinality.Single);
    }

    protected override ShapedQueryExpression TranslateCount(ShapedQueryExpression source, LambdaExpression? predicate)
        => TranslateCountCore(source, predicate, typeof(int));

    protected override ShapedQueryExpression TranslateLongCount(
        ShapedQueryExpression source,
        LambdaExpression? predicate)
        => TranslateCountCore(source, predicate, typeof(long));

    private ShapedQueryExpression TranslateCountCore(
        ShapedQueryExpression source,
        LambdaExpression? predicate,
        Type resultType)
    {
        ArgumentNullException.ThrowIfNull(source);

        var filtered = predicate is null ? source : TranslateWhere(source, predicate);
        var select = SelectOf(filtered).WithAggregate("COUNT(*)");

        return new ShapedQueryExpression(
                select,
                Expression.Convert(new ProjectionBindingExpression(select, 0, typeof(long)), resultType))
            .UpdateResultCardinality(ResultCardinality.Single);
    }

    // A cast between compatible entity types changes nothing about the CQL.
    protected override ShapedQueryExpression TranslateCast(ShapedQueryExpression source, Type castType)
        => source;

    protected override ShapedQueryExpression TranslateContains(ShapedQueryExpression source, Expression item)
        => throw new InvalidOperationException(ScyllaStrings.NotTranslatable("Contains over a query"));

    protected override ShapedQueryExpression TranslateSkip(ShapedQueryExpression source, Expression count)
        => throw new InvalidOperationException(ScyllaStrings.SkipNotSupported());

    protected override ShapedQueryExpression TranslateSkipWhile(ShapedQueryExpression source, LambdaExpression predicate)
        => throw new InvalidOperationException(ScyllaStrings.SkipNotSupported());

    protected override ShapedQueryExpression TranslateTakeWhile(ShapedQueryExpression source, LambdaExpression predicate)
        => throw new InvalidOperationException(ScyllaStrings.NotTranslatable("TakeWhile"));

    protected override ShapedQueryExpression TranslateElementAtOrDefault(
        ShapedQueryExpression source,
        Expression index,
        bool returnDefault)
        => throw new InvalidOperationException(ScyllaStrings.SkipNotSupported());

    protected override ShapedQueryExpression TranslateGroupBy(
        ShapedQueryExpression source,
        LambdaExpression keySelector,
        LambdaExpression? elementSelector,
        LambdaExpression? resultSelector)
        => throw new InvalidOperationException(ScyllaStrings.GroupByNotSupported());

    protected override ShapedQueryExpression TranslateJoin(
        ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression? outerKeySelector,
        LambdaExpression? innerKeySelector,
        LambdaExpression resultSelector)
        => throw new InvalidOperationException(ScyllaStrings.JoinsNotSupported("Join"));

    protected override ShapedQueryExpression TranslateLeftJoin(
        ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression? outerKeySelector,
        LambdaExpression? innerKeySelector,
        LambdaExpression resultSelector)
        => throw new InvalidOperationException(ScyllaStrings.JoinsNotSupported("LeftJoin"));

    protected override ShapedQueryExpression TranslateRightJoin(
        ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression? outerKeySelector,
        LambdaExpression? innerKeySelector,
        LambdaExpression resultSelector)
        => throw new InvalidOperationException(ScyllaStrings.JoinsNotSupported("RightJoin"));

    protected override ShapedQueryExpression TranslateGroupJoin(
        ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector,
        LambdaExpression resultSelector)
        => throw new InvalidOperationException(ScyllaStrings.JoinsNotSupported("GroupJoin"));

    protected override ShapedQueryExpression TranslateSelectMany(
        ShapedQueryExpression source,
        LambdaExpression collectionSelector,
        LambdaExpression resultSelector)
        => throw new InvalidOperationException(ScyllaStrings.JoinsNotSupported("SelectMany"));

    protected override ShapedQueryExpression TranslateSelectMany(
        ShapedQueryExpression source,
        LambdaExpression selector)
        => throw new InvalidOperationException(ScyllaStrings.JoinsNotSupported("SelectMany"));

    protected override ShapedQueryExpression TranslateUnion(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => throw new InvalidOperationException(ScyllaStrings.SetOperationsNotSupported("Union"));

    protected override ShapedQueryExpression TranslateConcat(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => throw new InvalidOperationException(ScyllaStrings.SetOperationsNotSupported("Concat"));

    protected override ShapedQueryExpression TranslateExcept(ShapedQueryExpression source1, ShapedQueryExpression source2)
        => throw new InvalidOperationException(ScyllaStrings.SetOperationsNotSupported("Except"));

    protected override ShapedQueryExpression TranslateIntersect(
        ShapedQueryExpression source1,
        ShapedQueryExpression source2)
        => throw new InvalidOperationException(ScyllaStrings.SetOperationsNotSupported("Intersect"));

    protected override ShapedQueryExpression TranslateAll(ShapedQueryExpression source, LambdaExpression predicate)
        => throw new InvalidOperationException(ScyllaStrings.AggregateNotSupported("All"));

    protected override ShapedQueryExpression TranslateAverage(
        ShapedQueryExpression source,
        LambdaExpression? selector,
        Type resultType)
        => throw new InvalidOperationException(ScyllaStrings.AggregateNotSupported("Average"));

    protected override ShapedQueryExpression TranslateSum(
        ShapedQueryExpression source,
        LambdaExpression? selector,
        Type resultType)
        => throw new InvalidOperationException(ScyllaStrings.AggregateNotSupported("Sum"));

    protected override ShapedQueryExpression TranslateMax(
        ShapedQueryExpression source,
        LambdaExpression? selector,
        Type resultType)
        => throw new InvalidOperationException(ScyllaStrings.AggregateNotSupported("Max"));

    protected override ShapedQueryExpression TranslateMin(
        ShapedQueryExpression source,
        LambdaExpression? selector,
        Type resultType)
        => throw new InvalidOperationException(ScyllaStrings.AggregateNotSupported("Min"));

    protected override ShapedQueryExpression TranslateDistinct(ShapedQueryExpression source)
        => throw new InvalidOperationException(ScyllaStrings.NotTranslatable("Distinct"));

    protected override ShapedQueryExpression TranslateReverse(ShapedQueryExpression source)
        => throw new InvalidOperationException(ScyllaStrings.NotTranslatable("Reverse"));

    protected override ShapedQueryExpression TranslateOfType(ShapedQueryExpression source, Type resultType)
        => throw new InvalidOperationException(ScyllaStrings.NotTranslatable("OfType"));

    protected override ShapedQueryExpression TranslateDefaultIfEmpty(
        ShapedQueryExpression source,
        Expression? defaultValue)
        => throw new InvalidOperationException(ScyllaStrings.NotTranslatable("DefaultIfEmpty"));

    protected override ShapedQueryExpression TranslateLastOrDefault(
        ShapedQueryExpression source,
        LambdaExpression? predicate,
        Type returnType,
        bool returnDefault)
        => throw new InvalidOperationException(
            ScyllaStrings.NotTranslatable("Last") + " Reverse the clustering order and take the first row instead.");
}

/// <summary>
///     Turns a predicate lambda into CQL restrictions.
/// </summary>
internal static class ScyllaPredicateTranslator
{
    private static readonly MethodInfo AllowFilteringMethod =
        typeof(ScyllaDbFunctionsExtensions).GetMethod(nameof(ScyllaDbFunctionsExtensions.AllowFiltering))!;

    public static CqlSelectExpression Apply(CqlSelectExpression select, LambdaExpression predicate)
        => Visit(select, predicate.Body);

    private static CqlSelectExpression Visit(CqlSelectExpression select, Expression expression)
    {
        switch (expression)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso:
                return Visit(Visit(select, andAlso.Left), andAlso.Right);

            case BinaryExpression { NodeType: ExpressionType.OrElse }:
                throw new InvalidOperationException(ScyllaStrings.OrElseNotSupported());

            case BinaryExpression { NodeType: ExpressionType.NotEqual } notEqual:
                throw new InvalidOperationException(
                    ScyllaStrings.NotEqualNotSupported(
                        ResolveProperty(select.EntityType, notEqual.Left)?.GetColumnName()
                        ?? notEqual.Left.ToString()));

            case BinaryExpression binary when TryOperator(binary.NodeType, out var op):
                return AddComparison(select, binary, op);

            case MethodCallExpression call when call.Method == AllowFilteringMethod:
                return select.WithAllowFiltering();

            case MethodCallExpression call when TryContains(select.EntityType, call, out var restriction):
                return select.AddRestriction(restriction);

            default:
                throw new InvalidOperationException(ScyllaStrings.NotTranslatable(expression.ToString()));
        }
    }

    private static CqlSelectExpression AddComparison(
        CqlSelectExpression select,
        BinaryExpression binary,
        CqlRestrictionOperator op)
    {
        // The column may be on either side; flipping the comparison keeps `5 < x.Value` meaning the same thing.
        if (ResolveProperty(select.EntityType, binary.Left) is { } leftProperty
            && ResolveValue(binary.Right) is { } rightValue)
        {
            return select.AddRestriction(new CqlRestriction(leftProperty, op, [rightValue]));
        }

        if (ResolveProperty(select.EntityType, binary.Right) is { } rightProperty
            && ResolveValue(binary.Left) is { } leftValue)
        {
            return select.AddRestriction(new CqlRestriction(rightProperty, Flip(op), [leftValue]));
        }

        throw new InvalidOperationException(ScyllaStrings.NotTranslatable(binary.ToString()));
    }

    private static bool TryContains(
        IEntityType entityType,
        MethodCallExpression call,
        out CqlRestriction restriction)
    {
        restriction = null!;

        if (!string.Equals(call.Method.Name, nameof(Enumerable.Contains), StringComparison.Ordinal))
        {
            return false;
        }

        // Enumerable.Contains(source, item) as an extension, or source.Contains(item) as an instance call.
        var (collection, item) = call.Object is null
            ? (call.Arguments[0], call.Arguments[1])
            : (call.Object, call.Arguments[0]);

        if (ResolveProperty(entityType, item) is not { } property
            || ResolveValue(collection) is not { } values
            || !typeof(IEnumerable).IsAssignableFrom(collection.Type)
            || collection.Type == typeof(string))
        {
            return false;
        }

        restriction = new CqlRestriction(property, CqlRestrictionOperator.In, [values]);
        return true;
    }

    public static IProperty? ResolveProperty(IEntityType entityType, Expression expression)
        => Unwrap(expression) is MemberExpression { Expression: ParameterExpression } member
            ? entityType.FindProperty(member.Member.Name)
            : null;

    /// <summary>
    ///     Reads a comparison's value, whether it is a literal or a query parameter EF Core lifted out of a
    ///     captured variable.
    /// </summary>
    public static CqlValue? ResolveValue(Expression expression)
        => Unwrap(expression) switch
        {
            ConstantExpression constant => CqlValue.FromConstant(constant.Value),
            QueryParameterExpression parameter => CqlValue.FromParameter(parameter.Name),
            _ => null
        };

    private static Expression Unwrap(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? Unwrap(unary.Operand)
            : expression;

    private static bool TryOperator(ExpressionType nodeType, out CqlRestrictionOperator op)
    {
        op = nodeType switch
        {
            ExpressionType.Equal => CqlRestrictionOperator.Equal,
            ExpressionType.GreaterThan => CqlRestrictionOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => CqlRestrictionOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => CqlRestrictionOperator.LessThan,
            ExpressionType.LessThanOrEqual => CqlRestrictionOperator.LessThanOrEqual,
            _ => default
        };

        return nodeType is ExpressionType.Equal or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;
    }

    private static CqlRestrictionOperator Flip(CqlRestrictionOperator op)
        => op switch
        {
            CqlRestrictionOperator.GreaterThan => CqlRestrictionOperator.LessThan,
            CqlRestrictionOperator.GreaterThanOrEqual => CqlRestrictionOperator.LessThanOrEqual,
            CqlRestrictionOperator.LessThan => CqlRestrictionOperator.GreaterThan,
            CqlRestrictionOperator.LessThanOrEqual => CqlRestrictionOperator.GreaterThanOrEqual,
            _ => op
        };
}
