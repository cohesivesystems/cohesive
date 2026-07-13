using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Compilation;

/// <summary>
/// Compiles transition-domain entity reference fields into canonical semantic relationships.
/// </summary>
public static class DomainRelationshipCompiler
{
    /// <summary>
    /// Compiles every <see cref="EntityReferenceTypeRef"/> field in a domain model into one relationship catalog.
    /// </summary>
    /// <param name="model">Domain model containing source and target entity definitions.</param>
    /// <param name="graphId">Stable identifier of the shape-graph snapshot containing the entity shapes.</param>
    /// <returns>
    /// A compilation result containing a complete catalog on success, or structured diagnostics and no catalog on failure.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="graphId"/> is the default value or has no identifier text.</exception>
    public static DomainRelationshipCompilationResult Compile(
        DomainModelDefinition model,
        GraphId graphId)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (string.IsNullOrWhiteSpace(graphId.Value))
            throw new ArgumentException("A stable graph id is required.", nameof(graphId));

        List<DocumentValidationDiagnostic> diagnostics = [];
        var entities = model.Entities.IsDefault ? [] : model.Entities;
        if (entities.IsDefaultOrEmpty)
        {
            Add(
                diagnostics,
                code: "transitions.relationship.domain.entitiesMissing",
                message: "A domain model must contain at least one entity before relationships can be compiled.",
                location: "/entities");
            return Invalid(diagnostics);
        }

        Dictionary<string, List<(EntityDefinition Entity, int Index)>> entitiesByName = new(StringComparer.Ordinal);
        Dictionary<ShapeId, (EntityDefinition Entity, int Index)> entitiesByShape = [];
        List<(EntityDefinition Entity, int Index)> validEntities = new(entities.Length);

