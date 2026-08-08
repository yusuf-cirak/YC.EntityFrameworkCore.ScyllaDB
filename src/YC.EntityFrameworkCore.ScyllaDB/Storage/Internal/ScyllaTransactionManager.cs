using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Declares that ScyllaDB has no transactions.
/// </summary>
/// <remarks>
///     CQL offers no transaction of any kind. A logged batch applies all statements or none, eventually, but
///     provides no isolation and no rollback; lightweight transactions are per-partition compare-and-set. Neither
///     is a transaction, so rather than pretend, every member here throws. The alternative — a manager that
///     silently no-ops — would let application code believe it had rolled back when nothing was undone.
/// </remarks>
public class ScyllaTransactionManager : IDbContextTransactionManager
{
    public virtual IDbContextTransaction? CurrentTransaction
        => null;

    public virtual IDbContextTransaction BeginTransaction()
        => throw new InvalidOperationException(ScyllaStrings.TransactionsNotSupported());

    public virtual Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ScyllaStrings.TransactionsNotSupported());

    public virtual void CommitTransaction()
        => throw new InvalidOperationException(ScyllaStrings.TransactionsNotSupported());

    public virtual Task CommitTransactionAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ScyllaStrings.TransactionsNotSupported());

    public virtual void RollbackTransaction()
        => throw new InvalidOperationException(ScyllaStrings.TransactionsNotSupported());

    public virtual Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(ScyllaStrings.TransactionsNotSupported());

    // EF Core calls these when resetting a pooled context. They must stay silent — there is never any
    // transaction state to discard.
    public virtual void ResetState()
    {
    }

    public virtual Task ResetStateAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
