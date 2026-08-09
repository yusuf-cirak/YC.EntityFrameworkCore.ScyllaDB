using Cassandra;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.FunctionalTests;

/// <summary>
///     Exercises lightweight transactions and time-to-live against a real node.
/// </summary>
/// <remarks>
///     These are the behaviours that cannot be proven by inspecting CQL text: whether Paxos actually rejects a
///     competing write, and whether a row really disappears when its time is up.
/// </remarks>
[Collection(ScyllaCollection.Name)]
public class ConcurrencyAndTtlTests(ScyllaContainerFixture fixture)
{
    private sealed class Document
    {
        public Guid Id { get; set; }

        public string Body { get; set; } = string.Empty;

        public int Version { get; set; }
    }

    private sealed class Session
    {
        public Guid Id { get; set; }

        public string Token { get; set; } = string.Empty;
    }

    private sealed class LwtContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Document> Documents
            => Set<Document>();

        public DbSet<Session> Sessions
            => Set<Session>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Document>(e =>
            {
                e.ToScyllaTable("documents");
                e.HasPartitionKey(d => d.Id);
                e.Property(d => d.Version).IsConcurrencyToken();
                e.UseInsertIfNotExists();
            });

            modelBuilder.Entity<Session>(e =>
            {
                e.ToScyllaTable("sessions");
                e.HasPartitionKey(s => s.Id);
            });
        }
    }

    private LwtContext CreateContext(string keyspace)
        => new(
            new DbContextOptionsBuilder<LwtContext>()
                .UseScyllaDb(fixture.ContactPoints, keyspace, o => o.WithPort(fixture.Port))
                .Options);

    [Fact]
    public async Task Conditional_insert_rejects_a_duplicate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("lwt_insert_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var id = Guid.NewGuid();
        context.Add(new Document { Id = id, Body = "first", Version = 1 });
        await context.SaveChangesAsync(cancellationToken);

        await using var second = CreateContext("lwt_insert_tests");
        second.Add(new Document { Id = id, Body = "second", Version = 1 });

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync(cancellationToken));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The whole point of the concurrency token: a second writer working from a stale version must lose.
    /// </summary>
    [Fact]
    public async Task Stale_concurrency_token_loses_the_race()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("lwt_update_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var id = Guid.NewGuid();
        context.Add(new Document { Id = id, Body = "original", Version = 1 });
        await context.SaveChangesAsync(cancellationToken);

        await using var winner = CreateContext("lwt_update_tests");
        var winning = new Document { Id = id, Body = "original", Version = 1 };
        winner.Attach(winning);
        winning.Body = "winner";
        winning.Version = 2;
        await winner.SaveChangesAsync(cancellationToken);

        // This writer still believes the version is 1, which is no longer true.
        await using var loser = CreateContext("lwt_update_tests");
        var losing = new Document { Id = id, Body = "original", Version = 1 };
        loser.Attach(losing);
        losing.Body = "loser";
        losing.Version = 2;

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => loser.SaveChangesAsync(cancellationToken));

        Assert.Contains("another writer changed the row first", exception.Message, StringComparison.Ordinal);
        // ScyllaDB returns the primary key plus the columns the condition named, so the message can show the
        // version that actually won — but not the whole row.
        Assert.Contains("Version = 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matching_concurrency_token_applies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("lwt_success_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var id = Guid.NewGuid();
        context.Add(new Document { Id = id, Body = "original", Version = 1 });
        await context.SaveChangesAsync(cancellationToken);

        await using var update = CreateContext("lwt_success_tests");
        var document = new Document { Id = id, Body = "original", Version = 1 };
        update.Attach(document);
        document.Body = "updated";
        document.Version = 2;

        await update.SaveChangesAsync(cancellationToken);

        var reloaded = await update.Documents.FirstAsync(d => d.Id == id, cancellationToken);
        Assert.Equal("updated", reloaded.Body);
        Assert.Equal(2, reloaded.Version);
    }

    [Fact]
    public async Task Rows_written_with_a_time_to_live_expire()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("ttl_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var id = Guid.NewGuid();

        using (context.UseScyllaWriteOptions(new ScyllaWriteOptions(TimeToLive: TimeSpan.FromSeconds(2))))
        {
            context.Add(new Session { Id = id, Token = "abc" });
            await context.SaveChangesAsync(cancellationToken);
        }

        var client = context.GetService<IScyllaClientWrapper>();

        var before = await client.ExecuteAsync(
            new SimpleStatement("SELECT \"Token\" FROM ttl_tests.\"sessions\" WHERE \"Id\" = ?", id),
            cancellationToken);
        Assert.Single(before);

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        var after = await client.ExecuteAsync(
            new SimpleStatement("SELECT \"Token\" FROM ttl_tests.\"sessions\" WHERE \"Id\" = ?", id),
            cancellationToken);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Write_options_apply_only_inside_their_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = CreateContext("ttl_scope_tests");
        await context.Database.EnsureCreatedAsync(cancellationToken);

        var expiring = Guid.NewGuid();
        var permanent = Guid.NewGuid();

        using (context.UseScyllaWriteOptions(new ScyllaWriteOptions(TimeToLive: TimeSpan.FromSeconds(2))))
        {
            context.Add(new Session { Id = expiring, Token = "temporary" });
            await context.SaveChangesAsync(cancellationToken);
        }

        context.Add(new Session { Id = permanent, Token = "durable" });
        await context.SaveChangesAsync(cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        var client = context.GetService<IScyllaClientWrapper>();

        Assert.Empty(
            await client.ExecuteAsync(
                new SimpleStatement("SELECT \"Token\" FROM ttl_scope_tests.\"sessions\" WHERE \"Id\" = ?", expiring),
                cancellationToken));

        Assert.Single(
            await client.ExecuteAsync(
                new SimpleStatement("SELECT \"Token\" FROM ttl_scope_tests.\"sessions\" WHERE \"Id\" = ?", permanent),
                cancellationToken));
    }
}
