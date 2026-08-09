namespace YC.EntityFrameworkCore.ScyllaDB;

/// <summary>
///     Per-write options applied to everything a <c>SaveChangesAsync</c> sends while the scope is open.
/// </summary>
/// <param name="TimeToLive">
///     How long the written columns survive before expiring. Applies to inserts and updates; CQL does not allow
///     it on a delete.
/// </param>
/// <param name="Timestamp">
///     The write timestamp used to resolve conflicts. ScyllaDB keeps the value with the highest timestamp, so
///     setting this by hand takes over last-write-wins from the server clock — useful when replaying events in
///     their original order, and a good way to lose data otherwise.
/// </param>
public sealed record ScyllaWriteOptions(TimeSpan? TimeToLive = null, DateTimeOffset? Timestamp = null);
