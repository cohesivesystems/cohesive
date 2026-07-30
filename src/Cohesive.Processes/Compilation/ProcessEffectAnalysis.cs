using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Processes.Compilation;

/// <summary>Atomic-scope demand retained through target-independent structural Process analysis.</summary>
/// <remarks>
/// This demand requests an eventual realization guarantee. Passing static analysis proves only that the canonical
/// graph has no known structural blocker; a target compiler must still supply capability evidence.
/// </remarks>
public enum ProcessAtomicScopeDemand
{
    /// <summary>No whole-Process atomic realization is requested.</summary>
    /// <remarks>
    /// This does not weaken the reference runtime's separate per-activation checkpoint, inbox, and outbox commit
    /// guarantees.
    /// </remarks>
    None = 0,

    /// <summary>Demand eventual realization of the complete Process definition in one uninterrupted atomic scope.</summary>
    /// <remarks>
    /// Whole-definition scope is the only analysis-demand scope. Arbitrary authored scope regions are deferred.
    /// </remarks>
    WholeDefinition = 1
}

/// <summary>Explicit Process demands retained and structurally preflighted by target-independent compilation.</summary>
public sealed record ProcessCompilationOptions
{
    /// <summary>Default compilation options with no whole-definition atomic demand.</summary>
    public static ProcessCompilationOptions Default { get; } = new(ProcessAtomicScopeDemand.None);

    /// <summary>Creates Process compilation options.</summary>
    /// <param name="atomicScope">
    /// Atomic-scope demand to preflight structurally and retain for downstream realization proof.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="atomicScope"/> is unsupported.</exception>
    public ProcessCompilationOptions(ProcessAtomicScopeDemand atomicScope = ProcessAtomicScopeDemand.None)
    {
        if (!Enum.IsDefined(atomicScope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(atomicScope),
                atomicScope,
                "Unsupported Process atomic scope demand.");
        }

        AtomicScope = atomicScope;
    }

    /// <summary>
    /// Atomic-scope demand checked for known structural blockers and retained for downstream realization proof.
    /// </summary>
    public ProcessAtomicScopeDemand AtomicScope { get; }
}

/// <summary>Target-independent operational effect derived from one canonical Process node.</summary>
public enum ProcessEffectKind
{
    /// <summary>The node reads through an exact Relation or Query definition.</summary>
    Observation = 1,

    /// <summary>The node requests an aggregate-local mutation through an exact Transition definition.</summary>
    AggregateMutation = 2,

    /// <summary>The node crosses a durable wait or activation boundary.</summary>
    DurableWait = 3,

    /// <summary>
    /// The node emits an interaction directly or invokes a host operation whose current result contract permits
    /// canonical interaction emissions.
    /// </summary>
    ExternalInteraction = 4,

    /// <summary>The node starts and joins one or more exact child Processes.</summary>
    ChildProcess = 5,

    /// <summary>The node admits a finite set of concurrent work.</summary>
    BoundedParallelWork = 6,

    /// <summary>The node makes an explicitly bounded recurrence decision across activations.</summary>
    Recurrence = 7,

    /// <summary>The node invokes explicitly authored compensation behavior.</summary>
    Compensation = 8,

    /// <summary>The node invokes explicitly authored reconciliation behavior.</summary>
    Reconciliation = 9
}

/// <summary>How a Process node uses one exact semantic definition resource.</summary>
public enum ProcessResourceAccessKind
{
    /// <summary>The node observes a resource without requesting a semantic mutation.</summary>
    Observe = 1,

    /// <summary>The node requests a semantic mutation of a resource.</summary>
    Mutate = 2,

    /// <summary>The node coordinates through a Process or interaction-contract resource.</summary>
    Coordinate = 3
}

