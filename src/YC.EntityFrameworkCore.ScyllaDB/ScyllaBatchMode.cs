namespace YC.EntityFrameworkCore.ScyllaDB;

/// <summary>
///     How <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)" />
///     groups the statements it sends.
/// </summary>
public enum ScyllaBatchMode
{
    /// <summary>
    ///     The default. Writes that share a partition are sent as one <c>UNLOGGED</c> batch, which is atomic for that
    ///     partition and costs no more than the individual statements. Writes to different partitions are sent as
    ///     independent statements, concurrently.
    /// </summary>
    /// <remarks>
    ///     This means a single <c>SaveChangesAsync</c> spanning several partitions is <em>not</em> atomic. That is
    ///     inherent to CQL, and the provider logs a warning when it happens.
    /// </remarks>
    PerPartitionUnlogged,

    /// <summary>
    ///     Sends everything as one <c>LOGGED</c> batch, which eventually applies all statements or none — but still
    ///     provides no isolation, and is markedly slower across partitions.
    /// </summary>
    /// <remarks>
    ///     Widely considered an anti-pattern in Cassandra and ScyllaDB. It exists here because atomicity across
    ///     partitions is occasionally worth its cost; do not reach for it by default.
    /// </remarks>
    Logged,

    /// <summary>
    ///     Never batches. Every change is sent as its own statement, giving the most predictable latency and the
    ///     highest throughput, and no atomicity at all.
    /// </summary>
    Never
}
