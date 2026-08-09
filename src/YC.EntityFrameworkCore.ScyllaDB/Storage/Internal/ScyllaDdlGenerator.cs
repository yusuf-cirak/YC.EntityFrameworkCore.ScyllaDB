using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Produces the CQL that creates and drops a model's schema.
/// </summary>
/// <remarks>
///     Deliberately free of any session dependency: DDL generation is a pure function of the model, which keeps
///     it unit-testable without a database and leaves it reusable by a future migrations path.
/// </remarks>
public interface IScyllaDdlGenerator
{
    string CreateKeyspace(IModel model, string keyspace);

    string DropKeyspace(string keyspace);

    string CreateTable(IEntityType entityType);

    string DropTable(IEntityType entityType);

    IReadOnlyList<string> CreateIndexes(IEntityType entityType);
}

/// <inheritdoc cref="IScyllaDdlGenerator" />
public class ScyllaDdlGenerator : IScyllaDdlGenerator
{
    public virtual string CreateKeyspace(IModel model, string keyspace)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        // SimpleStrategy with a single replica is the only sane default: it is the one setting that works on a
        // single node, and any production topology has to be stated explicitly anyway.
        var replication = model.GetReplication() ?? ScyllaReplication.SimpleStrategy(1);

        var builder = new StringBuilder("CREATE KEYSPACE IF NOT EXISTS ")
            .Append(Quote(keyspace))
            .Append(" WITH replication = ")
            .Append(replication.ToCql());

        if (model.GetDurableWrites() is { } durableWrites)
        {
            builder.Append(" AND durable_writes = ").Append(durableWrites ? "true" : "false");
        }

        return builder.ToString();
    }

    public virtual string DropKeyspace(string keyspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        return $"DROP KEYSPACE IF EXISTS {Quote(keyspace)}";
    }

    public virtual string CreateTable(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        var partitionKeys = entityType.GetPartitionKeyProperties();
        var clusteringKeys = entityType.GetClusteringKeyProperties();

        if (partitionKeys.Count == 0)
        {
            throw new InvalidOperationException(ScyllaStrings.NoPartitionKey(entityType.DisplayName()));
        }

        var builder = new StringBuilder("CREATE TABLE IF NOT EXISTS ")
            .Append(QualifiedName(entityType))
            .AppendLine(" (");

        foreach (var property in entityType.GetProperties())
        {
            builder.Append("    ")
                .Append(Quote(property.GetColumnName()))
                .Append(' ')
                .Append(CqlTypeOf(property));

            // Static columns hold one value per partition, so they can only be declared where partitions can
            // contain more than one row.
            if (property.IsStaticColumn())
            {
                builder.Append(" STATIC");
            }

            builder.AppendLine(",");
        }

        builder.Append("    PRIMARY KEY (")
            .Append(PrimaryKeyDefinition(partitionKeys, clusteringKeys))
            .AppendLine(")")
            .Append(')');

        var withClauses = new List<string>();

        if (clusteringKeys.Count > 0)
        {
            var orders = clusteringKeys.Select(
                p => $"{Quote(p.GetColumnName())} {(p.GetClusteringOrder() == ScyllaClusteringOrder.Descending ? "DESC" : "ASC")}");

            withClauses.Add($"CLUSTERING ORDER BY ({string.Join(", ", orders)})");
        }

        if (entityType.GetDefaultTimeToLive() is { } timeToLive)
        {
            withClauses.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"default_time_to_live = {(long)timeToLive.TotalSeconds}"));
        }

        if (withClauses.Count > 0)
        {
            builder.Append(" WITH ").Append(string.Join(" AND ", withClauses));
        }

        return builder.ToString();
    }

    public virtual string DropTable(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        return $"DROP TABLE IF EXISTS {QualifiedName(entityType)}";
    }

    public virtual IReadOnlyList<string> CreateIndexes(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        return
        [
            .. entityType.GetScyllaIndexes().Select(
                index =>
                {
                    var column = entityType.FindProperty(index.PropertyName)
                                 ?? throw new InvalidOperationException(
                                     ScyllaStrings.IndexOnUnknownProperty(
                                         entityType.DisplayName(), index.PropertyName));

                    // An unnamed index lets ScyllaDB derive the name, which keeps the statement idempotent
                    // without the model having to invent one.
                    var name = index.Name is null ? string.Empty : $"{Quote(index.Name)} ";

                    return $"CREATE INDEX IF NOT EXISTS {name}ON {QualifiedName(entityType)} "
                           + $"({Quote(column.GetColumnName())})";
                })
        ];
    }

    /// <summary>
    ///     Renders the primary key: the partition key in parentheses, then the clustering columns.
    /// </summary>
    /// <remarks>
    ///     The inner parentheses are not cosmetic. <c>PRIMARY KEY (a, b)</c> means "partition by a, cluster by
    ///     b", while <c>PRIMARY KEY ((a, b))</c> means "partition by both" — completely different data
    ///     distribution. They are always emitted so the intent cannot be misread.
    /// </remarks>
    protected virtual string PrimaryKeyDefinition(
        IReadOnlyList<IReadOnlyProperty> partitionKeys,
        IReadOnlyList<IReadOnlyProperty> clusteringKeys)
    {
        var partition = $"({string.Join(", ", partitionKeys.Select(p => Quote(p.GetColumnName())))})";

        return clusteringKeys.Count == 0
            ? partition
            : $"{partition}, {string.Join(", ", clusteringKeys.Select(p => Quote(p.GetColumnName())))}";
    }

    protected virtual string CqlTypeOf(IProperty property)
        => (property.FindTypeMapping() as ScyllaTypeMapping)?.CqlTypeName
           ?? property.GetCqlType()
           ?? throw new InvalidOperationException(
               ScyllaStrings.NoCqlTypeForProperty(
                   property.DeclaringType.DisplayName(), property.Name, property.ClrType.Name));

    protected virtual string QualifiedName(IEntityType entityType)
    {
        var keyspace = entityType.GetKeyspace()
                       ?? throw new InvalidOperationException(
                           ScyllaStrings.NoKeyspace(entityType.DisplayName()));

        return $"{Quote(keyspace)}.{Quote(entityType.GetScyllaTableName())}";
    }

    /// <summary>
    ///     Quotes an identifier.
    /// </summary>
    /// <remarks>
    ///     Everything is quoted, always. An unquoted CQL identifier is folded to lower case, so a
    ///     <c>SensorId</c> property would become a <c>sensorid</c> column — and any hand-written CQL that
    ///     quoted the name would then fail to find it. Quoting uniformly keeps the model and the database
    ///     spelling the same.
    /// </remarks>
    protected static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
