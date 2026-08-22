using System.Collections.Immutable;
using System.Reflection;
using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Compilation;

/// <summary>Semantic use made of one exact canonical Request by a Process node.</summary>
public enum ProcessRequestRequirementKind
{
    /// <summary>No Request use was declared; invalid in an acquired requirement.</summary>
    Unknown = 0,

    /// <summary>The Request must be interpreted by an external durable-operation adapter.</summary>
    ExternalOperation = 1,

    /// <summary>The Request is the canonical protocol for a natively interpreted child Process invocation.</summary>
    ChildProcessInvocation = 2
}

/// <summary>One exact Request capability required by a canonical Process node.</summary>
public sealed record ProcessRequestRequirement
{
    internal ProcessRequestRequirement(
        ExecutionNodeId node,
        ProcessRequestRequirementKind kind,
        RequestContractReference request)
    {
        Node = node;
        Kind = kind;
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    /// <summary>Stable canonical Process node that requires the Request capability.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Semantic use that determines how a physical target must realize the Request.</summary>
    public ProcessRequestRequirementKind Kind { get; }

    /// <summary>Exact Request definition identity, revision, and fingerprint that must be realized.</summary>
    public RequestContractReference Request { get; }
}

/// <summary>Complete exact Request requirement inventory acquired from one canonical Process plan.</summary>
public sealed class ProcessRequestRequirementInventory
{
    internal ProcessRequestRequirementInventory(
        ExecutionDefinitionReference definition,
        ImmutableArray<ProcessRequestRequirement> requirements)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Requirements = requirements;
    }

    /// <summary>Exact canonical Process definition from which the inventory was acquired.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Every exact Request requirement in deterministic node-identity order.</summary>
    public ImmutableArray<ProcessRequestRequirement> Requirements { get; }
}

/// <summary>Acquires exact Request capability requirements from canonical Process IR.</summary>
/// <remarks>
/// The canonical node union remains the semantic authority. Acquisition requires an explicit Request or no-Request
/// disposition for every declared node type and cross-checks direct <see cref="RequestContractReference"/>
/// properties. A newly added construct therefore cannot silently escape deployment qualification merely because its
/// Request representation was not recognized.
/// </remarks>
public static class ProcessRequestRequirementCollector
{
    static readonly ImmutableDictionary<Type, ProcessRequestRequirementKind?> Dispositions =
        new Dictionary<Type, ProcessRequestRequirementKind?>
        {
            [typeof(InvokeTransitionProcessNode)] = null,
            [typeof(EvaluateRelationProcessNode)] = null,
            [typeof(RequestProcessNode)] = ProcessRequestRequirementKind.ExternalOperation,
            [typeof(EmitEventProcessNode)] = null,
            [typeof(SendSignalProcessNode)] = null,
            [typeof(ChoiceProcessNode)] = null,
            [typeof(MatchProcessNode)] = null,
            [typeof(ForkProcessNode)] = null,
            [typeof(JoinProcessNode)] = null,
            [typeof(AwaitMatchProcessNode)] = null,
            [typeof(TimerProcessNode)] = null,
            [typeof(ReplyProcessNode)] = null,
            [typeof(DurableCutProcessNode)] = null,
            [typeof(InvokeProcessProcessNode)] = ProcessRequestRequirementKind.ChildProcessInvocation,
            [typeof(ForEachPartitionProcessNode)] = ProcessRequestRequirementKind.ChildProcessInvocation,
            [typeof(RepeatAcrossActivationProcessNode)] = null,
            [typeof(ReturnProcessNode)] = null,
            [typeof(FailProcessNode)] = null
        }.ToImmutableDictionary();

    /// <summary>Collects exact Request requirements in deterministic node-identity order.</summary>
    /// <param name="plan">Successfully compiled canonical Process plan.</param>
    /// <returns>A definition-bound inventory containing one requirement for every Request-bearing node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The persisted node union and the Request disposition table are incomplete or inconsistent.
    /// </exception>
    public static ProcessRequestRequirementInventory Collect(CompiledProcessPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateDispositionCompleteness();

        ImmutableArray<ProcessRequestRequirement> requirements =
        [
            .. plan.Definition.Nodes
                .Select(static node => CreateRequirement(node))
                .Where(static requirement => requirement is not null)
                .Select(static requirement => requirement!)
                .OrderBy(static requirement => requirement.Node.Value, StringComparer.Ordinal)
        ];
        return new(plan.DefinitionReference, requirements);
    }

    static void ValidateDispositionCompleteness()
    {
        var declaredNodeTypes = ProcessNodeConstructCatalog.DeclaredRuntimeTypes.ToImmutableHashSet();
        var disposedNodeTypes = Dispositions.Keys.ToImmutableHashSet();
        var undisposed = declaredNodeTypes.Except(disposedNodeTypes).ToArray();
        var stale = disposedNodeTypes.Except(declaredNodeTypes).ToArray();
        var contradictory = Dispositions
            .Where(static pair => DeclaresRequestContract(pair.Key) != pair.Value.HasValue)
            .Select(static pair => pair.Key)
            .ToArray();
        if (undisposed.Length == 0 && stale.Length == 0 && contradictory.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Canonical Process Request requirement acquisition is incomplete. "
            + $"Undisposed node types: {Format(undisposed)}. "
            + $"Stale dispositions: {Format(stale)}. "
            + $"Dispositions contradicting declared Request contracts: {Format(contradictory)}.");

        static string Format(IEnumerable<Type> types)
        {
            var names = types
                .Select(static type => type.FullName ?? type.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
            return names.Length == 0 ? "none" : string.Join(", ", names);
        }
    }

    static ProcessRequestRequirement? CreateRequirement(ProcessNode node)
    {
        var kind = Dispositions[node.GetType()];
        if (kind is null)
        {
            return null;
        }

        var request = node switch
        {
            RequestProcessNode external => external.Contract,
            InvokeProcessProcessNode child => child.Contract,
            ForEachPartitionProcessNode partition => partition.Contract,
            _ => throw new InvalidOperationException(
                $"Canonical Process node type '{node.GetType().FullName}' has a Request disposition but no "
                + "Request acquisition rule.")
        };
        return new(node.Id, kind.Value, request);
    }

    static bool DeclaresRequestContract(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Any(static property => property.PropertyType == typeof(RequestContractReference));
}