/// <summary>One canonical node and one target-independent effect derived from it.</summary>
public readonly record struct ProcessEffectSite
{
    /// <summary>Creates an effect site.</summary>
    /// <param name="node">Stable canonical Process node identity.</param>
    /// <param name="kind">Target-independent effect kind derived from the node.</param>
    internal ProcessEffectSite(ExecutionNodeId node, ProcessEffectKind kind)
    {
        Node = node;
        Kind = kind;
    }

    /// <summary>Stable canonical Process node identity.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Target-independent effect kind derived from the node.</summary>
    public ProcessEffectKind Kind { get; }
}

/// <summary>One exact semantic resource required by a canonical Process node.</summary>
public sealed record ProcessResourceRequirement
{
    /// <summary>Creates a resource requirement.</summary>
    /// <param name="node">Stable canonical Process node identity.</param>
    /// <param name="resource">Exact referenced definition revision and fingerprint.</param>
    /// <param name="access">Semantic access requested by the node.</param>
    internal ProcessResourceRequirement(
        ExecutionNodeId node,
        ExecutionDefinitionReference resource,
        ProcessResourceAccessKind access)
    {
        Node = node;
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        Access = access;
    }

    /// <summary>Stable canonical Process node identity.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Exact referenced definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Resource { get; }

    /// <summary>Semantic access requested by the node.</summary>
    public ProcessResourceAccessKind Access { get; }
}

/// <summary>Deterministic compilation-only effect and resource projection of canonical Process IR.</summary>
/// <remarks>
/// The canonical Process remains the source of truth. This summary is one derived compiler interpretation used by
/// scope and capability analysis; it is neither persisted Process semantics nor runtime continuation state.
/// </remarks>
public sealed record ProcessEffectSummary
{
    internal ProcessEffectSummary(
        ImmutableArray<ProcessEffectSite> effects,
        ImmutableArray<ProcessResourceRequirement> resources)
    {
        Effects = effects;
        Resources = resources;
    }

    /// <summary>Effect sites sorted by node identity and effect kind.</summary>
    public ImmutableArray<ProcessEffectSite> Effects { get; }

    /// <summary>
    /// Exact statically referenced resources sorted by node, definition reference, and access kind. This collection
    /// does not enumerate resources of possible host-operation emissions when linked effect evidence is not closed.
    /// </summary>
    public ImmutableArray<ProcessResourceRequirement> Resources { get; }

    /// <summary>Compares summaries by their complete deterministic derived evidence.</summary>
    /// <param name="other">Summary to compare with this value.</param>
    /// <returns><see langword="true"/> when every effect and resource requirement is equal.</returns>
    public bool Equals(ProcessEffectSummary? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Effects.SequenceEqual(other.Effects)
        && Resources.SequenceEqual(other.Resources);

    /// <summary>Returns a structural hash for all effect and resource evidence.</summary>
    /// <returns>A hash code derived from every normalized effect and resource requirement.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var effect in Effects)
            hash.Add(effect);
        foreach (var resource in Resources)
            hash.Add(resource);
        return hash.ToHashCode();
    }
}

