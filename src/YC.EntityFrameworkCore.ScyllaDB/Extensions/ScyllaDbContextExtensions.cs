using Microsoft.EntityFrameworkCore.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB;
using YC.EntityFrameworkCore.ScyllaDB.Update.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     ScyllaDB-specific operations on a <see cref="DbContext" />.
/// </summary>
public static class ScyllaDbContextExtensions
{
    /// <summary>
    ///     Applies write options to everything <see cref="DbContext.SaveChangesAsync(CancellationToken)" />
    ///     sends until the returned scope is disposed.
    /// </summary>
    /// <example>
    ///     <code>
    ///     using (context.UseScyllaWriteOptions(new ScyllaWriteOptions(TimeToLive: TimeSpan.FromDays(7))))
    ///     {
    ///         context.AddRange(readings);
    ///         await context.SaveChangesAsync();
    ///     }
    ///     </code>
    /// </example>
    public static IDisposable UseScyllaWriteOptions(this DbContext context, ScyllaWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        return context.GetService<IScyllaWriteOptionsAccessor>().Push(options);
    }
}
