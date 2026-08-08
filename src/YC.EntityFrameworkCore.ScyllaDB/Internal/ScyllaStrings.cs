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
}
