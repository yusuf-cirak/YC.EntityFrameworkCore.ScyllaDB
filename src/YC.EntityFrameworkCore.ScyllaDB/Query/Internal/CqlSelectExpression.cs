using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Query.Internal;

/// <summary>
///     A value in a restriction: either known at translation time, or supplied per execution.
/// </summary>
/// <remarks>
///     EF Core lifts captured variables into query parameters so one compiled query serves every value, so a
///     restriction cannot simply hold the object — it has to remember where to fetch it from at execution.
/// </remarks>
public readonly record struct CqlValue(object? Constant, string? ParameterName)
{
    public static CqlValue FromConstant(object? value)
        => new(value, null);

    public static CqlValue FromParameter(string name)
        => new(null, name);

    public object? Resolve(IReadOnlyDictionary<string, object?> parameters)
        => ParameterName is null
            ? Constant
            : parameters.TryGetValue(ParameterName, out var value) ? value : null;
}

/// <summary>
///     How a column is restricted.
/// </summary>
public enum CqlRestrictionOperator
{
    Equal,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    In
}

/// <summary>
///     One <c>WHERE</c> term.
/// </summary>
public sealed record CqlRestriction(IProperty Property, CqlRestrictionOperator Operator, IReadOnlyList<CqlValue> Values)
{
    public string ColumnName
        => Property.GetColumnName();

    public bool IsRange
        => Operator is not CqlRestrictionOperator.Equal and not CqlRestrictionOperator.In;
}

public sealed record CqlOrdering(IProperty Property, bool Ascending);

/// <summary>
///     A CQL <c>SELECT</c>.
/// </summary>
/// <remarks>
///     <para>
///         Restrictions are kept in three typed buckets rather than as a boolean predicate tree, because CQL's
///         <c>WHERE</c> is not a boolean algebra: it is a positional restriction list over the primary key.
///         Storing <c>a AND b AND c</c> as a tree would mean re-decomposing it at generation time simply to
///         answer "is the clustering prefix contiguous?", and would lose the ability to point an error at the
///         one term that broke the rule.
///     </para>
///     <para>
///         Immutable. Each translation step returns a new instance, which is what lets EF Core's visitors treat
///         it like any other expression.
///     </para>
/// </remarks>
public sealed class CqlSelectExpression : Expression
{
    public CqlSelectExpression(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        EntityType = entityType;
        PartitionKeyRestrictions = [];
        ClusteringRestrictions = [];
        NonKeyRestrictions = [];
        Orderings = [];
    }

    private CqlSelectExpression(CqlSelectExpression source)
    {
        EntityType = source.EntityType;
        PartitionKeyRestrictions = source.PartitionKeyRestrictions;
        ClusteringRestrictions = source.ClusteringRestrictions;
        NonKeyRestrictions = source.NonKeyRestrictions;
        Orderings = source.Orderings;
        Limit = source.Limit;
        PerPartitionLimit = source.PerPartitionLimit;
        AllowFiltering = source.AllowFiltering;
        Aggregate = source.Aggregate;
        Projection = source.Projection;
    }

    public IEntityType EntityType { get; }

    public IReadOnlyList<CqlRestriction> PartitionKeyRestrictions { get; private init; }

    public IReadOnlyList<CqlRestriction> ClusteringRestrictions { get; private init; }

    /// <summary>
    ///     Restrictions on columns outside the primary key, which CQL can only satisfy with an index or a full
    ///     scan.
    /// </summary>
    public IReadOnlyList<CqlRestriction> NonKeyRestrictions { get; private init; }

    public IReadOnlyList<CqlOrdering> Orderings { get; private init; }

    public CqlValue? Limit { get; private init; }

    /// <summary>
    ///     Caps the rows read from each partition rather than from the result as a whole.
    /// </summary>
    /// <remarks>
    ///     The one thing a plain <c>LIMIT</c> cannot express: "the newest three readings from every sensor" is a
    ///     single query with this, and one query per sensor without it.
    /// </remarks>
    public CqlValue? PerPartitionLimit { get; private init; }

    public bool AllowFiltering { get; private init; }

    /// <summary>
    ///     The columns a projection asked for, or null when the whole entity is being read.
    /// </summary>
    /// <remarks>
    ///     Narrowing matters more here than in SQL: CQL rows can be very wide, and every column read is a cell
    ///     fetched from storage.
    /// </remarks>
    public IReadOnlyList<IProperty>? Projection { get; private init; }

