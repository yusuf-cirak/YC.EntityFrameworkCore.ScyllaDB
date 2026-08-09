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

    public static string ProjectionNotTranslatable(string projection)
        => $"The projection '{projection}' reads no mapped column, so it cannot be translated. Project "
           + "properties of the entity, or read the rows first and project afterwards.";

    public static string NotTranslatable(string method)
        => $"'{method}' cannot be translated to CQL.";

    public static string JoinsNotSupported(string method)
        => $"CQL has no JOIN, so '{method}' cannot be translated. ScyllaDB expects related data to be "
           + "denormalised into a single table, or exposed through a materialized view — query that instead.";

    public static string SkipNotSupported()
        => "CQL has no OFFSET, so 'Skip' cannot be translated. Paging in ScyllaDB is cursor-based: read a page, "
           + "keep the paging state the server returns, and pass it back for the next page.";

    public static string GroupByNotSupported()
        => "'GroupBy' cannot be translated. CQL's GROUP BY only groups by a prefix of the primary key and "
           + "aggregates on the coordinator. Restrict the query to a single partition and group in memory after "
           + "'ToListAsync', or maintain a counter table.";

    public static string SetOperationsNotSupported(string method)
        => $"CQL has no set operators, so '{method}' cannot be translated. Run the queries separately and "
           + "combine the results in memory.";

    public static string SubqueriesNotSupported()
        => "CQL has no subqueries. Split the query into separate round trips.";

    public static string OrElseNotSupported()
        => "CQL's WHERE does not support OR. Run one query per branch and combine the results in memory, or "
           + "restructure the table so the alternatives share a partition.";

    public static string NotEqualNotSupported(string column)
        => $"CQL's WHERE does not support '!='. Column '{column}' was compared with inequality; only =, <, <=, "
           + ">, >= and IN are valid restrictions.";

    public static string AggregateNotSupported(string method)
        => $"'{method}' cannot be translated. CQL computes aggregates on the coordinator over every row it "
           + "scans, which is unbounded outside a single partition. Read the rows and aggregate in memory, or "
           + "maintain a counter table.";

    public static string PartitionKeyNotRestricted(string entityType, IReadOnlyList<string> missing)
        => $"This query would scan every partition of '{entityType}': "
           + $"{string.Join(", ", missing.Select(m => $"'{m}'"))} "
           + (missing.Count == 1 ? "is" : "are")
           + " part of the partition key but unrestricted. Restrict the whole partition key with '==', or opt "
           + "into a full scan with 'EF.Functions.AllowFiltering()'.";

    public static string RangeOnPartitionKey(string entityType, string column)
        => $"Partition key column '{entityType}.{column}' can only be restricted with '==' or 'Contains'. A "
           + "partition key has no ordering — it is hashed — so a range comparison cannot be answered.";

    public static string InOnNonLastKeyColumn(string column, int position, int total)
        => $"'Contains' translates to CQL's IN, which is only valid on the last partition key column. "
           + $"'{column}' is column {position} of {total}.";

    public static string ClusteringPrefixBroken(
        string entityType,
        string skipped,
        string restricted,
        IReadOnlyList<string> clusteringOrder)
        => $"Clustering columns must be restricted as a contiguous prefix. This query restricts "
           + $"'{restricted}' but skips '{skipped}' on '{entityType}' (clustering order: "
           + $"{string.Join(", ", clusteringOrder)}). Rows are stored in that order, so the engine cannot seek "
           + $"past a gap. Restrict '{skipped}' too, or opt into a scan with 'EF.Functions.AllowFiltering()'.";

    public static string MultipleClusteringRanges(string first, string second)
        => $"Only the last restricted clustering column may use a range comparison, but both '{first}' and "
           + $"'{second}' do.";

    public static string RangeNotOnLastClusteringColumn(string column, string lastRestricted)
        => $"A range comparison is only valid on the last restricted clustering column. '{column}' uses one, "
           + $"but '{lastRestricted}' is restricted after it.";

    public static string IndexOnUnknownProperty(string entityType, string property)
        => $"A secondary index on '{entityType}' names property '{property}', which is not mapped.";

    public static string MultipleIndexedRestrictions(string first, string second)
        => $"ScyllaDB uses at most one secondary index per query, but both '{first}' and '{second}' are "
           + "restricted. Keep one and let the rest be filtered, opting in with "
           + "'EF.Functions.AllowFiltering()'.";

    public static string NonKeyRestrictionNeedsFiltering(string entityType, string column)
        => $"Column '{entityType}.{column}' is not part of the primary key, so filtering on it means scanning "
           + "every row. Restrict the partition key instead, or opt in explicitly with "
           + "'EF.Functions.AllowFiltering()'.";

    public static string OrderByNonClusteringColumn(
        string entityType,
        string column,
        IReadOnlyList<string> clusteringColumns)
        => $"CQL can only order by clustering columns, and '{entityType}.{column}' is not one "
           + (clusteringColumns.Count == 0
               ? "— the table has none."
               : $"(clustering columns: {string.Join(", ", clusteringColumns)}). ")
           + "Order in memory after 'ToListAsync', or add a table or materialized view clustered by that column.";

    public static string OrderByNotClusteringOrder(
        IReadOnlyList<string> requested,
        IReadOnlyList<string> clusteringOrder)
        => $"CQL can only return rows in the declared clustering order ({string.Join(", ", clusteringOrder)}) "
           + $"or its exact reverse. The requested order was {string.Join(", ", requested)}. Rows are stored in "
           + "clustering order, so the engine can walk them forwards or backwards and nothing else.";

    public static string TimestampWithLightweightTransaction()
        => "A write timestamp cannot be set on a conditional write. Lightweight transactions order themselves "
           + "through Paxos and assign their own timestamps, so ScyllaDB rejects a custom one. Drop the "
           + "timestamp, or drop the concurrency token that made the write conditional.";

    public static string ConcurrencyConditionFailed(string entityType, string currentValues)
        => $"The conditional write to '{entityType}' was not applied: another writer changed the row first. "
           + $"Current database values: {currentValues}.";

    public static string InsertIfNotExistsFailed(string entityType, string currentValues)
        => $"The row already exists, so the conditional insert into '{entityType}' was not applied. Existing "
           + $"row: {currentValues}.";

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
