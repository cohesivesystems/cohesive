using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;

namespace Cohesive.Adapters.Postgres.Infra;

/// <summary>Prepared query evidence bound to one explicitly named logical database.</summary>
public sealed class PostgresPreparedQueryEvidence : IInfrastructureResourcePreparation
{
    readonly InfrastructureTargetImplementation implementation;
    internal PostgresPreparedQueryEvidence(PostgresRelationQueryDatabaseId database, InfrastructureTargetImplementation implementation)
    {
        Database = database;
        this.implementation = implementation;
        Requirements = implementation.Evidence.Select(item => item.Capability).ToImmutableArray();
    }
    /// <summary>Logical database that owns all prepared mappings.</summary>
    public PostgresRelationQueryDatabaseId Database { get; }
    /// <summary>Declared capabilities derived from the same query list as the evidence.</summary>
    public ImmutableArray<InfrastructureCapabilityId> Requirements { get; }
    /// <summary>Checks the selected native database before exposing its implementation evidence.</summary>
    /// <param name="databaseName">Actual database name from native resource metadata, not a display label.</param>
    /// <returns>The validated resource implementation.</returns>
    /// <exception cref="ArgumentException">The native database differs from the prepared logical database.</exception>
    public InfrastructureTargetImplementation ForDatabase(string databaseName)
    {
        if (!string.Equals(Database.Value, databaseName, StringComparison.Ordinal))
            throw new ArgumentException("Prepared query evidence belongs to a different database.", nameof(databaseName));
        return implementation;
    }
}
