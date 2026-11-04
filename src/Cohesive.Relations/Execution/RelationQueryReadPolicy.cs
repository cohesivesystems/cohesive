namespace Cohesive.Relations.Execution;

/// <summary>Explicit query read realization, independent of its portable API contract.</summary>
public enum RelationQueryReadSource
{
    /// <summary>Evaluate the canonical relation against its declared live sources.</summary>
    Live,
    /// <summary>Read an operator-selected asynchronous generation; read-your-writes is not promised.</summary>
    Materialized
}

/// <summary>Per-query startup selection, shared by offline admission and native binding.</summary>
public sealed record RelationQueryReadPolicy
{
    /// <summary>Defaults to live evaluation; changing selection requires rebinding the host.</summary>
    public RelationQueryReadSource Source { get; init; }
    /// <summary>Explicit consent to asynchronous visibility, required for Materialized.</summary>
    public bool AcceptEventualConsistency { get; init; }

    /// <summary>Validates the selection before native resources are prepared.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Source)) throw new ArgumentException("Unknown query read source.");
        if (Source == RelationQueryReadSource.Materialized && !AcceptEventualConsistency)
            throw new ArgumentException("Materialized reads require explicit acceptance of eventual consistency.");
    }

    /// <summary>Resolves the requirement for this selection without weakening the live interpretation.</summary>
    /// <param name="liveRequirement">The application's explicit live-read consistency requirement.</param>
    /// <returns>That live requirement, or Eventual after explicit materialized-read consent.</returns>
    /// <exception cref="ArgumentException">Selection, consent or the live requirement is invalid.</exception>
    public RelationQueryConsistency ConsistencyFor(RelationQueryConsistency liveRequirement)
    {
        Validate();
        if (!Enum.IsDefined(liveRequirement)) throw new ArgumentException("Unknown live-read consistency requirement.", nameof(liveRequirement));
        return Source == RelationQueryReadSource.Materialized ? RelationQueryConsistency.Eventual : liveRequirement;
    }
}
