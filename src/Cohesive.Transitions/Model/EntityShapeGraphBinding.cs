using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Transitions.Model;

/// <summary>Stable diagnostics emitted while linking entity-state validation to a named shape graph.</summary>
public static class EntityShapeGraphDiagnosticCodes
{
    /// <summary>A named entity field has no explicit shape-graph binding.</summary>
    public const string BindingMissing = "entity.shapeGraph.bindingMissing";

    /// <summary>The bound graph snapshot is not the revision named by the qualified root shape.</summary>
    public const string RevisionIncompatible = "entity.shapeGraph.revisionIncompatible";

    /// <summary>The bound graph does not contain the referenced root shape.</summary>
    public const string RootShapeMissing = "entity.shapeGraph.rootShapeMissing";

    /// <summary>The entity root shape differs from the shape in the bound graph snapshot.</summary>
    public const string RootShapeIncompatible = "entity.shapeGraph.rootShapeIncompatible";

    /// <summary>A reachable named type reference cannot be resolved.</summary>
    public const string NamedTypeMissing = "entity.shapeGraph.namedTypeMissing";

    /// <summary>The bound snapshot contains a duplicate shape or named-type identity.</summary>
    public const string DuplicateIdentity = "entity.shapeGraph.duplicateIdentity";

    /// <summary>Reachable named type definitions contain a reference cycle.</summary>
    public const string NamedTypeCycle = "entity.shapeGraph.namedTypeCycle";
}

/// <summary>
/// Exact immutable shape-graph snapshot used to resolve named types while validating one entity shape.
/// </summary>
/// <remarks>
/// <see cref="Shape"/> names the expected graph revision and root shape. <see cref="Document"/> is the
/// authoritative snapshot containing that shape and every transitively referenced named type. Linkage is validated
/// separately so imported invalid documents remain inspectable and produce structured diagnostics.
/// </remarks>
public sealed record EntityShapeGraphBinding
{
    /// <summary>Creates one exact entity shape-graph binding.</summary>
    /// <param name="shape">Graph-qualified entity root shape.</param>
    /// <param name="document">Exact immutable graph document used for named-type resolution.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public EntityShapeGraphBinding(
        QualifiedShapeId shape,
        ShapeGraphDocument document)
    {
        Shape = shape;
        Document = Guard.RequireNotNull(document);
    }

    /// <summary>Graph-qualified entity root shape.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Exact immutable graph document used for named-type resolution.</summary>
    public ShapeGraphDocument Document { get; }
}

/// <summary>Validates exact shape-graph linkage and the reachable named-type closure for entity state.</summary>
public static class EntityShapeGraphValidator
{
    /// <summary>Validates one entity's inline or graph-backed state schema.</summary>
    /// <param name="definition">Entity definition whose state schema is inspected.</param>
    /// <returns>Deterministically ordered structured linkage diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(EntityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var binding = definition.ShapeGraph;
        if (binding is null)
        {
            if (EnumerateNamedTypeReferences(definition.Shape.Fields.Select(static field => field.Type)).Any())
            {
                diagnostics.Add(new(
                    Code: EntityShapeGraphDiagnosticCodes.BindingMissing,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Entity '{definition.Name.Value}' contains named type references but has no exact shape-graph binding.",
                    Location: "/shapeGraph",
                    Evidence: new(
                        stage: "entity-state-shape-linking",
                        subject: definition.Name.Value,
                        resolutionOptions: ["Bind the graph-qualified root shape to its exact ShapeGraphDocument revision."])));
            }

            return Result(diagnostics);
        }

        var document = binding.Document;
        var graph = document.Graph;
        ProjectGraphDiagnostics(
            ShapeGraphDocumentSemanticValidator.Validate(document),
            diagnostics);

        if (binding.Shape.GraphId != graph.Id)
        {
            diagnostics.Add(new(
                Code: EntityShapeGraphDiagnosticCodes.RevisionIncompatible,
                Severity: DiagnosticSeverity.Error,
                Message: $"Entity root shape expects graph revision '{binding.Shape.GraphId.Value}', but the supplied snapshot is '{graph.Id.Value}'.",
                Location: "/shapeGraph/shape/graphId",
                Evidence: new(
                    stage: "entity-state-shape-linking",
                    subject: definition.Name.Value,
                    expected: binding.Shape.GraphId.Value,
                    observed: graph.Id.Value,
                    resolutionOptions: ["Supply the exact graph snapshot named by the qualified entity shape."])));
        }