/// <summary>Single compiler authority for deriving Process effect and resource evidence.</summary>
internal static class ProcessEffectAnalyzer
{
    internal static ProcessEffectSummary Analyze(CanonicalProcessDefinition definition)
    {
        var effects = ImmutableArray.CreateBuilder<ProcessEffectSite>();
        var resources = ImmutableArray.CreateBuilder<ProcessResourceRequirement>();

        foreach (var node in definition.Nodes)
        {
            if (ProcessRequestSemantics.TryProjectChild(node, out var child))
            {
                AddEffect(node, ProcessEffectKind.DurableWait);
                AddEffect(node, ProcessEffectKind.ExternalInteraction);
                AddEffect(node, ProcessEffectKind.ChildProcess);
                AddResource(node, child.Contract.Definition, ProcessResourceAccessKind.Coordinate);
                AddResource(node, child.Process, ProcessResourceAccessKind.Coordinate);
                if (child.Multiplicity == ProcessChildRequestMultiplicity.Partitioned)
                    AddEffect(node, ProcessEffectKind.BoundedParallelWork);
                if (child.Purpose == ProcessChildPurpose.Compensation)
                    AddEffect(node, ProcessEffectKind.Compensation);
                else if (child.Purpose == ProcessChildPurpose.Reconciliation)
                    AddEffect(node, ProcessEffectKind.Reconciliation);
                continue;
            }

            if (ProcessRequestSemantics.TryProject(node, out var request))
            {
                AddEffect(node, ProcessEffectKind.DurableWait);
                AddEffect(node, ProcessEffectKind.ExternalInteraction);
                AddResource(node, request.Contract.Definition, ProcessResourceAccessKind.Coordinate);
                continue;
            }

            switch (node)
            {
                case InvokeTransitionProcessNode invocation:
                    // ProcessOperationResult currently permits canonical emissions and definition links do not yet
                    // carry a closed effect summary, so host operations must remain conservative for scope analysis.
                    AddEffect(node, ProcessEffectKind.AggregateMutation);
                    AddEffect(node, ProcessEffectKind.ExternalInteraction);
                    AddResource(node, invocation.Transition, ProcessResourceAccessKind.Mutate);
                    break;
                case EvaluateRelationProcessNode evaluation:
                    AddEffect(node, ProcessEffectKind.Observation);
                    AddEffect(node, ProcessEffectKind.ExternalInteraction);
                    AddResource(node, evaluation.Relation, ProcessResourceAccessKind.Observe);
                    break;
                case EmitEventProcessNode emission:
                    AddExternalInteraction(emission, emission.Contract.Definition);
                    break;
                case SendSignalProcessNode signal:
                    AddExternalInteraction(signal, signal.Contract.Definition);
                    break;
                case ReplyProcessNode reply:
                    AddExternalInteraction(reply, reply.Contract.Definition);
                    break;
                case AwaitMatchProcessNode wait:
                    AddEffect(wait, ProcessEffectKind.DurableWait);
                    foreach (var clause in wait.Clauses)
                    {
                        if (clause is ProcessAwaitInteractionClause interaction)
                        {
                            AddResource(
                                wait,
                                interaction.Contract.Definition,
                                ProcessResourceAccessKind.Coordinate);
                        }
                    }
                    break;
                case TimerProcessNode timer:
                    AddEffect(timer, ProcessEffectKind.DurableWait);
                    break;
                case DurableCutProcessNode durableCut:
                    AddEffect(durableCut, ProcessEffectKind.DurableWait);
                    break;
                case ForkProcessNode fork:
                    AddEffect(fork, ProcessEffectKind.BoundedParallelWork);
                    break;
                case RepeatAcrossActivationProcessNode recurrence:
                    AddEffect(recurrence, ProcessEffectKind.DurableWait);
                    AddEffect(recurrence, ProcessEffectKind.Recurrence);
                    break;
            }
        }

        effects.Sort(CompareEffects);
        resources.Sort(CompareResources);
        return new(effects.ToImmutable(), resources.ToImmutable());

        void AddExternalInteraction(CanonicalProcessNode node, ExecutionDefinitionReference contract)
        {
            AddEffect(node, ProcessEffectKind.ExternalInteraction);
            AddResource(node, contract, ProcessResourceAccessKind.Coordinate);
        }

        void AddEffect(CanonicalProcessNode node, ProcessEffectKind kind) => effects.Add(new(node.Id, kind));

        void AddResource(
            CanonicalProcessNode node,
            ExecutionDefinitionReference resource,
            ProcessResourceAccessKind access) => resources.Add(new(node.Id, resource, access));
    }

    static int CompareEffects(ProcessEffectSite left, ProcessEffectSite right)
    {
        var comparison = StringComparer.Ordinal.Compare(left.Node.Value, right.Node.Value);
        return comparison != 0 ? comparison : left.Kind.CompareTo(right.Kind);
    }

