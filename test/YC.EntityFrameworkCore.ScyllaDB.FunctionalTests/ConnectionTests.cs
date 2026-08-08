using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Connects the provider to a real ScyllaDB node.
/// </summary>
/// <remarks>
///     The driver ships only a <c>netstandard2.0</c> target and its vendor-tested matrix stops at .NET 8, so
///     running it on .NET 10 rests on binary compatibility. Unit tests show the types resolve; only a live node
///     shows the wire protocol, connection pooling and shard-aware routing all work.
/// </remarks>
[Collection(ScyllaCollection.Name)]
public class ConnectionTests(ScyllaContainerFixture fixture)
{
    private sealed class ConnectionTestContext(DbContextOptions options) : DbContext(options);

    private ConnectionTestContext CreateContext(string keyspace)
        => new(
            new DbContextOptionsBuilder<ConnectionTestContext>()
                .UseScyllaDb(fixture.ContactPoints, keyspace, o => o.WithPort(fixture.Port))
                .Options);

    [Fact]
    public async Task CanConnectAsync_succeeds_against_a_live_node()
    {
        var keyspace = await fixture.CreateKeyspaceAsync(
            "connection_tests", TestContext.Current.CancellationToken);

        await using var context = CreateContext(keyspace);

        Assert.True(await context.Database.CanConnectAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanConnectAsync_returns_false_when_the_keyspace_does_not_exist()
    {
        await using var context = CreateContext("keyspace_that_was_never_created");

        Assert.False(await context.Database.CanConnectAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     A driver session is designed to be long-lived and shared. Two contexts built from the same
    ///     configuration must land on the same one, or connection pooling and shard-aware routing are lost.
    /// </summary>
    [Fact]
    public async Task Contexts_sharing_a_configuration_share_one_session()
    {
        var keyspace = await fixture.CreateKeyspaceAsync(
            "shared_session_tests", TestContext.Current.CancellationToken);

        await using var first = CreateContext(keyspace);
        await using var second = CreateContext(keyspace);

        var firstSession = await first.GetService<IScyllaClientWrapper>()
            .GetSessionAsync(TestContext.Current.CancellationToken);
        var secondSession = await second.GetService<IScyllaClientWrapper>()
            .GetSessionAsync(TestContext.Current.CancellationToken);

        Assert.Same(firstSession, secondSession);
    }
}
