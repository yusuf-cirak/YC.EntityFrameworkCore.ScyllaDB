using Cassandra;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Creates and drops the keyspace and its schema.
/// </summary>
/// <remarks>
///     ScyllaDB propagates schema changes across the cluster eventually rather than atomically, so the
///     create and drop paths have to wait for schema agreement before anything depending on the new schema runs.
/// </remarks>
public class ScyllaDatabaseCreator : IDatabaseCreator
{
    private readonly IScyllaClientWrapper _clientWrapper;

    public ScyllaDatabaseCreator(IScyllaClientWrapper clientWrapper)
    {
        ArgumentNullException.ThrowIfNull(clientWrapper);

        _clientWrapper = clientWrapper;
    }

    public virtual bool CanConnect()
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported(
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.CanConnect)}",
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.CanConnectAsync)}"));

    public virtual async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Reaching system.local proves both that a node answered and that the session's keyspace resolved.
            await _clientWrapper.ExecuteAsync("SELECT release_version FROM system.local", cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (NoHostAvailableException)
        {
            return false;
        }
        catch (UnauthorizedException)
        {
            return false;
        }
        catch (AuthenticationException)
        {
            return false;
        }
        catch (InvalidQueryException)
        {
            // Raised when the configured keyspace does not exist.
            return false;
        }
    }

    public virtual bool EnsureCreated()
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported(
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.EnsureCreated)}",
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.EnsureCreatedAsync)}"));

    public virtual Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            ScyllaStrings.NotImplementedUntilPhase("Schema creation", "the DDL milestone"));

    public virtual bool EnsureDeleted()
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported(
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.EnsureDeleted)}",
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.EnsureDeletedAsync)}"));

    public virtual Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException(
            ScyllaStrings.NotImplementedUntilPhase("Schema deletion", "the DDL milestone"));
}
