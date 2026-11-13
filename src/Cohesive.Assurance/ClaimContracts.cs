using System.Collections.Immutable;
using Cohesive.Execution;
using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Assurance;

/// <summary>Initial executable claim forms.</summary>
public enum ClaimForm
{
    /// <summary>Predicate at each selected committed state.</summary>
    StateInvariant,
    /// <summary>Predicate on each selected successful commit.</summary>
    TransitionContract,
    /// <summary>Existential, non-consuming, strictly earlier correlated event.</summary>
    HistoricalRequirement,
    /// <summary>Existential, non-consuming outcome by an inclusive deadline.</summary>
    BoundedResponse
}

/// <summary>Meaning of an input in a claim predicate.</summary>
public enum ClaimInputRole
{
    /// <summary>State at a selected boundary.</summary>
    State,
    /// <summary>State before a successful commit.</summary>
    Before,
    /// <summary>Command causing a successful commit.</summary>
    Command,
    /// <summary>State after a successful commit.</summary>
    After,
    /// <summary>Event creating a historical or response obligation.</summary>
    Trigger,
    /// <summary>Candidate earlier event or response outcome.</summary>
    Witness
}

/// <summary>Whether an invariant selects its scope's initial state.</summary>
public enum InitialStateSelection
{
    /// <summary>Only committed states are selected.</summary>
    Exclude,
    /// <summary>The initial state is also selected.</summary>
    Include
}

/// <summary>Exact reference to a domain-owned subject, contract, selector or assumption.</summary>
public sealed record ClaimReference
{
    /// <summary>Creates an exact reference without copying the referenced semantic model.</summary>
    /// <param name="source">Existing canonical subject identity.</param>
    /// <param name="revision">Exact immutable revision.</param>
    /// <param name="fingerprint">Content fingerprint supplied by its owning model.</param>
    /// <exception cref="ArgumentException">An identity, revision or fingerprint is empty.</exception>
    /// <exception cref="ArgumentNullException">Revision or fingerprint is null.</exception>
    [JsonConstructor]
    public ClaimReference(SourceReference source, string revision, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(source.Value)) throw new ArgumentException("An exact source is required.", nameof(source));
        Source = source;
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        Fingerprint = Guard.RequireNotNullOrWhiteSpace(fingerprint);
    }
    /// <summary>Domain-owned identity.</summary>
    public SourceReference Source { get; }
    /// <summary>Exact revision.</summary>
    public string Revision { get; }
    /// <summary>Owning domain's content fingerprint.</summary>
    public string Fingerprint { get; }

    /// <summary>Attaches an exact Cohesive execution definition without hand-copying its identity or fingerprint.</summary>
    /// <param name="reference">Owning model's typed reference, for example a compiled transition's DefinitionReference.</param>
    /// <returns>An exact claim reference retaining the definition identity, revision, algorithm, canonicalization and digest.</returns>
    /// <remarks>This projection does not validate the referenced document or establish execution/commit evidence.</remarks>
    /// <exception cref="ArgumentNullException">Reference is null.</exception>
    public static ClaimReference FromExecution(ExecutionDefinitionReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var fingerprint = reference.Fingerprint;
        return new(SourceReference.Create("execution", Uri.EscapeDataString(reference.DefinitionId.Value)), reference.RevisionId.Value,
            fingerprint.ToCanonicalString());
    }

    /// <summary>Creates an authoring scope for explicit fixture references used by examples, articles and tests.</summary>
    /// <param name="sourceRoot">Canonical source namespace, for example example://fulfillment; a trailing slash is optional.</param>
    /// <param name="revision">Explicit immutable fixture revision shared by its declarations.</param>
    /// <returns>A single-threaded scope that fingerprints declaration text and collects exact references.</returns>
    /// <exception cref="ArgumentNullException">Root or revision is null.</exception>
    /// <exception cref="ArgumentException">Root or revision is invalid.</exception>
    public static ClaimReferenceFixture Fixtures(string sourceRoot, string revision) => new(sourceRoot, revision);

    internal static IComparer<ClaimReference> CanonicalComparer { get; } = Comparer<ClaimReference>.Create((left, right) =>
    {
        var order = StringComparer.Ordinal.Compare(left.Source.Value, right.Source.Value);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.Revision, right.Revision);
        return order != 0 ? order : StringComparer.Ordinal.Compare(left.Fingerprint, right.Fingerprint);
    });

    internal static ImmutableArray<ClaimReference> NormalizeSet(ImmutableArray<ClaimReference> references)
    {
        if (references.IsDefaultOrEmpty) return [];
        if (references.Any(x => x is null)) throw new ArgumentException("Exact references cannot be null.", nameof(references));
        return [.. references.Distinct().OrderBy(x => x, CanonicalComparer)];
    }
}

/// <summary>Portable input contract with an explicit evaluation role.</summary>
public sealed record ClaimInput
{
    /// <summary>Creates a predicate input.</summary>
    /// <param name="id">Stable expression binding identity.</param>
    /// <param name="role">Evaluation role in the selected claim form.</param>
    /// <param name="contract">Exact portable value contract.</param>
    /// <exception cref="ArgumentException">Identity or role is invalid.</exception>
    /// <exception cref="ArgumentNullException">Contract is null.</exception>
    [JsonConstructor]
    public ClaimInput(ValueBindingId id, ClaimInputRole role, ValueContract contract)
    {
        if (string.IsNullOrWhiteSpace(id.Value)) throw new ArgumentException("An input identity is required.", nameof(id));
        if (!Enum.IsDefined(role)) throw new ArgumentException("Unknown input role.", nameof(role));
        Id = id;
        Role = role;
        Contract = Guard.RequireNotNull(contract);
    }
    /// <summary>Expression binding identity.</summary>
    public ValueBindingId Id { get; }
    /// <summary>Evaluation role.</summary>
    public ClaimInputRole Role { get; }
    /// <summary>Portable input contract.</summary>
    public ValueContract Contract { get; }
}
