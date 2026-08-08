using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;

/// <summary>
///     Rejects models CQL cannot express.
/// </summary>
/// <remarks>
///     These run before the core validator so that a ScyllaDB-specific problem is reported in ScyllaDB's terms.
///     An unmappable collection, for instance, would otherwise surface as EF Core's generic "no type mapping"
///     error, which says nothing about needing to choose between a list and a set.
/// </remarks>
public class ScyllaModelValidator : ModelValidator
{
    public ScyllaModelValidator(ModelValidatorDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Validate(IModel model, IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        ArgumentNullException.ThrowIfNull(model);

        foreach (var entityType in model.GetEntityTypes())
        {
            ValidateKeyspace(entityType);
            ValidateNoNavigations(entityType);
            ValidatePartitionKey(entityType);
            ValidateStaticColumns(entityType);
            ValidateCounters(entityType);
            ValidateTypeMappings(entityType);
        }

        base.Validate(model, logger);
    }

    protected virtual void ValidateKeyspace(IEntityType entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType.GetKeyspace()))
        {
            throw new InvalidOperationException(ScyllaStrings.NoKeyspace(entityType.DisplayName()));
        }
    }

    protected virtual void ValidateNoNavigations(IEntityType entityType)
    {
        var navigation = entityType.GetNavigations().FirstOrDefault();

        if (navigation is not null)
        {
            throw new InvalidOperationException(
                ScyllaStrings.NavigationsNotSupported(
                    entityType.DisplayName(),
                    navigation.Name,
                    navigation.TargetEntityType.DisplayName()));
        }
    }

    protected virtual void ValidatePartitionKey(IEntityType entityType)
    {
        if (entityType.GetPartitionKeyProperties().Count == 0)
        {
            throw new InvalidOperationException(ScyllaStrings.NoPartitionKey(entityType.DisplayName()));
        }
    }

    protected virtual void ValidateStaticColumns(IEntityType entityType)
    {
        if (entityType.GetClusteringKeyProperties().Count > 0)
        {
            return;
        }

        var staticColumn = entityType.GetProperties().FirstOrDefault(p => p.IsStaticColumn());

        if (staticColumn is not null)
        {
            throw new InvalidOperationException(
                ScyllaStrings.StaticColumnWithoutClusteringKey(entityType.DisplayName(), staticColumn.Name));
        }
    }

    /// <summary>
    ///     Enforces CQL's rule that a table is either entirely counters plus its primary key, or has none.
    /// </summary>
    protected virtual void ValidateCounters(IEntityType entityType)
    {
        var counters = entityType.GetProperties().Where(p => p.IsCounter()).ToList();

        if (counters.Count == 0)
        {
            return;
        }

        var keyProperties = entityType.GetPartitionKeyProperties()
            .Concat(entityType.GetClusteringKeyProperties())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var counterInKey = counters.FirstOrDefault(p => keyProperties.Contains(p.Name));

        if (counterInKey is not null)
        {
            throw new InvalidOperationException(
                ScyllaStrings.CounterInPrimaryKey(entityType.DisplayName(), counterInKey.Name));
        }

        var regularColumn = entityType.GetProperties()
            .FirstOrDefault(p => !p.IsCounter() && !keyProperties.Contains(p.Name) && !p.IsStaticColumn());

        if (regularColumn is not null)
        {
            throw new InvalidOperationException(
                ScyllaStrings.CounterMixedWithRegularColumn(
                    entityType.DisplayName(), counters[0].Name, regularColumn.Name));
        }

        if (entityType.GetDefaultTimeToLive() is not null)
        {
            throw new InvalidOperationException(
                ScyllaStrings.CounterTableWithDefaultTimeToLive(entityType.DisplayName()));
        }
    }

    protected virtual void ValidateTypeMappings(IEntityType entityType)
    {
        foreach (var property in entityType.GetProperties())
        {
            if (property.FindTypeMapping() is not null || property.GetCqlType() is not null)
            {
                continue;
            }

            // A collection that reached here could have been either a list or a set. Saying so is far more use
            // than "no type mapping found".
            throw new InvalidOperationException(
                IsAmbiguousCollection(property.ClrType)
                    ? ScyllaStrings.AmbiguousCollectionKind(
                        entityType.DisplayName(), property.Name, property.ClrType.Name)
                    : ScyllaStrings.NoCqlTypeForProperty(
                        entityType.DisplayName(), property.Name, property.ClrType.Name));
        }
    }

    private static bool IsAmbiguousCollection(Type clrType)
        => clrType != typeof(string)
           && clrType != typeof(byte[])
           && typeof(IEnumerable).IsAssignableFrom(clrType);
}
