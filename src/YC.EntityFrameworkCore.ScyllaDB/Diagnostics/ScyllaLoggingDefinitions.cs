using Microsoft.EntityFrameworkCore.Diagnostics;

namespace YC.EntityFrameworkCore.ScyllaDB.Diagnostics;

/// <summary>
///     Caches the provider's logging delegates. EF Core requires every provider to supply one of these; the
///     cached delegate fields are added alongside the events that need them.
/// </summary>
public class ScyllaLoggingDefinitions : LoggingDefinitions
{
}