    /// <summary>
    ///     Set for <c>COUNT(*)</c>. Other aggregates are coordinator-side and unbounded, so they are not
    ///     translated.
    /// </summary>
    public string? Aggregate { get; private init; }

    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public override Type Type
        => typeof(object);

    public CqlSelectExpression AddRestriction(CqlRestriction restriction)
    {
        var partitionKeys = EntityType.GetPartitionKeyProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var clusteringKeys = EntityType.GetClusteringKeyProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        if (partitionKeys.Contains(restriction.Property.Name))
        {
            return new CqlSelectExpression(this) { PartitionKeyRestrictions = [.. PartitionKeyRestrictions, restriction] };
        }

        return clusteringKeys.Contains(restriction.Property.Name)
            ? new CqlSelectExpression(this) { ClusteringRestrictions = [.. ClusteringRestrictions, restriction] }
            : new CqlSelectExpression(this) { NonKeyRestrictions = [.. NonKeyRestrictions, restriction] };
    }

    public CqlSelectExpression AddOrdering(CqlOrdering ordering, bool replace)
        => new(this) { Orderings = replace ? [ordering] : [.. Orderings, ordering] };

    public CqlSelectExpression WithLimit(CqlValue limit)
        => new(this) { Limit = limit };

    public CqlSelectExpression WithPerPartitionLimit(CqlValue limit)
        => new(this) { PerPartitionLimit = limit };

    public CqlSelectExpression WithAllowFiltering()
        => new(this) { AllowFiltering = true };

    public CqlSelectExpression WithAggregate(string aggregate)
        => new(this) { Aggregate = aggregate };

    public CqlSelectExpression WithProjection(IReadOnlyList<IProperty> projection)
        => new(this) { Projection = projection };

    /// <summary>
    ///     The columns actually selected: a projection's columns, or every property in EF Core's own index
    ///     order so the value buffer lines up with what the materialiser expects.
    /// </summary>
    public IReadOnlyList<IProperty> ProjectedProperties
        => Projection ?? [.. EntityType.GetProperties()];

    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => this;
}

/// <summary>
///     Checks a translated query against the rules CQL enforces on <c>WHERE</c> and <c>ORDER BY</c>.
/// </summary>
/// <remarks>
///     Runs once over the finished expression rather than per clause, because restrictions accumulate across
///     several <c>Where</c> calls and a prefix rule cannot be judged from one term in isolation.
/// </remarks>
public static class CqlQueryValidator
{
    public static void Validate(CqlSelectExpression select)
    {
        ArgumentNullException.ThrowIfNull(select);

        var entityType = select.EntityType;
        var partitionKeys = entityType.GetPartitionKeyProperties();
        var clusteringKeys = entityType.GetClusteringKeyProperties();

        // Checked first: a secondary index is precisely what lets a query find rows without naming the
        // partition they live in, so it changes what the partition-key rule demands.
        ValidateNonKeyRestrictions(select, entityType);

        var indexed = entityType.GetScyllaIndexes().Select(i => i.PropertyName).ToHashSet(StringComparer.Ordinal);
        var usesIndex = select.NonKeyRestrictions.Any(r => indexed.Contains(r.Property.Name));

        ValidatePartitionKey(select, entityType, partitionKeys, usesIndex);
        ValidateClusteringPrefix(select, entityType, clusteringKeys);
        ValidateOrderings(select, entityType, clusteringKeys);
    }

    private static void ValidatePartitionKey(
        CqlSelectExpression select,
        IEntityType entityType,
        IReadOnlyList<IReadOnlyProperty> partitionKeys,
        bool usesIndex)
    {
        var restricted = select.PartitionKeyRestrictions.Select(r => r.Property.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var restriction in select.PartitionKeyRestrictions.Where(r => r.IsRange))
        {
            throw new InvalidOperationException(
                ScyllaStrings.RangeOnPartitionKey(entityType.DisplayName(), restriction.ColumnName));
        }

        // `IN` is only legal on the last partition key column; earlier ones must be equality.
        for (var i = 0; i < partitionKeys.Count - 1; i++)
        {
            var name = partitionKeys[i].Name;

            if (select.PartitionKeyRestrictions.Any(
                    r => string.Equals(r.Property.Name, name, StringComparison.Ordinal)
                         && r.Operator == CqlRestrictionOperator.In))
            {
                throw new InvalidOperationException(
                    ScyllaStrings.InOnNonLastKeyColumn(name, i + 1, partitionKeys.Count));
            }
        }

        if (restricted.Count == partitionKeys.Count || select.AllowFiltering || usesIndex)
        {
            return;
        }

        var missing = partitionKeys.Where(p => !restricted.Contains(p.Name)).Select(p => p.Name).ToList();

        throw new InvalidOperationException(
            ScyllaStrings.PartitionKeyNotRestricted(entityType.DisplayName(), missing));
    }

