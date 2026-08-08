using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Turns tracked changes into CQL writes.
/// </summary>
public class ScyllaDatabaseWrapper : Database, IResettableService
{
    public ScyllaDatabaseWrapper(DatabaseDependencies dependencies)
        : base(dependencies)
    {
    }

    public override int SaveChanges(IList<IUpdateEntry> entries)
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported(
                $"{nameof(DbContext)}.{nameof(DbContext.SaveChanges)}",
                $"{nameof(DbContext)}.{nameof(DbContext.SaveChangesAsync)}"));

    public override Task<int> SaveChangesAsync(
        IList<IUpdateEntry> entries,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            ScyllaStrings.NotImplementedUntilPhase("Saving changes", "the SaveChanges milestone"));

    public virtual void ResetState()
    {
    }

    public virtual Task ResetStateAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
