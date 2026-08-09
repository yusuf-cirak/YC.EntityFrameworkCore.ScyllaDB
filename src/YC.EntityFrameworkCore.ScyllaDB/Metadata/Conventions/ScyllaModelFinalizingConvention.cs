using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Metadata.Conventions;

/// <summary>
///     Applies the ScyllaDB attributes, then derives each table's primary key from them.
/// </summary>
/// <remarks>
///     <para>
///         Both jobs run at model finalizing so that the fluent API and the attributes cannot fight: anything
///         already configured fluently is left alone, and attributes only fill the gaps.
///     </para>
///     <para>
///         The primary key has to be derived rather than discovered, because in CQL it is not an identity
///         constraint but the physical layout: partition key columns first, in declaration order, then
///         clustering columns in theirs. EF Core's own key discovery would happily pick an <c>Id</c> property
///         and produce a table nobody can query.
///     </para>
/// </remarks>
public class ScyllaModelFinalizingConvention : IModelFinalizingConvention
{
    private readonly string? _defaultKeyspaceFromOptions;

    /// <param name="defaultKeyspaceFromOptions">
    ///     The keyspace named when configuring the connection, used when the model does not set one of its own.
    /// </param>
    public ScyllaModelFinalizingConvention(string? defaultKeyspaceFromOptions = null)
        => _defaultKeyspaceFromOptions = defaultKeyspaceFromOptions;

    public virtual void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        if (_defaultKeyspaceFromOptions is not null
            && modelBuilder.Metadata.FindAnnotation(ScyllaAnnotationNames.DefaultKeyspace) is null)
        {
            modelBuilder.Metadata.SetAnnotation(
                ScyllaAnnotationNames.DefaultKeyspace, _defaultKeyspaceFromOptions);
        }

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            ApplyEntityTypeAttributes(entityType);

            foreach (var property in entityType.GetProperties())
            {
                ApplyPropertyAttributes(property);
            }

