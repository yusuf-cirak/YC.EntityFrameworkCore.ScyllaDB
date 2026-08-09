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
/// <param name="IsConditional">
///     Whether this is a lightweight transaction. Those run through Paxos, are restricted to one partition, and
///     report whether they applied — so they are executed on their own and their result is inspected.
/// </param>
/// <param name="Entry">The tracked entry, needed to report a concurrency conflict against it.</param>
public sealed record ScyllaWriteOperation(
    string Cql,
    object?[] Values,
    ScyllaPartitionKey PartitionKey,
    bool IsCounter,
    bool IsConditional = false,
    IUpdateEntry? Entry = null);

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
    public static ScyllaWriteOperation Create(IUpdateEntry entry, ScyllaWriteOptions? writeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var entityType = entry.EntityType;
        var keyProperties = KeyProperties(entityType);

        return entry.EntityState switch
        {
            EntityState.Added when entityType.IsCounterTable() => CounterUpdate(entry, keyProperties, isNewRow: true),
            EntityState.Added => Insert(entry, keyProperties, writeOptions),
            EntityState.Modified when entityType.IsCounterTable() => CounterUpdate(entry, keyProperties, isNewRow: false),
            EntityState.Modified => Update(entry, keyProperties, writeOptions),
            EntityState.Deleted => Delete(entry, keyProperties, writeOptions),
            _ => throw new InvalidOperationException(
                $"Entity state '{entry.EntityState}' does not produce a write.")
        };
    }

    private static ScyllaWriteOperation Insert(
        IUpdateEntry entry,
        IReadOnlyList<IProperty> keyProperties,
        ScyllaWriteOptions? writeOptions)
    {
        var entityType = entry.EntityType;
        var properties = entityType.GetProperties().ToList();
        var conditional = entityType.UsesInsertIfNotExists();
        var values = new List<object?>(properties.Count + 2);

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
            values.Add(ProviderValue(entry, properties[i]));
        }

        builder.Append(") VALUES (")
            .AppendJoin(", ", Enumerable.Repeat("?", properties.Count))
            .Append(')');

        if (conditional)
        {
            builder.Append(" IF NOT EXISTS");
        }

        AppendUsing(builder, values, writeOptions, conditional, allowTimeToLive: true);

        return new ScyllaWriteOperation(
            builder.ToString(), [.. values], PartitionKeyOf(entry), IsCounter: false, conditional, entry);
    }

    private static ScyllaWriteOperation Update(
        IUpdateEntry entry,
        IReadOnlyList<IProperty> keyProperties,
        ScyllaWriteOptions? writeOptions)
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
            return Insert(entry, keyProperties, writeOptions);
        }

        var tokens = ConcurrencyTokens(entityType);
        var values = new List<object?>(modified.Count + keyProperties.Count + tokens.Count + 2);

        var builder = new StringBuilder("UPDATE ").Append(QualifiedName(entityType));

        // The USING clause precedes SET in CQL, and markers bind in text order, so its values go in first.
        AppendUsing(builder, values, writeOptions, tokens.Count > 0, allowTimeToLive: true);

        builder.Append(" SET ");

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
        AppendConcurrencyCondition(builder, entry, tokens, values);

        return new ScyllaWriteOperation(
            builder.ToString(), [.. values], PartitionKeyOf(entry), IsCounter: false, tokens.Count > 0, entry);
    }

    private static ScyllaWriteOperation Delete(
        IUpdateEntry entry,
        IReadOnlyList<IProperty> keyProperties,
        ScyllaWriteOptions? writeOptions)
    {
        var tokens = ConcurrencyTokens(entry.EntityType);
        var values = new List<object?>(keyProperties.Count + tokens.Count + 1);

        var builder = new StringBuilder("DELETE FROM ").Append(QualifiedName(entry.EntityType));

        // CQL rejects TTL on a delete: there is nothing left to expire.
        AppendUsing(builder, values, writeOptions, tokens.Count > 0, allowTimeToLive: false);

        AppendKeyPredicate(builder, entry, keyProperties, values);
        AppendConcurrencyCondition(builder, entry, tokens, values);

        return new ScyllaWriteOperation(
            builder.ToString(), [.. values], PartitionKeyOf(entry), IsCounter: false, tokens.Count > 0, entry);
    }

    private static IReadOnlyList<IProperty> ConcurrencyTokens(IEntityType entityType)
        => [.. entityType.GetProperties().Where(p => p.IsConcurrencyToken)];

    /// <summary>
    ///     Appends <c>USING TTL ... AND TIMESTAMP ...</c> when write options ask for it.
    /// </summary>
    private static void AppendUsing(
        StringBuilder builder,
        List<object?> values,
        ScyllaWriteOptions? writeOptions,
        bool isConditional,
        bool allowTimeToLive)
    {
        var timeToLive = allowTimeToLive ? writeOptions?.TimeToLive : null;
        var timestamp = writeOptions?.Timestamp;

        if (timestamp is not null && isConditional)
        {
            // Paxos assigns its own timestamps to keep the ballot ordering sound, so ScyllaDB rejects a custom
            // one outright. Saying so here beats surfacing the server's terser complaint.
            throw new InvalidOperationException(ScyllaStrings.TimestampWithLightweightTransaction());
        }

        if (timeToLive is null && timestamp is null)
        {
            return;
        }

        builder.Append(" USING ");

        if (timeToLive is { } ttl)
        {
            builder.Append("TTL ?");
            values.Add((int)ttl.TotalSeconds);
        }

        if (timestamp is { } at)
        {
            builder.Append(timeToLive is null ? "TIMESTAMP ?" : " AND TIMESTAMP ?");
            values.Add(at.ToUnixTimeMilliseconds() * 1000);
        }
    }

    /// <summary>
    ///     Appends the <c>IF</c> clause that turns the write into a lightweight transaction.
    /// </summary>
    /// <remarks>
    ///     This is the only optimistic concurrency CQL can offer. An ordinary write is a blind upsert with no
    ///     affected-row count, so EF Core's usual "zero rows changed means someone else won" cannot work.
    /// </remarks>
    private static void AppendConcurrencyCondition(
        StringBuilder builder,
        IUpdateEntry entry,
        IReadOnlyList<IProperty> tokens,
        List<object?> values)
    {
        if (tokens.Count == 0)
        {
            return;
        }

        builder.Append(" IF ");

        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" AND ");
            }

            builder.Append(Quote(tokens[i].GetColumnName())).Append(" = ?");

            var original = entry.GetOriginalValue(tokens[i]);

            values.Add(
                tokens[i].GetTypeMapping().Converter is { } converter
                    ? converter.ConvertToProvider(original)
                    : original);
        }
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