        if (binding.Shape.ShapeId != definition.Shape.Id)
        {
            diagnostics.Add(new(
                Code: EntityShapeGraphDiagnosticCodes.RootShapeIncompatible,
                Severity: DiagnosticSeverity.Error,
                Message: $"Entity '{definition.Name.Value}' carries shape '{definition.Shape.Id.Value}' but binds root shape '{binding.Shape.ShapeId.Value}'.",
                Location: "/shapeGraph/shape/shapeId",
                Evidence: new(
                    stage: "entity-state-shape-linking",
                    subject: definition.Name.Value,
                    expected: definition.Shape.Id.Value,
                    observed: binding.Shape.ShapeId.Value)));
        }

        if (!graph.TryGetShape(binding.Shape.ShapeId, out var graphShape))
        {
            diagnostics.Add(new(
                Code: EntityShapeGraphDiagnosticCodes.RootShapeMissing,
                Severity: DiagnosticSeverity.Error,
                Message: $"Graph revision '{graph.Id.Value}' does not contain entity root shape '{binding.Shape.ShapeId.Value}'.",
                Location: "/shapeGraph/shape/shapeId",
                Evidence: new(
                    stage: "entity-state-shape-linking",
                    subject: definition.Name.Value,
                    expected: binding.Shape.ToString(),
                    observed: "missing")));
        }
        else if (!IsCompatibleRootShape(definition, graphShape))
        {
            diagnostics.Add(new(
                Code: EntityShapeGraphDiagnosticCodes.RootShapeIncompatible,
                Severity: DiagnosticSeverity.Error,
                Message: $"Entity root shape '{binding.Shape}' differs from the shape stored in its graph snapshot.",
                Location: "/shape",
                SchemaLocation: $"/shapeGraph/document/graph/shapes/{Encode(binding.Shape.ShapeId.Value)}",
                Evidence: new(
                    stage: "entity-state-shape-linking",
                    subject: definition.Name.Value,
                    resolutionOptions: ["Project the EntityDefinition from the canonical root shape in the bound graph snapshot."])));
        }

