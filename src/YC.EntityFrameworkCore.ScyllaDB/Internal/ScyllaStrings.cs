namespace YC.EntityFrameworkCore.ScyllaDB.Internal;

/// <summary>
///     Every user-facing message the provider produces.
/// </summary>
/// <remarks>
///     CQL cannot express most of what LINQ can, so the quality of these messages is a feature rather than an
///     afterthought: each one names the construct, says why it cannot work, and gives the alternative. They live
///     here — plain C#, no <c>.resx</c>, since the provider ships no localisation — so that a test can assert
///     the exact text and stop them from rotting.
/// </remarks>
internal static class ScyllaStrings
{
    public static string SyncNotSupported(string syncMember, string asyncMember)
        => $"The ScyllaDB provider is asynchronous only, so '{syncMember}' is not supported. Use '{asyncMember}' instead. "
           + "The driver's I/O is natively asynchronous; running it synchronously would block a thread for no benefit and "
           + "risks deadlocks under a synchronization context.";

    public static string TransactionsNotSupported()
        => "ScyllaDB does not support transactions. Writes that fall in the same partition are sent as a single atomic "
           + "batch by SaveChangesAsync automatically. For a conditional write, use a lightweight transaction by marking a "
           + "property with 'IsConcurrencyToken()'. For cross-partition atomicity without isolation, opt in with "
           + "'UseScyllaDb(..., o => o.WithBatchMode(ScyllaBatchMode.Logged))'.";

    public static string NoConnectionConfigured()
        => "No ScyllaDB connection was configured. Call 'UseScyllaDb' with contact points and a keyspace, with an "
           + "'ICluster', or with an existing 'ISession'.";

    public static string KeyspaceRequired()
        => "A keyspace is required. Pass it to 'UseScyllaDb', or connect an 'ISession' that is already bound to one.";

    public static string ContactPointsRequired()
        => "At least one contact point is required. Pass the addresses of one or more ScyllaDB nodes to 'UseScyllaDb'.";

    public static string ContactPointMustNotIncludePort(string contactPoint)
        => $"Contact point '{contactPoint}' includes a port. The driver applies one port to the whole cluster, so "
           + "contact points are host names or addresses only. Pass the port separately: "
           + "'UseScyllaDb(hosts, keyspace, o => o.WithPort(9042))'.";

    public static string NotImplementedUntilPhase(string feature, string phase)
        => $"{feature} is not implemented yet (planned for {phase}).";

    public static string CounterUnchanged(string entityType)
        => $"An entry of counter table '{entityType}' was marked as changed, but no counter moved. CQL can only "
           + "increment a counter, so there is nothing to send. Change a counter value, or leave the entry "
           + "untracked.";

    public static string MultiPartitionSaveChanges(int partitionCount)
        => $"SaveChangesAsync wrote to {partitionCount} partitions. CQL is only atomic within a single "
           + "partition, so these statements were sent independently and a failure can leave the change "
           + "half-applied. Group the writes by partition, or accept the cost of a logged batch with "
           + "'WithBatchMode(ScyllaBatchMode.Logged)'.";

    public static string SchemaAgreementTimedOut(TimeSpan timeout)
        => $"The schema change was applied, but the cluster did not reach agreement within {timeout}. Some "
           + "nodes have not yet converged, so statements depending on the new schema may fail. Raise the "
           + "limit with 'WithSchemaAgreementTimeout', or retry.";

    public static string NoPartitionKey(string entityType)
        => $"Entity type '{entityType}' has no partition key, and every CQL table requires one — it is what "
           + "decides which node owns a row. Declare it with 'HasPartitionKey(x => x.Property)' or the "
           + "[PartitionKey] attribute.";

    public static string NoKeyspace(string entityType)
        => $"Entity type '{entityType}' is not mapped to a keyspace. Set one for the whole model with "
           + "'modelBuilder.HasDefaultKeyspace(\"name\")', or for this type with "
           + "'ToScyllaTable(\"table\", keyspace: \"name\")'.";

    public static string NoCqlTypeForProperty(string entityType, string property, string clrType)
        => $"Property '{entityType}.{property}' of type '{clrType}' has no CQL type. Map it explicitly with "
           + "'HasCqlType(\"...\")' or the [CqlType] attribute, convert it to a supported type with "
           + "'HasConversion', or ignore it.";

    public static string AmbiguousCollectionKind(string entityType, string property, string clrType)
        => $"Property '{entityType}.{property}' of type '{clrType}' could map to either a CQL list or a CQL "
           + "set, and the two behave differently — a list keeps order and duplicates, a set does not. Say "
           + "which with '.IsList()' or '.IsSet()'.";

    public static string CounterMixedWithRegularColumn(string entityType, string counter, string other)
        => $"Entity type '{entityType}' mixes counter column '{counter}' with regular column '{other}'. A CQL "
           + "table is either all counters plus its primary key, or contains none at all. Move "
           + $"'{other}' to a separate table.";

    public static string CounterInPrimaryKey(string entityType, string property)
        => $"Counter column '{entityType}.{property}' cannot be part of the primary key.";

    public static string CounterTableWithDefaultTimeToLive(string entityType)
        => $"Entity type '{entityType}' is a counter table, which cannot have a default time to live. Counters "
           + "have no expiry.";

    public static string StaticColumnWithoutClusteringKey(string entityType, string property)
        => $"Column '{entityType}.{property}' is static, but '{entityType}' has no clustering key. A static "
           + "column holds one value per partition, which means nothing when each partition holds a single "
           + "row. Add a clustering key, or drop the static marker.";

    public static string NavigationsNotSupported(string entityType, string navigation, string target)
        => $"Entity type '{entityType}' declares navigation '{navigation}' to '{target}'. CQL has no joins and "
           + "no foreign keys, so related data cannot be loaded in one query. Denormalise "
           + $"'{target}' into '{entityType}', or query the two tables separately.";
}
