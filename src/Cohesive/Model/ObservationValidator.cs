using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cohesive.Model;

/// <summary>Validates identity-free observation fields against shape semantics.</summary>
public static class ObservationValidator
{
    const int MaxValidationDepth = 64;

    /// <summary>Validates that a concrete object value adheres to the supplied shape semantics.</summary>
    /// <param name="value">Concrete object value to validate.</param>
    /// <param name="shape">Expected semantic shape.</param>
    /// <param name="validationError">Validation failure reason when validation fails.</param>
    /// <param name="graph">Optional shape graph used to resolve named type references.</param>
    /// <returns><see langword="true"/> when the value is concrete, portable, and satisfies the shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static bool TryValidateAgainstShape(
        ObservationValue value,
        Shape shape,
        out string? validationError,
        ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        NoDiagnostics noDiagnostics = default;
        if (TryValidateRoot(value, shape, graph, ref noDiagnostics))
        {
            validationError = null;
            return true;
        }

        DetailedDiagnostics detailedDiagnostics = new();
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            _ = TryValidateRoot(value, shape, graph, ref detailedDiagnostics);
        else if (TryValidatePortableTree(value, MaxValidationDepth, ref detailedDiagnostics))
            _ = TryValidateRoot(value, shape, graph, ref detailedDiagnostics);
        validationError = detailedDiagnostics.Error ?? "The observation does not adhere to the supplied shape.";
        return false;
    }

    /// <summary>Validates that canonical observation fields adhere to the supplied shape semantics.</summary>
    /// <param name="fields">Observation fields keyed by canonical semantic identity.</param>
    /// <param name="shape">Expected semantic shape.</param>
    /// <param name="validationError">Validation failure reason when validation fails.</param>
    /// <param name="graph">Optional shape graph used to resolve named type references.</param>
    /// <returns><see langword="true"/> when the fields are portable and satisfy the shape.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fields"/> or <paramref name="shape"/> is <see langword="null"/>.
    /// </exception>
    public static bool TryValidateAgainstShape(
        IReadOnlyDictionary<string, ObservationValue> fields,
        Shape shape,
        out string? validationError,
        ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(shape);
        NoDiagnostics noDiagnostics = default;
        if (TryValidateShapeFields(
                fields,
                shape,
                graph,
                keysAreCanonical: false,
                ref noDiagnostics))
        {
            validationError = null;
            return true;
        }

        DetailedDiagnostics detailedDiagnostics = new();
        _ = TryValidateShapeFields(
            fields,
            shape,
            graph,
            keysAreCanonical: false,
            ref detailedDiagnostics);
        validationError = detailedDiagnostics.Error ?? "The observation fields do not adhere to the supplied shape.";
        return false;
    }

    /// <summary>
    /// Validates ordinal-aligned observation fields directly against one exact graph-scoped shape.
    /// </summary>
    /// <param name="shape">Exact graph and shape governing the physical values.</param>
    /// <param name="layout">Layout assigning each physical value slot to a canonical shape field.</param>
    /// <param name="valuesByOrdinal">One value slot for every layout ordinal.</param>
    /// <param name="hasValueBitMask">Packed presence bits for the ordinal-aligned value slots.</param>
    /// <param name="validationError">Validation failure reason when a present value violates shape semantics.</param>
    /// <returns><see langword="true"/> when the present fields satisfy the exact shape; otherwise false.</returns>
    /// <remarks>
    /// Successful validation does not materialize a dictionary-backed object value. The supplied storage is read
    /// only for the duration of this call and is not retained.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="shape"/> is default or <paramref name="layout"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The layout belongs to another shape, the value or bitmap length is invalid, or the bitmap contains presence
    /// bits outside the layout.
    /// </exception>
    public static bool TryValidateAgainstShape(
        GraphShapeId shape,
        ObservationLayout layout,
        ReadOnlySpan<ObservationValue> valuesByOrdinal,
        ReadOnlySpan<ulong> hasValueBitMask,
        out string? validationError)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.ShapeId != shape.QualifiedId)
        {
            throw new ArgumentException(
                $"Observation layout shape '{layout.ShapeId}' does not match supplied shape '{shape.QualifiedId}'.",
                nameof(layout));
        }
        if (valuesByOrdinal.Length != layout.Count)
        {
            throw new ArgumentException(
                "Ordinal-aligned observation values must contain one slot per layout field.",
                nameof(valuesByOrdinal));
        }

        var requiredWords = RequiredPresenceWordCount(layout.Count);
        if (hasValueBitMask.Length != requiredWords)
        {
            throw new ArgumentException(
                "Observation presence bitmap length does not match the layout field count.",
                nameof(hasValueBitMask));
        }
        RequireNoPresenceOutsideLayout(hasValueBitMask, layout.Count);

        var definition = shape.Graph.GetShape(shape.ShapeId);

        NoDiagnostics noDiagnostics = default;
        if (TryValidateOrdinalFields(
                valuesByOrdinal,
                hasValueBitMask,
                layout,
                definition,
                shape.Graph,
                ref noDiagnostics))
        {
            validationError = null;
            return true;
        }

        DetailedDiagnostics detailedDiagnostics = new();
        _ = TryValidateOrdinalFields(
            valuesByOrdinal,
            hasValueBitMask,
            layout,
            definition,
            shape.Graph,
            ref detailedDiagnostics);
        validationError = detailedDiagnostics.Error ?? "The ordinal observation fields do not adhere to the supplied shape.";
        return false;
    }

    static bool TryValidateRoot<TDiagnostics>(
        in ObservationValue value,
        Shape shape,
        ShapeGraph? graph,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return Fail(ref diagnostics, new(ErrorCode.RootMustBeObject));

        return TryValidateShapeFields(
            value.Fields,
            shape,
            graph,
            keysAreCanonical: true,
            ref diagnostics);
    }

    static bool TryValidateOrdinalFields<TDiagnostics>(
        ReadOnlySpan<ObservationValue> valuesByOrdinal,
        ReadOnlySpan<ulong> hasValueBitMask,
        ObservationLayout layout,
        Shape shape,
        ShapeGraph graph,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        var ordinalsByShapeFieldIndex = layout.OrdinalsByShapeFieldIndex;
        for (var fieldIndex = 0; fieldIndex < shape.Fields.Length; fieldIndex++)
        {
            var field = shape.Fields[fieldIndex];
            var ordinal = ordinalsByShapeFieldIndex[fieldIndex];
            if (ordinal < 0 || !HasValue(hasValueBitMask, ordinal))
            {
                if (field.Presence != FieldPresence.Required)
                    continue;

                diagnostics.PushField(field.Name.Value);
                return Fail(ref diagnostics, new(ErrorCode.MissingRequiredShapeField, field.Name.Value));
            }

            diagnostics.PushField(field.Name.Value);
            if (!TryValidateFieldValue(valuesByOrdinal[ordinal], field, graph, ref diagnostics))
                return false;
            diagnostics.Pop();
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool HasValue(ReadOnlySpan<ulong> hasValueBitMask, int ordinal) =>
        (hasValueBitMask[ordinal >> 6] & (1UL << (ordinal & 63))) != 0;

    static int RequiredPresenceWordCount(int fieldCount) =>
        fieldCount == 0 ? 0 : ((fieldCount - 1) >> 6) + 1;

    static void RequireNoPresenceOutsideLayout(ReadOnlySpan<ulong> hasValueBitMask, int fieldCount)
    {
        var remainder = fieldCount & 63;
        if (remainder == 0 || hasValueBitMask.IsEmpty)
            return;

        var allowed = (1UL << remainder) - 1UL;
        if ((hasValueBitMask[^1] & ~allowed) != 0)
        {
            throw new ArgumentException(
                "The observation presence bitmap contains values outside the layout.",
                nameof(hasValueBitMask));
        }
    }

    static bool TryValidateShapeFields<TDiagnostics>(
        IReadOnlyDictionary<string, ObservationValue> fields,
        Shape shape,
        ShapeGraph? graph,
        bool keysAreCanonical,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (!keysAreCanonical)
        {
            foreach (var fieldName in fields.Keys)
            {
                if (shape.TryGetField(fieldName, out _))
                    continue;

                return Fail(
                    ref diagnostics,
                    new(ErrorCode.UnknownShapeField, fieldName, shape.Id.Value));
            }
        }

        var matchedCount = 0;
        foreach (var field in shape.Fields)
        {
            if (fields.ContainsKey(field.Name.Value))
                matchedCount++;
        }

        if (matchedCount != fields.Count)
        {
            var unknown = diagnostics.RequiresFailureDetails ? FindUnknownField(fields, shape) : null;
            return Fail(ref diagnostics, new(ErrorCode.UnknownShapeField, unknown, shape.Id.Value));
        }

        foreach (var field in shape.Fields)
        {
            if (!fields.TryGetValue(field.Name.Value, out var value))
            {
                if (field.Presence != FieldPresence.Required)
                    continue;

                diagnostics.PushField(field.Name.Value);
                return Fail(ref diagnostics, new(ErrorCode.MissingRequiredShapeField, field.Name.Value));
            }

            diagnostics.PushField(field.Name.Value);
            if (!TryValidateFieldValue(value, field, graph, ref diagnostics))
                return false;
            diagnostics.Pop();
        }

        return true;
    }

    static string? FindUnknownField(IReadOnlyDictionary<string, ObservationValue> fields, Shape shape)
    {
        string? unknown = null;
        foreach (var name in fields.Keys)
        {
            if (shape.TryGetField(name, out _))
                continue;
            if (unknown is null || string.CompareOrdinal(name, unknown) < 0)
                unknown = name;
        }
        return unknown;
    }

    static bool TryValidateFieldValue<TDiagnostics>(
        in ObservationValue value,
        FieldDefinition field,
        ShapeGraph? graph,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (!TryValidatePortableKind(value, ref diagnostics))
            return false;

        if (value.Kind == ObservationValueKind.Null)
        {
            if (field.Presence == FieldPresence.Required && field.Nullability == FieldNullability.NonNullable)
                return Fail(ref diagnostics, new(ErrorCode.RequiredValueIsNull));
            return field.Nullability == FieldNullability.Nullable
                || Fail(ref diagnostics, new(ErrorCode.NonNullableValueIsNull));
        }

        if (field.Cardinality != FieldCardinality.Many)
            return TryMatchTypeCore(field.Type, value, graph, MaxValidationDepth, ref diagnostics);

        if (value.Kind != ObservationValueKind.Array)
            return Fail(ref diagnostics, new(ErrorCode.ExpectedArray));

        var items = value.EnumerateArray();
        for (var index = 0; index < items.Length; index++)
        {
            diagnostics.PushElement(index);
            var item = items[index];
            if (!TryValidatePortableKind(item, ref diagnostics))
                return false;
            if (item.Kind == ObservationValueKind.Null)
            {
                if (field.Nullability == FieldNullability.NonNullable)
                    return Fail(ref diagnostics, new(ErrorCode.NonNullableValueIsNull));
            }
            else if (!TryMatchTypeCore(field.Type, item, graph, MaxValidationDepth, ref diagnostics))
            {
                return false;
            }
            diagnostics.Pop();
        }
        return true;
    }

    static bool TryMatchType<TDiagnostics>(
        TypeRef type,
        in ObservationValue value,
        ShapeGraph? graph,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics =>
        TryValidatePortableKind(value, ref diagnostics)
        && TryMatchTypeCore(type, value, graph, maxDepth, ref diagnostics);

    static bool TryMatchTypeCore<TDiagnostics>(
        TypeRef type,
        in ObservationValue value,
        ShapeGraph? graph,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (maxDepth <= 0)
            return Fail(ref diagnostics, new(ErrorCode.MaximumDepthExceeded, type: type));

        switch (type)
        {
            case ScalarTypeRef scalar:
                return MatchesScalarType(scalar.Kind, value)
                    || Fail(ref diagnostics, new(ErrorCode.ScalarTypeMismatch, numeric: (int)scalar.Kind));
            case EnumTypeRef enumType:
                return TryMatchInlineEnum(enumType, value, ref diagnostics);
            case EntityReferenceTypeRef:
                return TryGetString(value, out var entityReference)
                       && !string.IsNullOrWhiteSpace(entityReference)
                    || Fail(ref diagnostics, new(ErrorCode.EntityReferenceMismatch));
            case ArrayTypeRef arrayType:
                return TryMatchArray(arrayType, value, graph, maxDepth, ref diagnostics);
            case ObjectTypeRef objectType:
                return TryMatchObject(objectType, value, graph, maxDepth - 1, ref diagnostics);
            case QuantityTypeRef quantityType:
                return TryMatchQuantity(quantityType, value, maxDepth - 1, ref diagnostics);
            case NamedTypeRef namedType:
                return TryMatchNamed(namedType, value, graph, maxDepth - 1, ref diagnostics);
            case OpaqueRuntimeTypeRef opaqueType:
                return MatchesOpaque(opaqueType, value)
                    || Fail(ref diagnostics, new(ErrorCode.OpaqueTypeMismatch, opaqueType.RuntimeType));
            case JsonTypeRef jsonType:
                return TryMatchJson(jsonType, value, maxDepth - 1, ref diagnostics);
            default:
                return Fail(ref diagnostics, new(ErrorCode.UnsupportedType, type.GetType().Name));
        }
    }

    static bool TryMatchInlineEnum<TDiagnostics>(
        EnumTypeRef enumType,
        in ObservationValue value,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (!TryGetString(value, out var enumValue))
            return Fail(ref diagnostics, new(ErrorCode.ExpectedStringEnum));

        foreach (var member in enumType.Members)
        {
            if (string.Equals(member, enumValue, StringComparison.Ordinal))
                return true;
        }

        return Fail(ref diagnostics, new(ErrorCode.InvalidInlineEnum, enumType.Name, enumValue));
    }

    static bool TryMatchArray<TDiagnostics>(
        ArrayTypeRef arrayType,
        in ObservationValue value,
        ShapeGraph? graph,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (value.Kind != ObservationValueKind.Array)
            return Fail(ref diagnostics, new(ErrorCode.ExpectedArray));

        var items = value.EnumerateArray();
        for (var index = 0; index < items.Length; index++)
        {
            diagnostics.PushElement(index);
            var item = items[index];
            if (!TryMatchType(arrayType.ElementType, item, graph, maxDepth - 1, ref diagnostics))
                return false;
            diagnostics.Pop();
        }
        return true;
    }

    static bool TryMatchObject<TDiagnostics>(
        ObjectTypeRef objectType,
        in ObservationValue value,
        ShapeGraph? graph,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return Fail(ref diagnostics, new(ErrorCode.ExpectedObject));

        var matchedCount = 0;
        foreach (var field in objectType.Fields)
        {
            if (TryGetPropertyIgnoreCase(value.Fields, field.Name, out _))
                matchedCount++;
        }

        if (matchedCount != value.Fields.Count)
        {
            var unknown = diagnostics.RequiresFailureDetails
                ? FindUnknownObjectProperty(value.Fields, objectType.Fields)
                : null;
            return Fail(ref diagnostics, new(ErrorCode.UnknownObjectProperty, unknown));
        }

        foreach (var field in objectType.Fields)
        {
            if (!TryGetPropertyIgnoreCase(value.Fields, field.Name, out var fieldValue))
            {
                if (field.Presence != FieldPresence.Required)
                    continue;
                diagnostics.PushField(field.Name);
                return Fail(ref diagnostics, new(ErrorCode.MissingObjectProperty, field.Name));
            }

            diagnostics.PushField(field.Name);
            if (!TryValidateObjectField(
                    fieldValue,
                    field.Type,
                    field.Cardinality,
                    field.Presence,
                    field.Nullability,
                    graph,
                    maxDepth,
                    ref diagnostics))
            {
                return false;
            }
            diagnostics.Pop();
        }
        return true;
    }

    static bool TryMatchNamed<TDiagnostics>(
        NamedTypeRef namedType,
        in ObservationValue value,
        ShapeGraph? graph,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (graph is null)
            return Fail(ref diagnostics, new(ErrorCode.MissingGraph, namedType.TypeId.Value));
        if (!graph.TryGetType(namedType.TypeId, out var definition))
            return Fail(ref diagnostics, new(ErrorCode.MissingNamedType, namedType.TypeId.Value));

        return definition switch
        {
            TypeDefinition.Structural structural => TryMatchStructural(
                structural, value, graph, maxDepth, ref diagnostics),
            TypeDefinition.Enum enumType => TryMatchNamedEnum(enumType, value, ref diagnostics),
            TypeDefinition.Union unionType => TryMatchUnion(
                unionType, value, graph, maxDepth, ref diagnostics),
            _ => Fail(ref diagnostics, new(ErrorCode.UnsupportedNamedType, definition.GetType().Name))
        };
    }

    static bool TryMatchStructural<TDiagnostics>(
        TypeDefinition.Structural structural,
        in ObservationValue value,
        ShapeGraph graph,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return Fail(ref diagnostics, new(ErrorCode.ExpectedStructuralObject, structural.Id.Value));

        var matchedCount = 0;
        foreach (var field in structural.Fields)
        {
            if (TryGetPropertyIgnoreCase(value.Fields, field.Name.Value, out _))
                matchedCount++;
        }

        if (matchedCount != value.Fields.Count)
        {
            var unknown = diagnostics.RequiresFailureDetails
                ? FindUnknownStructuralProperty(value.Fields, structural)
                : null;
            return Fail(
                ref diagnostics,
                new(ErrorCode.UnknownStructuralProperty, unknown, structural.Id.Value));
        }

        foreach (var field in structural.Fields)
        {
            if (!TryGetPropertyIgnoreCase(value.Fields, field.Name.Value, out var fieldValue))
            {
                if (field.Presence != FieldPresence.Required)
                    continue;
                diagnostics.PushField(field.Name.Value);
                return Fail(
                    ref diagnostics,
                    new(ErrorCode.MissingStructuralProperty, field.Name.Value, structural.Id.Value));
            }

            diagnostics.PushField(field.Name.Value);
            if (!TryValidateObjectField(
                    fieldValue,
                    field.Type,
                    field.Cardinality,
                    field.Presence,
                    field.Nullability,
                    graph,
                    maxDepth,
                    ref diagnostics))
            {
                return false;
            }
            diagnostics.Pop();
        }
        return true;
    }

    static bool TryValidateObjectField<TDiagnostics>(
        in ObservationValue value,
        TypeRef type,
        FieldCardinality cardinality,
        FieldPresence presence,
        FieldNullability nullability,
        ShapeGraph? graph,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (!TryValidatePortableKind(value, ref diagnostics))
            return false;
        if (value.Kind == ObservationValueKind.Null)
        {
            if (presence == FieldPresence.Required && nullability == FieldNullability.NonNullable)
                return Fail(ref diagnostics, new(ErrorCode.RequiredValueIsNull));
            return nullability == FieldNullability.Nullable
                || Fail(ref diagnostics, new(ErrorCode.NonNullableValueIsNull));
        }

        if (cardinality != FieldCardinality.Many)
            return TryMatchTypeCore(type, value, graph, maxDepth, ref diagnostics);
        if (value.Kind != ObservationValueKind.Array)
            return Fail(ref diagnostics, new(ErrorCode.ExpectedArray));

        var items = value.EnumerateArray();
        for (var index = 0; index < items.Length; index++)
        {
            diagnostics.PushElement(index);
            var item = items[index];
            if (!TryValidatePortableKind(item, ref diagnostics))
                return false;
            if (item.Kind == ObservationValueKind.Null)
            {
                if (nullability == FieldNullability.NonNullable)
                    return Fail(ref diagnostics, new(ErrorCode.NonNullableValueIsNull));
            }
            else if (!TryMatchTypeCore(type, item, graph, maxDepth, ref diagnostics))
            {
                return false;
            }
            diagnostics.Pop();
        }
        return true;
    }

    static bool TryMatchNamedEnum<TDiagnostics>(
        TypeDefinition.Enum enumType,
        in ObservationValue value,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (!MatchesPrimitiveType(enumType.Underlying, value))
            return Fail(ref diagnostics, new(ErrorCode.NamedEnumMismatch, enumType.Id.Value));

        foreach (var enumValue in enumType.Values)
        {
            if (enumType.Underlying == PrimitiveType.String
                && TryGetString(value, out var stringValue)
                && string.Equals(enumValue.Name, stringValue, StringComparison.Ordinal))
            {
                return true;
            }
            if (enumValue.Value is { } literal
                && MatchesPrimitiveLiteral(enumType.Underlying, value, literal))
                return true;
        }

        return Fail(ref diagnostics, new(ErrorCode.NamedEnumMismatch, enumType.Id.Value));
    }

    static bool TryMatchUnion<TDiagnostics>(
        TypeDefinition.Union unionType,
        in ObservationValue value,
        ShapeGraph graph,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return Fail(ref diagnostics, new(ErrorCode.ExpectedUnionObject, unionType.Id.Value));

        if (!TryGetPropertyIgnoreCase(
                value.Fields,
                unionType.Discriminator.FieldName,
                out var discriminatorValue))
        {
            return Fail(
                ref diagnostics,
                new(ErrorCode.MissingUnionDiscriminator, unionType.Discriminator.FieldName, unionType.Id.Value));
        }

        diagnostics.PushField(unionType.Discriminator.FieldName);
        if (!TryValidatePortableKind(discriminatorValue, ref diagnostics))
            return false;
        diagnostics.Pop();

        if (!MatchesPrimitiveType(unionType.Discriminator.Type, discriminatorValue))
        {
            return Fail(
                ref diagnostics,
                new(
                    ErrorCode.UnionDiscriminatorTypeMismatch,
                    unionType.Discriminator.FieldName,
                    unionType.Id.Value,
                    (int)unionType.Discriminator.Type));
        }

        var matchingType = TryResolveUnionCase(unionType, discriminatorValue);

        if (matchingType is null)
        {
            return Fail(
                ref diagnostics,
                new(ErrorCode.InvalidUnionDiscriminator, unionType.Id.Value, unionType.Discriminator.FieldName));
        }

        return TryMatchTypeCore(matchingType, value, graph, maxDepth, ref diagnostics);
    }

    internal static TypeRef? TryResolveUnionCase(
        TypeDefinition.Union unionType,
        in ObservationValue discriminatorValue)
    {
        foreach (var unionCase in unionType.Cases)
        {
            if (MatchesPrimitiveLiteral(
                    unionType.Discriminator.Type,
                    discriminatorValue,
                    unionCase.DiscriminatorValue))
            {
                return unionCase.Type;
            }
        }

        return null;
    }

    static bool TryMatchQuantity<TDiagnostics>(
        QuantityTypeRef quantityType,
        in ObservationValue value,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (MatchesScalarType(quantityType.BaseKind, value))
            return true;
        if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            return Fail(ref diagnostics, new(ErrorCode.QuantityMismatch, type: quantityType));
        if (!TryValidatePortableDescendants(value, maxDepth, ref diagnostics))
            return false;

        if (TryGetPropertyIgnoreCase(value.Fields, "baseValue", out var directBaseValue)
            && directBaseValue.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
            && MatchesScalarType(quantityType.BaseKind, directBaseValue))
        {
            return true;
        }
        if (TryGetPropertyIgnoreCase(value.Fields, "value", out var wrappedValue)
            && wrappedValue.Kind == ObservationValueKind.Object
            && wrappedValue.Fields is not null
            && TryGetPropertyIgnoreCase(wrappedValue.Fields, "baseValue", out var wrappedBaseValue)
            && wrappedBaseValue.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
            && MatchesScalarType(quantityType.BaseKind, wrappedBaseValue))
        {
            return true;
        }

        return Fail(ref diagnostics, new(ErrorCode.QuantityMismatch, type: quantityType));
    }

    static bool TryMatchJson<TDiagnostics>(
        JsonTypeRef jsonType,
        in ObservationValue value,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        var matches = jsonType.Kind switch
        {
            JsonTypeKind.Any => value.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined),
            JsonTypeKind.Object => value.Kind == ObservationValueKind.Object,
            JsonTypeKind.Array => value.Kind == ObservationValueKind.Array,
            JsonTypeKind.String => value.Kind == ObservationValueKind.String,
            JsonTypeKind.Number => value.Kind is ObservationValueKind.Int64
                or ObservationValueKind.Double
                or ObservationValueKind.Decimal,
            JsonTypeKind.Boolean => value.Kind == ObservationValueKind.Bool,
            _ => false
        };
        if (!matches)
            return Fail(ref diagnostics, new(ErrorCode.JsonTypeMismatch, numeric: (int)jsonType.Kind));
        return value.Kind is not (ObservationValueKind.Object or ObservationValueKind.Array) || TryValidatePortableDescendants(value, maxDepth, ref diagnostics);
    }

    static bool TryValidatePortableDescendants<TDiagnostics>(
        in ObservationValue value,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (value.Kind == ObservationValueKind.Object && value.Fields is not null)
        {
            if (maxDepth <= 0)
                return Fail(ref diagnostics, new(ErrorCode.MaximumPortableDepthExceeded));

            if (diagnostics.RequiresFailureDetails)
            {
                foreach (var (name, child) in value.Fields.OrderBy(
                             static field => field.Key,
                             StringComparer.Ordinal))
                {
                    if (!TryValidatePortableField(name, child, maxDepth, ref diagnostics))
                        return false;
                }
            }
            else if (value.Fields is ImmutableDictionary<string, ObservationValue> immutableFields)
            {
                foreach (var (name, child) in immutableFields)
                {
                    if (!TryValidatePortableField(name, child, maxDepth, ref diagnostics))
                        return false;
                }
            }
            else if (value.Fields is ImmutableSortedDictionary<string, ObservationValue> sortedFields)
            {
                foreach (var (name, child) in sortedFields)
                {
                    if (!TryValidatePortableField(name, child, maxDepth, ref diagnostics))
                        return false;
                }
            }
            else
            {
                foreach (var (name, child) in value.Fields)
                {
                    if (!TryValidatePortableField(name, child, maxDepth, ref diagnostics))
                        return false;
                }
            }
        }
        else if (value.Kind == ObservationValueKind.Array)
        {
            if (maxDepth <= 0)
                return Fail(ref diagnostics, new(ErrorCode.MaximumPortableDepthExceeded));
            var items = value.EnumerateArray();
            for (var index = 0; index < items.Length; index++)
            {
                diagnostics.PushElement(index);
                var item = items[index];
                if (!TryValidatePortableTree(item, maxDepth - 1, ref diagnostics))
                    return false;
                diagnostics.Pop();
            }
        }
        return true;
    }

    static bool TryValidatePortableField<TDiagnostics>(
        string name,
        in ObservationValue child,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        diagnostics.PushField(name);
        if (!TryValidatePortableTree(child, maxDepth - 1, ref diagnostics))
            return false;
        diagnostics.Pop();
        return true;
    }

    static bool TryValidatePortableTree<TDiagnostics>(
        in ObservationValue value,
        int maxDepth,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics =>
        TryValidatePortableKind(value, ref diagnostics)
        && TryValidatePortableDescendants(value, maxDepth, ref diagnostics);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TryValidatePortableKind<TDiagnostics>(
        in ObservationValue value,
        ref TDiagnostics diagnostics)
        where TDiagnostics : IValidationDiagnostics
    {
        if (value.Kind == ObservationValueKind.Undefined)
            return Fail(ref diagnostics, new(ErrorCode.UndefinedValue));
        if (value.Kind == ObservationValueKind.Double && !double.IsFinite(value.Double))
            return Fail(ref diagnostics, new(ErrorCode.NonFiniteNumber));
        return true;
    }

    static bool MatchesOpaque(OpaqueRuntimeTypeRef opaqueType, in ObservationValue value) =>
        opaqueType.RuntimeType switch
        {
            "DateOnly" => value.TryGetDateOnly(out _),
            "TimeOnly" => value.TryGetTimeOnly(out _),
            _ => false
        };

    static bool MatchesScalarType(ScalarTypeKind scalarType, in ObservationValue value) =>
        scalarType switch
        {
            ScalarTypeKind.Bool => value.TryGetBoolean(out _),
            ScalarTypeKind.Int32 => value.TryGetInt32(out _),
            ScalarTypeKind.Int64 => value.TryGetInt64(out _),
            ScalarTypeKind.Decimal => value.TryGetDecimal(out _),
            ScalarTypeKind.String => TryGetString(value, out _),
            ScalarTypeKind.Guid => TryGetString(value, out var guidValue) && Guid.TryParse(guidValue, out _),
            ScalarTypeKind.Date => value.TryGetDateOnly(out _),
            ScalarTypeKind.DateTime => value.TryGetDateTimeOffset(out _),
            ScalarTypeKind.Instant => value.TryGetInstant(out _),
            ScalarTypeKind.Bytes => value.Kind == ObservationValueKind.Bytes,
            _ => false
        };

    static bool MatchesPrimitiveType(PrimitiveType primitiveType, in ObservationValue value) =>
        TryMapPrimitiveType(primitiveType, out var scalarType)
        && MatchesScalarType(scalarType, value);

    static bool MatchesPrimitiveLiteral(
        PrimitiveType primitiveType,
        in ObservationValue value,
        string literal)
    {
        if (!MatchesPrimitiveType(primitiveType, value))
            return false;

        switch (value.Kind)
        {
            case ObservationValueKind.String:
            case ObservationValueKind.DateTimeOffset:
            case ObservationValueKind.DateOnly:
            case ObservationValueKind.TimeOnly:
            case ObservationValueKind.TimeSpan:
                return string.Equals(value.String, literal, StringComparison.Ordinal);
            case ObservationValueKind.Int64:
                return MatchesFormattedScalar(value.Int64, literal);
            case ObservationValueKind.Double:
                return MatchesFormattedScalar(value.Double, literal);
            case ObservationValueKind.Decimal:
                return MatchesFormattedScalar(value.Decimal, literal);
            case ObservationValueKind.Bool:
                return literal == (value.Bool ? "true" : "false");
            case ObservationValueKind.Bytes:
                return MatchesCanonicalBase64(value.Bytes.Span, literal);
            default:
                return false;
        }
    }

    static bool MatchesFormattedScalar<T>(T value, ReadOnlySpan<char> literal)
        where T : ISpanFormattable
    {
        Span<char> formatted = stackalloc char[64];
        return value.TryFormat(
                   formatted,
                   out var charsWritten,
                   format: default,
                   CultureInfo.InvariantCulture)
               && literal.SequenceEqual(formatted[..charsWritten]);
    }

    static bool MatchesCanonicalBase64(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> literal)
    {
        var encodedLength = ((bytes.Length + 2) / 3) * 4;
        if (encodedLength <= 1_024)
        {
            Span<char> encoded = stackalloc char[encodedLength];
            return Convert.TryToBase64Chars(bytes, encoded, out var charsWritten)
                   && literal.SequenceEqual(encoded[..charsWritten]);
        }

        var rented = ArrayPool<char>.Shared.Rent(encodedLength);
        try
        {
            return Convert.TryToBase64Chars(bytes, rented, out var charsWritten)
                   && literal.SequenceEqual(rented.AsSpan(0, charsWritten));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    static bool TryMapPrimitiveType(PrimitiveType primitiveType, out ScalarTypeKind scalarType)
    {
        switch (primitiveType)
        {
            case PrimitiveType.Bool:
                scalarType = ScalarTypeKind.Bool;
                return true;
            case PrimitiveType.Int32:
                scalarType = ScalarTypeKind.Int32;
                return true;
            case PrimitiveType.Int64:
                scalarType = ScalarTypeKind.Int64;
                return true;
            case PrimitiveType.Decimal:
                scalarType = ScalarTypeKind.Decimal;
                return true;
            case PrimitiveType.String:
                scalarType = ScalarTypeKind.String;
                return true;
            case PrimitiveType.Guid:
                scalarType = ScalarTypeKind.Guid;
                return true;
            case PrimitiveType.Date:
                scalarType = ScalarTypeKind.Date;
                return true;
            case PrimitiveType.DateTime:
                scalarType = ScalarTypeKind.DateTime;
                return true;
            case PrimitiveType.Instant:
                scalarType = ScalarTypeKind.Instant;
                return true;
            case PrimitiveType.Bytes:
                scalarType = ScalarTypeKind.Bytes;
                return true;
            default:
                scalarType = default;
                return false;
        }
    }

    static bool TryGetString(in ObservationValue value, out string result)
    {
        switch (value.Kind)
        {
            case ObservationValueKind.String:
            case ObservationValueKind.DateTimeOffset:
            case ObservationValueKind.DateOnly:
            case ObservationValueKind.TimeOnly:
            case ObservationValueKind.TimeSpan:
                result = value.GetString() ?? string.Empty;
                return true;
            default:
                result = string.Empty;
                return false;
        }
    }

    static bool TryGetPropertyIgnoreCase(
        IReadOnlyDictionary<string, ObservationValue> obj,
        string propertyName,
        out ObservationValue value)
    {
        if (obj.TryGetValue(propertyName, out value))
            return true;
        foreach (var property in obj)
        {
            if (!string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    static string? FindUnknownObjectProperty(
        IReadOnlyDictionary<string, ObservationValue> fields,
        IReadOnlyList<ObjectFieldTypeDef> definitions)
    {
        string? unknown = null;
        foreach (var propertyName in fields.Keys)
        {
            var known = false;
            foreach (var field in definitions)
            {
                if (!string.Equals(field.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    continue;
                known = true;
                break;
            }
            if (!known && (unknown is null || string.CompareOrdinal(propertyName, unknown) < 0))
                unknown = propertyName;
        }
        return unknown;
    }

    static string? FindUnknownStructuralProperty(
        IReadOnlyDictionary<string, ObservationValue> fields,
        TypeDefinition.Structural structural)
    {
        string? unknown = null;
        foreach (var propertyName in fields.Keys)
        {
            var known = false;
            foreach (var field in structural.Fields)
            {
                if (!string.Equals(field.Name.Value, propertyName, StringComparison.OrdinalIgnoreCase))
                    continue;
                known = true;
                break;
            }
            if (!known && (unknown is null || string.CompareOrdinal(propertyName, unknown) < 0))
                unknown = propertyName;
        }
        return unknown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Fail<TDiagnostics>(ref TDiagnostics diagnostics, in ValidationFailure failure)
        where TDiagnostics : IValidationDiagnostics
    {
        diagnostics.Report(failure);
        return false;
    }

    interface IValidationDiagnostics
    {
        bool RequiresFailureDetails { get; }
        void PushField(string fieldName);
        void PushElement(int index);
        void Pop();
        void Report(in ValidationFailure failure);
    }

    readonly struct NoDiagnostics : IValidationDiagnostics
    {
        public bool RequiresFailureDetails => false;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public void PushField(string fieldName) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public void PushElement(int index) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Pop() { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Report(in ValidationFailure failure) { }
    }

    sealed class DetailedDiagnostics : IValidationDiagnostics
    {
        readonly List<PathSegment> path = [];

        public bool RequiresFailureDetails => true;
        public string? Error { get; private set; }
        public void PushField(string fieldName) => path.Add(new(PathKind.Field, fieldName));
        public void PushElement(int index) => path.Add(new(PathKind.Element, index: index));
        public void Pop() => path.RemoveAt(path.Count - 1);

        public void Report(in ValidationFailure failure)
        {
            if (Error is not null)
                return;

            var context = FormatContext();
            Error = failure.Code switch
            {
                ErrorCode.RootMustBeObject =>
                    "An observation must be a concrete, present, non-null object value.",
                ErrorCode.UndefinedValue =>
                    $"An observation cannot contain an undefined value at '{FormatJsonPath()}'. Omit an absent optional field instead.",
                ErrorCode.NonFiniteNumber =>
                    $"An observation cannot contain a non-finite number at '{FormatJsonPath()}'.",
                ErrorCode.UnknownShapeField =>
                    $"Observation contains unknown field '{failure.Name ?? "<unknown>"}' for shape '{failure.Detail}'.",
                ErrorCode.MissingRequiredShapeField =>
                    $"Observation is missing required field '{failure.Name}'.",
                ErrorCode.RequiredValueIsNull => $"{context} is required and cannot be null.",
                ErrorCode.NonNullableValueIsNull => $"{context} is non-nullable and cannot be null.",
                ErrorCode.ExpectedArray => $"{context} expects an array value.",
                ErrorCode.MaximumDepthExceeded =>
                    $"{context} exceeded maximum validation depth while checking type '{DescribeType(failure.Type!)}'.",
                ErrorCode.MaximumPortableDepthExceeded =>
                    $"{context} exceeded maximum portable value depth.",
                ErrorCode.ScalarTypeMismatch =>
                    $"{context} does not match expected scalar type '{(ScalarTypeKind)failure.Numeric}'.",
                ErrorCode.ExpectedStringEnum => $"{context} must be a string enum value.",
                ErrorCode.InvalidInlineEnum =>
                    $"{context} value '{failure.Detail}' is not a valid member of enum '{failure.Name}'.",
                ErrorCode.EntityReferenceMismatch =>
                    $"{context} must contain a non-empty entity reference string.",
                ErrorCode.OpaqueTypeMismatch =>
                    $"{context} does not match opaque runtime type '{failure.Name}'.",
                ErrorCode.JsonTypeMismatch =>
                    $"{context} does not match JSON type '{(JsonTypeKind)failure.Numeric}'.",
                ErrorCode.UnsupportedType => $"{context} references unsupported type '{failure.Name}'.",
                ErrorCode.ExpectedObject => $"{context} expects an object value.",
                ErrorCode.UnknownObjectProperty =>
                    $"{context} contains unknown property '{failure.Name ?? "<unknown>"}'.",
                ErrorCode.MissingObjectProperty =>
                    $"{ParentContext()} is missing required property '{failure.Name}'.",
                ErrorCode.MissingGraph =>
                    $"{context} references named type '{failure.Name}', but no shape graph was provided for resolution.",
                ErrorCode.MissingNamedType => $"{context} references missing named type '{failure.Name}'.",
                ErrorCode.UnsupportedNamedType =>
                    $"{context} resolved unsupported named type '{failure.Name}'.",
                ErrorCode.ExpectedStructuralObject =>
                    $"{context} expects an object value for type '{failure.Name}'.",
                ErrorCode.UnknownStructuralProperty =>
                    $"{context} contains unknown property '{failure.Name ?? "<unknown>"}' for type '{failure.Detail}'.",
                ErrorCode.MissingStructuralProperty =>
                    $"{ParentContext()} is missing required property '{failure.Name}' for type '{failure.Detail}'.",
                ErrorCode.NamedEnumMismatch => $"{context} does not match enum type '{failure.Name}'.",
                ErrorCode.ExpectedUnionObject =>
                    $"{context} expects an object value for union type '{failure.Name}'.",
                ErrorCode.MissingUnionDiscriminator =>
                    $"{context} is missing discriminator field '{failure.Name}' for union type '{failure.Detail}'.",
                ErrorCode.UnionDiscriminatorTypeMismatch =>
                    $"{context} discriminator field '{failure.Name}' does not match expected primitive type '{(PrimitiveType)failure.Numeric}'.",
                ErrorCode.InvalidUnionDiscriminator =>
                    $"{context} discriminator field '{failure.Detail}' is not valid for union type '{failure.Name}'.",
                ErrorCode.QuantityMismatch =>
                    $"{context} must contain a '{((QuantityTypeRef)failure.Type!).BaseKind}' base value for quantity '{((QuantityTypeRef)failure.Type!).Quantity}'.",
                _ => "The observation does not adhere to the supplied shape."
            };
        }

        string ParentContext()
        {
            if (path.Count == 0)
                return "value";
            var terminal = path[^1];
            path.RemoveAt(path.Count - 1);
            var context = FormatContext();
            path.Add(terminal);
            return context;
        }

        string FormatContext()
        {
            if (path.Count == 0)
                return "value";
            StringBuilder builder = new();
            for (var index = 0; index < path.Count; index++)
            {
                var segment = path[index];
                if (segment.Kind == PathKind.Field)
                {
                    if (index == 0)
                        builder.Append("field '").Append(segment.FieldName).Append('\'');
                    else
                        builder.Append('.').Append(segment.FieldName);
                }
                else
                {
                    builder.Append(" element at index ").Append(segment.Index);
                }
            }
            return builder.ToString();
        }

        string FormatJsonPath()
        {
            StringBuilder builder = new("$");
            foreach (var segment in path)
            {
                if (segment.Kind == PathKind.Field)
                    builder.Append('.').Append(segment.FieldName);
                else
                    builder.Append('[').Append(segment.Index).Append(']');
            }
            return builder.ToString();
        }
    }

    readonly struct ValidationFailure(
        ErrorCode code,
        string? name = null,
        string? detail = null,
        int numeric = 0,
        TypeRef? type = null
        )
    {
        public ErrorCode Code { get; } = code;
        public string? Name { get; } = name;
        public string? Detail { get; } = detail;
        public int Numeric { get; } = numeric;
        public TypeRef? Type { get; } = type;
    }

    readonly struct PathSegment(PathKind kind, string? fieldName = null, int index = -1)
    {
        public PathKind Kind { get; } = kind;
        public string? FieldName { get; } = fieldName;
        public int Index { get; } = index;
    }

    enum PathKind { Field, Element }

    enum ErrorCode
    {
        RootMustBeObject,
        UndefinedValue,
        NonFiniteNumber,
        UnknownShapeField,
        MissingRequiredShapeField,
        RequiredValueIsNull,
        NonNullableValueIsNull,
        ExpectedArray,
        MaximumDepthExceeded,
        MaximumPortableDepthExceeded,
        ScalarTypeMismatch,
        ExpectedStringEnum,
        InvalidInlineEnum,
        EntityReferenceMismatch,
        OpaqueTypeMismatch,
        JsonTypeMismatch,
        UnsupportedType,
        ExpectedObject,
        UnknownObjectProperty,
        MissingObjectProperty,
        MissingGraph,
        MissingNamedType,
        UnsupportedNamedType,
        ExpectedStructuralObject,
        UnknownStructuralProperty,
        MissingStructuralProperty,
        NamedEnumMismatch,
        ExpectedUnionObject,
        MissingUnionDiscriminator,
        UnionDiscriminatorTypeMismatch,
        InvalidUnionDiscriminator,
        QuantityMismatch
    }

    static string DescribeType(TypeRef type) => type switch
    {
        ScalarTypeRef scalar => scalar.Kind.ToString(),
        EnumTypeRef enumType => $"Enum({enumType.Name})",
        EntityReferenceTypeRef entityRef => $"EntityRef({entityRef.Entity.Value})",
        ArrayTypeRef array => $"Array({DescribeType(array.ElementType)})",
        ObjectTypeRef => "Object",
        QuantityTypeRef quantity => $"Quantity({quantity.Quantity},{quantity.BaseKind})",
        NamedTypeRef named => $"Named({named.TypeId.Value})",
        OpaqueRuntimeTypeRef opaque => $"Opaque({opaque.RuntimeType})",
        JsonTypeRef json => $"Json({json.Kind})",
        _ => type.GetType().Name
    };
}