        ValidateReachableTypes(definition.Shape, graph, diagnostics);
        return Result(diagnostics);
    }

    static void ProjectGraphDiagnostics(
        DocumentValidationResult graphValidation,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var diagnostic in graphValidation.Diagnostics)
        {
            var code = diagnostic.Code switch
            {
                "shapeGraph.shape.duplicateId" or "shapeGraph.type.duplicateId" =>
                    EntityShapeGraphDiagnosticCodes.DuplicateIdentity,
                "shapeGraph.type.ref.missing" => EntityShapeGraphDiagnosticCodes.NamedTypeMissing,
                _ => diagnostic.Code
            };
            diagnostics.Add(diagnostic with
            {
                Code = code,
                Location = diagnostic.Location is null
                    ? "/shapeGraph/document"
                    : $"/shapeGraph/document{diagnostic.Location}"
            });
        }
    }

    static void ValidateReachableTypes(
        Shape root,
        ShapeGraph graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        HashSet<TypeId> visited = [];
        Dictionary<TypeId, int> active = [];
        List<TypeId> path = [];

        foreach (var field in root.Fields)
        {
            VisitType(
                field.Type,
                $"/shape/fields/{Encode(field.Name.Value)}/type",
                graph,
                visited,
                active,
                path,
                diagnostics);
        }
    }

    static void VisitType(
        TypeRef type,
        string location,
        ShapeGraph graph,
        ISet<TypeId> visited,
        IDictionary<TypeId, int> active,
        IList<TypeId> path,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        switch (type)
        {
            case NamedTypeRef named:
                VisitNamedType(named, location, graph, visited, active, path, diagnostics);
                return;
            case ArrayTypeRef array:
                VisitType(array.ElementType, $"{location}/elementType", graph, visited, active, path, diagnostics);
                return;
            case ObjectTypeRef objectType:
                foreach (var field in objectType.Fields)
                {
                    VisitType(
                        field.Type,
                        $"{location}/fields/{Encode(field.Name)}/type",
                        graph,
                        visited,
                        active,
                        path,
                        diagnostics);
                }
                return;
            case ScalarTypeRef:
            case EnumTypeRef:
            case EntityReferenceTypeRef:
            case QuantityTypeRef:
            case OpaqueRuntimeTypeRef:
            case JsonTypeRef:
                return;
            default:
                throw new InvalidOperationException($"Unsupported type reference '{type.GetType().Name}'.");
        }
    }

    static void VisitNamedType(
        NamedTypeRef named,
        string location,
        ShapeGraph graph,
        ISet<TypeId> visited,
        IDictionary<TypeId, int> active,
        IList<TypeId> path,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (active.TryGetValue(named.TypeId, out var cycleStart))
        {
            var cycle = path.Skip(cycleStart).Append(named.TypeId).Select(static type => type.Value);
            diagnostics.Add(new(
                Code: EntityShapeGraphDiagnosticCodes.NamedTypeCycle,
                Severity: DiagnosticSeverity.Error,
                Message: $"Entity-state named type references contain cycle '{string.Join(" -> ", cycle)}'.",
                Location: location,
                Evidence: new(
                    stage: "entity-state-shape-linking",
                    subject: named.TypeId.Value,
                    resolutionOptions: ["Replace recursive named components with an acyclic finite entity-state schema."])));
            return;
        }
        if (visited.Contains(named.TypeId))
            return;
        if (!graph.TryGetType(named.TypeId, out var definition))
        {
            diagnostics.Add(new(
                Code: EntityShapeGraphDiagnosticCodes.NamedTypeMissing,
                Severity: DiagnosticSeverity.Error,
                Message: $"Named type '{named.TypeId.Value}' cannot be resolved from graph revision '{graph.Id.Value}'.",
                Location: location,
                Evidence: new(
                    stage: "entity-state-shape-linking",
                    subject: named.TypeId.Value,
                    expected: named.TypeId.Value,
                    observed: "missing")));
            return;
        }

        active.Add(named.TypeId, path.Count);
        path.Add(named.TypeId);
        try
        {
            switch (definition)
            {
                case TypeDefinition.Structural structural:
                    foreach (var field in structural.Fields)
                    {
                        VisitType(
                            field.Type,
                            $"/shapeGraph/document/graph/namedTypes/{Encode(structural.Id.Value)}/fields/{Encode(field.Name.Value)}/type",
                            graph,
                            visited,
                            active,
                            path,
                            diagnostics);
                    }
                    break;
                case TypeDefinition.Union union:
                    foreach (var unionCase in union.Cases)
                    {
                        VisitType(
                            unionCase.Type,
                            $"/shapeGraph/document/graph/namedTypes/{Encode(union.Id.Value)}/cases/{Encode(unionCase.Name)}/type",
                            graph,
                            visited,
                            active,
                            path,
                            diagnostics);
                    }
                    break;
                case TypeDefinition.Enum:
                    break;
            }
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
            active.Remove(named.TypeId);
        }
        visited.Add(named.TypeId);
    }

    static IEnumerable<TypeId> EnumerateNamedTypeReferences(IEnumerable<TypeRef> types)
    {
        foreach (var type in types)
        {
            switch (type)
            {
                case NamedTypeRef named:
                    yield return named.TypeId;
                    break;
                case ArrayTypeRef array:
                    foreach (var nested in EnumerateNamedTypeReferences([array.ElementType]))
                        yield return nested;
                    break;
                case ObjectTypeRef objectType:
                    foreach (var nested in EnumerateNamedTypeReferences(objectType.Fields.Select(static field => field.Type)))
                        yield return nested;
                    break;
            }
        }
    }

    static DocumentValidationResult Result(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        new(DocumentValidationDiagnostics.Normalize([.. diagnostics.Distinct()]));

    static bool IsCompatibleRootShape(EntityDefinition definition, Shape graphShape)
    {
        try
        {
            return graphShape.WithEntityType(definition.Name).Equals(definition.Shape);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static string Encode(string value) => Uri.EscapeDataString(value);
}

/// <summary>Entity-state failure carrying structured shape-graph linkage diagnostics.</summary>
public sealed class EntityShapeGraphValidationException : SemanticRuleViolationException
{
    /// <summary>Creates a failure for invalid graph-backed entity state semantics.</summary>
    /// <param name="diagnostics">Structured deterministic validation diagnostics.</param>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> is empty.</exception>
    public EntityShapeGraphValidationException(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
        if (Diagnostics.IsDefaultOrEmpty)
            throw new ArgumentException("Shape-graph validation failure requires diagnostics.", nameof(diagnostics));
    }

    /// <summary>Structured deterministic validation diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    static string CreateMessage(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        var normalized = DocumentValidationDiagnostics.Normalize(diagnostics);
        return normalized.IsDefaultOrEmpty
            ? "Entity shape-graph validation failed."
            : string.Join(" ", normalized.Select(static diagnostic => diagnostic.Message));
    }
}