            ApplyPrimaryKey(entityType);
            ValidateCollectionMembers(entityType);
        }
    }

    /// <summary>
    ///     Rejects a collection member that could map to either a CQL list or a CQL set.
    /// </summary>
    /// <remarks>
    ///     Such a member never becomes a property at all, so the model validator never sees it and EF Core
    ///     reports a generic "could not be mapped" instead. Catching it here — while the CLR member is still in
    ///     view — is what makes the message name the actual choice the model has to make.
    /// </remarks>
    protected virtual void ValidateCollectionMembers(IConventionEntityType entityType)
    {
        foreach (var clrProperty in entityType.ClrType.GetRuntimeProperties())
        {
            if (!clrProperty.CanRead
                || clrProperty.GetIndexParameters().Length > 0
                || entityType.FindProperty(clrProperty.Name) is not null
                || entityType.FindNavigation(clrProperty.Name) is not null
                || entityType.FindSkipNavigation(clrProperty.Name) is not null
                || entityType.FindComplexProperty(clrProperty.Name) is not null
                || entityType.FindIgnoredConfigurationSource(clrProperty.Name) is not null)
            {
                continue;
            }

            if (IsAmbiguousCollection(clrProperty.PropertyType))
            {
                throw new InvalidOperationException(
                    ScyllaStrings.AmbiguousCollectionKind(
                        entityType.DisplayName(), clrProperty.Name, clrProperty.PropertyType.Name));
            }
        }
    }

    private static bool IsAmbiguousCollection(Type clrType)
        => clrType != typeof(string)
           && clrType != typeof(byte[])
           && typeof(System.Collections.IEnumerable).IsAssignableFrom(clrType);

    protected virtual void ApplyEntityTypeAttributes(IConventionEntityType entityType)
    {
        if (entityType.ClrType.GetCustomAttribute<ScyllaTableAttribute>() is not { } table)
        {
            return;
        }

        if (entityType.FindAnnotation(ScyllaAnnotationNames.TableName) is null)
        {
            entityType.SetAnnotation(ScyllaAnnotationNames.TableName, table.Name);
        }

        if (table.Keyspace is not null && entityType.FindAnnotation(ScyllaAnnotationNames.Keyspace) is null)
        {
            entityType.SetAnnotation(ScyllaAnnotationNames.Keyspace, table.Keyspace);
        }
    }

    protected virtual void ApplyPropertyAttributes(IConventionProperty property)
    {
        if (property.PropertyInfo is not { } propertyInfo)
        {
            return;
        }

        if (propertyInfo.GetCustomAttribute<PartitionKeyAttribute>() is { } partitionKey
            && property.FindAnnotation(ScyllaAnnotationNames.PartitionKeyOrdinal) is null)
        {
            property.SetAnnotation(ScyllaAnnotationNames.PartitionKeyOrdinal, partitionKey.Order);
        }

        if (propertyInfo.GetCustomAttribute<ClusteringKeyAttribute>() is { } clusteringKey
            && property.FindAnnotation(ScyllaAnnotationNames.ClusteringKeyOrdinal) is null)
        {
            property.SetAnnotation(ScyllaAnnotationNames.ClusteringKeyOrdinal, clusteringKey.Order);
            property.SetAnnotation(ScyllaAnnotationNames.ClusteringOrder, clusteringKey.Direction);
        }

        if (propertyInfo.GetCustomAttribute<StaticColumnAttribute>() is not null
            && property.FindAnnotation(ScyllaAnnotationNames.StaticColumn) is null)
        {
            property.SetAnnotation(ScyllaAnnotationNames.StaticColumn, true);
        }

        if (propertyInfo.GetCustomAttribute<CounterAttribute>() is not null
            && property.FindAnnotation(ScyllaAnnotationNames.Counter) is null)
        {
            property.SetAnnotation(ScyllaAnnotationNames.Counter, true);
        }

        if (propertyInfo.GetCustomAttribute<CqlTypeAttribute>() is { } cqlType
            && property.FindAnnotation(ScyllaAnnotationNames.CqlType) is null)
        {
            property.SetAnnotation(ScyllaAnnotationNames.CqlType, cqlType.CqlType);
        }

        if (propertyInfo.GetCustomAttribute<SecondaryIndexAttribute>() is { } index)
        {
            var entityType = property.DeclaringType as IConventionEntityType;
            var existing = (IReadOnlyList<ScyllaIndex>?)entityType?
                .FindAnnotation(ScyllaAnnotationNames.Indexes)?.Value ?? [];

            if (entityType is not null && existing.All(i => i.PropertyName != property.Name))
            {
                entityType.SetAnnotation(
                    ScyllaAnnotationNames.Indexes,
                    (IReadOnlyList<ScyllaIndex>)[.. existing, new ScyllaIndex(property.Name, index.Name)]);
            }
        }
    }

    protected virtual void ApplyPrimaryKey(IConventionEntityType entityType)
    {
        var partitionKeys = OrderedByAnnotation(entityType, ScyllaAnnotationNames.PartitionKeyOrdinal);

        if (partitionKeys.Count == 0)
        {
            // Left for the model validator to report, which can say far more about how to fix it than a
            // convention can at this point.
            return;
        }

        var clusteringKeys = OrderedByAnnotation(entityType, ScyllaAnnotationNames.ClusteringKeyOrdinal);

        entityType.Builder.PrimaryKey([.. partitionKeys, .. clusteringKeys]);
    }

    private static List<IConventionProperty> OrderedByAnnotation(IConventionEntityType entityType, string annotation)
        => [.. entityType.GetProperties()
            .Select(p => (Property: p, Ordinal: p.FindAnnotation(annotation)?.Value as int?))
            .Where(x => x.Ordinal is not null)
            .OrderBy(x => x.Ordinal!.Value)
            .Select(x => x.Property)];
}
