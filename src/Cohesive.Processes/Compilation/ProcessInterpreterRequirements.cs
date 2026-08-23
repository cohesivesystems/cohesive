using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Compilation;

/// <summary>Semantic family of one target-neutral Process interpreter requirement.</summary>
public enum ProcessInterpreterRequirementCategory
{
    /// <summary>No requirement family was declared; invalid in an interpreter inventory or profile.</summary>
    Unknown = 0,

    /// <summary>A concrete canonical Process node kind must be interpreted.</summary>
    Construct = 1,

    /// <summary>A cross-cutting Process guarantee must be preserved.</summary>
    Guarantee = 2
}

/// <summary>Stable key shared by a Process requirement, target capability assertion, and realization decision.</summary>
public readonly record struct ProcessInterpreterRequirementKey
{
    /// <summary>Creates a Process interpreter requirement key.</summary>
    /// <param name="category">Semantic requirement family.</param>
    /// <param name="name">Stable canonical construct discriminator or guarantee identity.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is unknown or unsupported.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    [JsonConstructor]
    public ProcessInterpreterRequirementKey(ProcessInterpreterRequirementCategory category, string name)
    {
        if (!Enum.IsDefined(category) || category == ProcessInterpreterRequirementCategory.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(category),
                category,
                "A Process interpreter requirement requires a declared category.");
        }

        Category = category;
        Name = Guard.RequireNotNullOrWhiteSpace(name);
    }

    /// <summary>Semantic requirement family.</summary>
    public ProcessInterpreterRequirementCategory Category { get; }

    /// <summary>Stable canonical construct discriminator or guarantee identity.</summary>
    public string Name { get; }

    /// <summary>Creates a key for one canonical Process node discriminator.</summary>
    /// <param name="wireName">Stable discriminator owned by canonical Process IR.</param>
    /// <returns>A construct requirement key.</returns>
    public static ProcessInterpreterRequirementKey ForConstruct(string wireName) =>
        new(ProcessInterpreterRequirementCategory.Construct, wireName);

    /// <summary>Creates a key for one cross-cutting Process guarantee.</summary>
    /// <param name="name">Stable guarantee identity.</param>
    /// <returns>A guarantee requirement key.</returns>
    public static ProcessInterpreterRequirementKey ForGuarantee(string name) =>
        new(ProcessInterpreterRequirementCategory.Guarantee, name);

    /// <inheritdoc />
    public override string ToString() => $"{Category.ToString().ToLowerInvariant()}:{Name}";
}

/// <summary>Canonical target-neutral Process interpreter guarantee catalog.</summary>
/// <remarks>
/// These values identify compiler-derived requirements. They do not describe any target strategy and remain
/// independent of Durable Task, native Process stores, or another physical runtime.
/// </remarks>
public static class ProcessInterpreterGuarantees
{
    /// <summary>Bind execution and replay to one exact canonical definition reference.</summary>
    public static ProcessInterpreterRequirementKey ExactDefinitionPinning { get; } = Guarantee("exactDefinitionPinning");

    /// <summary>Preserve stable instance, attempt, activation, token, node, occurrence, and interaction identities.</summary>
    public static ProcessInterpreterRequirementKey StableExecutionIdentity { get; } = Guarantee("stableExecutionIdentity");

    /// <summary>Reproduce canonical finite decisions from retained observations without ambient nondeterminism.</summary>
    public static ProcessInterpreterRequirementKey DeterministicReplay { get; } = Guarantee("deterministicReplay");

    /// <summary>Retain authored admission and terminal dispositions for external Process inputs.</summary>
    public static ProcessInterpreterRequirementKey InputAdmissionAndDisposition { get; } = Guarantee("inputAdmissionAndDisposition");

    /// <summary>Preserve canonical inspection, signal, pause, continue, restart, cancellation, and termination semantics.</summary>
    public static ProcessInterpreterRequirementKey LifecycleControl { get; } = Guarantee("lifecycleControl");

    /// <summary>Preserve durable Request dispatch, recovery, arbitration, reconciliation, and terminal obligations.</summary>
    public static ProcessInterpreterRequirementKey DurableRequestRecovery { get; } = Guarantee("durableRequestRecovery");

    /// <summary>Preserve external-effect identity and require idempotency or authored reconciliation when delivery repeats.</summary>
    public static ProcessInterpreterRequirementKey ExternalEffectDelivery { get; } = Guarantee("externalEffectDelivery");

    /// <summary>Preserve fork membership, join decisions, child identity, cancellation, and outcome lineage.</summary>
    public static ProcessInterpreterRequirementKey ForkJoinChildLineage { get; } = Guarantee("forkJoinChildLineage");

