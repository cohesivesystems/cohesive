using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Processes.IR;

/// <summary>Shared semantic view of one single-obligation Request-bearing Process node.</summary>
/// <remarks>
/// This view is derived from canonical nodes and is not an independently persisted authority. Consumers use it to
/// keep Request wait, inbox, outbox, Reply, and durable-operation behavior identical for ordinary Requests and
/// child Process invocation.
/// </remarks>
public readonly struct ProcessRequestSemanticView
{
    internal ProcessRequestSemanticView(
        RequestContractReference contract,
        Expr payload,
        ImmutableArray<ProcessRequestOutcomeBranch> outcomes,
        bool isChildProcess,
        ExecutionDefinitionReference? childProcess,
        ProcessChildOutcomeMapping? childOutcomeMapping,
        ProcessChildPurpose childPurpose,
        ProcessChildCancellationPolicy childCancellation)
    {
        Contract = contract;
        Payload = payload;
        Outcomes = outcomes;
        IsChildProcess = isChildProcess;
        ChildProcess = childProcess;
        ChildOutcomeMapping = childOutcomeMapping;
        ChildPurpose = childPurpose;
        ChildCancellation = childCancellation;
    }

    /// <summary>Exact Request contract.</summary>
    public RequestContractReference Contract { get; }

    /// <summary>Portable typed Request payload expression.</summary>
    public Expr Payload { get; }

    /// <summary>Normalized terminal Request outcome continuations.</summary>
    public ImmutableArray<ProcessRequestOutcomeBranch> Outcomes { get; }

    /// <summary>
    /// Exact child Process definition supplied by a child invocation, or null for an ordinary Request.
    /// </summary>
    public ExecutionDefinitionReference? ChildProcess { get; }

    /// <summary>Authored child terminal-to-Request outcome mapping, or null for an ordinary Request.</summary>
    public ProcessChildOutcomeMapping? ChildOutcomeMapping { get; }

    /// <summary>Explicit child purpose, or <see cref="ProcessChildPurpose.Unspecified"/> for an ordinary Request.</summary>
    public ProcessChildPurpose ChildPurpose { get; }

    /// <summary>
    /// Explicit child cancellation behavior, or <see cref="ProcessChildCancellationPolicy.Unspecified"/> for an
    /// ordinary Request.
    /// </summary>
    public ProcessChildCancellationPolicy ChildCancellation { get; }

    /// <summary>Whether this semantic view was projected from a child Process node, independent of reference validity.</summary>
    public bool IsChildProcess { get; }
}

internal enum ProcessChildRequestMultiplicity
{
    Single = 1,
    Partitioned = 2
}

internal readonly record struct ProcessChildSemanticView(
    ExecutionDefinitionReference Process,
    RequestContractReference Contract,
    ProcessChildOutcomeMapping OutcomeMapping,
    ProcessChildPurpose Purpose,
    ProcessChildCancellationPolicy Cancellation,
    ProcessChildRequestMultiplicity Multiplicity);

