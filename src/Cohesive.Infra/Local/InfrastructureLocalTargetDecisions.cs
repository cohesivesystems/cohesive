using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra.Local;

/// <summary>Inspectable capability decision made by one local lifecycle interpreter.</summary>
/// <remarks>
/// Decisions are attributable interpretation evidence, not canonical infrastructure semantics. Concern identities are
/// target-neutral so conformance tooling can compare how several interpreters realize the same local requirement.
/// </remarks>
public sealed record InfrastructureLocalTargetDecision
{
    /// <summary>Creates a local target decision.</summary>
    /// <param name="target">Stable lifecycle-interpreter identity.</param>
    /// <param name="concern">Stable target-neutral concern identity.</param>
    /// <param name="kind">Capability realization kind.</param>
    /// <param name="rationale">Human-readable interpretation rationale.</param>
    /// <param name="boundaries">Exact semantic boundaries or constraints.</param>
    /// <param name="sourceReferences">Attributable source references.</param>
    /// <exception cref="ArgumentException">A string or reference collection is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureLocalTargetDecision(
        string target,
        string concern,
        CapabilityRealizationKind kind,
        string rationale,
        ImmutableArray<string> boundaries,
        ImmutableArray<SourceReference> sourceReferences)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported capability realization kind.");
        }

        if (!boundaries.IsDefaultOrEmpty && boundaries.Any(static item => string.IsNullOrWhiteSpace(item)))
        {
            throw new ArgumentException("Local target-decision boundaries cannot contain empty values.", nameof(boundaries));
        }

        if (kind is (CapabilityRealizationKind.Native or CapabilityRealizationKind.Composed) && !boundaries.IsDefaultOrEmpty)
        {
            throw new ArgumentException($"{kind} local target decisions cannot retain semantic boundaries.", nameof(boundaries));
        }

        if (kind is (CapabilityRealizationKind.Constrained or CapabilityRealizationKind.Override) && boundaries.IsDefaultOrEmpty)
        {
            throw new ArgumentException($"{kind} local target decisions require at least one semantic boundary.", nameof(boundaries));
        }

        Target = Guard.RequireNotNullOrWhiteSpace(target);
        Concern = Guard.RequireNotNullOrWhiteSpace(concern);
        Kind = kind;
        Rationale = Guard.RequireNotNullOrWhiteSpace(rationale);
        Boundaries = boundaries.IsDefaultOrEmpty ? [] : boundaries.Sort(StringComparer.Ordinal);
        SourceReferences = SourceReference.NormalizeSet(
            sourceReferences,
            requireNonEmpty: true);
    }

    /// <summary>Stable lifecycle-interpreter identity.</summary>
    public string Target { get; }

    /// <summary>Stable target-neutral concern identity.</summary>
    public string Concern { get; }

    /// <summary>Capability realization kind.</summary>
    public CapabilityRealizationKind Kind { get; }

    /// <summary>Human-readable interpretation rationale.</summary>
    public string Rationale { get; }

    /// <summary>Exact semantic boundaries or constraints.</summary>
    public ImmutableArray<string> Boundaries { get; }

    /// <summary>Attributable source references.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }
}
