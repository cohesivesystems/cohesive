using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Cohesive.Adapters.Postgres.Infra;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Adapters.Aspire.Postgres;

/// <summary>Associates prepared PostgreSQL query evidence with its native Aspire database.</summary>
public static class PostgresInfrastructureAssociationExtensions
{
    /// <summary>Validates native database affinity before adding an association.</summary>
    /// <param name="associations">Current association builder.</param>
    /// <param name="node">Canonical storage node.</param>
    /// <param name="native">Existing native PostgreSQL database resource.</param>
    /// <param name="queries">Prepared evidence for the database's actual name, not its resource label.</param>
    /// <param name="authority">Owner of the native resource lifecycle.</param>
    /// <param name="sources">Explicit attribution, or deterministic association defaults.</param>
    /// <returns>The original association builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Database affinity or association membership is invalid.</exception>
    /// <exception cref="InvalidOperationException">The association is frozen or already registered.</exception>
    public static AspireInfrastructureAssociationBuilder Resource(this AspireInfrastructureAssociationBuilder associations,
        InfrastructureNodeId node, IResourceBuilder<PostgresDatabaseResource> native,
        PostgresPreparedQueryEvidence queries, InfrastructureLifecycleAuthorityId authority,
        ImmutableArray<SourceReference> sources = default)
    {
        ArgumentNullException.ThrowIfNull(associations);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(queries);
        return associations.Resource(node, native, queries.ForDatabase(native.Resource.DatabaseName), authority, sources);
    }
}
