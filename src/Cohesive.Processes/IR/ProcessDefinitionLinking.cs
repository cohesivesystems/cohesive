using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
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
    RelationQuery = 2,

    /// <summary>The exact definition is a canonical child Process.</summary>
    Process = 3
}

/// <summary>
/// Derived type evidence for one exact definition referenced by canonical Process IR.
/// </summary>
/// <remarks>
/// This value is compiler/linker evidence, not another persisted semantic definition. The referenced block remains
/// authoritative for its input and result contracts; a Process validator uses this projection to prove that call-site
/// expressions and output bindings agree with that authority. The public constructor is an attestation boundary for
/// external linkers and does not independently validate the supplied contracts against a canonical document. Prefer
/// the corresponding <c>TryCreateProcess</c> or <c>TryCreateTransition</c> factory when a canonical document is
/// available.
/// </remarks>
public sealed record ProcessDefinitionLink
{
    /// <summary>Attempts to derive exact child-Process linking evidence from a canonical Process document.</summary>
    /// <param name="document">Canonical Process document that remains authoritative for the derived link.</param>
    /// <param name="link">Receives exact Process linking evidence when the document is valid; otherwise null.</param>
    /// <returns>The shared envelope, canonical-wire, and Process semantic validation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Semantic content cannot be encoded using the strict JSON contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult TryCreateProcess(
        ExecutionDefinitionDocument document,
        out ProcessDefinitionLink? link) =>
        TryCreateProcessCore(document, context: null, out link);

    /// <summary>
    /// Attempts to derive exact child-Process linking evidence using a graph that resolves referenced portable types
    /// and shapes.
    /// </summary>
    /// <param name="document">Canonical Process document that remains authoritative for the derived link.</param>
    /// <param name="graph">Shared shape graph used to resolve named types and qualified shapes.</param>
    /// <param name="link">Receives exact Process linking evidence when the document is valid; otherwise null.</param>
    /// <returns>The shared envelope, canonical-wire, and Process semantic validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Semantic content cannot be encoded using the strict JSON contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult TryCreateProcess(
        ExecutionDefinitionDocument document,
        ShapeGraph graph,
        out ProcessDefinitionLink? link)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return TryCreateProcessCore(
            document,
            new ProcessDefinitionValidationContext(shapeGraph: graph),
            out link);
    }

    /// <summary>
    /// Attempts to derive exact child-Process linking evidence using complete external definition, interaction, and
    /// shape evidence.
    /// </summary>
    /// <param name="document">Canonical Process document that remains authoritative for the derived link.</param>
    /// <param name="context">External semantic evidence used to validate the Process before deriving its link.</param>
    /// <param name="link">Receives exact Process linking evidence when the document is valid; otherwise null.</param>
    /// <returns>The shared envelope, canonical-wire, and Process semantic validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Semantic content cannot be encoded using the strict JSON contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult TryCreateProcess(
        ExecutionDefinitionDocument document,
        ProcessDefinitionValidationContext context,
        out ProcessDefinitionLink? link)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryCreateProcessCore(document, context, out link);
    }

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
    /// <param name="processDependencies">
    /// Complete direct child-Process references when <paramref name="kind"/> is
    /// <see cref="ProcessDefinitionLinkKind.Process"/>. Null denotes unavailable dependency evidence.
    /// </param>
    /// <param name="recoveryPolicy">
    /// Exact recovery policy projected from a Process definition. Required only when <paramref name="kind"/> is
    /// <see cref="ProcessDefinitionLinkKind.Process"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="input"/>, or <paramref name="result"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="processDependencies"/> contains a null or duplicate exact reference, or dependencies are
    /// supplied for a non-Process link, or recovery evidence is absent/inapplicable for the declared kind.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unspecified or unsupported.</exception>
    public ProcessDefinitionLink(
        ExecutionDefinitionReference definition,
        ProcessDefinitionLinkKind kind,
        ValueContract input,
        ValueContract result,
        IEnumerable<ExecutionDefinitionReference>? processDependencies = null,
        ProcessRecoveryPolicy? recoveryPolicy = null)
    {
        if (!Enum.IsDefined(kind) || kind == ProcessDefinitionLinkKind.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A Process definition link requires an explicit semantic family.");

        Definition = Guard.RequireNotNull(definition);
        Kind = kind;
        Input = Guard.RequireNotNull(input);
        Result = Guard.RequireNotNull(result);

        var dependencyBuilder = ImmutableArray.CreateBuilder<ExecutionDefinitionReference>();
        HashSet<ExecutionDefinitionReference> observedDependencies = [];
        if (processDependencies is not null)
        {
            foreach (var dependency in processDependencies)
            {
                if (dependency is null)
                {
                    throw new ArgumentException(
                        "Process dependency evidence cannot contain null entries.",
                        nameof(processDependencies));
                }
                if (!observedDependencies.Add(dependency))
                {
                    throw new ArgumentException(
                        "Process dependency evidence cannot repeat an exact definition reference.",
                        nameof(processDependencies));
                }
                dependencyBuilder.Add(dependency);
            }
        }

        if (kind != ProcessDefinitionLinkKind.Process && processDependencies is not null)
        {
            throw new ArgumentException(
                "Only a Process definition link can declare child Process dependencies.",
                nameof(processDependencies));
        }
        if (kind == ProcessDefinitionLinkKind.Process)
        {
            if (recoveryPolicy is not { } processRecovery
                || !Enum.IsDefined(processRecovery)
                || processRecovery == ProcessRecoveryPolicy.Unspecified)
            {
                throw new ArgumentException(
                    "A Process definition link requires an exact recovery-policy attestation.",
                    nameof(recoveryPolicy));
            }
        }
        else if (recoveryPolicy is not null)
        {
            throw new ArgumentException(
                "Only a Process definition link can carry recovery-policy evidence.",
                nameof(recoveryPolicy));
        }
        dependencyBuilder.Sort(CompareReferences);
        ProcessDependencies = dependencyBuilder.Count == dependencyBuilder.Capacity
            ? dependencyBuilder.MoveToImmutable()
            : dependencyBuilder.ToImmutable();
        HasCompleteProcessDependencyEvidence = kind != ProcessDefinitionLinkKind.Process
                                               || processDependencies is not null;
        RecoveryPolicy = recoveryPolicy;
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

    /// <summary>
    /// Known direct child-Process references in deterministic exact-reference order. This set is complete only when
    /// <see cref="HasCompleteProcessDependencyEvidence"/> is <see langword="true"/>.
    /// </summary>
    public ImmutableArray<ExecutionDefinitionReference> ProcessDependencies { get; }

    /// <summary>Whether <see cref="ProcessDependencies"/> is known to be a complete direct dependency set.</summary>
    public bool HasCompleteProcessDependencyEvidence { get; }

    /// <summary>Exact child recovery policy for a Process link; null for other semantic families.</summary>
    public ProcessRecoveryPolicy? RecoveryPolicy { get; }

    /// <summary>Compares definition links by their complete derived semantic evidence.</summary>
    /// <param name="other">Definition link to compare with this value.</param>
    /// <returns>
    /// <see langword="true"/> when references, contracts, kind, recovery policy, and dependency evidence are equal.
    /// </returns>
    public bool Equals(ProcessDefinitionLink? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Definition == other.Definition
        && Kind == other.Kind
        && Input == other.Input
        && Result == other.Result
        && RecoveryPolicy == other.RecoveryPolicy
        && HasCompleteProcessDependencyEvidence == other.HasCompleteProcessDependencyEvidence
        && ProcessDependencies.SequenceEqual(other.ProcessDependencies);

    /// <summary>Returns a structural hash code for complete linking evidence.</summary>
    /// <returns>A hash code derived from exact definition, contracts, kind, recovery policy, and child dependencies.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Definition);
        hash.Add(Kind);
        hash.Add(Input);
        hash.Add(Result);
        hash.Add(RecoveryPolicy);
        hash.Add(HasCompleteProcessDependencyEvidence);
        foreach (var dependency in ProcessDependencies)
            hash.Add(dependency);
        return hash.ToHashCode();
    }

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

    static DocumentValidationResult TryCreateProcessCore(
        ExecutionDefinitionDocument document,
        ProcessDefinitionValidationContext? context,
        out ProcessDefinitionLink? link)
    {
        ArgumentNullException.ThrowIfNull(document);
        var validation = context is null
            ? ProcessDefinitionDocuments.Validate(document)
            : ProcessDefinitionDocuments.Validate(document, context);
        if (!validation.IsValid)
        {
            link = null;
            return validation;
        }

        var definition = document.GetDefinition<CanonicalProcessDefinition>();
        var dependencies = ImmutableArray.CreateBuilder<ExecutionDefinitionReference>();
        HashSet<ExecutionDefinitionReference> observedDependencies = [];
        foreach (var node in definition.Nodes)
        {
            var dependency = ProcessRequestSemantics.TryProjectChild(node, out var child)
                ? child.Process
                : null;
            if (dependency is not null && observedDependencies.Add(dependency))
                dependencies.Add(dependency);
        }
        dependencies.Sort(CompareReferences);
        var normalizedDependencies = dependencies.Count == dependencies.Capacity
            ? dependencies.MoveToImmutable()
            : dependencies.ToImmutable();

        link = new(
            new(
                document.Metadata.DefinitionId,
                document.Metadata.RevisionId,
                document.Metadata.Fingerprint),
            ProcessDefinitionLinkKind.Process,
            definition.Input,
            definition.Result,
            document.Extensions.IsDefaultOrEmpty ? normalizedDependencies : null,
            definition.RecoveryPolicy);
        return validation;
    }

    static int CompareReferences(ExecutionDefinitionReference? left, ExecutionDefinitionReference? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        return ExecutionDefinitionReference.CompareCanonical(left, right);
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
    /// <param name="definitions">Exact Transition, Relation/Query, and child Process links available to the validator.</param>
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

    /// <summary>Attempts to resolve an exact Transition, Relation/Query, or child Process definition reference.</summary>
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
