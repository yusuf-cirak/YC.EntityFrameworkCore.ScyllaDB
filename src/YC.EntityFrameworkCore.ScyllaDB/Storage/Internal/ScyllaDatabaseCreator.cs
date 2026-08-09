using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Creates and drops the keyspace and its tables.
/// </summary>
/// <remarks>
///     ScyllaDB propagates schema changes across the cluster eventually rather than atomically, so every DDL
///     step waits for the cluster to agree before the next one runs. Skipping that wait produces failures that
///     look random: a table created against a node that has not yet seen its keyspace.
/// </remarks>
public class ScyllaDatabaseCreator : IDatabaseCreator
{
    private readonly IScyllaClientWrapper _clientWrapper;
    private readonly IScyllaDdlGenerator _ddlGenerator;
    private readonly IScyllaSingletonOptions _options;
    private readonly ICurrentDbContext _currentContext;

    public ScyllaDatabaseCreator(
        IScyllaClientWrapper clientWrapper,
        IScyllaDdlGenerator ddlGenerator,
        IScyllaSingletonOptions options,
        ICurrentDbContext currentContext)
    {
        ArgumentNullException.ThrowIfNull(clientWrapper);
        ArgumentNullException.ThrowIfNull(ddlGenerator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(currentContext);

        _clientWrapper = clientWrapper;
        _ddlGenerator = ddlGenerator;
        _options = options;
        _currentContext = currentContext;
    }

    public virtual bool CanConnect()
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported(
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.CanConnect)}",
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.CanConnectAsync)}"));

    /// <summary>
    ///     Reports whether the cluster answers and the configured keyspace exists.
    /// </summary>
    /// <remarks>
    ///     The session is not bound to a keyspace, so reachability alone would say nothing about whether the
    ///     model's keyspace is actually there. Both are checked.
    /// </remarks>
    public virtual async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await KeyspaceExistsAsync(cancellationToken).ConfigureAwait(false);
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
    }

    public virtual bool EnsureCreated()
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported(
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.EnsureCreated)}",
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.EnsureCreatedAsync)}"));

    public virtual async Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var model = _currentContext.Context.Model;
        var keyspace = Keyspace(model);

        var created = !await KeyspaceExistsAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteDdlAsync(_ddlGenerator.CreateKeyspace(model, keyspace), cancellationToken)
            .ConfigureAwait(false);

        // Tables first: a materialized view cannot be created before the table it reads from exists.
        foreach (var entityType in model.GetEntityTypes().Where(e => !e.IsMaterializedView()))
        {
            await ExecuteDdlAsync(_ddlGenerator.CreateTable(entityType), cancellationToken).ConfigureAwait(false);

            foreach (var createIndex in _ddlGenerator.CreateIndexes(entityType))
            {
                await ExecuteDdlAsync(createIndex, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var view in model.GetEntityTypes().Where(e => e.IsMaterializedView()))
        {
            await ExecuteDdlAsync(_ddlGenerator.CreateMaterializedView(view), cancellationToken)
                .ConfigureAwait(false);
        }

        return created;
    }

    public virtual bool EnsureDeleted()
        => throw new InvalidOperationException(
            ScyllaStrings.SyncNotSupported(
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.EnsureDeleted)}",
                $"{nameof(DatabaseFacade)}.{nameof(DatabaseFacade.EnsureDeletedAsync)}"));

    public virtual async Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
    {
        if (!await KeyspaceExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await ExecuteDdlAsync(
                _ddlGenerator.DropKeyspace(Keyspace(_currentContext.Context.Model)), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    protected virtual async Task<bool> KeyspaceExistsAsync(CancellationToken cancellationToken)
    {
        var keyspace = Keyspace(_currentContext.Context.Model);

        var rows = await _clientWrapper.ExecuteAsync(
                new SimpleStatement(
                    "SELECT keyspace_name FROM system_schema.keyspaces WHERE keyspace_name = ?", keyspace),
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Any();
    }

    /// <summary>
    ///     Runs one DDL statement and waits for the cluster to agree on the resulting schema.
    /// </summary>
    protected virtual async Task ExecuteDdlAsync(string cql, CancellationToken cancellationToken)
    {
        var rowSet = await _clientWrapper.ExecuteAsync(new SimpleStatement(cql), cancellationToken)
            .ConfigureAwait(false);

        if (rowSet.Info.IsSchemaInAgreement)
        {
            return;
        }

        var session = await _clientWrapper.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + _options.SchemaAgreementTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await session.Cluster.Metadata.CheckSchemaAgreementAsync().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(ScyllaStrings.SchemaAgreementTimedOut(_options.SchemaAgreementTimeout));
    }

    private string Keyspace(IModel model)
        => model.GetDefaultKeyspace()
           ?? (string.IsNullOrWhiteSpace(_options.Keyspace) ? null : _options.Keyspace)
           ?? throw new InvalidOperationException(ScyllaStrings.KeyspaceRequired());
}
