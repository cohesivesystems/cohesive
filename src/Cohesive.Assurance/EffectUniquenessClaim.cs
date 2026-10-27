using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Assurance;

/// <summary>Single-trace safety claim requiring at most one durable effect per exact operation occurrence.</summary>
/// <remarks>This initial assertion profile has finite uniqueness semantics, not a host-language predicate.</remarks>
public sealed record EffectUniquenessClaim
{
    /// <summary>Creates a claim over one exact transition definition.</summary>
    /// <param name="id">Stable claim identity.</param>
    /// <param name="revision">Explicit semantic revision.</param>
    /// <param name="transition">Exact transition whose effects are selected.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">An identity or revision is blank.</exception>
    [JsonConstructor]
    public EffectUniquenessClaim(string id, string revision, ExecutionDefinitionReference transition)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        Transition = Guard.RequireNotNull(transition);
    }

    /// <summary>Stable claim identity.</summary>
    public string Id { get; }
    /// <summary>Exact semantic revision.</summary>
    public string Revision { get; }
    /// <summary>Exact selected transition definition.</summary>
    public ExecutionDefinitionReference Transition { get; }
}

/// <summary>Finite-prefix truth values; these are independent of acceptance and verification-job lifecycle.</summary>
public enum AssuranceVerdict
{
    /// <summary>Every admissible completion satisfies the claim within the declared scope.</summary>
    Satisfied,
    /// <summary>Consistent observations already witness a violation.</summary>
    Violated,
    /// <summary>Available observations do not establish satisfaction or violation.</summary>
    Inconclusive
}

/// <summary>Explicit producer declaration of the observed scope's closure and acquisition completeness.</summary>
public enum ObservationScopeCompletion
{
    /// <summary>Further effects may occur; ending a run does not close this scope.</summary>
    Open,
    /// <summary>No further effects may occur in this scope and all relevant effects were acquired.</summary>
    ClosedComplete,
    /// <summary>The scope ended, but relevant observations may be missing.</summary>
    ClosedIncomplete
}

/// <summary>Kind of target actually evaluated; a model is never implicitly a deployed system.</summary>
public enum AssuranceTargetKind
{
    /// <summary>An abstract or reference behavior model.</summary>
    ReferenceModel,
    /// <summary>A pinned implementation under test.</summary>
    ImplementationCandidate,
    /// <summary>A pinned deployed realization.</summary>
    DeployedSystem
}

/// <summary>Exact target attribution supplied by the producer; this is not a deployment attestation.</summary>
public sealed record AssuranceTarget
{
    /// <summary>Creates immutable target attribution.</summary>
    /// <param name="kind">Kind of system actually observed.</param>
    /// <param name="id">Stable target identity.</param>
    /// <param name="revision">Exact build or realization revision.</param>
    /// <param name="fingerprint">Content identity of the target and its selected dependencies.</param>
    /// <exception cref="ArgumentException">A required string is blank or the kind is unsupported.</exception>
    /// <exception cref="ArgumentNullException">A required string is null.</exception>
    [JsonConstructor]
    public AssuranceTarget(AssuranceTargetKind kind, string id, string revision, string fingerprint)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        Fingerprint = Guard.RequireNotNullOrWhiteSpace(fingerprint);
    }

    /// <summary>Kind of target.</summary>
    public AssuranceTargetKind Kind { get; }
    /// <summary>Stable target identity.</summary>
    public string Id { get; }
    /// <summary>Exact target revision.</summary>
    public string Revision { get; }
    /// <summary>Producer-supplied target content identity.</summary>
    public string Fingerprint { get; }
}
