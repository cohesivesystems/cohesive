using System.Collections.Immutable;
using Cohesive.Infra;
using Cohesive.Infra.Realization;

namespace Cohesive.Relations.Infra;

/// <summary>Plan-derived acquisition evidence for one explicitly associated native resource.</summary>
public sealed class ComposedQueryResourceEvidence : IInfrastructureResourcePreparation
{
    readonly InfrastructureTargetImplementation implementation;
    readonly string identity;
    /// <summary>Retains prepared acquisition evidence qualified by its canonical source identity.</summary>
    /// <param name="identity">Exact native database name or canonical source ID.</param>
    /// <param name="kind">Adapter kind used to namespace the implementation ID.</param>
    /// <param name="evidence">Evidence produced by query preparation.</param>
    /// <exception cref="ArgumentException">An identity or kind is empty.</exception>
    /// <exception cref="ArgumentNullException">Evidence is null.</exception>
    internal ComposedQueryResourceEvidence(string identity, string kind, IEnumerable<InfrastructureCapabilityEvidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(evidence);
        this.identity = identity;
        implementation = InfrastructureTargetImplementation.ResourceImplementation(new("query-source/" + kind + "/" + Uri.EscapeDataString(identity)), evidence.ToArray());
        Requirements = [.. implementation.Evidence.Select(item => item.Capability)];
    }
    /// <inheritdoc />
    public ImmutableArray<InfrastructureCapabilityId> Requirements { get; }
    /// <summary>Checks resource affinity before exposing the implementation.</summary>
    /// <param name="identity">Exact native database name or canonical remote source identity.</param>
    /// <returns>Acquisition evidence for this resource only.</returns>
    /// <exception cref="ArgumentException">The requested identity differs from the prepared source.</exception>
    public InfrastructureTargetImplementation ForSource(string identity) => this.identity == identity ? implementation
        : throw new ArgumentException("Prepared acquisition evidence belongs to a different source.", nameof(identity));
}
