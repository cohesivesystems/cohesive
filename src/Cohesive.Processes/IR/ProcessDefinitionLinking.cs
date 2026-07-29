using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Processes.IR;

/// <summary>Semantic family supplied by one exact Process definition link.</summary>
public enum ProcessDefinitionLinkKind
{
    /// <summary>No semantic family was supplied; this value is invalid linking evidence.</summary>
    Unspecified = 0,

    /// <summary>The exact definition is a canonical aggregate Transition.</summary>
    Transition = 1,

    /// <summary>The exact definition is a canonical Relation or Query evaluation.</summary>
    RelationQuery = 2
}

/// <summary>
/// Derived type evidence for one exact definition referenced by canonical Process IR.
/// </summary>
/// <remarks>
/// This value is compiler/linker evidence, not another persisted semantic definition. The referenced block remains
/// authoritative for its input and result contracts; a Process validator uses this projection to prove that call-site
/// expressions and output bindings agree with that authority. The public constructor is an attestation boundary for
/// external linkers and does not independently validate the supplied contracts against a canonical document. Prefer
/// <see cref="TryCreateTransition(ExecutionDefinitionDocument, out ProcessDefinitionLink?)"/> when linking a
/// Transition document.
/// </remarks>
public sealed record ProcessDefinitionLink
{
    /// <summary>Attempts to derive exact Process linking evidence from a canonical Transition document.</summary>
    /// <param name="document">Canonical Transition document that remains authoritative for the derived link.</param>
    /// <param name="link">
    /// Receives exact Transition linking evidence when the document is valid; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>The shared envelope, canonical-wire, and Transition semantic validation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Semantic content cannot be encoded using the strict JSON contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult TryCreateTransition(
        ExecutionDefinitionDocument document,
        out ProcessDefinitionLink? link) =>
        TryCreateTransitionCore(document, graph: null, out link);

    /// <summary>
    /// Attempts to derive exact Process linking evidence from a canonical Transition document using a graph that
    /// resolves referenced portable types and shapes.
    /// </summary>
    /// <param name="document">Canonical Transition document that remains authoritative for the derived link.</param>
    /// <param name="graph">Shared shape graph used to resolve named types and qualified shapes.</param>
    /// <param name="link">
    /// Receives exact Transition linking evidence when the document is valid; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>The shared envelope, canonical-wire, and Transition semantic validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Semantic content cannot be encoded using the strict JSON contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult TryCreateTransition(
        ExecutionDefinitionDocument document,
        ShapeGraph graph,
        out ProcessDefinitionLink? link)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return TryCreateTransitionCore(document, graph, out link);
    }

    /// <summary>Creates exact Process linking evidence.</summary>
    /// <param name="definition">Exact definition identity, revision, and fingerprint.</param>
    /// <param name="kind">Semantic family of the referenced definition.</param>
    /// <param name="input">Portable invocation input contract projected from the referenced definition.</param>
    /// <param name="result">Portable result contract projected from the referenced definition.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="input"/>, or <paramref name="result"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unspecified or unsupported.</exception>
    public ProcessDefinitionLink(
        ExecutionDefinitionReference definition,
        ProcessDefinitionLinkKind kind,
        ValueContract input,
        ValueContract result)
    {
        if (!Enum.IsDefined(kind) || kind == ProcessDefinitionLinkKind.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A Process definition link requires an explicit semantic family.");

        Definition = Guard.RequireNotNull(definition);
        Kind = kind;
        Input = Guard.RequireNotNull(input);
        Result = Guard.RequireNotNull(result);
    }

    /// <summary>Exact definition identity, revision, and fingerprint.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Semantic family of the referenced definition.</summary>
    public ProcessDefinitionLinkKind Kind { get; }

    /// <summary>Portable invocation input contract projected from the referenced definition.</summary>
    public ValueContract Input { get; }

    /// <summary>Portable result contract projected from the referenced definition.</summary>
    /// <remarks>
    /// For a Transition link this is the authored outcome contract, not the runtime interpreter's infrastructure or
    /// conflict decision envelope.
    /// </remarks>
    public ValueContract Result { get; }

    static DocumentValidationResult TryCreateTransitionCore(
        ExecutionDefinitionDocument document,
        ShapeGraph? graph,
        out ProcessDefinitionLink? link)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validation = graph is null
            ? TransitionDefinitionDocuments.Validate(document)
            : TransitionDefinitionDocuments.Validate(document, graph);
        if (!validation.IsValid)
        {
            link = null;
            return validation;
        }

        var definition = document.GetDefinition<CanonicalTransitionDefinition>();
        link = new(
            new(
                document.Metadata.DefinitionId,
                document.Metadata.RevisionId,
                document.Metadata.Fingerprint),
            ProcessDefinitionLinkKind.Transition,
            definition.Input,
            definition.Outcome);
        return validation;
    }
}

