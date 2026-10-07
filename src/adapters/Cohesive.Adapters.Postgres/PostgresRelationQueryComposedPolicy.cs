using Cohesive.Relations.Physical;

namespace Cohesive.Adapters.Postgres;

/// <summary>Composed query bounds with a required physical policy and a derived native batch limit.</summary>
public sealed record PostgresRelationQueryComposedPolicy
{
    /// <summary>Creates composed execution policy with the batch limit derived from its physical plan policy.</summary>
    /// <param name="physicalPlanningPolicy">Authoritative physical bounds and policy identity.</param>
    /// <param name="maximumRowsPerRead">Maximum observations retained per native read.</param>
    /// <param name="maximumPageItems">Maximum observations per materialization page.</param>
    /// <param name="maximumPageBytes">Maximum decoded page bytes.</param>
    /// <param name="partitionScope">Explicit logical partition and native selector.</param>
    /// <param name="temporalSemantics">Explicit native temporal execution mode.</param>
    /// <param name="maximumKeyBytes">Maximum encoded key size.</param>
    /// <exception cref="ArgumentNullException">Physical policy is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is unsupported by the native source reader.</exception>
    public PostgresRelationQueryComposedPolicy(RelationQueryPhysicalPlanningPolicy physicalPlanningPolicy,
        int maximumRowsPerRead, int maximumPageItems, long maximumPageBytes,
        PostgresRelationQueryPartitionScope? partitionScope,
        PostgresNpgsqlTemporalSemantics temporalSemantics = PostgresNpgsqlTemporalSemantics.Unsupported,
        int maximumKeyBytes = 256)
    {
        SourcePolicy = new(BatchLimit(physicalPlanningPolicy), maximumRowsPerRead, maximumPageItems,
            maximumPageBytes, temporalSemantics, maximumKeyBytes, partitionScope);
        PhysicalPlanningPolicy = physicalPlanningPolicy;
    }

    /// <summary>Required physical policy for composed registration.</summary>
    public RelationQueryPhysicalPlanningPolicy PhysicalPlanningPolicy { get; }

    static int BatchLimit(RelationQueryPhysicalPlanningPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaximumBatchSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(policy), "Physical batch size exceeds the native source's supported range.");
        return (int)policy.MaximumBatchSize;
    }

    /// <summary>Native acquisition bounds derived once for the composed reader.</summary>
    public PostgresRelationQuerySourcePolicy SourcePolicy { get; }
}
