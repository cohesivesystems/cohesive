using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;

namespace Cohesive.Adapters.Postgres;

/// <summary>
/// Shared semantic conformance checks for plan-affine PostgreSQL bindings consumed by compilation and execution.
/// </summary>
internal static class PostgresRelationQueryBindingSemanticValidator
{
    internal static ImmutableArray<string> ValidateCompilation(
        CompiledRelationQueryPlan plan,
        IReadOnlyList<PostgresRelationQueryTableBinding> tables,
        IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements,
        IReadOnlyCollection<RelationQueryFieldInputContract> fields,
        IReadOnlyCollection<RelationQueryTraversalInputContract> traversals,
        IReadOnlySet<ValueBindingId> requiredIdentityBindings) => Validate(
        plan,
        tables,
        placements,
        fields,
        traversals,
        requiredIdentityBindings,
        validateFieldValues: false);

    internal static ImmutableArray<string> ValidateRegistration(
        CompiledRelationQueryPlan plan,
        IReadOnlyList<PostgresRelationQueryTableBinding> tables,
        IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements,
        IReadOnlyCollection<RelationQueryFieldInputContract> fields,
        IReadOnlyCollection<RelationQueryTraversalInputContract> traversals,
        IReadOnlySet<ValueBindingId> requiredIdentityBindings) => Validate(
        plan,
        tables,
        placements,
        fields,
        traversals,
        requiredIdentityBindings,
        validateFieldValues: true);

    static ImmutableArray<string> Validate(
        CompiledRelationQueryPlan plan,
        IReadOnlyList<PostgresRelationQueryTableBinding> tables,
        IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements,
        IReadOnlyCollection<RelationQueryFieldInputContract> fields,
        IReadOnlyCollection<RelationQueryTraversalInputContract> traversals,
        IReadOnlySet<ValueBindingId> requiredIdentityBindings,
        bool validateFieldValues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(traversals);
        ArgumentNullException.ThrowIfNull(requiredIdentityBindings);

        var errors = ImmutableArray.CreateBuilder<string>();
        var fieldsByInput = fields.ToDictionary(static field => field.Input.Id);
        var traversalsByInput = traversals.ToDictionary(static traversal => traversal.Input.Id);
        foreach (var table in tables)
        {
            ValidateFields(table, placements, fieldsByInput, validateFieldValues, errors);
            ValidateRelationships(plan, table, placements, traversalsByInput, errors);
            ValidateIdentity(plan, table, placements, requiredIdentityBindings, errors);
        }

        return errors.ToImmutable();
    }

    internal static string? GetValueSemanticsMismatch(
        ValueContract contract,
        PostgresRelationQueryScalarType scalarType,
        PostgresRelationQueryMissingValueEncoding missingValueEncoding,
        PostgresRelationQueryNullValueEncoding nullValueEncoding,
        PostgresRelationQueryNumericDomainEvidence? numericDomain,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.Cardinality != FieldCardinality.Single
            || !PostgresRelationQueryScalarCatalog.TryFromSemanticType(contract.Type, out var expected))
        {
            return $"Semantic value contract '{contract}' has no exact single-valued PostgreSQL scalar representation.";
        }
        if (scalarType != expected)
        {
            return $"Physical scalar type '{scalarType}' does not match semantic scalar type '{expected}'.";
        }

        var expectedMissing = contract.Presence == FieldPresence.Required
            ? PostgresRelationQueryMissingValueEncoding.Prohibited
            : PostgresRelationQueryMissingValueEncoding.SqlNull;
        var expectedNull = contract.Nullability == FieldNullability.NonNullable
            ? PostgresRelationQueryNullValueEncoding.Prohibited
            : PostgresRelationQueryNullValueEncoding.SqlNull;
        if (missingValueEncoding != expectedMissing || nullValueEncoding != expectedNull)
        {
            return "Physical SQL null encoding does not preserve the field's semantic missing/null distinction.";
        }
        if (scalarType == PostgresRelationQueryScalarType.Numeric && numericDomain is null)
        {
            return "A PostgreSQL numeric value requires explicit finite CLR-decimal domain evidence.";
        }
        if (scalarType is PostgresRelationQueryScalarType.Date
                or PostgresRelationQueryScalarType.Timestamp
                or PostgresRelationQueryScalarType.TimestampWithTimeZone
            && temporalDomain is null)
        {
            return "A PostgreSQL temporal value requires explicit finite canonical CLR-domain evidence.";
        }

