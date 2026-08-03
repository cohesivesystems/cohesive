using System.Collections.Immutable;
using System.Text.Json;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Entity-state helpers for canonical shape field definitions.
/// </summary>
public static class EntityFieldDefinitionExtensions
{
    static readonly AnnotationKey EntityInvariantsAnnotation = new("entity.invariants");

    extension(FieldDefinition)
    {
        /// <summary>
        /// Creates a canonical field definition with entity-state metadata.
        /// </summary>
        /// <param name="name">The canonical field name.</param>
        /// <param name="type">The semantic field type.</param>
        /// <param name="cardinality">Whether the field contains one value or a collection.</param>
        /// <param name="presence">Whether the field is required in an entity state.</param>
        /// <param name="mutability">How the field may change after creation.</param>
        /// <param name="constraints">Entity-state constraints evaluated for the field.</param>
        /// <param name="compute">The expression used to derive a computed field.</param>
        /// <param name="annotations">Additional shape annotations for the field.</param>
        /// <returns>A canonical shape field carrying the supplied entity-state metadata.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="mutability"/> is computed without a <paramref name="compute"/> expression,
        /// or <paramref name="compute"/> is supplied for a field that is not computed.
        /// </exception>
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

            var entityConstraints = constraints.IsDefault ? [] : constraints;

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
            if (!entityConstraints.IsDefaultOrEmpty)
            {
                normalizedAnnotations = normalizedAnnotations.SetItem(
                    EntityInvariantsAnnotation,
                    AnnotationValue.FromObject(entityConstraints)
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
        /// Returns entity-state constraint metadata for a field.
        /// </summary>
        /// <returns>The constraints encoded on the receiver, or an empty array when none are present.</returns>
        /// <exception cref="ArgumentNullException">The receiver is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The stored constraint annotation is not a valid constraint payload.</exception>
        public ImmutableArray<InvariantDefinition> GetEntityConstraints()
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!field.Annotations.TryGetValue(EntityInvariantsAnnotation, out var encoded) || encoded.Value is null)
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
                    $"Field '{field.Name.Value}' has invalid '{EntityInvariantsAnnotation.Value}' annotation payload.",
                    ex);
            }
        }
    }
}
