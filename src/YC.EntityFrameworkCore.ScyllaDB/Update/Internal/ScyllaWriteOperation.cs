using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;
using YC.EntityFrameworkCore.ScyllaDB.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Update.Internal;

/// <summary>
///     One CQL write, with the values to bind and the partition it belongs to.
/// </summary>
/// <param name="Cql">The statement, with <c>?</c> markers.</param>
/// <param name="Values">Values for those markers, already converted to provider types.</param>
/// <param name="PartitionKey">
///     Identifies the partition, so writes that share one can be batched atomically.
/// </param>
/// <param name="IsCounter">Counter writes cannot be mixed with ordinary ones in a batch.</param>
public sealed record ScyllaWriteOperation(
    string Cql,
    object?[] Values,
    ScyllaPartitionKey PartitionKey,
    bool IsCounter);

/// <summary>
///     A table plus a set of partition key values, compared by value.
/// </summary>
/// <remarks>
///     This is the grouping key for batching. CQL only makes an unlogged batch cheap and atomic when every
///     statement in it targets the same partition, so equality here has to be exact.
/// </remarks>
public sealed class ScyllaPartitionKey : IEquatable<ScyllaPartitionKey>
{
    private readonly object?[] _values;

    public ScyllaPartitionKey(string keyspace, string table, object?[] values)
    {
        Keyspace = keyspace;
        Table = table;
        _values = values;
    }

    public string Keyspace { get; }

    public string Table { get; }

