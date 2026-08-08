using Microsoft.EntityFrameworkCore.Storage;

namespace YC.EntityFrameworkCore.ScyllaDB.Storage.Internal;

/// <summary>
///     Maps CLR types onto CQL types.
/// </summary>
/// <remarks>
///     Currently a shell: it resolves nothing, so a model with mapped properties fails with EF Core's standard
///     "no type mapping" error. The CQL type table, collection disambiguation and user-defined types arrive with
///     the type-mapping milestone. It is registered now because EF Core requires every provider to supply one
///     before a context can be built at all.
/// </remarks>
public class ScyllaTypeMappingSource : TypeMappingSource
{
    public ScyllaTypeMappingSource(TypeMappingSourceDependencies dependencies)
        : base(dependencies)
    {
    }
}