/// <summary>
/// Immutable external semantic evidence used while validating one canonical Process definition.
/// </summary>
/// <remarks>
/// The context is deliberately not serializable Process state. It links exact persisted definitions and optional
/// shape evidence at validation or compilation time without copying those definitions into Process IR.
/// </remarks>
public sealed class ProcessDefinitionValidationContext
{
    readonly ImmutableDictionary<ExecutionDefinitionReference, ProcessDefinitionLink> definitions;

    /// <summary>Creates a Process-definition validation context.</summary>
    /// <param name="definitions">Exact Transition and Relation/Query links available to the validator.</param>
    /// <param name="interactionContracts">Exact canonical interaction-contract catalog, when available.</param>
    /// <param name="shapeGraph">
    /// Shape authority used to resolve named and qualified portable contracts. When omitted, the interaction
    /// catalog's retained graph is used when available.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="definitions"/> contains a <see langword="null"/> entry, repeats an exact reference, or supplies
    /// different fingerprints for the same definition identity and semantic revision.
    /// </exception>
    public ProcessDefinitionValidationContext(
        IEnumerable<ProcessDefinitionLink>? definitions = null,
        InteractionContractCatalog? interactionContracts = null,
        ShapeGraph? shapeGraph = null)
    {
        var candidates = definitions is null ? [] : definitions.ToImmutableArray();
        if (candidates.Any(static candidate => candidate is null))
            throw new ArgumentException("Process definition links cannot contain null entries.", nameof(definitions));

        var ordered = candidates
            .OrderBy(static candidate => candidate.Definition.DefinitionId.Value, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Definition.RevisionId.Value, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Definition.Fingerprint.Algorithm, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Definition.Fingerprint.Canonicalization, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Definition.Fingerprint.Value, StringComparer.Ordinal)
            .ToImmutableArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1].Definition;
            var current = ordered[index].Definition;
            if (previous.DefinitionId != current.DefinitionId || previous.RevisionId != current.RevisionId)
                continue;

            if (previous.Fingerprint != current.Fingerprint)
            {
                throw new ArgumentException(
                    $"Process definition '{current.DefinitionId.Value}' revision '{current.RevisionId.Value}' has conflicting exact fingerprints.",
                    nameof(definitions));
            }

            throw new ArgumentException(
                $"Process definition '{current.DefinitionId.Value}' revision '{current.RevisionId.Value}' is linked more than once.",
                nameof(definitions));
        }

        this.definitions = ordered.ToImmutableDictionary(static candidate => candidate.Definition);
        DefinitionLinks = ordered;
        InteractionContracts = interactionContracts;
        ShapeGraph = shapeGraph ?? interactionContracts?.ShapeGraph;
    }

    /// <summary>Exact definition links in deterministic canonical-reference order.</summary>
    public ImmutableArray<ProcessDefinitionLink> DefinitionLinks { get; }

    /// <summary>Exact canonical interaction contracts available to Process validation.</summary>
    public InteractionContractCatalog? InteractionContracts { get; }

    /// <summary>Effective shape authority used to resolve named and qualified portable contracts.</summary>
    public ShapeGraph? ShapeGraph { get; }

    /// <summary>Attempts to resolve an exact Transition or Relation/Query definition reference.</summary>
    /// <param name="reference">Exact definition identity, revision, and fingerprint.</param>
    /// <param name="definition">Resolved derived link evidence when present.</param>
    /// <returns><see langword="true"/> when the exact reference is linked; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    public bool TryResolve(
        ExecutionDefinitionReference reference,
        [NotNullWhen(true)] out ProcessDefinitionLink? definition)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return definitions.TryGetValue(reference, out definition);
    }
}
