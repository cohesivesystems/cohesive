using System.Collections.Immutable;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Model;

/// <summary>
/// Validates canonical relationship catalogs structurally and against explicit shape-graph snapshots.
/// </summary>
public static class RelationshipCatalogValidator
{
    /// <summary>Validates catalog-local semantic invariants without resolving shapes.</summary>
    /// <param name="catalog">Relationship catalog to validate.</param>
    /// <returns>Structured catalog diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult Validate(RelationshipCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        List<DocumentValidationDiagnostic> diagnostics = [];
        ValidateDefinitions(catalog, diagnostics);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    /// <summary>Validates a catalog against one explicit shape-graph snapshot.</summary>
    /// <param name="catalog">Relationship catalog to validate.</param>
    /// <param name="shapeGraph">Shape graph containing relationship endpoints.</param>
    /// <returns>Structured catalog and shape-resolution diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="catalog"/> or <paramref name="shapeGraph"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(RelationshipCatalog catalog, ShapeGraph shapeGraph)
    {
        ArgumentNullException.ThrowIfNull(shapeGraph);
        return Validate(catalog, [shapeGraph]);
    }

    /// <summary>Validates a catalog against explicit graph snapshots for all qualified endpoints.</summary>
    /// <param name="catalog">Relationship catalog to validate.</param>
    /// <param name="shapeGraphs">Exact shape-graph snapshots available to validation.</param>
    /// <returns>Structured catalog and shape-resolution diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="catalog"/> or <paramref name="shapeGraphs"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(
        RelationshipCatalog catalog,
        IEnumerable<ShapeGraph> shapeGraphs)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(shapeGraphs);

        List<DocumentValidationDiagnostic> diagnostics = [];
        ValidateDefinitions(catalog, diagnostics);

        Dictionary<GraphId, ShapeGraph> graphsById = [];
        var graphIndex = 0;
        foreach (var graph in shapeGraphs)
        {
            if (graph is null)
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.shapeGraph.missing",
                    "A supplied shape-graph snapshot cannot be null.",
                    $"/shapeGraphs/{graphIndex}");
                graphIndex++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(graph.Id.Value))
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.shapeGraph.idMissing",
                    "A supplied shape graph must have a stable non-empty id.",
                    $"/shapeGraphs/{graphIndex}/id");
            }
            else if (!graphsById.TryAdd(graph.Id, graph))
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.shapeGraph.duplicateId",
                    $"Multiple supplied shape graphs have id '{graph.Id.Value}'.",
                    $"/shapeGraphs/{graphIndex}/id");
            }

            graphIndex++;
        }

        for (var index = 0; index < catalog.Relationships.Length; index++)
            ValidateAgainstShapes(catalog.Relationships[index], index, graphsById, diagnostics);

        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateDefinitions(
        RelationshipCatalog catalog,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        Dictionary<RelationshipId, (RelationshipDefinition Definition, int Index)> byId = [];
        Dictionary<RelationshipSemanticKey, (RelationshipDefinition Definition, int Index)> bySemantics = [];

        for (var index = 0; index < catalog.Relationships.Length; index++)
        {
            var relationship = catalog.Relationships[index];
            var location = RelationshipLocation(index);

            if (string.IsNullOrWhiteSpace(relationship.Id.Value))
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.relationship.idMissing",
                    "A relationship must have a non-empty id.",
                    $"{location}/id");
            }
            else if (!byId.TryAdd(relationship.Id, (relationship, index)))
            {
                var first = byId[relationship.Id];
                Add(
                    diagnostics,
                    "relationshipCatalog.relationship.duplicateId",
                    $"Relationship id '{relationship.Id.Value}' is declared more than once.",
                    $"{location}/id");

                if (!SemanticallyEqual(first.Definition, relationship))
                {
                    Add(
                        diagnostics,
                        "relationshipCatalog.relationship.conflictingId",
                        $"Relationship id '{relationship.Id.Value}' refers to conflicting semantic definitions.",
                        location);

                    if (RelationshipIdConvention.IsConventionId(relationship.Id))
                    {
                        Add(
                            diagnostics,
                            "relationshipCatalog.relationship.generatedIdCollision",
                            $"Convention relationship id '{relationship.Id.Value}' collides for different semantics.",
                            $"{location}/id");
                    }
                }
            }

            ValidateQualifiedShape(relationship.SourceShape, "sourceShape", location, diagnostics);
            ValidateQualifiedShape(relationship.TargetShape, "targetShape", location, diagnostics);

            if (relationship.SourceReference.Segments.IsDefaultOrEmpty)
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.relationship.sourceReferenceMissing",
                    "A relationship must identify a source reference field.",
                    $"{location}/sourceReference");
            }
            else if (!TryGetTopLevelField(relationship.SourceReference, out _))
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.relationship.sourceReferenceNestedUnsupported",
                    "relationship-catalog/v1 supports exactly one top-level source reference field.",
                    $"{location}/sourceReference");
            }

            if (relationship.TargetKey is null)
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.relationship.targetKeyMissing",
                    "A relationship must declare a target key.",
                    $"{location}/targetKey");
            }
            else if (relationship.TargetKey is not ObservationIdentityRelationshipTargetKey)
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.relationship.targetKeyUnsupported",
                    $"Target key '{relationship.TargetKey.GetType().Name}' is not supported by relationship-catalog/v1.",
                    $"{location}/targetKey");
            }

            if (!Enum.IsDefined(relationship.SourceReferenceUniqueness))
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.relationship.sourceReferenceUniquenessInvalid",
                    $"Source-reference uniqueness value '{relationship.SourceReferenceUniqueness}' is invalid.",
                    $"{location}/sourceReferenceUniqueness");
            }

            if (RelationshipIdConvention.IsConventionId(relationship.Id)
                && HasConventionIdInputs(relationship))
            {
                var expectedId = RelationshipIdConvention.Create(relationship);
                if (relationship.Id != expectedId)
                {
                    Add(
                        diagnostics,
                        "relationshipCatalog.relationship.generatedIdMismatch",
                        $"Convention relationship id '{relationship.Id.Value}' does not match the relationship's canonical semantic inputs; expected '{expectedId.Value}'.",
                        $"{location}/id");
                }
            }

            var semanticKey = RelationshipSemanticKey.From(relationship);
            if (bySemantics.TryGetValue(semanticKey, out var equivalent)
                && equivalent.Definition.Id != relationship.Id)
            {
                Add(
                    diagnostics,
                    "relationshipCatalog.relationship.duplicateSemantics",
                    $"Relationships '{equivalent.Definition.Id.Value}' and '{relationship.Id.Value}' declare the same semantics under different ids.",
                    location);
            }
            else
            {
                bySemantics.TryAdd(semanticKey, (relationship, index));
            }
        }
    }

    static void ValidateAgainstShapes(
        RelationshipDefinition relationship,
        int index,
        IReadOnlyDictionary<GraphId, ShapeGraph> graphsById,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var location = RelationshipLocation(index);
        var sourceShape = ResolveShape(
            relationship.SourceShape,
            "sourceShape",
            location,
            graphsById,
            diagnostics);
        var targetShape = ResolveShape(
            relationship.TargetShape,
            "targetShape",
            location,
            graphsById,
            diagnostics);

        if (sourceShape is null || !TryGetTopLevelField(relationship.SourceReference, out var fieldName))
            return;

        if (!sourceShape.TryGetField(fieldName, out var sourceField))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.sourceReferenceFieldMissing",
                $"Source shape '{relationship.SourceShape}' does not contain reference field '{fieldName}'.",
                $"{location}/sourceReference");
            return;
        }

        if (!IsObservationIdentityCompatible(sourceField.Type))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.sourceReferenceIdentityIncompatible",
                $"Source reference field '{fieldName}' cannot address an observation identity.",
                $"{location}/sourceReference");
        }

        if (!Enum.IsDefined(sourceField.Cardinality))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.sourceReferenceCardinalityInvalid",
                $"Source reference field '{fieldName}' has invalid cardinality '{sourceField.Cardinality}'.",
                $"{location}/sourceReference");
        }

        if (!Enum.IsDefined(sourceField.Presence))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.sourceReferencePresenceInvalid",
                $"Source reference field '{fieldName}' has invalid presence '{sourceField.Presence}'.",
                $"{location}/sourceReference");
        }

        if (!Enum.IsDefined(sourceField.Nullability))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.sourceReferenceNullabilityInvalid",
                $"Source reference field '{fieldName}' has invalid nullability '{sourceField.Nullability}'.",
                $"{location}/sourceReference");
        }

        if (sourceField.Type is EntityReferenceTypeRef entityReference
            && targetShape is not null
            && TryGetEntityType(targetShape, out var targetEntityType)
            && entityReference.Entity != targetEntityType)
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.entityReferenceTargetMismatch",
                $"Source field '{fieldName}' references entity '{entityReference.Entity.Value}', but target shape '{relationship.TargetShape}' represents '{targetEntityType.Value}'.",
                $"{location}/targetShape");
        }
    }

    static Shape? ResolveShape(
        QualifiedShapeId qualifiedShape,
        string property,
        string relationshipLocation,
        IReadOnlyDictionary<GraphId, ShapeGraph> graphsById,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(qualifiedShape.GraphId.Value)
            || string.IsNullOrWhiteSpace(qualifiedShape.ShapeId.Value))
        {
            return null;
        }

        if (!graphsById.TryGetValue(qualifiedShape.GraphId, out var graph))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.endpointGraphMissing",
                $"No supplied shape graph has id '{qualifiedShape.GraphId.Value}'.",
                $"{relationshipLocation}/{property}/graphId");
            return null;
        }

        if (!graph.TryGetShape(qualifiedShape.ShapeId, out var shape))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.endpointShapeMissing",
                $"Shape graph '{qualifiedShape.GraphId.Value}' does not contain shape '{qualifiedShape.ShapeId.Value}'.",
                $"{relationshipLocation}/{property}/shapeId");
            return null;
        }

        return shape;
    }

    static void ValidateQualifiedShape(
        QualifiedShapeId shape,
        string property,
        string relationshipLocation,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.endpointGraphIdMissing",
                "A relationship endpoint must have a non-empty graph id.",
                $"{relationshipLocation}/{property}/graphId");
        }

        if (string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            Add(
                diagnostics,
                "relationshipCatalog.relationship.endpointShapeIdMissing",
                "A relationship endpoint must have a non-empty shape id.",
                $"{relationshipLocation}/{property}/shapeId");
        }
    }

    static bool TryGetTopLevelField(FieldPath path, out string fieldName)
    {
        if (path.Segments.Length == 1
            && path.Segments[0].Kind == SegmentKind.Field
            && !string.IsNullOrWhiteSpace(path.Segments[0].Segment))
        {
            fieldName = path.Segments[0].Segment!;
            return true;
        }

        fieldName = string.Empty;
        return false;
    }

    static bool IsObservationIdentityCompatible(TypeRef type) => type switch
    {
        EntityReferenceTypeRef => true,
        ScalarTypeRef { Kind: ScalarTypeKind.String or ScalarTypeKind.Guid } => true,
        _ => false
    };

    static bool HasConventionIdInputs(RelationshipDefinition relationship) =>
        !string.IsNullOrWhiteSpace(relationship.SourceShape.GraphId.Value)
        && !string.IsNullOrWhiteSpace(relationship.SourceShape.ShapeId.Value)
        && TryGetTopLevelField(relationship.SourceReference, out _)
        && !string.IsNullOrWhiteSpace(relationship.TargetShape.GraphId.Value)
        && !string.IsNullOrWhiteSpace(relationship.TargetShape.ShapeId.Value)
        && relationship.TargetKey is ObservationIdentityRelationshipTargetKey
        && Enum.IsDefined(relationship.SourceReferenceUniqueness);

    static bool TryGetEntityType(Shape shape, out EntityTypeName entityType)
    {
        if (shape.EntityType is { } declaredEntityType)
        {
            entityType = declaredEntityType;
            return true;
        }

        entityType = default;
        return false;
    }

    static bool SemanticallyEqual(RelationshipDefinition left, RelationshipDefinition right) =>
        RelationshipSemanticKey.From(left) == RelationshipSemanticKey.From(right);

    static string RelationshipLocation(int index) => $"/catalog/relationships/{index}";

    static void Add(
        List<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location) => diagnostics.Add(new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location));

    readonly record struct RelationshipSemanticKey(
        QualifiedShapeId SourceShape,
        FieldPath SourceReference,
        QualifiedShapeId TargetShape,
        string TargetKey,
        SourceReferenceUniqueness SourceReferenceUniqueness)
    {
        public static RelationshipSemanticKey From(RelationshipDefinition relationship) => new(
            relationship.SourceShape,
            relationship.SourceReference,
            relationship.TargetShape,
            relationship.TargetKey switch
            {
                ObservationIdentityRelationshipTargetKey => RelationshipWireNames.ObservationIdentityTargetKey,
                null => "<missing>",
                _ => relationship.TargetKey.GetType().FullName ?? relationship.TargetKey.GetType().Name
            },
            relationship.SourceReferenceUniqueness);
    }
}
