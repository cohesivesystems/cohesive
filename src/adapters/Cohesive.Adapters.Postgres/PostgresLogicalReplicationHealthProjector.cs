using Cohesive.Execution;

namespace Cohesive.Adapters.Postgres;

/// <summary>Projects PostgreSQL logical-replication health into the common execution-health contract.</summary>
public static class PostgresLogicalReplicationHealthProjector
{
    /// <summary>Projects one attributable adapter observation without exposing slot or publication names.</summary>
    /// <param name="observation">Existing provider-neutral logical-replication health observation.</param>
    /// <param name="provenance">Producer and source attribution for the adapter projection.</param>
    /// <returns>An immutable common health observation retaining the adapter evidence reference.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="observation"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    public static ExecutionHealthObservation Project(
        PostgresLogicalReplicationHealthObservation observation,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(provenance);
        var (health, readiness) = observation.State switch
        {
            PostgresLogicalReplicationHealthState.Healthy =>
                (ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready),
            PostgresLogicalReplicationHealthState.Inactive or PostgresLogicalReplicationHealthState.RetentionDanger =>
                (ExecutionHealthStatus.Degraded, ExecutionReadinessStatus.Ready),
            PostgresLogicalReplicationHealthState.SlotLost =>
                (ExecutionHealthStatus.Unhealthy, ExecutionReadinessStatus.NotReady),
            PostgresLogicalReplicationHealthState.Unavailable =>
                (ExecutionHealthStatus.Unknown, ExecutionReadinessStatus.Unknown),
            _ => throw new ArgumentOutOfRangeException(
                nameof(observation),
                observation.State,
                "Unsupported PostgreSQL logical-replication health state.")
        };
        return new(
            health: health,
            readiness: readiness,
            observedAtUtc: observation.ObservedAtUtc,
            provenance: provenance,
            evidenceReferences: [observation.EvidenceReference]);
    }
}