/// <summary>
/// Projects every single-obligation Request-bearing Process node onto the shared Request semantic contract.
/// </summary>
/// <remarks>
/// This is the single shared authority used by validation and interpretations so child Process invocation retains
/// the same Request identity, wait, Reply, inbox, outbox, and durable-operation behavior as an ordinary Request node.
/// Bounded partition work owns multiple Request occurrences and is therefore deliberately not projected here.
/// </remarks>
public static class ProcessRequestSemantics
{
    /// <summary>Gets self-contained child Process start and terminal-mapping semantics from a child-bearing node.</summary>
    /// <param name="node">Canonical Process node to inspect.</param>
    /// <param name="process">Receives the exact pinned child Process definition.</param>
    /// <param name="outcomeMapping">Receives the authored total terminal-to-Request outcome mapping.</param>
    /// <returns>
    /// <see langword="true"/> for a direct or partitioned child node whose required members are present; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    public static bool TryGetChildTarget(
        ProcessNode node,
        out ExecutionDefinitionReference process,
        out ProcessChildOutcomeMapping outcomeMapping)
    {
        if (TryProjectChild(node, out var child))
        {
            process = child.Process;
            outcomeMapping = child.OutcomeMapping;
            return true;
        }

        process = null!;
        outcomeMapping = null!;
        return false;
    }

    internal static bool TryProjectChild(
        ProcessNode node,
        out ProcessChildSemanticView child)
    {
        ArgumentNullException.ThrowIfNull(node);
        switch (node)
        {
            case InvokeProcessProcessNode invocation
                when invocation.Process is not null
                     && invocation.Contract is not null
                     && invocation.OutcomeMapping is not null:
                child = new(
                    invocation.Process,
                    invocation.Contract,
                    invocation.OutcomeMapping,
                    invocation.Purpose,
                    invocation.Cancellation,
                    ProcessChildRequestMultiplicity.Single);
                return true;
            case ForEachPartitionProcessNode partition
                when partition.Process is not null
                     && partition.Contract is not null
                     && partition.OutcomeMapping is not null:
                child = new(
                    partition.Process,
                    partition.Contract,
                    partition.OutcomeMapping,
                    ProcessChildPurpose.Work,
                    partition.Cancellation,
                    ProcessChildRequestMultiplicity.Partitioned);
                return true;
            case CancellationFinalizerProcessNode finalizer
                when finalizer.Process is not null
                     && finalizer.Contract is not null
                     && finalizer.OutcomeMapping is not null:
                child = new(
                    finalizer.Process,
                    finalizer.Contract,
                    finalizer.OutcomeMapping,
                    ProcessChildPurpose.Compensation,
                    ProcessChildCancellationPolicy.Propagate,
                    ProcessChildRequestMultiplicity.Single);
                return true;
            default:
                child = default;
                return false;
        }
    }

    /// <summary>Gets the exact Request contract carried by any canonical Request-bearing Process node.</summary>
    /// <param name="node">Canonical Process node to inspect.</param>
    /// <param name="contract">
    /// Exact Request contract for an ordinary Request, direct child invocation, or bounded partition child Request.
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="node"/> carries one or more Request obligations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Unlike <see cref="TryProject"/>, this narrow projection includes <see cref="ForEachPartitionProcessNode"/>
    /// because consumers that validate contracts do not need the single-obligation outcome-branch shape.
    /// </remarks>
    public static bool TryGetContract(
        ProcessNode node,
        out RequestContractReference contract)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (TryProject(node, out var semantics) && semantics.Contract is not null)
        {
            contract = semantics.Contract;
            return true;
        }
        if (node is ForEachPartitionProcessNode { Contract: not null } partition)
        {
            contract = partition.Contract;
            return true;
        }
        if (node is CancellationFinalizerProcessNode { Contract: not null } finalizer)
        {
            contract = finalizer.Contract;
            return true;
        }

        contract = null!;
        return false;
    }

    /// <summary>Projects an ordinary or child Process Request node onto their shared Request semantics.</summary>
    /// <param name="node">Canonical Process node to inspect.</param>
    /// <param name="semantics">Derived Request semantics when the node carries one single Request obligation.</param>
    /// <returns>
    /// <see langword="true"/> for a <see cref="RequestProcessNode"/> or <see cref="InvokeProcessProcessNode"/> whose
    /// required Request and child members are present; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    public static bool TryProject(
        ProcessNode node,
        out ProcessRequestSemanticView semantics)
    {
        if (!TryProjectDeclaredVariant(node, out semantics)
            || semantics.Contract is null
            || semantics.Payload is null
            || semantics.IsChildProcess
            && (semantics.ChildProcess is null || semantics.ChildOutcomeMapping is null))
        {
            semantics = default;
            return false;
        }

        return true;
    }

    internal static bool TryProjectDeclaredVariant(
        ProcessNode node,
        out ProcessRequestSemanticView semantics)
    {
        ArgumentNullException.ThrowIfNull(node);
        switch (node)
        {
            case RequestProcessNode request:
                semantics = new(
                    request.Contract,
                    request.Payload,
                    request.Outcomes,
                    isChildProcess: false,
                    childProcess: null,
                    childOutcomeMapping: null,
                    childPurpose: ProcessChildPurpose.Unspecified,
                    childCancellation: ProcessChildCancellationPolicy.Unspecified);
                return true;
            case InvokeProcessProcessNode child:
                semantics = new(
                    child.Contract,
                    child.Input,
                    child.Outcomes,
                    isChildProcess: true,
                    child.Process,
                    child.OutcomeMapping,
                    child.Purpose,
                    child.Cancellation);
                return true;
            default:
                semantics = default;
                return false;
        }
    }

    internal static ImmutableArray<ProcessRequestOutcomeBranch> NormalizeOutcomes(
        ImmutableArray<ProcessRequestOutcomeBranch> outcomes) =>
        ProcessIrCollections.NormalizeSet(outcomes, CompareOutcomeBranches);

    internal static int CompareOutcomeBranches(
        ProcessRequestOutcomeBranch? left,
        ProcessRequestOutcomeBranch? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var comparison = StringComparer.Ordinal.Compare(left.Outcome.Value, right.Outcome.Value);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
    }
}