    private static void ValidateClusteringPrefix(
        CqlSelectExpression select,
        IEntityType entityType,
        IReadOnlyList<IReadOnlyProperty> clusteringKeys)
    {
        if (select.ClusteringRestrictions.Count == 0 || select.AllowFiltering)
        {
            return;
        }

        var restricted = select.ClusteringRestrictions.Select(r => r.Property.Name).ToHashSet(StringComparer.Ordinal);

        // Clustering columns are only usable as a contiguous prefix: the storage engine seeks by them in
        // order, so a gap makes the ones after it unusable.
        var gapIndex = -1;

        for (var i = 0; i < clusteringKeys.Count; i++)
        {
            if (!restricted.Contains(clusteringKeys[i].Name))
            {
                gapIndex = i;
                continue;
            }

            if (gapIndex >= 0)
            {
                throw new InvalidOperationException(
                    ScyllaStrings.ClusteringPrefixBroken(
                        entityType.DisplayName(),
                        clusteringKeys[gapIndex].Name,
                        clusteringKeys[i].Name,
                        clusteringKeys.Select(p => p.Name).ToList()));
            }
        }

        var ranges = select.ClusteringRestrictions.Where(r => r.IsRange).ToList();

        if (ranges.Count > 1)
        {
            throw new InvalidOperationException(
                ScyllaStrings.MultipleClusteringRanges(ranges[0].ColumnName, ranges[1].ColumnName));
        }

        if (ranges.Count == 1)
        {
            var lastRestricted = clusteringKeys.Last(k => restricted.Contains(k.Name)).Name;

            if (!string.Equals(ranges[0].Property.Name, lastRestricted, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    ScyllaStrings.RangeNotOnLastClusteringColumn(ranges[0].ColumnName, lastRestricted));
            }
        }
    }

    private static void ValidateNonKeyRestrictions(CqlSelectExpression select, IEntityType entityType)
    {
        if (select.NonKeyRestrictions.Count == 0 || select.AllowFiltering)
        {
            return;
        }

        // A secondary index makes the column seekable, which is exactly what turns a scan back into a lookup.
        var indexed = entityType.GetScyllaIndexes().Select(i => i.PropertyName).ToHashSet(StringComparer.Ordinal);

        var unindexed = select.NonKeyRestrictions
            .FirstOrDefault(r => !indexed.Contains(r.Property.Name));

        if (unindexed is not null)
        {
            throw new InvalidOperationException(
                ScyllaStrings.NonKeyRestrictionNeedsFiltering(entityType.DisplayName(), unindexed.ColumnName));
        }

        // ScyllaDB will not combine more than one indexed column in a single query.
        if (select.NonKeyRestrictions.Count > 1)
        {
            throw new InvalidOperationException(
                ScyllaStrings.MultipleIndexedRestrictions(
                    select.NonKeyRestrictions[0].ColumnName, select.NonKeyRestrictions[1].ColumnName));
        }
    }

    private static void ValidateOrderings(
        CqlSelectExpression select,
        IEntityType entityType,
        IReadOnlyList<IReadOnlyProperty> clusteringKeys)
    {
        if (select.Orderings.Count == 0)
        {
            return;
        }

        var clusteringNames = clusteringKeys.Select(p => p.Name).ToList();

        foreach (var ordering in select.Orderings)
        {
            if (!clusteringNames.Contains(ordering.Property.Name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    ScyllaStrings.OrderByNonClusteringColumn(
                        entityType.DisplayName(), ordering.Property.Name, clusteringNames));
            }
        }

        // Rows are stored in clustering order, so the engine can walk them forwards or backwards and nothing
        // else. Any other permutation would need a sort CQL will not perform.
        var requested = select.Orderings.Select(o => o.Property.Name).ToList();
        var expectedPrefix = clusteringNames.Take(requested.Count).ToList();

        if (!requested.SequenceEqual(expectedPrefix, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                ScyllaStrings.OrderByNotClusteringOrder(requested, clusteringNames));
        }

        var declared = select.Orderings
            .Select(o => o.Ascending == (o.Property.GetClusteringOrder() == ScyllaClusteringOrder.Ascending))
            .ToList();

        if (declared.Distinct().Count() > 1)
        {
            throw new InvalidOperationException(
                ScyllaStrings.OrderByNotClusteringOrder(requested, clusteringNames));
        }
    }
}

