using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.Postgres.Infra;

/// <summary>Projects exact native query preparation into infrastructure capability evidence.</summary>
/// <remarks>Evidence covers a selected query plan on one database, not arbitrary SQL or all queries.
/// It does not attest schema deployment, access, live readiness, distributed snapshots or transition receipts.</remarks>
public static class PostgresInfrastructure
{
    /// <summary>Requires execution of the exact semantic query in a single PostgreSQL statement snapshot.</summary>
    /// <param name="request">Canonical query and semantic snapshots; no physical mapping is needed.</param>
    /// <returns>A plan-specific capability identity, independent of native table names.</returns>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    /// <exception cref="RelationQueryPreparationException">Semantic compilation fails.</exception>
    public static InfrastructureCapabilityId QuerySnapshot(RelationQueryCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var compilation = RelationQueryStaticCompiler.Compile(request);
        if (!compilation.IsSuccessful) throw new RelationQueryPreparationException("infrastructure requirement", compilation);
        var plan = compilation.Plan!;
        return QuerySnapshot(RelationQueryCompiledPlanReference.From(plan));
    }

    static InfrastructureCapabilityId QuerySnapshot(RelationQueryCompiledPlanReference plan) =>
        new($"postgres/query-statement-snapshot/v1/{RelationQueryCompiledPlanReferenceFingerprinter.Compute(plan).Value}");

    /// <summary>Derives a resource implementation from fingerprint-validated, fully placed native queries.</summary>
    /// <param name="id">Target-local facility identity; must be unique within the deployment.</param>
    /// <param name="queries">Prepared unpaged query-row artifacts for one database.</param>
    /// <returns>Native evidence retaining each exact artifact and storage-binding fingerprint.</returns>
    /// <exception cref="ArgumentNullException">Queries or a query is null.</exception>
    /// <exception cref="ArgumentException">Queries are empty, duplicated, source-scoped, paged, or span databases.</exception>
    public static InfrastructureTargetImplementation QueryImplementation(InfrastructureTargetFacilityId id,
        params PostgresRelationQueryCompiledArtifact[] queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Length == 0) throw new ArgumentException("At least one prepared query is required.", nameof(queries));
        var evidence = new InfrastructureCapabilityEvidence[queries.Length];
        var capabilities = new HashSet<InfrastructureCapabilityId>();
        PostgresRelationQueryDatabaseId? database = null;
        for (var i = 0; i < queries.Length; i++)
        {
            var query = queries[i];
            ArgumentNullException.ThrowIfNull(query);
            if (query.Branch.Kind != RelationQueryNativeResultKind.QueryRows || query.Paging is not null
                || !query.SuppliedFields.IsEmpty || !query.Invariants.IsEmpty || query.RelationKey is not null
                || query.StorageBinding.SourceScope is not null || query.StorageBinding.CompiledPlanFingerprint is null
                || query.StorageBinding.PlacementFingerprint is null)
                throw new ArgumentException("Evidence requires a complete, unpaged native query-row artifact.", nameof(queries));
            database ??= query.StorageBinding.Database;
            if (database != query.StorageBinding.Database)
                throw new ArgumentException("One resource implementation cannot claim a snapshot across databases.", nameof(queries));
            var capability = QuerySnapshot(query.Provenance.Plan);
            if (!capabilities.Add(capability)) throw new ArgumentException("A query plan is included more than once.", nameof(queries));
            evidence[i] = new(new($"{id.Value}/query/{query.Fingerprint.Value}"), capability, CapabilityRealizationKind.Native,
                sourceReferences:
                [
                    SourceReference.Create("postgres-artifact", $"{query.Fingerprint.Canonicalization}/{query.Fingerprint.Value}"),
                    SourceReference.Create("postgres-binding", $"{query.StorageBinding.Fingerprint.Canonicalization}/{query.StorageBinding.Fingerprint.Value}")
                ]);
        }
        return InfrastructureTargetImplementation.ResourceImplementation(id, evidence);
    }
}
