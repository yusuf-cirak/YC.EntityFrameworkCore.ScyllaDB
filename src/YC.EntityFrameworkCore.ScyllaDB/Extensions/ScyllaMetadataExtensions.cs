using Microsoft.EntityFrameworkCore.Metadata;
using YC.EntityFrameworkCore.ScyllaDB;
using YC.EntityFrameworkCore.ScyllaDB.Metadata;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Reads the provider's annotations back off a built model.
/// </summary>
public static class ScyllaModelExtensions
{
    /// <summary>
    ///     The keyspace used by entity types that do not name one of their own.
    /// </summary>
    public static string? GetDefaultKeyspace(this IReadOnlyModel model)
        => (string?)model.FindAnnotation(ScyllaAnnotationNames.DefaultKeyspace)?.Value;

    public static void SetDefaultKeyspace(this IMutableModel model, string? keyspace)
        => model.SetOrRemoveAnnotation(ScyllaAnnotationNames.DefaultKeyspace, keyspace);

    /// <summary>
    ///     The replication strategy used when the provider creates the keyspace.
    /// </summary>
    public static ScyllaReplication? GetReplication(this IReadOnlyModel model)
        => (ScyllaReplication?)model.FindAnnotation(ScyllaAnnotationNames.Replication)?.Value;

    public static void SetReplication(this IMutableModel model, ScyllaReplication? replication)
        => model.SetOrRemoveAnnotation(ScyllaAnnotationNames.Replication, replication);

    public static bool? GetDurableWrites(this IReadOnlyModel model)
        => (bool?)model.FindAnnotation(ScyllaAnnotationNames.DurableWrites)?.Value;

    public static void SetDurableWrites(this IMutableModel model, bool? durableWrites)
        => model.SetOrRemoveAnnotation(ScyllaAnnotationNames.DurableWrites, durableWrites);
}

/// <inheritdoc cref="ScyllaModelExtensions" />
public static class ScyllaEntityTypeExtensions
{
    /// <summary>
    ///     The CQL table name, falling back to the entity type's own table name and then its short name.
    /// </summary>
    public static string GetScyllaTableName(this IReadOnlyEntityType entityType)
        => (string?)entityType.FindAnnotation(ScyllaAnnotationNames.TableName)?.Value
           ?? entityType.ShortName();

    public static void SetScyllaTableName(this IMutableEntityType entityType, string? tableName)
        => entityType.SetOrRemoveAnnotation(ScyllaAnnotationNames.TableName, tableName);

    /// <summary>
    ///     The keyspace holding this table, falling back to the model's default.
    /// </summary>
    public static string? GetKeyspace(this IReadOnlyEntityType entityType)
        => (string?)entityType.FindAnnotation(ScyllaAnnotationNames.Keyspace)?.Value
           ?? entityType.Model.GetDefaultKeyspace();

    public static void SetKeyspace(this IMutableEntityType entityType, string? keyspace)
        => entityType.SetOrRemoveAnnotation(ScyllaAnnotationNames.Keyspace, keyspace);

    /// <summary>
    ///     The table's <c>default_time_to_live</c>, after which rows expire on their own.
    /// </summary>
    public static TimeSpan? GetDefaultTimeToLive(this IReadOnlyEntityType entityType)
        => (TimeSpan?)entityType.FindAnnotation(ScyllaAnnotationNames.DefaultTimeToLive)?.Value;

    public static void SetDefaultTimeToLive(this IMutableEntityType entityType, TimeSpan? timeToLive)
        => entityType.SetOrRemoveAnnotation(ScyllaAnnotationNames.DefaultTimeToLive, timeToLive);

    /// <summary>
    ///     The partition key columns, in the order they were declared.
    /// </summary>
    public static IReadOnlyList<IReadOnlyProperty> GetPartitionKeyProperties(this IReadOnlyEntityType entityType)
        => [.. entityType.GetProperties()
            .Where(p => p.GetPartitionKeyOrdinal() is not null)
            .OrderBy(p => p.GetPartitionKeyOrdinal()!.Value)];

