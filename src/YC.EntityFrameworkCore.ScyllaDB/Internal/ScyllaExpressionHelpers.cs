using System.Linq.Expressions;
using System.Reflection;

namespace YC.EntityFrameworkCore.ScyllaDB.Internal;

/// <summary>
///     Turns the lambdas used by the fluent API into property names.
/// </summary>
internal static class ScyllaExpressionHelpers
{
    /// <summary>
    ///     Reads the properties named by <c>x =&gt; x.Property</c> or <c>x =&gt; new { x.A, x.B }</c>.
    /// </summary>
    /// <remarks>
    ///     The anonymous-object form is how a composite key is expressed, and its member order is the key order,
    ///     so this must preserve it.
    /// </remarks>
    public static IReadOnlyList<string> GetPropertyNames(LambdaExpression propertyExpression)
    {
        ArgumentNullException.ThrowIfNull(propertyExpression);

        var body = RemoveConvert(propertyExpression.Body);

        return body switch
        {
            NewExpression newExpression when newExpression.Arguments.Count > 0
                => [.. newExpression.Arguments.Select(a => GetSinglePropertyName(a, propertyExpression))],
            _ => [GetSinglePropertyName(body, propertyExpression)]
        };
    }

    private static string GetSinglePropertyName(Expression expression, LambdaExpression source)
        => RemoveConvert(expression) is MemberExpression { Member: PropertyInfo property }
            ? property.Name
            : throw new ArgumentException(
                $"The expression '{source}' does not name a property. Use 'x => x.Property' for a single "
                + "property, or 'x => new {{ x.First, x.Second }}' for several.",
                nameof(source));

    // A lambda typed as Func<TEntity, object?> boxes value-type properties, so the member access arrives
    // wrapped in a conversion.
    private static Expression RemoveConvert(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? RemoveConvert(unary.Operand)
            : expression;
}
