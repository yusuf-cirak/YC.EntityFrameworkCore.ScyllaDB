using System.Globalization;

namespace YC.EntityFrameworkCore.ScyllaDB;

/// <summary>
///     A keyspace's replication strategy.
/// </summary>
/// <remarks>
///     Modelled as a small type rather than a raw string so that a typo becomes a compile error instead of a
///     keyspace that silently replicates to a data centre that does not exist.
/// </remarks>
public sealed class ScyllaReplication
{
    private readonly string _cql;

    private ScyllaReplication(string cql)
        => _cql = cql;

    /// <summary>
    ///     Replicates every row to <paramref name="replicationFactor" /> nodes, ignoring data centres.
    ///     Suitable for a single data centre, and for development.
    /// </summary>
    public static ScyllaReplication SimpleStrategy(int replicationFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replicationFactor);

        return new ScyllaReplication(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{{'class': 'SimpleStrategy', 'replication_factor': {replicationFactor}}}"));
    }

    /// <summary>
    ///     Replicates per data centre. This is the production strategy: it is what makes
    ///     <c>LOCAL_QUORUM</c> meaningful, and it cannot be changed later without a full repair.
    /// </summary>
    /// <param name="dataCenters">Each data centre name with its replication factor.</param>
    public static ScyllaReplication NetworkTopologyStrategy(
        params (string DataCenter, int ReplicationFactor)[] dataCenters)
    {
        ArgumentNullException.ThrowIfNull(dataCenters);

        if (dataCenters.Length == 0)
        {
            throw new ArgumentException(
                "NetworkTopologyStrategy needs at least one data centre and its replication factor.",
                nameof(dataCenters));
        }

        var entries = dataCenters.Select(
            dc =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(dc.DataCenter, nameof(dataCenters));
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dc.ReplicationFactor, nameof(dataCenters));

                return string.Create(CultureInfo.InvariantCulture, $"'{dc.DataCenter}': {dc.ReplicationFactor}");
            });

        return new ScyllaReplication($"{{'class': 'NetworkTopologyStrategy', {string.Join(", ", entries)}}}");
    }

    /// <summary>
    ///     The map literal used in a <c>CREATE KEYSPACE ... WITH replication = ...</c> statement.
    /// </summary>
    public string ToCql()
        => _cql;

    public override string ToString()
        => _cql;
}
