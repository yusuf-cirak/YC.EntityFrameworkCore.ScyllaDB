using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using YC.EntityFrameworkCore.ScyllaDB.Infrastructure.Internal;

namespace YC.EntityFrameworkCore.ScyllaDB.Metadata.Conventions;

/// <summary>
///     Builds the convention set, adding the ScyllaDB attribute and primary-key conventions.
/// </summary>
public class ScyllaConventionSetBuilder : ProviderConventionSetBuilder
{
    private readonly IScyllaSingletonOptions _options;

    public ScyllaConventionSetBuilder(
        ProviderConventionSetBuilderDependencies dependencies,
        IScyllaSingletonOptions options)
        : base(dependencies)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    public override ConventionSet CreateConventionSet()
    {
        var conventionSet = base.CreateConventionSet();

        // Runs last so that anything configured fluently has already been applied and can be left alone.
        // The keyspace from UseScyllaDb becomes the model's default: having named it once when configuring the
        // connection, nobody should have to repeat it in OnModelCreating.
        conventionSet.ModelFinalizingConventions.Add(
            new ScyllaModelFinalizingConvention(
                string.IsNullOrWhiteSpace(_options.Keyspace) ? null : _options.Keyspace));

        return conventionSet;
    }
}
