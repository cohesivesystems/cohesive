using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;

namespace Cohesive.Model;

/// <summary>Opt-in lowering of DataAnnotations required/min/max length into portable semantic constraints.</summary>
/// <remarks>Only exact built-in attribute types are supported. No CLR validator or callback survives projection.</remarks>
public sealed class DataAnnotationsShapeMetadataProvider : IClrShapeMetadataProvider
{
    /// <summary>Stateless shared provider.</summary>
    public static DataAnnotationsShapeMetadataProvider Instance { get; } = new();
    DataAnnotationsShapeMetadataProvider() { }

    /// <summary>Projects supported property attributes without changing unrelated metadata.</summary>
    /// <param name="context">CLR shape/member currently being lowered.</param>
    /// <returns>Portable constraints and requiredness, or empty metadata.</returns>
    /// <exception cref="ArgumentNullException">Context is null.</exception>
    /// <exception cref="NotSupportedException">Validation is custom, unsupported, type-level, or resource-dependent.</exception>
    /// <exception cref="ArgumentException">Length bounds are invalid or contradictory.</exception>
    public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target != ClrShapeMetadataTarget.Field && typeof(IValidatableObject).IsAssignableFrom(context.ClrType))
            throw new NotSupportedException($"constraint.unsupportedAttribute: IValidatableObject on {context.ClrType.Name}.");
        var attributes = context.AttributeProvider.GetCustomAttributes(typeof(ValidationAttribute), inherit: true);
        if (attributes.Length == 0) return ClrShapeMetadata.Empty;
        var constraints = ImmutableArray.CreateBuilder<ShapeConstraint>();
        var required = false;
        foreach (ValidationAttribute attribute in attributes)
        {
            if (context.Target != ClrShapeMetadataTarget.Field || attribute.ErrorMessageResourceType is not null
                || attribute.ErrorMessageResourceName is not null)
                throw Unsupported(attribute, context);
            if (attribute is MinLengthAttribute or MaxLengthAttribute)
            {
                var type = context.ClrType;
                if (type != typeof(string) && !type.IsArray && !typeof(System.Collections.ICollection).IsAssignableFrom(type)
                    && !type.GetInterfaces().Append(type).Any(static candidate => candidate.IsGenericType
                        && candidate.GetGenericTypeDefinition() == typeof(ICollection<>)))
                    throw new NotSupportedException($"constraint.lengthType: {type.Name} does not have supported string/collection length semantics.");
            }
            if (attribute.GetType() == typeof(RequiredAttribute))
            {
                required = true;
                constraints.Add(new RequiredConstraint(message: attribute.ErrorMessage,
                    rejectEmptyStrings: !((RequiredAttribute)attribute).AllowEmptyStrings));
            }
            else if (attribute.GetType() == typeof(MinLengthAttribute))
            {
                var length = ((MinLengthAttribute)attribute).Length;
                if (length < 0) throw new ArgumentException("constraint.invalidBound: MinLength must be nonnegative.");
                constraints.Add(new MinLengthConstraint(length, message: attribute.ErrorMessage));
            }
            else if (attribute.GetType() == typeof(MaxLengthAttribute))
            {
                var length = ((MaxLengthAttribute)attribute).Length;
                if (length < -1 || length == 0) throw new ArgumentException("constraint.invalidBound: MaxLength must be -1 or positive.");
                if (length != -1) constraints.Add(new MaxLengthConstraint(length, message: attribute.ErrorMessage));
            }
            else throw Unsupported(attribute, context);
        }
        var result = constraints.ToImmutable();
        ConstraintSemantics.RequireConsistent(result);
        return new()
        {
            Constraints = result,
            Presence = required ? FieldPresence.Required : null,
            Nullability = required ? FieldNullability.NonNullable : null
        };
    }

    static NotSupportedException Unsupported(ValidationAttribute attribute, ClrShapeMetadataContext context) =>
        new($"constraint.unsupportedAttribute: {attribute.GetType().Name} on {context.DeclaringType?.Name ?? context.ClrType.Name}.{context.Property?.Name}.");
}
