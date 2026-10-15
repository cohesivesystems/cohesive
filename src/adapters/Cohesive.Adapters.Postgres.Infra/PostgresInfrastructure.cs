using Cohesive.Relations.Infra;
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
    /// <summary>Prepares each declared requirement once for one logical database.</summary>
    /// <param name="id">Target-local implementation identity.</param>
    /// <param name="bindings">Offline physical mappings.</param>
    /// <param name="requirements">Single authoritative list of versioned query requirements.</param>
    /// <returns>Database-affine evidence and its declared capabilities.</returns>
    /// <exception cref="ArgumentException">Requirements are empty, duplicated or unsupported.</exception>
    /// <exception cref="ArgumentNullException">Bindings or requirements are null.</exception>
    /// <exception cref="RelationQueryPreparationException">Semantic compilation fails.</exception>
    /// <exception cref="PostgresQueryPreparationException">Native compilation fails.</exception>
    public static PostgresPreparedQueryEvidence Prepare(InfrastructureTargetFacilityId id,
        PostgresPersistenceBindings bindings, params QuerySnapshotRequirement[] requirements)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(requirements);
        var queries = requirements.Select(requirement =>
        {
            ArgumentNullException.ThrowIfNull(requirement);
            return (requirement, bindings.Prepare(requirement.Request));
        }).ToArray();
        return QueryImplementation(id, queries);
    }

    /// <summary>Derives a resource implementation from fingerprint-validated, fully placed native queries.</summary>
    /// <param name="id">Target-local facility identity; must be unique within the deployment.</param>
    /// <param name="queries">Prepared unpaged query-row artifacts for one database.</param>
    /// <returns>Native evidence retaining each exact artifact and storage-binding fingerprint.</returns>
    /// <exception cref="ArgumentNullException">Queries or a query is null.</exception>
    /// <exception cref="ArgumentException">Queries are empty, duplicated, source-scoped, paged, or span databases.</exception>
    public static PostgresPreparedQueryEvidence QueryImplementation(InfrastructureTargetFacilityId id,
        params (QuerySnapshotRequirement Requirement, PostgresRelationQueryCompiledArtifact Artifact)[] queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Length == 0) throw new ArgumentException("At least one prepared query is required.", nameof(queries));
        var evidence = new InfrastructureCapabilityEvidence[queries.Length];
        var capabilities = new HashSet<InfrastructureCapabilityId>();
        PostgresRelationQueryDatabaseId? database = null;
        for (var i = 0; i < queries.Length; i++)
        {
            var (requirement, query) = queries[i];
            ArgumentNullException.ThrowIfNull(requirement);
            ArgumentNullException.ThrowIfNull(query);
            if (query.Branch.Kind != RelationQueryNativeResultKind.QueryRows || query.Paging is not null
                || !query.SuppliedFields.IsEmpty || !query.Invariants.IsEmpty || query.RelationKey is not null
                || query.StorageBinding.SourceScope is not null || query.StorageBinding.CompiledPlanFingerprint is null
                || query.StorageBinding.PlacementFingerprint is null)
                throw new ArgumentException("Evidence requires a complete, unpaged native query-row artifact.", nameof(queries));
            var planFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(query.Provenance.Plan);
            if (query.StorageBinding.CompiledPlanFingerprint != planFingerprint
                || planFingerprint != RelationQueryCompiledPlanReferenceFingerprinter.Compute(requirement.Plan))
                throw new ArgumentException("Native binding, provenance and required query plans must match exactly.", nameof(queries));
            database ??= query.StorageBinding.Database;
            if (database != query.StorageBinding.Database)
                throw new ArgumentException("One resource implementation cannot claim a snapshot across databases.", nameof(queries));
            var capability = requirement.Capability;
            if (!capabilities.Add(capability)) throw new ArgumentException("A query plan is included more than once.", nameof(queries));
            evidence[i] = new(new($"{id.Value}/query/{query.Fingerprint.Value}"), capability, CapabilityRealizationKind.Native,
                sourceReferences:
                [
                    SourceReference.Create("postgres-artifact", $"{query.Fingerprint.Canonicalization}/{query.Fingerprint.Value}"),
                    SourceReference.Create("postgres-binding", $"{query.StorageBinding.Fingerprint.Canonicalization}/{query.StorageBinding.Fingerprint.Value}")
                ]);
        }
        return new(database!.Value, InfrastructureTargetImplementation.ResourceImplementation(id, evidence));
    }
}
