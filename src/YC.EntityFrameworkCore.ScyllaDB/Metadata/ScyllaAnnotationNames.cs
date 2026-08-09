namespace YC.EntityFrameworkCore.ScyllaDB.Metadata;

/// <summary>
///     The annotation keys the provider stores on the model.
/// </summary>
public static class ScyllaAnnotationNames
{
    public const string Prefix = "ScyllaDb:";

    public const string DefaultKeyspace = Prefix + "DefaultKeyspace";
    public const string Replication = Prefix + "Replication";
    public const string DurableWrites = Prefix + "DurableWrites";

    public const string Keyspace = Prefix + "Keyspace";
    public const string TableName = Prefix + "TableName";
    public const string DefaultTimeToLive = Prefix + "Table:DefaultTimeToLive";

    public const string PartitionKeyOrdinal = Prefix + "PartitionKeyOrdinal";
    public const string ClusteringKeyOrdinal = Prefix + "ClusteringKeyOrdinal";
    public const string ClusteringOrder = Prefix + "ClusteringOrder";
    public const string StaticColumn = Prefix + "StaticColumn";
    public const string Counter = Prefix + "Counter";
    public const string CqlType = Prefix + "CqlType";
    public const string CollectionKind = Prefix + "CollectionKind";
    public const string InsertIfNotExists = Prefix + "Lwt:InsertIfNotExists";
    public const string Indexes = Prefix + "Indexes";
    public const string MaterializedViewBaseTable = Prefix + "MaterializedView:BaseTable";
}