    /// <summary>Enforce authored finite item, concurrency, capacity, recurrence, and progress boundaries.</summary>
    public static ProcessInterpreterRequirementKey BoundedWorkAndRecurrence { get; } = Guarantee("boundedWorkAndRecurrence");

    /// <summary>Compose canonical definition compatibility with target worker or orchestration evolution.</summary>
    public static ProcessInterpreterRequirementKey DefinitionAndWorkerEvolution { get; } = Guarantee("definitionAndWorkerEvolution");

    /// <summary>Project normalized status, trace, and explain evidence with definition and realization provenance.</summary>
    public static ProcessInterpreterRequirementKey StatusTraceAndExplain { get; } = Guarantee("statusTraceAndExplain");

    /// <summary>Preserve payload contracts while validating limits, redaction, and exact externalization.</summary>
    public static ProcessInterpreterRequirementKey SensitiveAndOversizedPayloads { get; } = Guarantee("sensitiveAndOversizedPayloads");

    /// <summary>Preserve an explicitly requested whole-definition atomic scope.</summary>
    public static ProcessInterpreterRequirementKey WholeDefinitionAtomicity { get; } = Guarantee("wholeDefinitionAtomicity");

    /// <summary>Every guarantee that a compiled Process inventory can currently demand.</summary>
    public static ImmutableArray<ProcessInterpreterRequirementKey> All { get; } =
    [
        ExactDefinitionPinning,
        StableExecutionIdentity,
        DeterministicReplay,
        InputAdmissionAndDisposition,
        LifecycleControl,
        DurableRequestRecovery,
        ExternalEffectDelivery,
        ForkJoinChildLineage,
        BoundedWorkAndRecurrence,
        DefinitionAndWorkerEvolution,
        StatusTraceAndExplain,
        SensitiveAndOversizedPayloads,
        WholeDefinitionAtomicity
    ];

    /// <summary>Whether a key names one guarantee in the current compiler-owned catalog.</summary>
    /// <param name="key">Requirement key to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="key"/> is a declared guarantee.</returns>
    public static bool Contains(ProcessInterpreterRequirementKey key) =>
        key.Category == ProcessInterpreterRequirementCategory.Guarantee && All.Contains(key);

    static ProcessInterpreterRequirementKey Guarantee(string name) =>
        ProcessInterpreterRequirementKey.ForGuarantee(name);
}

/// <summary>Canonical Process node-kind catalog projected from the persisted closed union metadata.</summary>
/// <remarks>
/// The <see cref="JsonDerivedTypeAttribute"/> declarations on <see cref="ProcessNode"/> remain the single authority.
/// The catalog caches their projection so inventory acquisition does not maintain another node enum or switch table.
/// </remarks>
public static class ProcessNodeConstructCatalog
{
    static readonly ImmutableDictionary<Type, ProcessInterpreterRequirementKey> ByRuntimeType = CreateCatalog();

    /// <summary>Canonical runtime node types declared by the persisted closed union.</summary>
    internal static ImmutableArray<Type> DeclaredRuntimeTypes { get; } =
        [.. ByRuntimeType.Keys.OrderBy(static type => type.FullName, StringComparer.Ordinal)];

    /// <summary>All declared canonical node kinds in deterministic wire-name order.</summary>
    public static ImmutableArray<ProcessInterpreterRequirementKey> DeclaredRequirements { get; } =
        [.. ByRuntimeType.Values.OrderBy(static key => key.Name, StringComparer.Ordinal)];

    /// <summary>Returns the canonical construct key for one Process node.</summary>
    /// <param name="node">Canonical node from a compiled Process plan.</param>
    /// <returns>The key projected from the node's persisted union discriminator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The runtime node type is absent from the canonical persisted union metadata.
    /// </exception>
    public static ProcessInterpreterRequirementKey GetRequirement(ProcessNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return ByRuntimeType.TryGetValue(node.GetType(), out var requirement)
            ? requirement
            : throw new InvalidOperationException(
                $"Canonical Process node type '{node.GetType().FullName}' has no declared persisted discriminator.");
    }

    /// <summary>Whether a requirement key names one currently declared canonical Process node kind.</summary>
    /// <param name="key">Requirement key to inspect.</param>
    /// <returns><see langword="true"/> when the key belongs to the canonical closed node union.</returns>
    public static bool Contains(ProcessInterpreterRequirementKey key) =>
        key.Category == ProcessInterpreterRequirementCategory.Construct && DeclaredRequirements.Contains(key);

