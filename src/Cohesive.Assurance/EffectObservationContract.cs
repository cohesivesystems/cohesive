using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Assurance;

/// <summary>Versioned mapping from committed effects to occurrence identities within one authority and subject.</summary>
/// <remarks>
/// The v1 profile is untimed and order-insensitive. Effect identity distinguishes commits from duplicate telemetry.
/// Bindings preserve the source protocol's occurrence identity, not a request-content hash or business intent.
/// </remarks>
public sealed record EffectObservationContract
{
    /// <summary>Creates the pinned observation mapping and selected scope.</summary>
    /// <param name="id">Stable mapping identity.</param>
    /// <param name="revision">Exact mapping revision.</param>
    /// <param name="mappingFingerprint">Content identity of the mapping rules and dependencies.</param>
    /// <param name="authorityScope">Canonical authority and tenant scope supplied by the binding.</param>
    /// <param name="subjectIdentity">Canonical selected entity type and identity supplied by the binding.</param>
    /// <exception cref="ArgumentException">A required string is blank.</exception>
    /// <exception cref="ArgumentNullException">A required string is null.</exception>
    [JsonConstructor]
    public EffectObservationContract(string id, string revision, string mappingFingerprint,
        string authorityScope, string subjectIdentity)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        MappingFingerprint = Guard.RequireNotNullOrWhiteSpace(mappingFingerprint);
        AuthorityScope = Guard.RequireNotNullOrWhiteSpace(authorityScope);
        SubjectIdentity = Guard.RequireNotNullOrWhiteSpace(subjectIdentity);
    }

    /// <summary>Stable mapping identity.</summary>
    public string Id { get; }
    /// <summary>Exact mapping revision.</summary>
    public string Revision { get; }
    /// <summary>Mapping content identity; changing observation rules requires reassessment.</summary>
    public string MappingFingerprint { get; }
    /// <summary>Selected authority and tenant scope.</summary>
    public string AuthorityScope { get; }
    /// <summary>Selected entity type and identity.</summary>
    public string SubjectIdentity { get; }
}

/// <summary>Projected durable-effect evidence; null means unobserved identity, not an absent effect.</summary>
public sealed record CommittedEffectObservation
{
    /// <summary>Creates one projected observation.</summary>
    /// <param name="operationIdentity">Exact occurrence identity, or null when unavailable.</param>
    /// <param name="effectIdentity">Durable effect identity within the selected scope, or null when unavailable.</param>
    /// <exception cref="ArgumentException">A supplied identity is empty or white-space.</exception>
    [JsonConstructor]
    public CommittedEffectObservation(string? operationIdentity, string? effectIdentity)
    {
        OperationIdentity = operationIdentity is null ? null : Guard.RequireNotNullOrWhiteSpace(operationIdentity);
        EffectIdentity = effectIdentity is null ? null : Guard.RequireNotNullOrWhiteSpace(effectIdentity);
    }

    /// <summary>Exact occurrence identity or missing evidence.</summary>
    public string? OperationIdentity { get; }
    /// <summary>Durable effect identity or missing evidence.</summary>
    public string? EffectIdentity { get; }
}

/// <summary>Immutable observation projection for a single claim check, not a second executable scenario model.</summary>
public sealed record EffectUniquenessCheck
{
    /// <summary>Creates a bounded check input with explicit target and observation provenance.</summary>
    /// <param name="claim">Canonical claim.</param>
    /// <param name="observationContract">Pinned mapping and selected scope.</param>
    /// <param name="target">Exact observed target.</param>
    /// <param name="observedTransition">Transition reference attested by the observation producer.</param>
    /// <param name="completion">Explicit scope closure and completeness declaration.</param>
    /// <param name="observations">Immutable observations in source order; default means an empty batch.</param>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    /// <exception cref="ArgumentException">An observation is null or completion is unsupported.</exception>
    [JsonConstructor]
    public EffectUniquenessCheck(EffectUniquenessClaim claim, EffectObservationContract observationContract,
        AssuranceTarget target, ExecutionDefinitionReference observedTransition, ObservationScopeCompletion completion,
        ImmutableArray<CommittedEffectObservation> observations)
    {
        Claim = Guard.RequireNotNull(claim);
        ObservationContract = Guard.RequireNotNull(observationContract);
        Target = Guard.RequireNotNull(target);
        ObservedTransition = Guard.RequireNotNull(observedTransition);
        if (!Enum.IsDefined(completion)) throw new ArgumentOutOfRangeException(nameof(completion));
        Completion = completion;
        Observations = observations.IsDefault ? [] : observations;
        foreach (var observation in Observations)
            if (observation is null) throw new ArgumentException("An observation cannot be null.", nameof(observations));
    }

    /// <summary>Canonical claim.</summary>
    public EffectUniquenessClaim Claim { get; }
    /// <summary>Pinned mapping and authority scope.</summary>
    public EffectObservationContract ObservationContract { get; }
    /// <summary>Exact observed target.</summary>
    public AssuranceTarget Target { get; }
    /// <summary>Observed transition; mismatch with the claim invalidates evidence.</summary>
    public ExecutionDefinitionReference ObservedTransition { get; }
    /// <summary>Producer's scope closure declaration.</summary>
    public ObservationScopeCompletion Completion { get; }
    /// <summary>Immutable projected observations in retained source order.</summary>
    public ImmutableArray<CommittedEffectObservation> Observations { get; }
}