    static int CompareResources(ProcessResourceRequirement? left, ProcessResourceRequirement? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var comparison = StringComparer.Ordinal.Compare(left.Node.Value, right.Node.Value);
        if (comparison != 0)
            return comparison;
        comparison = ExecutionDefinitionReference.CompareCanonical(left.Resource, right.Resource);
        return comparison != 0 ? comparison : left.Access.CompareTo(right.Access);
    }
}

/// <summary>Structural preflight of explicit scope demands against the canonical derived effect summary.</summary>
internal static class ProcessScopeAnalyzer
{
    internal const string Stage = "processScopeAnalysis";

    internal static DocumentValidationResult Validate(
        ExecutionDefinitionDocument document,
        CanonicalProcessDefinition definition,
        ProcessEffectSummary summary,
        ProcessCompilationOptions options)
    {
        if (options.AtomicScope == ProcessAtomicScopeDemand.None)
            return DocumentValidationResult.Valid;

        List<DocumentValidationDiagnostic> diagnostics = [];
        Dictionary<ExecutionNodeId, int> nodeIndexes = [];
        for (var index = 0; index < definition.Nodes.Length; index++)
            nodeIndexes[definition.Nodes[index].Id] = index;

        HashSet<ExecutionNodeId> durableNodes = [];
        HashSet<ExecutionNodeId> externalNodes = [];
        foreach (var site in summary.Effects)
        {
            if (site.Kind == ProcessEffectKind.DurableWait)
                durableNodes.Add(site.Node);
            else if (site.Kind == ProcessEffectKind.ExternalInteraction)
                externalNodes.Add(site.Node);
        }

        foreach (var node in durableNodes.OrderBy(static id => id.Value, StringComparer.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(
                ProcessCompilationDiagnosticCodes.AtomicScopeCrossesDurableBoundary,
                "Whole-definition atomicity cannot span a durable wait or activation boundary.",
                node,
                nodeIndexes,
                document,
                summary,
                observed: "durable wait or cut"));
        }

        foreach (var node in externalNodes.OrderBy(static id => id.Value, StringComparer.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(
                ProcessCompilationDiagnosticCodes.AtomicScopeContainsExternalInteraction,
                "Whole-definition atomicity cannot include an external interaction, child Process invocation, or "
                + "host operation that may emit an interaction.",
                node,
                nodeIndexes,
                document,
                summary,
                observed: "external interaction or emission-capable host operation"));
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static DocumentValidationDiagnostic CreateDiagnostic(
        string code,
        string message,
        ExecutionNodeId node,
        IReadOnlyDictionary<ExecutionNodeId, int> nodeIndexes,
        ExecutionDefinitionDocument document,
        ProcessEffectSummary summary,
        string observed)
    {
        var resources = summary.Resources
            .Where(requirement => requirement.Node == node)
            .Select(static requirement => Describe(requirement.Resource))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reference => reference, StringComparer.Ordinal)
            .ToImmutableArray();
        var location = nodeIndexes.TryGetValue(node, out var index)
            ? $"/definition/nodes/{index.ToString(CultureInfo.InvariantCulture)}"
            : "/definition/nodes";
        var detailedObserved = resources.IsDefaultOrEmpty
            ? observed
            : $"{observed}; exact resources: {string.Join(", ", resources)}";
        return new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(
                stage: Stage,
                subject: node.Value,
                sourceReferences: document.Metadata.SourceMap.ResolveReferences(
                    location,
                    document.Metadata.Provenance.Source.Reference),
                resolutionOptions:
                [
                    "Remove the whole-definition atomic demand and retain per-activation durable commits.",
                    "Move the boundary or external interaction outside the demanded atomic definition."
                ],
                expected: "one uninterrupted whole-definition atomic scope",
                observed: detailedObserved));
    }

    static string Describe(ExecutionDefinitionReference reference) =>
        $"{reference.DefinitionId.Value}@{reference.RevisionId.Value}"
        + $"#{reference.Fingerprint.Algorithm}:{reference.Fingerprint.Canonicalization}:{reference.Fingerprint.Value}";
}
