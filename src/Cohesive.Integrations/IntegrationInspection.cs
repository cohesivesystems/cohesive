using Cohesive.Execution;

namespace Cohesive.Integrations;

/// <summary>Host-attributed operational evidence for one exact integration binding, with an explicit expiry.</summary>
/// <remarks>Source health does not establish complete observations, accepted destination coverage or source progress.
/// The host authenticates the evidence producer and decides its validity horizon. This wrapper does not renew evidence.</remarks>
public sealed record IntegrationHealthEvidence
{
    /// <summary>Associates an existing health observation with its exact binding and validity horizon.</summary>
    /// <param name="binding">Exact binding observed by the producer, not a logical provider name.</param>
    /// <param name="observation">Shared execution-health evidence, including provenance and at least one source reference.</param>
    /// <param name="validUntilUtc">Exclusive UTC expiry, strictly after the observation time.</param>
    /// <exception cref="ArgumentNullException">Binding or observation is null.</exception>
    /// <exception cref="ArgumentException">Expiry is not UTC, the interval is empty/reversed, or source references are missing.</exception>
    public IntegrationHealthEvidence(ExecutionDefinitionReference binding, ExecutionHealthObservation observation, DateTimeOffset validUntilUtc)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(observation);
        if (validUntilUtc.Offset != TimeSpan.Zero || validUntilUtc <= observation.ObservedAtUtc)
            throw new ArgumentException("Health expiry must be UTC and follow the observation time.", nameof(validUntilUtc));
        if (observation.EvidenceReferences.IsEmpty)
            throw new ArgumentException("Integration health requires attributable source evidence.", nameof(observation));
        Binding = binding; Observation = observation; ValidUntilUtc = validUntilUtc;
    }

    /// <summary>Exact observed binding, including revision and fingerprint.</summary>
    public ExecutionDefinitionReference Binding { get; }
    /// <summary>Original observation, retained even when it is no longer current.</summary>
    public ExecutionHealthObservation Observation { get; }
    /// <summary>Exclusive expiry of the producer's observation under explicit host policy.</summary>
    public DateTimeOffset ValidUntilUtc { get; }
}

/// <summary>Read-only inspection separating lifecycle, current provider evidence and unresolved consumer work.</summary>
/// <remarks>This projection does not authorize admission, dispatch, retry or removal of pending work. It reports
/// provider readiness independently of lifecycle acceptance and does not infer coverage or a cause for pending work.
/// Exact binding/session/evidence identities belong in inspection and traces, not metric dimension labels.</remarks>
public sealed class IntegrationInspection
{
    IntegrationInspection(IntegrationAdmissionBinding binding, IntegrationLifecycleSnapshot lifecycle,
        DateTimeOffset atUtc, IntegrationHealthEvidence? providerEvidence, DateTimeOffset? oldestPendingAtUtc)
    {
        Binding = binding.Binding; Lifecycle = lifecycle; AtUtc = atUtc;
        ProviderEvidence = providerEvidence; OldestPendingAtUtc = oldestPendingAtUtc;
    }

    /// <summary>Projects a coherent host-supplied lifecycle snapshot and optional exact provider observation.</summary>
    /// <param name="binding">Prevalidated binding to inspect; no compilation is repeated.</param>
    /// <param name="lifecycle">Consumer-wide snapshot, including work admitted under other bindings of the same consumer.</param>
    /// <param name="atUtc">Explicit UTC inspection boundary, no earlier than any pending admission.</param>
    /// <param name="providerEvidence">Optional host-authenticated evidence; absent, future or expired evidence yields Unknown.</param>
    /// <returns>An immutable projection retaining the original evidence and lifecycle revision.</returns>
    /// <exception cref="ArgumentNullException">Binding or lifecycle is null.</exception>
    /// <exception cref="ArgumentException">The UTC boundary, consumer scope, pending snapshot or evidence binding is invalid.</exception>
    public static IntegrationInspection Create(IntegrationAdmissionBinding binding, IntegrationLifecycleSnapshot lifecycle,
        DateTimeOffset atUtc, IntegrationHealthEvidence? providerEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(lifecycle);
        if (atUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Inspection must use UTC.", nameof(atUtc));
        if (binding.Consumer != lifecycle.Consumer) throw new ArgumentException("Lifecycle belongs to another consumer.", nameof(lifecycle));
        if (providerEvidence is not null && providerEvidence.Binding != binding.Binding)
            throw new ArgumentException("Health evidence belongs to another binding revision or scope.", nameof(providerEvidence));
        if (lifecycle.Pending.IsDefault) throw new ArgumentException("Pending snapshot must be materialized.", nameof(lifecycle));
        DateTimeOffset? oldest = null;
        foreach (var admission in lifecycle.Pending)
        {
            if (admission is null || admission.Consumer != lifecycle.Consumer || admission.AtUtc.Offset != TimeSpan.Zero || admission.AtUtc > atUtc)
                throw new ArgumentException("Pending admissions must belong to the consumer and precede inspection in UTC.", nameof(lifecycle));
            if (oldest is null || admission.AtUtc < oldest) oldest = admission.AtUtc;
        }
        return new(binding, lifecycle, atUtc, providerEvidence, oldest);
    }

    /// <summary>Exact binding whose provider evidence is being inspected.</summary>
    public ExecutionDefinitionReference Binding { get; }
    /// <summary>Original consumer lifecycle snapshot and control revision; this is not an execution-liveness observation.</summary>
    public IntegrationLifecycleSnapshot Lifecycle { get; }
    /// <summary>Explicit observation boundary for this projection.</summary>
    public DateTimeOffset AtUtc { get; }
    /// <summary>Original provider evidence, or null when none was supplied.</summary>
    public IntegrationHealthEvidence? ProviderEvidence { get; }
    /// <summary>Whether evidence covers the half-open interval containing the inspection boundary.</summary>
    public bool HasCurrentProviderEvidence => ProviderEvidence is { } evidence
        && evidence.Observation.ObservedAtUtc <= AtUtc && AtUtc < evidence.ValidUntilUtc;
    /// <summary>Current provider health, or Unknown for missing, future or expired evidence.</summary>
    public ExecutionHealthStatus ProviderHealth => HasCurrentProviderEvidence ? ProviderEvidence!.Observation.Health : ExecutionHealthStatus.Unknown;
    /// <summary>Current provider readiness only, independent of lifecycle, qualification and authorization.</summary>
    public ExecutionReadinessStatus ProviderReadiness => HasCurrentProviderEvidence ? ProviderEvidence!.Observation.Readiness : ExecutionReadinessStatus.Unknown;
    /// <summary>Number of unresolved admitted sessions across the consumer; not a count of ambiguous effects or queued jobs.</summary>
    public int PendingSessionCount => Lifecycle.Pending.Length;
    /// <summary>Earliest pending admission time, or null with no pending sessions.</summary>
    public DateTimeOffset? OldestPendingAtUtc { get; }
}
