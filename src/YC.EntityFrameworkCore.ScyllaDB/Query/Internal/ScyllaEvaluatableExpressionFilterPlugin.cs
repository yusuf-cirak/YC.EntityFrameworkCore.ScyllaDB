using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

/// <summary>
///     Keeps the provider's <see cref="DbFunctions" /> methods out of client-side evaluation.
/// </summary>
/// <remarks>
///     <c>EF.Functions.AllowFiltering()</c> takes no argument that mentions the entity, so EF Core would
///     otherwise treat it as a constant, run it in process, and hit the "only for use inside a query" guard
///     instead of translating it. Marking it non-evaluatable is what routes it to the translator.
/// </remarks>
public class ScyllaEvaluatableExpressionFilterPlugin : IEvaluatableExpressionFilterPlugin
{
    public virtual bool IsEvaluatableExpression(Expression expression)
        => expression is not MethodCallExpression call
           || call.Method.DeclaringType != typeof(ScyllaDbFunctionsExtensions);
}
