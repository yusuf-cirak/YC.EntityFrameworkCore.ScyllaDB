using Microsoft.EntityFrameworkCore.Metadata.Builders;
using YC.EntityFrameworkCore.ScyllaDB;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Configures how a property maps to a CQL column.
/// </summary>
public static class ScyllaPropertyBuilderExtensions
{
    /// <summary>
    ///     Stores this column once per partition rather than once per row.
    /// </summary>
    /// <remarks>
    ///     Requires at least one clustering column; a table without one holds a single row per partition, so a
    ///     static column would mean nothing.
    /// </remarks>
    public static PropertyBuilder<TProperty> IsStaticColumn<TProperty>(
        this PropertyBuilder<TProperty> builder,
        bool isStatic = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.SetIsStaticColumn(isStatic);

        return builder;
    }

    /// <summary>
    ///     Maps this property to a CQL <c>counter</c>.
    /// </summary>
    /// <remarks>
    ///     A counter table may contain nothing but counters and its primary key. Counters cannot be inserted or
    ///     assigned, only incremented, so they sit outside EF Core's usual snapshot change tracking.
    /// </remarks>
    public static PropertyBuilder<TProperty> IsCounter<TProperty>(
        this PropertyBuilder<TProperty> builder,
        bool isCounter = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.SetIsCounter(isCounter);

        return builder;
    }

    /// <summary>
    ///     Sets the CQL type explicitly, overriding whatever the CLR type would map to.
    /// </summary>
    public static PropertyBuilder<TProperty> HasCqlType<TProperty>(
        this PropertyBuilder<TProperty> builder,
        string cqlType)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(cqlType);

        builder.Metadata.SetCqlType(cqlType);

        return builder;
    }

    /// <summary>
    ///     Maps this collection property to a CQL <c>list</c>, which keeps duplicates and order.
    /// </summary>
    public static PropertyBuilder<TProperty> IsList<TProperty>(this PropertyBuilder<TProperty> builder)
        => SetCollectionKind(builder, ScyllaCollectionKind.List);

    /// <summary>
    ///     Maps this collection property to a CQL <c>set</c>, which is unordered and de-duplicated.
    /// </summary>
    public static PropertyBuilder<TProperty> IsSet<TProperty>(this PropertyBuilder<TProperty> builder)
        => SetCollectionKind(builder, ScyllaCollectionKind.Set);

    /// <summary>
    ///     Maps this dictionary property to a CQL <c>map</c>.
    /// </summary>
    public static PropertyBuilder<TProperty> IsMap<TProperty>(this PropertyBuilder<TProperty> builder)
        => SetCollectionKind(builder, ScyllaCollectionKind.Map);

    private static PropertyBuilder<TProperty> SetCollectionKind<TProperty>(
        PropertyBuilder<TProperty> builder,
        ScyllaCollectionKind kind)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.SetCollectionKind(kind);

        return builder;
    }
}
