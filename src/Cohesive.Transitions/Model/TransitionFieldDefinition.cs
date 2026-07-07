using System.Collections.Immutable;
using System.Text.Json;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Transition-specific helpers for canonical shape field definitions.
/// </summary>
public static class TransitionFieldDefinition
{
    static readonly AnnotationKey TransitionInvariantsAnnotation = new("transition.invariants");

    extension(FieldDefinition)
    {
        /// <summary>
        /// Creates a canonical field definition with transition metadata.
        /// </summary>
        public static FieldDefinition Create(
            FieldName name,
            TypeRef type,
            FieldCardinality cardinality = FieldCardinality.Single,
            FieldPresence presence = FieldPresence.Required,
            FieldMutability mutability = FieldMutability.Mutable,
            ImmutableArray<InvariantDefinition> constraints = default,
            ComputeDefinition? compute = null,
            ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
            )
        {
            if (mutability == FieldMutability.Computed && compute is null)
                throw new ArgumentException(message: "Computed fields require a compute definition.", paramName: nameof(compute));

            if (compute is not null && mutability != FieldMutability.Computed)
                throw new ArgumentException(message: "Compute definition requires computed mutability.", paramName: nameof(compute));

            var transitionConstraints = constraints.IsDefault ? [] : constraints;

            var role = mutability == FieldMutability.Computed
                ? FieldRole.Computed
                : type is EntityReferenceTypeRef
                    ? FieldRole.Reference
                    : FieldRole.Data;

            var nullability = presence == FieldPresence.Optional
                ? FieldNullability.Nullable
                : FieldNullability.NonNullable;

            ImmutableArray<ShapeConstraint> shapeConstraints = [];
            if (presence == FieldPresence.Required)
                shapeConstraints = shapeConstraints.Insert(0, new RequiredConstraint());

            var normalizedAnnotations = AnnotationMap.Normalize(annotations);
            if (!transitionConstraints.IsDefaultOrEmpty)
            {
                normalizedAnnotations = normalizedAnnotations.SetItem(
                    TransitionInvariantsAnnotation,
                    AnnotationValue.FromObject(transitionConstraints)
                    );
            }

            return new FieldDefinition(
                name: name,
                type: type,
                cardinality: cardinality,
                presence: presence,
                nullability: nullability,
                role: role,
                mutability: mutability,
                compute: compute,
                constraints: shapeConstraints,
                annotations: normalizedAnnotations
                );
        }

    }

    extension(FieldDefinition field)
    {
        /// <summary>
        /// Returns transition constraint metadata for a field.
        /// </summary>
        public ImmutableArray<InvariantDefinition> GetTransitionConstraints()
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!field.Annotations.TryGetValue(TransitionInvariantsAnnotation, out var encoded) || encoded.Value is null)
            {
                return [];
            }

            try
            {
                var deserialized = encoded.Value.Deserialize<InvariantDefinition[]>();
                if (deserialized is null || deserialized.Length == 0)
                    return [];

                return [.. deserialized];
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Field '{field.Name.Value}' has invalid '{TransitionInvariantsAnnotation.Value}' annotation payload.",
                    ex);
            }
        }
    }
}
