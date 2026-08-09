using System.Globalization;
using System.Text;
using Cassandra;

namespace YC.EntityFrameworkCore.ScyllaDB.Scaffolding;

/// <summary>
///     Generates entity classes and a <c>DbContext</c> from an existing keyspace.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately standalone rather than plugged into <c>dotnet ef dbcontext scaffold</c>. EF Core's
///         reverse-engineering pipeline lives in <c>Microsoft.EntityFrameworkCore.Relational</c> and is built
///         around tables, columns and foreign keys; it has no way to express a partition key or a clustering
///         order, so a model produced through it would be missing exactly the parts that decide whether a CQL
///         table can be queried at all.
///     </para>
///     <para>
///         Reading <c>system_schema</c> directly costs less code and produces a model that is actually correct.
///     </para>
/// </remarks>
public static class ScyllaScaffolder
{
    /// <summary>
    ///     Reads a keyspace's schema and returns C# source for its entity types and a <c>DbContext</c>.
    /// </summary>
    /// <param name="session">A session connected to the cluster.</param>
    /// <param name="keyspace">The keyspace to read.</param>
    /// <param name="options">Naming and namespace options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<string> GenerateAsync(
        ISession session,
        string keyspace,
        ScyllaScaffoldingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        options ??= new ScyllaScaffoldingOptions();

        var tables = await ReadTablesAsync(session, keyspace).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (tables.Count == 0)
        {
            throw new InvalidOperationException(
                $"Keyspace '{keyspace}' contains no tables, or does not exist.");
        }

