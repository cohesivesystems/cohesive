using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Processes.Compilation;

/// <summary>Successful target-independent compilation plan for one exact canonical Process document.</summary>
/// <remarks>
/// The plan is an immutable executable index over canonical IR. It does not copy or replace the persisted
/// definition authority and contains no runtime state or infrastructure policy.
/// </remarks>
public sealed class CompiledProcessPlan
{
    readonly IReadOnlyDictionary<ExecutionNodeId, CanonicalProcessNode> nodes;

    internal CompiledProcessPlan(
        ExecutionDefinitionDocument document,
        CanonicalProcessDefinition definition,
        ProcessDefinitionValidationContext validationContext,
        ProcessCompilationOptions options,
        ProcessEffectSummary effectSummary)
    {
        Document = document;
        Definition = definition;
        ValidationContext = validationContext;
        Options = options;
        EffectSummary = effectSummary;
        nodes = definition.Nodes.ToDictionary(static node => node.Id);
    }

    /// <summary>Exact fingerprinted Process definition document.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Canonical typed Process definition projected from <see cref="Document"/>.</summary>
    public CanonicalProcessDefinition Definition { get; }

    /// <summary>Exact definition-link, interaction-contract, and shape evidence used to admit the plan.</summary>
    public ProcessDefinitionValidationContext ValidationContext { get; }

    /// <summary>
    /// Explicit demands retained after target-independent structural preflight for downstream realization proof.
    /// </summary>
    public ProcessCompilationOptions Options { get; }

    /// <summary>
    /// Deterministic compiler-derived effects and exact statically referenced resources. An ExternalInteraction
    /// effect may conservatively represent host-operation emissions whose resource set is not statically closed.
    /// </summary>
    public ProcessEffectSummary EffectSummary { get; }

    /// <summary>Exact identity, revision, and semantic fingerprint of the compiled Process.</summary>
    public ExecutionDefinitionReference DefinitionReference => new(
        Document.Metadata.DefinitionId,
        Document.Metadata.RevisionId,
        Document.Metadata.Fingerprint);

    /// <summary>Resolves one validated canonical node by stable identity.</summary>
    /// <param name="id">Stable node identity from this Process definition.</param>
    /// <returns>The canonical node indexed by <paramref name="id"/>.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is not present in this plan.</exception>
    public CanonicalProcessNode GetNode(ExecutionNodeId id) => nodes[id];
}

/// <summary>Result of attempting target-independent Process compilation.</summary>
public sealed class ProcessCompilationResult
{
    internal ProcessCompilationResult(
        ExecutionDefinitionDocument document,
        CanonicalProcessDefinition? definition,
        CompiledProcessPlan? plan,
        DocumentValidationResult validation)
    {
        Document = document;
        Definition = definition;
        Plan = plan;
        Validation = validation;
    }

    /// <summary>Exact supplied execution-definition document.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Typed Process payload when strict projection succeeded.</summary>
    public CanonicalProcessDefinition? Definition { get; }

    /// <summary>Executable plan only when canonical validation and structural guarantee-demand preflight succeed.</summary>
    public CompiledProcessPlan? Plan { get; }

    /// <summary>Deterministically ordered document, linking, expression, graph, and guarantee-demand diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether compilation produced a complete executable plan.</summary>
    public bool IsSuccessful => Plan is not null && Validation.IsValid;
}