    static ImmutableDictionary<Type, ProcessInterpreterRequirementKey> CreateCatalog()
    {
        var attributes = typeof(ProcessNode).GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false);
        Dictionary<Type, ProcessInterpreterRequirementKey> byType = [];
        HashSet<string> discriminators = new(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (attribute.TypeDiscriminator is not string discriminator || string.IsNullOrWhiteSpace(discriminator))
            {
                throw new InvalidOperationException(
                    "Every canonical Process node variant must declare a non-empty string discriminator.");
            }
            if (!typeof(ProcessNode).IsAssignableFrom(attribute.DerivedType))
            {
                throw new InvalidOperationException(
                    $"Declared Process node variant '{attribute.DerivedType.FullName}' does not derive from ProcessNode.");
            }
            if (!byType.TryAdd(attribute.DerivedType, ProcessInterpreterRequirementKey.ForConstruct(discriminator)))
            {
                throw new InvalidOperationException(
                    $"Canonical Process node type '{attribute.DerivedType.FullName}' is declared more than once.");
            }
            if (!discriminators.Add(discriminator))
            {
                throw new InvalidOperationException($"Canonical Process node discriminator '{discriminator}' is duplicated.");
            }
        }

        if (byType.Count == 0)
        {
            throw new InvalidOperationException("The canonical Process node union declares no variants.");
        }

        return byType.ToImmutableDictionary();
    }
}

/// <summary>One exact compiler-derived Process interpreter requirement with source evidence.</summary>
public sealed record ProcessInterpreterRequirement
{
    internal ProcessInterpreterRequirement(
        ProcessInterpreterRequirementKey key,
        ImmutableArray<ExecutionNodeId> nodes,
        ImmutableArray<ExecutionDefinitionReference> linkedDefinitions)
    {
        Key = key;
        Nodes = nodes;
        LinkedDefinitions = linkedDefinitions;
    }

    /// <summary>Stable requirement key used to match target capability evidence.</summary>
    public ProcessInterpreterRequirementKey Key { get; }

    /// <summary>Canonical source nodes requiring this capability, in stable identity order.</summary>
    public ImmutableArray<ExecutionNodeId> Nodes { get; }

    /// <summary>Exact linked definition and interaction-contract evidence relevant to the requirement.</summary>
    public ImmutableArray<ExecutionDefinitionReference> LinkedDefinitions { get; }

