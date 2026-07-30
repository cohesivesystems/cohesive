using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Exact primitive capability closure implemented by the Npgsql-backed PostgreSQL source reader.</summary>
/// <remarks>
/// This profile is intentionally distinct from <see cref="PostgresRelationQueryTargetProfile.Default"/>, which
/// describes native SQL compilation. Runtime source limits, table value semantics, ordering evidence, and snapshot
/// boundaries remain attributable to the source instance, storage binding, reader policy, and exact read request.
/// </remarks>
public static class PostgresRelationQuerySourceTargetProfile
{
    /// <summary>Stable PostgreSQL source-reader profile identity.</summary>
    public static RelationQueryTargetProfileId ProfileId { get; } = new(
        "cohesive.adapters.postgres.sql/source-reader-v1");

    /// <summary>Exact bounded-acquisition capabilities implemented by the Npgsql reader.</summary>
    public static RelationQueryTargetCapabilityProfile Default { get; } = new(
        PostgresRelationQueryTargetProfile.Target,
        ProfileId,
        [RelationQueryDocument.CurrentSchemaVersion],
        [RelationQueryCompilationProvenance.CurrentCompilerProfile],
        [
            .. PostgresRelationQueryTargetProfile.SourceAcquisitionCapabilities.Select(Capability)
        ],
        description: "Bounded, set-oriented canonical acquisition through one exact PostgreSQL storage binding.");

    static RelationQueryTargetCapabilityEvidence Capability(
        RelationQueryPrimitiveCapabilityKind capability) => new(
        new($"postgres/source-reader/capability/{(int)capability}"),
        new PrimitiveRelationQueryCapability(capability));
}