        return Render(keyspace, tables, options);
    }

    private static async Task<List<ScaffoldedTable>> ReadTablesAsync(ISession session, string keyspace)
    {
        var columnRows = await session.ExecuteAsync(
                new SimpleStatement(
                    "SELECT table_name, column_name, kind, position, clustering_order, type "
                    + "FROM system_schema.columns WHERE keyspace_name = ?",
                    keyspace))
            .ConfigureAwait(false);

        var tables = new Dictionary<string, ScaffoldedTable>(StringComparer.Ordinal);

        foreach (var row in columnRows)
        {
            var tableName = row.GetValue<string>("table_name");

            if (!tables.TryGetValue(tableName, out var table))
            {
                table = new ScaffoldedTable(tableName);
                tables.Add(tableName, table);
            }

            table.Columns.Add(
                new ScaffoldedColumn(
                    row.GetValue<string>("column_name"),
                    row.GetValue<string>("type"),
                    row.GetValue<string>("kind"),
                    row.GetValue<int>("position"),
                    row.GetValue<string>("clustering_order")));
        }

        // Views are materialised from a base table and are read-only, so they are scaffolded separately.
        var viewRows = await session.ExecuteAsync(
                new SimpleStatement(
                    "SELECT view_name, base_table_name FROM system_schema.views WHERE keyspace_name = ?",
                    keyspace))
            .ConfigureAwait(false);

        foreach (var row in viewRows)
        {
            if (tables.TryGetValue(row.GetValue<string>("view_name"), out var view))
            {
                view.BaseTableName = row.GetValue<string>("base_table_name");
            }
        }

        return [.. tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal)];
    }

    private static string Render(
        string keyspace,
        IReadOnlyList<ScaffoldedTable> tables,
        ScyllaScaffoldingOptions options)
    {
        var builder = new StringBuilder()
            .AppendLine("// Generated from the ScyllaDB schema. Edit freely — this is a starting point, not a")
            .AppendLine("// file that will be regenerated over your changes.")
            .AppendLine("using Microsoft.EntityFrameworkCore;")
            .AppendLine("using YC.EntityFrameworkCore.ScyllaDB;")
            .AppendLine()
            .Append("namespace ").Append(options.Namespace).AppendLine(";")
            .AppendLine();

        foreach (var table in tables)
        {
            RenderEntity(builder, table, options);
        }

        RenderContext(builder, keyspace, tables, options);

        return builder.ToString();
    }

    private static void RenderEntity(
        StringBuilder builder,
        ScaffoldedTable table,
        ScyllaScaffoldingOptions options)
    {
        if (table.BaseTableName is not null)
        {
            builder.Append("/// <summary>Materialized view over <c>").Append(table.BaseTableName)
                .AppendLine("</c>. Read-only.</summary>");
        }

        builder.Append("public class ").AppendLine(options.ToTypeName(table.Name)).AppendLine("{");

        foreach (var column in table.Columns.OrderBy(c => c.SortKey))
        {
            var clrType = ScyllaCqlTypeNames.ToClrTypeName(column.CqlType);

            builder.Append("    public ").Append(clrType).Append(' ')
                .Append(options.ToPropertyName(column.Name))
                .AppendLine(clrType.EndsWith('?') ? " { get; set; }" : " { get; set; }" )
                .AppendLine();
        }

        // Trim the blank line left by the last property.
        builder.Length -= Environment.NewLine.Length;
        builder.AppendLine("}").AppendLine();
    }

    private static void RenderContext(
        StringBuilder builder,
        string keyspace,
        IReadOnlyList<ScaffoldedTable> tables,
        ScyllaScaffoldingOptions options)
    {
        builder.Append("public class ").Append(options.ContextName).AppendLine(" : DbContext")
            .AppendLine("{")
            .Append("    public ").Append(options.ContextName)
            .Append("(DbContextOptions<").Append(options.ContextName).AppendLine("> options)")
            .AppendLine("        : base(options)")
            .AppendLine("    {")
            .AppendLine("    }")
            .AppendLine();

        foreach (var table in tables)
        {
            var typeName = options.ToTypeName(table.Name);

            builder.Append("    public DbSet<").Append(typeName).Append("> ")
                .Append(options.ToDbSetName(table.Name)).AppendLine()
                .Append("        => Set<").Append(typeName).AppendLine(">();")
                .AppendLine();
        }

        builder.AppendLine("    protected override void OnModelCreating(ModelBuilder modelBuilder)")
            .AppendLine("    {")
            .Append("        modelBuilder.HasDefaultKeyspace(\"").Append(keyspace).AppendLine("\");")
            .AppendLine();

        foreach (var table in tables)
        {
            RenderMapping(builder, table, options);
        }

        builder.AppendLine("    }").AppendLine("}");
    }

    private static void RenderMapping(
        StringBuilder builder,
        ScaffoldedTable table,
        ScyllaScaffoldingOptions options)
    {
        var typeName = options.ToTypeName(table.Name);

        builder.Append("        modelBuilder.Entity<").Append(typeName).AppendLine(">(e =>");
        builder.AppendLine("        {");

        if (table.BaseTableName is { } baseTable)
        {
            builder.Append("            e.ToMaterializedView(\"").Append(table.Name).Append("\", \"")
                .Append(baseTable).AppendLine("\");");
        }
        else
        {
            builder.Append("            e.ToScyllaTable(\"").Append(table.Name).AppendLine("\");");
        }

        var partitionKeys = table.Columns
            .Where(c => c.Kind == "partition_key")
            .OrderBy(c => c.Position)
            .Select(c => options.ToPropertyName(c.Name))
            .ToList();

        builder.Append("            e.HasPartitionKey(x => ")
            .Append(
                partitionKeys.Count == 1
                    ? $"x.{partitionKeys[0]}"
                    : $"new {{ {string.Join(", ", partitionKeys.Select(p => $"x.{p}"))} }}")
            .AppendLine(");");

        foreach (var clustering in table.Columns.Where(c => c.Kind == "clustering").OrderBy(c => c.Position))
        {
            builder.Append("            e.HasClusteringKey(x => x.")
                .Append(options.ToPropertyName(clustering.Name));

            if (string.Equals(clustering.ClusteringOrder, "desc", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(", ScyllaClusteringOrder.Descending");
            }

            builder.AppendLine(");");
        }

        foreach (var staticColumn in table.Columns.Where(c => c.Kind == "static"))
        {
            builder.Append("            e.Property(x => x.").Append(options.ToPropertyName(staticColumn.Name))
                .AppendLine(").IsStaticColumn();");
        }

        foreach (var counter in table.Columns.Where(c => c.CqlType == "counter"))
        {
            builder.Append("            e.Property(x => x.").Append(options.ToPropertyName(counter.Name))
                .AppendLine(").IsCounter();");
        }

        // Collection kinds and any CQL type the provider cannot infer are pinned explicitly, so the generated
        // model does not depend on the inference rules happening to agree.
        foreach (var column in table.Columns.Where(c => ScyllaCqlTypeNames.NeedsExplicitCqlType(c.CqlType)))
        {
            builder.Append("            e.Property(x => x.").Append(options.ToPropertyName(column.Name))
                .Append(").HasCqlType(\"").Append(column.CqlType).AppendLine("\");");
        }

        builder.AppendLine("        });").AppendLine();
    }

    private sealed class ScaffoldedTable(string name)
    {
        public string Name { get; } = name;

        public string? BaseTableName { get; set; }

        public List<ScaffoldedColumn> Columns { get; } = [];
    }

    private sealed record ScaffoldedColumn(
        string Name,
        string CqlType,
        string Kind,
        int Position,
        string? ClusteringOrder)
    {
        /// <summary>
        ///     Orders properties as the key is laid out: partition columns, clustering columns, then the rest.
        /// </summary>
        public string SortKey
            => string.Create(
                CultureInfo.InvariantCulture,
                $"{Kind switch { "partition_key" => 0, "clustering" => 1, "static" => 2, _ => 3 }}{Position:D4}{Name}");
    }
}