    public bool Equals(ScyllaPartitionKey? other)
        => other is not null
           && string.Equals(Keyspace, other.Keyspace, StringComparison.Ordinal)
           && string.Equals(Table, other.Table, StringComparison.Ordinal)
           && _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj)
        => Equals(obj as ScyllaPartitionKey);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Keyspace, StringComparer.Ordinal);
        hash.Add(Table, StringComparer.Ordinal);

        foreach (var value in _values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
///     Turns tracked changes into CQL statements.
/// </summary>
public static class ScyllaDmlGenerator
{
    /// <summary>
    ///     Builds the write for one tracked entry.
    /// </summary>
    /// <remarks>
    ///     CQL <c>INSERT</c> and <c>UPDATE</c> are both blind upserts, so the distinction here is only about
    ///     which columns are sent, never about whether the row already exists.
    /// </remarks>
    public static ScyllaWriteOperation Create(IUpdateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var entityType = entry.EntityType;
        var keyProperties = KeyProperties(entityType);

        return entry.EntityState switch
        {
            EntityState.Added when entityType.IsCounterTable() => CounterUpdate(entry, keyProperties, isNewRow: true),
            EntityState.Added => Insert(entry, keyProperties),
            EntityState.Modified when entityType.IsCounterTable() => CounterUpdate(entry, keyProperties, isNewRow: false),
            EntityState.Modified => Update(entry, keyProperties),
            EntityState.Deleted => Delete(entry, keyProperties),
            _ => throw new InvalidOperationException(
                $"Entity state '{entry.EntityState}' does not produce a write.")
        };
    }

    private static ScyllaWriteOperation Insert(IUpdateEntry entry, IReadOnlyList<IProperty> keyProperties)
    {
        var entityType = entry.EntityType;
        var properties = entityType.GetProperties().ToList();
        var values = new object?[properties.Count];

        var builder = new StringBuilder("INSERT INTO ")
            .Append(QualifiedName(entityType))
            .Append(" (");

        for (var i = 0; i < properties.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Quote(properties[i].GetColumnName()));
            values[i] = ProviderValue(entry, properties[i]);
        }

        builder.Append(") VALUES (")
            .AppendJoin(", ", Enumerable.Repeat("?", properties.Count))
            .Append(')');

        return new ScyllaWriteOperation(
            builder.ToString(), values, PartitionKeyOf(entry), IsCounter: false);
    }

    private static ScyllaWriteOperation Update(IUpdateEntry entry, IReadOnlyList<IProperty> keyProperties)
    {
        var entityType = entry.EntityType;
        var keyNames = keyProperties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var modified = entityType.GetProperties()
            .Where(p => !keyNames.Contains(p.Name) && entry.IsModified(p))
            .ToList();

        if (modified.Count == 0)
        {
            // Nothing but key columns changed, which in CQL is not a change at all — the key *is* the row's
            // identity. Re-inserting the key would be a no-op write, so send one anyway rather than special-case
            // an empty statement downstream.
            return Insert(entry, keyProperties);
        }

        var values = new List<object?>(modified.Count + keyProperties.Count);

        var builder = new StringBuilder("UPDATE ")
            .Append(QualifiedName(entityType))
            .Append(" SET ");

        for (var i = 0; i < modified.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Quote(modified[i].GetColumnName())).Append(" = ?");
            values.Add(ProviderValue(entry, modified[i]));
        }

        AppendKeyPredicate(builder, entry, keyProperties, values);

        return new ScyllaWriteOperation(
            builder.ToString(), [.. values], PartitionKeyOf(entry), IsCounter: false);
    }

    private static ScyllaWriteOperation Delete(IUpdateEntry entry, IReadOnlyList<IProperty> keyProperties)
    {
        var values = new List<object?>(keyProperties.Count);

        var builder = new StringBuilder("DELETE FROM ").Append(QualifiedName(entry.EntityType));

        AppendKeyPredicate(builder, entry, keyProperties, values);

        return new ScyllaWriteOperation(
            builder.ToString(), [.. values], PartitionKeyOf(entry), IsCounter: false);
    }

    /// <summary>
    ///     Builds an increment for a counter table.
    /// </summary>
    /// <remarks>
    ///     A counter row cannot be inserted, only incremented into existence, so <c>Added</c> is expressed as an
    ///     increment from zero. The delta for an existing row comes from EF Core's original-value snapshot,
    ///     which is what makes <c>counter.Value += 5</c> work at all: CQL has no read-modify-write.
    /// </remarks>
    private static ScyllaWriteOperation CounterUpdate(
        IUpdateEntry entry,
        IReadOnlyList<IProperty> keyProperties,
        bool isNewRow)
    {
        var entityType = entry.EntityType;
        var counters = entityType.GetProperties().Where(p => p.IsCounter()).ToList();

        var values = new List<object?>(counters.Count + keyProperties.Count);
        var builder = new StringBuilder("UPDATE ").Append(QualifiedName(entityType)).Append(" SET ");
        var written = 0;

        foreach (var counter in counters)
        {
            var current = Convert.ToInt64(entry.GetCurrentValue(counter) ?? 0L, provider: null);
            var original = isNewRow ? 0L : Convert.ToInt64(entry.GetOriginalValue(counter) ?? 0L, provider: null);
            var delta = current - original;

            if (delta == 0)
            {
                continue;
            }

            if (written++ > 0)
            {
                builder.Append(", ");
            }

            var column = Quote(counter.GetColumnName());
            builder.Append(column).Append(" = ").Append(column).Append(delta < 0 ? " - ?" : " + ?");
            values.Add(Math.Abs(delta));
        }

        if (written == 0)
        {
            throw new InvalidOperationException(ScyllaStrings.CounterUnchanged(entityType.DisplayName()));
        }

        AppendKeyPredicate(builder, entry, keyProperties, values);

        return new ScyllaWriteOperation(
            builder.ToString(), [.. values], PartitionKeyOf(entry), IsCounter: true);
    }

    private static void AppendKeyPredicate(
        StringBuilder builder,
        IUpdateEntry entry,
        IReadOnlyList<IProperty> keyProperties,
        List<object?> values)
    {
        builder.Append(" WHERE ");

        for (var i = 0; i < keyProperties.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" AND ");
            }

            builder.Append(Quote(keyProperties[i].GetColumnName())).Append(" = ?");
            values.Add(ProviderValue(entry, keyProperties[i]));
        }
    }

    private static IReadOnlyList<IProperty> KeyProperties(IEntityType entityType)
        => [.. entityType.GetPartitionKeyProperties().Concat(entityType.GetClusteringKeyProperties())
            .Cast<IProperty>()];

    private static ScyllaPartitionKey PartitionKeyOf(IUpdateEntry entry)
    {
        var entityType = entry.EntityType;
        var partitionProperties = entityType.GetPartitionKeyProperties();
        var values = new object?[partitionProperties.Count];

        for (var i = 0; i < partitionProperties.Count; i++)
        {
            values[i] = ProviderValue(entry, (IProperty)partitionProperties[i]);
        }

        return new ScyllaPartitionKey(entityType.GetKeyspace()!, entityType.GetScyllaTableName(), values);
    }

    /// <summary>
    ///     Reads a value and applies any value converter, so the driver receives the type CQL expects.
    /// </summary>
    private static object? ProviderValue(IUpdateEntry entry, IProperty property)
    {
        var value = entry.GetCurrentValue(property);

        return property.GetTypeMapping().Converter is { } converter
            ? converter.ConvertToProvider(value)
            : value;
    }

    private static string QualifiedName(IEntityType entityType)
        => $"{Quote(entityType.GetKeyspace()!)}.{Quote(entityType.GetScyllaTableName())}";

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