/// <summary>
///     Renders a <see cref="CqlSelectExpression" /> as CQL plus its bound values.
/// </summary>
public static class CqlQueryGenerator
{
    public static (string Cql, object?[] Values) Generate(
        CqlSelectExpression select,
        IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(parameters);

        var entityType = select.EntityType;
        var values = new List<object?>();

        var builder = new StringBuilder("SELECT ");

        if (select.Aggregate is { } aggregate)
        {
            builder.Append(aggregate);
        }
        else
        {
            builder.AppendJoin(", ", select.ProjectedProperties.Select(p => Quote(p.GetColumnName())));
        }

        builder.Append(" FROM ")
            .Append(Quote(entityType.GetKeyspace()!))
            .Append('.')
            .Append(Quote(entityType.GetScyllaTableName()));

        var restrictions = select.PartitionKeyRestrictions
            .Concat(select.ClusteringRestrictions)
            .Concat(select.NonKeyRestrictions)
            .ToList();

        if (restrictions.Count > 0)
        {
            builder.Append(" WHERE ");

            for (var i = 0; i < restrictions.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" AND ");
                }

                AppendRestriction(builder, restrictions[i], values, parameters);
            }
        }

        if (select.Orderings.Count > 0)
        {
            builder.Append(" ORDER BY ")
                .AppendJoin(
                    ", ",
                    select.Orderings.Select(o => $"{Quote(o.Property.GetColumnName())} {(o.Ascending ? "ASC" : "DESC")}"));
        }

        // CQL fixes this order: PER PARTITION LIMIT trims each partition first, then LIMIT trims the result.
        if (select.PerPartitionLimit is { } perPartitionLimit)
        {
            builder.Append(" PER PARTITION LIMIT ?");
            values.Add(Convert.ToInt32(perPartitionLimit.Resolve(parameters), provider: null));
        }

        if (select.Limit is { } limit)
        {
            builder.Append(" LIMIT ?");
            values.Add(Convert.ToInt32(limit.Resolve(parameters), provider: null));
        }

        if (select.AllowFiltering)
        {
            builder.Append(" ALLOW FILTERING");
        }

        return (builder.ToString(), [.. values]);
    }

    private static void AppendRestriction(
        StringBuilder builder,
        CqlRestriction restriction,
        List<object?> values,
        IReadOnlyDictionary<string, object?> parameters)
    {
        builder.Append(Quote(restriction.ColumnName));

        if (restriction.Operator == CqlRestrictionOperator.In)
        {
            var items = (restriction.Values[0].Resolve(parameters) as System.Collections.IEnumerable)?
                .Cast<object?>()
                .ToList() ?? [];

            builder.Append(" IN (").AppendJoin(", ", Enumerable.Repeat("?", items.Count)).Append(')');
            values.AddRange(items.Select(v => ConvertToProvider(restriction.Property, v)));
            return;
        }

        builder.Append(restriction.Operator switch
        {
            CqlRestrictionOperator.Equal => " = ?",
            CqlRestrictionOperator.GreaterThan => " > ?",
            CqlRestrictionOperator.GreaterThanOrEqual => " >= ?",
            CqlRestrictionOperator.LessThan => " < ?",
            CqlRestrictionOperator.LessThanOrEqual => " <= ?",
            _ => throw new InvalidOperationException($"Unhandled operator '{restriction.Operator}'.")
        });

        values.Add(ConvertToProvider(restriction.Property, restriction.Values[0].Resolve(parameters)));
    }

    private static object? ConvertToProvider(IProperty property, object? value)
        => property.GetTypeMapping().Converter is { } converter ? converter.ConvertToProvider(value) : value;

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
