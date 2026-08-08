using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using YC.EntityFrameworkCore.ScyllaDB.UnitTests.TestUtilities;

namespace YC.EntityFrameworkCore.ScyllaDB.UnitTests.Infrastructure;

/// <summary>
///     CQL cannot do much of what EF Core's surface implies, and the provider's value rests on saying so
///     precisely. These assert the exact wording, so a refactor cannot quietly degrade a message into
///     "operation not supported".
/// </summary>
public class UnsupportedOperationTests
{
    [Fact]
    public void SaveChanges_points_at_the_asynchronous_overload()
    {
        using var context = ScyllaTestContext.Create();
        var database = context.GetService<IDatabase>();

        var exception = Assert.Throws<InvalidOperationException>(() => database.SaveChanges([]));

        Assert.Contains("asynchronous only", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DbContext.SaveChangesAsync), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     EF Core short-circuits <see cref="DbContext.SaveChanges()" /> when nothing is tracked, so it never
    ///     reaches the provider and does not throw. Worth pinning: on an async-only provider it looks like the
    ///     synchronous path is supported right up until the first real change.
    /// </summary>
    [Fact]
    public void SaveChanges_with_nothing_tracked_never_reaches_the_provider()
    {
        using var context = ScyllaTestContext.Create();

        Assert.Equal(0, context.SaveChanges());
    }

    [Fact]
    public void BeginTransaction_explains_that_ScyllaDB_has_no_transactions()
    {
        using var context = ScyllaTestContext.Create();

        var exception = Assert.Throws<InvalidOperationException>(() => context.Database.BeginTransaction());

        Assert.Contains("does not support transactions", exception.Message, StringComparison.Ordinal);
        // The message has to leave the reader somewhere to go, not just refuse.
        Assert.Contains("same partition", exception.Message, StringComparison.Ordinal);
        Assert.Contains("IsConcurrencyToken", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeginTransactionAsync_explains_that_ScyllaDB_has_no_transactions()
    {
        await using var context = ScyllaTestContext.Create();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken));

        Assert.Contains("does not support transactions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentTransaction_is_always_null()
    {
        using var context = ScyllaTestContext.Create();

        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public void CanConnect_points_at_the_asynchronous_overload()
    {
        using var context = ScyllaTestContext.Create();

        var exception = Assert.Throws<InvalidOperationException>(() => context.Database.CanConnect());

        Assert.Contains(nameof(context.Database.CanConnectAsync), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCreated_points_at_the_asynchronous_overload()
    {
        using var context = ScyllaTestContext.Create();

        var exception = Assert.Throws<InvalidOperationException>(() => context.Database.EnsureCreated());

        Assert.Contains(nameof(context.Database.EnsureCreatedAsync), exception.Message, StringComparison.Ordinal);
    }
}
