using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Cohesive.Model;

// Prepared once by an explicitly layout-bound materializer. The original graph/validation plans
// own semantics; these private layouts are only token dispatch, storage order and lookup evidence.
internal sealed class ObservationJsonIngestionPlan
{
    readonly Dictionary<TypeRef, ObservationLayout> layouts;
    internal ObservationValidationPlan?[] ValidationPlans { get; }

    ObservationJsonIngestionPlan(Dictionary<TypeRef, ObservationLayout> layouts, ObservationValidationPlan?[] plans)
    { this.layouts = layouts; ValidationPlans = plans; }

    internal bool TryGetLayout(TypeRef type, [NotNullWhen(true)] out ObservationLayout? layout) => layouts.TryGetValue(type, out layout);

    internal static ObservationJsonIngestionPlan? Create(ObservationLayout root)
    {
        GraphShapeId owner = new(root.Graph, root.ShapeId.ShapeId);
        var definition = root.Graph.GetShape(owner.ShapeId);
        var plans = new ObservationValidationPlan?[definition.Fields.Length];
        var visited = new HashSet<ObservationValidationPlan>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<ObservationValidationPlan>();
        Dictionary<TypeRef, ObservationLayout> layouts = new(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < plans.Length; index++)
        {
            var type = definition.Fields[index].Type;
            if (type is not (ObjectTypeRef or ArrayTypeRef or NamedTypeRef or EnumTypeRef)) continue;
            plans[index] = ObservationValidationPlan.Get(type, root.Graph);
            pending.Enqueue(plans[index]!);
        }
        while (pending.TryDequeue(out var plan))
        {
            if (!visited.Add(plan)) continue;
            foreach (var child in plan.Children) if (child is not null) pending.Enqueue(child);
            ImmutableArray<FieldDefinition> fields;
            if (plan.Type is ObjectTypeRef obj)
                fields = [.. obj.Fields.Select(f => new FieldDefinition(new(f.Name), f.Type, f.Cardinality, f.Presence, f.Nullability))];
            else if (plan.Definition is TypeDefinition.Structural structural)
                fields = [.. structural.Fields.Select(f => new FieldDefinition(f.Name, f.Type, f.Cardinality, f.Presence, f.Nullability))];
            else continue;
            // Small records retain the existing inline-field reader: complete freight DTO
            // measurements did not justify their extra physical-view cost. Bind only beyond
            // that existing storage boundary, where sparse admission wins are demonstrated.
            if (fields.Length <= SmallObservationFields.JsonFieldLimit) continue;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unambiguous = true;
            foreach (var field in fields) if (!names.Add(field.Name.Value)) { unambiguous = false; break; }
            if (!unambiguous) continue;
            layouts.Add(plan.Type, ObservationLayout.CreateNested(owner, fields));
        }
        if (layouts.Count == 0) return null;
        for (var index = 0; index < plans.Length; index++)
            plans[index] = plans[index]?.BindLayouts(root.Graph, layouts);
        return new(layouts, plans);
    }
}
