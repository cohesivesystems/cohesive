using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// Prepared links and metadata interpret the original immutable declarations. Compound and enum
// nodes have slots; scalar checks remain direct. A completed reachable closure is published atomically per root.
internal sealed class ObservationValidationPlan(TypeRef type)
{
    internal TypeRef Type { get; } = type;
    ObservationValidationMetadata? metadata;
    internal ObservationValidationMetadata Metadata =>
        Volatile.Read(ref metadata) ?? InitializeMetadata();

    ObservationValidationMetadata InitializeMetadata()
    {
        var prepared = new ObservationValidationMetadata(Definition ?? (object)Type);
        return Interlocked.CompareExchange(ref metadata, prepared, null) ?? prepared;
    }

    internal ObservationValidationPlan? Child(int index, TypeRef expected)
    {
        var child = Children[index];
        child?.RequireType(expected);
        return child;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RequireType(TypeRef expected)
    {
        if (!ReferenceEquals(Type, expected))
            throw new InvalidOperationException("Validation plan does not belong to the expected type declaration.");
    }

    static readonly ConditionalWeakTable<ShapeGraph, GraphPlans> Graphs = new();

    internal TypeDefinition? Definition { get; private set; }
    internal ObservationValidationPlan?[] Children { get; private set; } = [];

    internal static ObservationValidationPlan Get(TypeRef root, ShapeGraph graph) =>
        Graphs.GetValue(graph, static value => new(value)).Get(root);

    internal static ObservationValidationMetadata GetDefinitionMetadata(TypeDefinition definition, ShapeGraph graph) =>
        Graphs.GetValue(graph, static value => new(value)).GetMetadata(definition);

    // Non-preparing inspection lets regression tests distinguish decoding from closure preparation.
    internal static bool TryGet(TypeRef root, ShapeGraph graph, out ObservationValidationPlan? plan)
    {
        plan = null;
        return Graphs.TryGetValue(graph, out var plans) && plans.TryGet(root, out plan);
    }

    sealed class GraphPlans(ShapeGraph graph)
    {
        readonly ConditionalWeakTable<TypeRef, ObservationValidationPlan> nodes = new();
        readonly object gate = new();
        readonly ConcurrentDictionary<TypeDefinition, ObservationValidationMetadata> definitions = new(ReferenceEqualityComparer.Instance);

        internal bool TryGet(TypeRef root, out ObservationValidationPlan? plan) => nodes.TryGetValue(root, out plan);

        internal ObservationValidationMetadata GetMetadata(TypeDefinition definition)
        {
            if (definitions.TryGetValue(definition, out var metadata)) return metadata;
            if (!graph.TryGetType(definition.Id, out var declared) || !ReferenceEquals(declared, definition))
                throw new InvalidOperationException("Validation metadata requires the graph's exact declaration.");
            return definitions.GetOrAdd(definition, static value => new(value));
        }

        internal ObservationValidationPlan Get(TypeRef root)
        {
            if (nodes.TryGetValue(root, out var plan)) return plan;
            // A recursive closure needs coordinated publication of several linked nodes. Never
            // hold independently locked per-node slots while acquiring another node's slot.
            lock (gate)
            {
                if (nodes.TryGetValue(root, out plan)) return plan;
                return Build(root, graph, nodes, this);
            }
        }
    }

    static ObservationValidationPlan Build(TypeRef root, ShapeGraph graph,
        ConditionalWeakTable<TypeRef, ObservationValidationPlan> prepared,
        GraphPlans plans)
    {
        Dictionary<TypeRef, ObservationValidationPlan> nodes = new(ReferenceEqualityComparer.Instance);
        Queue<TypeRef> pending = new();
        var result = Add(root)!;
        while (pending.TryDequeue(out var type))
        {
            var node = nodes[type];
            switch (type)
            {
                case ArrayTypeRef array:
                    node.Children = [Add(array.ElementType)];
                    break;
                case ObjectTypeRef obj:
                    node.Children = new ObservationValidationPlan?[obj.Fields.Length];
                    for (var i = 0; i < obj.Fields.Length; i++) node.Children[i] = Add(obj.Fields[i].Type);
                    break;
                case NamedTypeRef named:
                    if (graph.TryGetType(named.TypeId, out var definition))
                    {
                        node.Definition = definition;
                        // Different NamedTypeRef objects can resolve to the same declaration. Share
                        // its metadata within the graph without retaining those reference/root nodes.
                        node.metadata = plans.GetMetadata(definition);
                        if (definition is TypeDefinition.Structural structural)
                        {
                            node.Children = new ObservationValidationPlan?[structural.Fields.Length];
                            for (var i = 0; i < structural.Fields.Length; i++) node.Children[i] = Add(structural.Fields[i].Type);
                        }
                        else if (definition is TypeDefinition.Union union)
                        {
                            node.Children = new ObservationValidationPlan?[union.Cases.Length];
                            for (var i = 0; i < union.Cases.Length; i++) node.Children[i] = Add(union.Cases[i].Type);
                        }
                    }
                    break;
            }
        }
        // All new links, including cycles, are complete before any node is visible to a reader.
        // Existing published children are reused across independently requested roots.
        foreach (var entry in nodes) prepared.Add(entry.Key, entry.Value);
        return result;

        ObservationValidationPlan? Add(TypeRef type)
        {
            if (type is not (ArrayTypeRef or ObjectTypeRef or NamedTypeRef or EnumTypeRef)) return null;
            if (prepared.TryGetValue(type, out var node) || nodes.TryGetValue(type, out node)) return node;
            node = new(type);
            nodes.Add(type, node);
            pending.Enqueue(type);
            return node;
        }
    }
}