    /// <summary>Compares requirements by complete normalized source evidence.</summary>
    /// <param name="other">Requirement to compare.</param>
    /// <returns><see langword="true"/> when keys, source nodes, and linked definitions are equal.</returns>
    public bool Equals(ProcessInterpreterRequirement? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Key == other.Key
        && Nodes.SequenceEqual(other.Nodes)
        && LinkedDefinitions.SequenceEqual(other.LinkedDefinitions);

    /// <summary>Returns a structural hash for the complete requirement.</summary>
    /// <returns>A hash derived from the key and all normalized source evidence.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Key);
        foreach (var node in Nodes)
        {
            hash.Add(node);
        }

        foreach (var definition in LinkedDefinitions)
        {
            hash.Add(definition);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Complete requirement inventory acquired from one exact compiled canonical Process plan.</summary>
public sealed class ProcessInterpreterRequirementInventory
{
    internal ProcessInterpreterRequirementInventory(
        ExecutionDefinitionReference definition,
        ImmutableArray<ProcessInterpreterRequirement> requirements)
    {
        Definition = definition;
        Requirements = requirements;
    }

    /// <summary>Exact canonical Process definition from which the inventory was acquired.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Every demanded construct and guarantee in deterministic key order.</summary>
    public ImmutableArray<ProcessInterpreterRequirement> Requirements { get; }
}

/// <summary>Acquires the complete target-neutral interpreter requirement inventory for a compiled Process plan.</summary>
public static class ProcessInterpreterRequirementCollector
{
    /// <summary>Derives every concrete construct and applicable cross-cutting guarantee from an exact plan.</summary>
    /// <param name="plan">Successfully compiled canonical Process plan.</param>
    /// <returns>A complete, deterministically ordered requirement inventory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A node is absent from the canonical persisted union catalog.
    /// </exception>
    public static ProcessInterpreterRequirementInventory Collect(CompiledProcessPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<ProcessInterpreterRequirementKey, MutableRequirement> requirements = [];
        Dictionary<ExecutionNodeId, ImmutableArray<ExecutionDefinitionReference>> linksByNode = [];
        foreach (var grouping in plan.EffectSummary.Resources.GroupBy(static resource => resource.Node))
        {
            linksByNode.Add(
                grouping.Key,
                [.. grouping.Select(static resource => resource.Resource).Distinct().OrderBy(
                    static reference => reference,
                    ExecutionDefinitionReferenceComparer.Instance)]);
        }

        foreach (var node in plan.Definition.Nodes)
        {
            Add(ProcessNodeConstructCatalog.GetRequirement(node), [node.Id]);
        }

        var allNodes = plan.Definition.Nodes.Select(static node => node.Id).ToImmutableArray();
        Add(ProcessInterpreterGuarantees.ExactDefinitionPinning, allNodes);
        Add(ProcessInterpreterGuarantees.StableExecutionIdentity, allNodes);
        Add(ProcessInterpreterGuarantees.DeterministicReplay, allNodes);
        Add(ProcessInterpreterGuarantees.LifecycleControl, allNodes);
        Add(ProcessInterpreterGuarantees.DefinitionAndWorkerEvolution, allNodes);
        Add(ProcessInterpreterGuarantees.StatusTraceAndExplain, allNodes);
        Add(ProcessInterpreterGuarantees.SensitiveAndOversizedPayloads, allNodes);

        AddWhen(
            ProcessInterpreterGuarantees.InputAdmissionAndDisposition,
            plan.Definition.Nodes.Where(static node => node is RequestProcessNode
                or AwaitMatchProcessNode
                or InvokeProcessProcessNode
                or ForEachPartitionProcessNode
                or CancellationFinalizerProcessNode));
        AddWhen(
            ProcessInterpreterGuarantees.DurableRequestRecovery,
            plan.Definition.Nodes.Where(static node => node is RequestProcessNode
                or InvokeProcessProcessNode
                or ForEachPartitionProcessNode
                or CancellationFinalizerProcessNode));

        var externalNodes = plan.EffectSummary.Effects
            .Where(static effect => effect.Kind == ProcessEffectKind.ExternalInteraction)
            .Select(static effect => effect.Node)
            .Distinct()
            .ToImmutableArray();
        if (!externalNodes.IsDefaultOrEmpty)
        {
            Add(ProcessInterpreterGuarantees.ExternalEffectDelivery, externalNodes);
        }

        AddWhen(
            ProcessInterpreterGuarantees.ForkJoinChildLineage,
            plan.Definition.Nodes.Where(static node => node is ForkProcessNode
                or JoinProcessNode
                or InvokeProcessProcessNode
                or ForEachPartitionProcessNode
                or CancellationFinalizerProcessNode));
        AddWhen(
            ProcessInterpreterGuarantees.BoundedWorkAndRecurrence,
            plan.Definition.Nodes.Where(static node => node is ForkProcessNode
                or ForEachPartitionProcessNode
                or RepeatAcrossActivationProcessNode));

        if (plan.Options.AtomicScope == ProcessAtomicScopeDemand.WholeDefinition)
        {
            Add(ProcessInterpreterGuarantees.WholeDefinitionAtomicity, allNodes);
        }

        var normalized = requirements.Values
            .Select(static requirement => requirement.Freeze())
            .OrderBy(static requirement => requirement.Key.Category)
            .ThenBy(static requirement => requirement.Key.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        return new(plan.DefinitionReference, normalized);

        void AddWhen(ProcessInterpreterRequirementKey key, IEnumerable<ProcessNode> nodes)
        {
            var nodeIds = nodes.Select(static node => node.Id).ToImmutableArray();
            if (!nodeIds.IsDefaultOrEmpty)
            {
                Add(key, nodeIds);
            }
        }

        void Add(ProcessInterpreterRequirementKey key, IEnumerable<ExecutionNodeId> nodes)
        {
            if (!requirements.TryGetValue(key, out var requirement))
            {
                requirement = new(key);
                requirements.Add(key, requirement);
            }

            foreach (var node in nodes)
            {
                requirement.Nodes.Add(node);
                if (linksByNode.TryGetValue(node, out var definitions))
                {
                    foreach (var definition in definitions)
                    {
                        requirement.LinkedDefinitions.Add(definition);
                    }
                }
            }
        }
    }

    sealed class MutableRequirement(ProcessInterpreterRequirementKey key)
    {
        internal ProcessInterpreterRequirementKey Key { get; } = key;

        internal HashSet<ExecutionNodeId> Nodes { get; } = [];

        internal HashSet<ExecutionDefinitionReference> LinkedDefinitions { get; } = [];

        internal ProcessInterpreterRequirement Freeze() => new(
            Key,
            [.. Nodes.OrderBy(static node => node.Value, StringComparer.Ordinal)],
            [.. LinkedDefinitions.OrderBy(
                static definition => definition,
                ExecutionDefinitionReferenceComparer.Instance)]);
    }

    sealed class ExecutionDefinitionReferenceComparer : IComparer<ExecutionDefinitionReference>
    {
        internal static ExecutionDefinitionReferenceComparer Instance { get; } = new();

        public int Compare(ExecutionDefinitionReference? left, ExecutionDefinitionReference? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return ExecutionDefinitionReference.CompareCanonical(left, right);
        }
    }
}
