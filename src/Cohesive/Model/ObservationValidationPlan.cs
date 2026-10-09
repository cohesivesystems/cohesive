using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// Prepared links interpret the original immutable declarations. Only compound nodes need slots;
// scalar checks remain direct. A completed reachable closure is published atomically per root.
internal sealed class ObservationValidationPlan
{
    static readonly ConditionalWeakTable<ShapeGraph, GraphPlans> Graphs = new();

    internal TypeDefinition? Definition { get; private set; }
    internal ObservationValidationPlan?[] Children { get; private set; } = [];

    internal static ObservationValidationPlan Get(TypeRef root, ShapeGraph graph) =>
        Graphs.GetValue(graph, static value => new(value)).Get(root);

    sealed class GraphPlans(ShapeGraph graph)
    {
        readonly ConditionalWeakTable<TypeRef, ObservationValidationPlan> nodes = new();
        readonly object gate = new();

        internal ObservationValidationPlan Get(TypeRef root)
        {
            if (nodes.TryGetValue(root, out var plan)) return plan;
            // A recursive closure needs coordinated publication of several linked nodes. Never
            // hold independently locked per-node slots while acquiring another node's slot.
            lock (gate)
            {
                if (nodes.TryGetValue(root, out plan)) return plan;
                return Build(root, graph, nodes);
            }
        }
    }

    static ObservationValidationPlan Build(TypeRef root, ShapeGraph graph,
        ConditionalWeakTable<TypeRef, ObservationValidationPlan> prepared)
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
            if (type is not (ArrayTypeRef or ObjectTypeRef or NamedTypeRef)) return null;
            if (prepared.TryGetValue(type, out var node) || nodes.TryGetValue(type, out node)) return node;
            node = new();
            nodes.Add(type, node);
            pending.Enqueue(type);
            return node;
        }
    }
}