    /// <summary>
    ///     The clustering columns, in the order they were declared — which is also their on-disk order.
    /// </summary>
    public static IReadOnlyList<IReadOnlyProperty> GetClusteringKeyProperties(this IReadOnlyEntityType entityType)
        => [.. entityType.GetProperties()
            .Where(p => p.GetClusteringKeyOrdinal() is not null)
            .OrderBy(p => p.GetClusteringKeyOrdinal()!.Value)];

    /// <summary>
    ///     Whether this is a counter table. A CQL table is either all counters plus its primary key, or none.
    /// </summary>
    public static bool IsCounterTable(this IReadOnlyEntityType entityType)
        => entityType.GetProperties().Any(p => p.IsCounter());
}

/// <inheritdoc cref="ScyllaModelExtensions" />
public static class ScyllaPropertyExtensions
{
    public static int? GetPartitionKeyOrdinal(this IReadOnlyProperty property)
        => (int?)property.FindAnnotation(ScyllaAnnotationNames.PartitionKeyOrdinal)?.Value;

    public static void SetPartitionKeyOrdinal(this IMutableProperty property, int? ordinal)
        => property.SetOrRemoveAnnotation(ScyllaAnnotationNames.PartitionKeyOrdinal, ordinal);

    public static int? GetClusteringKeyOrdinal(this IReadOnlyProperty property)
        => (int?)property.FindAnnotation(ScyllaAnnotationNames.ClusteringKeyOrdinal)?.Value;

    public static void SetClusteringKeyOrdinal(this IMutableProperty property, int? ordinal)
        => property.SetOrRemoveAnnotation(ScyllaAnnotationNames.ClusteringKeyOrdinal, ordinal);

    public static ScyllaClusteringOrder GetClusteringOrder(this IReadOnlyProperty property)
        => (ScyllaClusteringOrder?)property.FindAnnotation(ScyllaAnnotationNames.ClusteringOrder)?.Value
           ?? ScyllaClusteringOrder.Ascending;

    public static void SetClusteringOrder(this IMutableProperty property, ScyllaClusteringOrder? order)
        => property.SetOrRemoveAnnotation(ScyllaAnnotationNames.ClusteringOrder, order);

    public static bool IsStaticColumn(this IReadOnlyProperty property)
        => (bool?)property.FindAnnotation(ScyllaAnnotationNames.StaticColumn)?.Value ?? false;

    public static void SetIsStaticColumn(this IMutableProperty property, bool? isStatic)
        => property.SetOrRemoveAnnotation(ScyllaAnnotationNames.StaticColumn, isStatic);

    public static bool IsCounter(this IReadOnlyProperty property)
        => (bool?)property.FindAnnotation(ScyllaAnnotationNames.Counter)?.Value ?? false;

    public static void SetIsCounter(this IMutableProperty property, bool? isCounter)
        => property.SetOrRemoveAnnotation(ScyllaAnnotationNames.Counter, isCounter);

    /// <summary>
    ///     An explicitly configured CQL type, which overrides whatever the CLR type would map to.
    /// </summary>
    public static string? GetCqlType(this IReadOnlyProperty property)
        => (string?)property.FindAnnotation(ScyllaAnnotationNames.CqlType)?.Value;

    public static void SetCqlType(this IMutableProperty property, string? cqlType)
        => property.SetOrRemoveAnnotation(ScyllaAnnotationNames.CqlType, cqlType);

    public static ScyllaCollectionKind? GetCollectionKind(this IReadOnlyProperty property)
        => (ScyllaCollectionKind?)property.FindAnnotation(ScyllaAnnotationNames.CollectionKind)?.Value;

    public static void SetCollectionKind(this IMutableProperty property, ScyllaCollectionKind? kind)
        => property.SetOrRemoveAnnotation(ScyllaAnnotationNames.CollectionKind, kind);

    /// <summary>
    ///     The CQL column name.
    /// </summary>
    /// <remarks>
    ///     Currently always the property name. Column renaming is a separate concern from the key and type
    ///     modelling this milestone covers, and adding it later will not move any call site.
    /// </remarks>
    public static string GetColumnName(this IReadOnlyProperty property)
        => property.Name;
}