        return null;
    }

    static void ValidateFields(
        PostgresRelationQueryTableBinding table,
        IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements,
        IReadOnlyDictionary<RelationQueryInputId, RelationQueryFieldInputContract> fields,
        bool validateValues,
        ImmutableArray<string>.Builder errors)
    {
        foreach (var field in table.Fields)
        {
            if (!fields.TryGetValue(field.Input, out var contract))
                continue;
            if (!placements.TryGetValue(table.Input, out var placement)
                || contract.Input.Field.Path != field.SemanticPath
                || contract.Input.Binding != placement.Binding
                || contract.Input.Field.Shape != table.Shape)
            {
                errors.Add(
                    $"PostgreSQL field binding '{field.Input.Value}' does not identify its exact canonical path and placed binding.");
                continue;
            }
            if (!validateValues)
                continue;
            if (contract.Input.ValueContract is not { } valueContract)
            {
                errors.Add($"PostgreSQL field binding '{field.Input.Value}' has no resolved semantic value contract.");
                continue;
            }
            if (GetValueSemanticsMismatch(
                    valueContract,
                    field.ScalarType,
                    field.MissingValueEncoding,
                    field.NullValueEncoding,
                    field.NumericDomain,
                    field.TemporalDomain) is { } mismatch)
            {
                errors.Add($"PostgreSQL field binding '{field.Input.Value}' is not semantically exact: {mismatch}");
            }
        }
    }

    static void ValidateRelationships(
        CompiledRelationQueryPlan plan,
        PostgresRelationQueryTableBinding table,
        IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements,
        IReadOnlyDictionary<RelationQueryInputId, RelationQueryTraversalInputContract> traversals,
        ImmutableArray<string>.Builder errors)
    {
        foreach (var reference in table.RelationshipReferences)
        {
            if (!traversals.TryGetValue(reference.Input, out var traversal))
                continue;
            if (reference.SemanticPath != traversal.Definition.SourceReference
                || reference.Uniqueness != traversal.Definition.SourceReferenceUniqueness)
            {
                errors.Add(
                    $"PostgreSQL relationship reference '{reference.Input.Value}' does not preserve the canonical source-reference path and uniqueness evidence.");
                continue;
            }

            var ownsReference = traversal.Input.Direction == RelationshipTraversalDirection.Forward
                ? placements.TryGetValue(table.Input, out var placement)
                  && placement.Binding == traversal.From
                  && table.Shape == traversal.Definition.SourceShape
                : table.Input == traversal.Input.Id
                  && table.Shape == traversal.Definition.SourceShape;
            if (!ownsReference)
            {
                errors.Add(
                    $"PostgreSQL relationship reference '{reference.Input.Value}' is attached to the wrong placed table.");
            }
            if (traversal.Input.Direction == RelationshipTraversalDirection.Inverse
                && traversal.Cardinality == RelationshipTraversalCardinality.AtMostOne
                && reference.Uniqueness != SourceReferenceUniqueness.GloballyUnique)
            {
                errors.Add(
                    $"Inverse at-most-one traversal '{reference.Input.Value}' lacks globally unique source-reference evidence.");
            }

            var valueContract = ResolveFieldContract(
                plan,
                traversal.Definition.SourceShape,
                traversal.Definition.SourceReference);
            if (valueContract is null)
            {
                errors.Add(
                    $"PostgreSQL relationship reference '{reference.Input.Value}' does not resolve to one canonical scalar field.");
                continue;
            }
            if (GetValueSemanticsMismatch(
                    valueContract,
                    reference.ScalarType,
                    reference.MissingValueEncoding,
                    reference.NullValueEncoding,
                    reference.NumericDomain,
                    reference.TemporalDomain) is { } mismatch)
            {
                errors.Add(
                    $"PostgreSQL relationship reference '{reference.Input.Value}' is not semantically exact: {mismatch}");
            }
        }
    }

    static void ValidateIdentity(
        CompiledRelationQueryPlan plan,
        PostgresRelationQueryTableBinding table,
        IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements,
        IReadOnlySet<ValueBindingId> requiredIdentityBindings,
        ImmutableArray<string>.Builder errors)
    {
        if (!placements.TryGetValue(table.Input, out var placement)
            || !requiredIdentityBindings.Contains(placement.Binding))
        {
            return;
        }
        if (table.Identity is not { } identity)
        {
            errors.Add($"PostgreSQL table '{table.PlacementBinding.Value}' lacks its required observation identity.");
            return;
        }

        var shape = plan.Provenance.ShapeDocuments
            .SingleOrDefault(document => document.Graph.Id == table.Shape.GraphId)
            ?.Graph.TryGetShape(table.Shape);
        var canonicalIdentities = shape?.Fields
            .Where(static field => field.Role == FieldRole.Identity)
            .ToArray() ?? [];
        var identityField = ResolveField(shape, identity.SemanticPath);
        if (identityField is null
            || placement.Identity is not { } placementIdentity
            || placementIdentity.Shape != table.Shape
            || placementIdentity.SemanticPath != identity.SemanticPath
            || canonicalIdentities.Length > 1
            || canonicalIdentities.Length == 1 && canonicalIdentities[0] != identityField)
        {
            errors.Add(
                $"PostgreSQL identity binding for table '{table.PlacementBinding.Value}' does not match exact placement and shape identity evidence.");
            return;
        }

        var valueContract = ValueContract.FromField(identityField);
        if (GetValueSemanticsMismatch(
                valueContract,
                identity.ScalarType,
                PostgresRelationQueryMissingValueEncoding.Prohibited,
                PostgresRelationQueryNullValueEncoding.Prohibited,
                identity.NumericDomain,
                identity.TemporalDomain) is { } mismatch)
        {
            errors.Add(
                $"PostgreSQL identity binding for table '{table.PlacementBinding.Value}' is not semantically exact: {mismatch}");
        }
    }

    static ValueContract? ResolveFieldContract(
        CompiledRelationQueryPlan plan,
        QualifiedShapeId shape,
        FieldPath path)
    {
        var semanticShape = plan.Provenance.ShapeDocuments
            .SingleOrDefault(document => document.Graph.Id == shape.GraphId)
            ?.Graph.TryGetShape(shape);
        var field = ResolveField(semanticShape, path);
        return field?.Type is null ? null : ValueContract.FromField(field);
    }

    static FieldDefinition? ResolveField(Shape? shape, FieldPath path)
    {
        if (path.Segments.Length != 1
            || !path.Segments[0].TryGetFieldIdentity(out var fieldName))
        {
            return null;
        }
        return shape?.TryGetField(fieldName, out var field) == true ? field : null;
    }
}
