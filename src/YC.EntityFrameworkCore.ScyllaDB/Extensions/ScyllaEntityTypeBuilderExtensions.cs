using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.ScyllaDB;
using YC.EntityFrameworkCore.ScyllaDB.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Maps an entity type onto a CQL table.
/// </summary>
public static class ScyllaEntityTypeBuilderExtensions
{
    /// <summary>
    ///     Sets the table name, and optionally a keyspace other than the model's default.
    /// </summary>
    public static EntityTypeBuilder<TEntity> ToScyllaTable<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string name,
        string? keyspace = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.Metadata.SetScyllaTableName(name);

        if (keyspace is not null)
        {
            builder.Metadata.SetKeyspace(keyspace);
        }

        return builder;
    }

    /// <summary>
    ///     Declares the partition key, which decides which node owns a row.
    /// </summary>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="propertyExpression">
    ///     A single property, or <c>x =&gt; new { x.First, x.Second }</c> for a composite key. Member order is
    ///     the key order and becomes part of the table's identity.
    /// </param>
    /// <remarks>
    ///     Every query must restrict the whole partition key by equality, so this choice determines what the
    ///     table can be queried by at all. Calling it again replaces the previous declaration.
    /// </remarks>
    public static EntityTypeBuilder<TEntity> HasPartitionKey<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, object?>> propertyExpression)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(propertyExpression);

        return builder.HasPartitionKey(ScyllaExpressionHelpers.GetPropertyNames(propertyExpression));
    }

    /// <inheritdoc cref="HasPartitionKey{TEntity}(EntityTypeBuilder{TEntity}, Expression{Func{TEntity, object}})" />
    public static EntityTypeBuilder<TEntity> HasPartitionKey<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        IEnumerable<string> propertyNames)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(propertyNames);

        foreach (var existing in builder.Metadata.GetProperties().ToList())
        {
            existing.SetPartitionKeyOrdinal(null);
        }

        var ordinal = 0;

        foreach (var name in propertyNames)
        {
            builder.Property(name).Metadata.SetPartitionKeyOrdinal(ordinal++);
        }

        return builder;
    }

    /// <summary>
    ///     Appends a clustering key, which orders rows within a partition.
    /// </summary>
    /// <remarks>
    ///     Call once per column; call order is the clustering order, and therefore the physical order rows are
    ///     stored in. Queries may only restrict clustering columns as a contiguous prefix.
    /// </remarks>
    public static EntityTypeBuilder<TEntity> HasClusteringKey<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, object?>> propertyExpression,
        ScyllaClusteringOrder order = ScyllaClusteringOrder.Ascending)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(propertyExpression);

        var names = ScyllaExpressionHelpers.GetPropertyNames(propertyExpression);

        if (names.Count != 1)
        {
            throw new ArgumentException(
                "HasClusteringKey takes one property at a time, because each clustering column has its own "
                + "sort order. Call it once per column.",
                nameof(propertyExpression));
        }

        return builder.HasClusteringKey(names[0], order);
    }

    /// <inheritdoc cref="HasClusteringKey{TEntity}(EntityTypeBuilder{TEntity}, Expression{Func{TEntity, object}}, ScyllaClusteringOrder)" />
    public static EntityTypeBuilder<TEntity> HasClusteringKey<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string propertyName,
        ScyllaClusteringOrder order = ScyllaClusteringOrder.Ascending)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        var property = builder.Property(propertyName).Metadata;

        // Re-declaring a column keeps its original position rather than moving it to the end, so that a
        // configuration that sets the order twice does not silently reshape the table.
        property.SetClusteringKeyOrdinal(
            property.GetClusteringKeyOrdinal()
            ?? builder.Metadata.GetProperties().Count(p => p.GetClusteringKeyOrdinal() is not null));

        property.SetClusteringOrder(order);

        return builder;
    }

    /// <summary>
    ///     Sets the table's <c>default_time_to_live</c>, after which rows expire on their own.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasDefaultTimeToLive<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        TimeSpan timeToLive)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        builder.Metadata.SetDefaultTimeToLive(timeToLive);

        return builder;
    }
}
