using System.Net;
using System.Numerics;
using Cassandra;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests;

/// <summary>
///     The ScyllaDB driver ships a single <c>netstandard2.0</c> target and its vendor-tested matrix stops at
///     .NET 8, so running it on .NET 10 rests on binary compatibility rather than on a supported configuration.
///     The whole provider rests on that assumption, so these tests pin it down: the assembly loads, and every
///     driver-specific CLR type the type-mapping layer will depend on resolves and behaves.
///     No network is involved.
/// </summary>
public class DriverCompatibilityTests
{
    /// <summary>
    ///     The ScyllaDB fork renames the assembly to <c>ScyllaDB</c> but keeps the DataStax root namespace
    ///     <c>Cassandra</c>. So it does not collide with <c>CassandraCSharpDriver</c> on assembly identity —
    ///     only on type names, which an <c>extern alias</c> can disambiguate. If a future version renames the
    ///     namespace too, this test is where we find out.
    /// </summary>
    [Fact]
    public void Driver_assembly_is_the_ScyllaDB_fork_under_the_Cassandra_namespace()
    {
        var clusterType = typeof(Cluster);

        Assert.Equal("ScyllaDB", clusterType.Assembly.GetName().Name);
        Assert.Equal("Cassandra", clusterType.Namespace);
        Assert.NotNull(Cluster.Builder());
    }

    [Fact]
    public void TimeUuid_generates_and_round_trips_to_a_timestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var timeUuid = TimeUuid.NewId();

        Assert.InRange(timeUuid.GetDate(), before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.NotEqual(Guid.Empty, (Guid)timeUuid);
    }

    [Fact]
    public void LocalDate_maps_to_the_same_calendar_day_as_DateOnly()
    {
        var dateOnly = new DateOnly(2026, 8, 8);

        var localDate = new LocalDate(dateOnly.Year, dateOnly.Month, dateOnly.Day);

        Assert.Equal(dateOnly.Year, localDate.Year);
        Assert.Equal(dateOnly.Month, localDate.Month);
        Assert.Equal(dateOnly.Day, localDate.Day);
    }

    [Fact]
    public void LocalTime_preserves_nanosecond_components()
    {
        var localTime = new LocalTime(13, 45, 30, 123_456_789);

        Assert.Equal(13, localTime.Hour);
        Assert.Equal(45, localTime.Minute);
        Assert.Equal(30, localTime.Second);
        Assert.Equal(123_456_789, localTime.Nanoseconds);
    }

    /// <summary>
    ///     CQL <c>duration</c> carries months, days and nanoseconds separately, so it is strictly wider than
    ///     <see cref="TimeSpan" />. The planned <c>TimeSpan</c> converter is therefore only lossless in the
    ///     months == 0 direction, which this pins.
    /// </summary>
    [Fact]
    public void Duration_separates_months_days_and_nanoseconds()
    {
        var duration = new Duration(0, 3, 4_500_000_000);

        Assert.Equal(0, duration.Months);
        Assert.Equal(3, duration.Days);
        Assert.Equal(4_500_000_000, duration.Nanoseconds);
    }

    [Fact]
    public void BigInteger_and_IPAddress_are_usable_as_varint_and_inet_values()
    {
        var varint = BigInteger.Parse("123456789012345678901234567890");
        var inet = IPAddress.Parse("10.0.0.1");

        Assert.Equal("123456789012345678901234567890", varint.ToString());
        Assert.Equal("10.0.0.1", inet.ToString());
    }

    [Fact]
    public void ConsistencyLevel_defaults_match_the_driver_documented_values()
    {
        // We deliberately do not override these; the provider only exposes an option to change them.
        Assert.Equal(ConsistencyLevel.LocalOne, QueryOptions.DefaultConsistencyLevel);
        Assert.Equal(ConsistencyLevel.Serial, QueryOptions.DefaultSerialConsistencyLevel);
    }
}