        for (var entityIndex = 0; entityIndex < entities.Length; entityIndex++)
        {
            var entity = entities[entityIndex];
            if (entity is null)
            {
                Add(
                    diagnostics,
                    code: "transitions.relationship.domain.entityMissing",
                    message: "A domain model cannot contain a null entity definition.",
                    location: $"/entities/{entityIndex}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entity.Name.Value))
            {
                Add(
                    diagnostics,
                    code: "transitions.relationship.entity.nameMissing",
                    message: "An entity must have a stable non-empty logical name.",
                    location: $"/entities/{entityIndex}/name");
                continue;
            }

            if (entity.Shape is null)
            {
                Add(
                    diagnostics,
                    code: "transitions.relationship.entity.shapeMissing",
                    message: $"Entity '{entity.Name.Value}' must have a canonical shape.",
                    location: $"/entities/{entityIndex}/shape");
                continue;
            }

            validEntities.Add((entity, entityIndex));

            if (!entitiesByName.TryGetValue(entity.Name.Value, out var sameName))
            {
                sameName = [];
                entitiesByName.Add(entity.Name.Value, sameName);
            }
            sameName.Add((entity, entityIndex));

            if (string.IsNullOrWhiteSpace(entity.Shape.Id.Value))
            {
                Add(
                    diagnostics,
                    code: "transitions.relationship.entity.shapeIdMissing",
                    message: $"Entity '{entity.Name.Value}' must have a stable non-empty shape id.",
                    location: $"/entities/{entityIndex}/shape/id");
            }
            else if (entitiesByShape.TryGetValue(entity.Shape.Id, out var sameShape))
            {
                Add(
                    diagnostics,
                    code: "transitions.relationship.entity.duplicateShapeId",
                    message: $"Entities '{sameShape.Entity.Name.Value}' and '{entity.Name.Value}' use the same shape id '{entity.Shape.Id.Value}' in graph '{graphId.Value}'.",
                    location: $"/entities/{entityIndex}/shape/id");
            }
            else
            {
                entitiesByShape.Add(entity.Shape.Id, (entity, entityIndex));
            }

            ValidateEntityTypeAnnotation(entity, entityIndex, diagnostics);
        }

        foreach (var (entityName, matches) in entitiesByName)
        {
            if (matches.Count < 2)
                continue;

            for (var index = 1; index < matches.Count; index++)
            {
                Add(
                    diagnostics,
                    code: "transitions.relationship.entity.duplicateName",
                    message: $"Entity name '{entityName}' is declared more than once.",
                    location: $"/entities/{matches[index].Index}/name");
            }
        }

        List<RelationshipDefinition> relationships = [];
        foreach (var (sourceEntity, sourceEntityIndex) in validEntities
                     .OrderBy(static item => item.Entity.Name.Value, StringComparer.Ordinal)
                     .ThenBy(static item => item.Entity.Shape.Id.Value, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(sourceEntity.Shape.Id.Value))
                continue;

            var fields = sourceEntity.Fields;
            for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
            {
                var field = fields[fieldIndex];
                if (field?.Type is not EntityReferenceTypeRef entityReference)
                    continue;

                var targetName = entityReference.Entity.Value;
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    Add(
                        diagnostics,
                        code: "transitions.relationship.entityReference.targetNameMissing",
                        message: $"Entity '{sourceEntity.Name.Value}' field '{field.Name.Value}' must reference a non-empty entity name.",
                        location: $"/entities/{sourceEntityIndex}/shape/fields/{fieldIndex}/type/entity");
                    continue;
                }

                if (!entitiesByName.TryGetValue(targetName, out var targets) || targets.Count == 0)
                {
                    Add(
                        diagnostics,
                        code: "transitions.relationship.entityReference.targetMissing",
                        message: $"Entity '{sourceEntity.Name.Value}' field '{field.Name.Value}' references unknown entity '{targetName}'.",
                        location: $"/entities/{sourceEntityIndex}/shape/fields/{fieldIndex}/type/entity");
                    continue;
                }

                if (targets.Count != 1)
                {
                    Add(
                        diagnostics,
                        code: "transitions.relationship.entityReference.targetAmbiguous",
                        message: $"Entity '{sourceEntity.Name.Value}' field '{field.Name.Value}' references ambiguous entity name '{targetName}'.",
                        location: $"/entities/{sourceEntityIndex}/shape/fields/{fieldIndex}/type/entity");
                    continue;
                }

                var targetEntity = targets[0].Entity;
                if (string.IsNullOrWhiteSpace(targetEntity.Shape.Id.Value))
                    continue;

                var sourceShape = new QualifiedShapeId(graphId, sourceEntity.Shape.Id);
                var targetShape = new QualifiedShapeId(graphId, targetEntity.Shape.Id);
                var sourceReference = FieldPath.FromField(field.Name.Value);
                var targetKey = ObservationIdentityRelationshipTargetKey.Instance;
                const SourceReferenceUniqueness uniqueness = SourceReferenceUniqueness.NotGuaranteed;
                var relationshipId = RelationshipIdConvention.Create(
                    sourceShape,
                    sourceReference,
                    targetShape,
                    targetKey,
                    uniqueness);

                relationships.Add(new(
                    relationshipId,
                    sourceShape,
                    sourceReference,
                    targetShape,
                    targetKey,
                    uniqueness));
            }
        }

        if (HasErrors(diagnostics))
            return Invalid(diagnostics);

        var catalog = new RelationshipCatalog([.. relationships]);
        var shapeGraph = new ShapeGraph(
            graphId,
            [.. validEntities.Select(static item => item.Entity.Shape)]);
        var catalogValidation = RelationshipCatalogValidator.Validate(catalog, shapeGraph);
        if (!catalogValidation.IsValid)
            return new(null, catalogValidation);

        return new(catalog, catalogValidation);
    }

    static void ValidateEntityTypeAnnotation(
        EntityDefinition entity,
        int entityIndex,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        var key = new AnnotationKey(ShapeAnnotationKeys.EntityType);
        if (!entity.Shape.Annotations.TryGetValue(key, out var annotation)
            || annotation.Value is not JsonValue value
            || !value.TryGetValue<string>(out var annotatedEntityType)
            || string.IsNullOrWhiteSpace(annotatedEntityType))
        {
            Add(
                diagnostics,
                code: "transitions.relationship.entity.entityTypeAnnotationMissing",
                message: $"Entity '{entity.Name.Value}' shape '{entity.Shape.Id.Value}' must identify its logical entity type.",
                location: $"/entities/{entityIndex}/shape/annotations/{ShapeAnnotationKeys.EntityType}");
            return;
        }

        if (!string.Equals(annotatedEntityType, entity.Name.Value, StringComparison.Ordinal))
        {
            Add(
                diagnostics,
                code: "transitions.relationship.entity.entityTypeAnnotationMismatch",
                message: $"Entity '{entity.Name.Value}' shape '{entity.Shape.Id.Value}' identifies entity type '{annotatedEntityType}'.",
                location: $"/entities/{entityIndex}/shape/annotations/{ShapeAnnotationKeys.EntityType}");
        }
    }

    static DomainRelationshipCompilationResult Invalid(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        new(null, DocumentValidationResult.FromDiagnostics(diagnostics));

    static bool HasErrors(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    static void Add(
        List<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location) => diagnostics.Add(new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location));
}
