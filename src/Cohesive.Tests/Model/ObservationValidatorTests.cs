using System.Collections.Immutable;

namespace Cohesive.Tests.Model;

public sealed class ObservationValidatorTests
{
    [Fact]
    public void RequiredNullableOrdinalValidationDoesNotAllocateAfterWarmup()
    {
        Shape definition = new(new("required-nullable"),
            [new(new("note"), new ScalarTypeRef(ScalarTypeKind.String), nullability: FieldNullability.Nullable)]);
        GraphShapeId shape = new(new ShapeGraph(new("nullable/v1"), [definition]), definition.Id);
        var layout = ObservationLayout.Create(shape, ["note"]);
        ObservationValue[] values = [ObservationValue.Null];
        ulong[] present = [1];
        for (var iteration = 0; iteration < 100; iteration++)
            _ = ObservationValidator.TryValidateAgainstShape(shape, layout, values, present, out _);
        var valid = true;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1_000; iteration++)
            valid &= ObservationValidator.TryValidateAgainstShape(shape, layout, values, present, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(valid);
        Assert.Equal(0, allocated);
        present[0] = 0;
        Assert.False(ObservationValidator.TryValidateAgainstShape(shape, layout, values, present, out _));
    }

    [Fact]
    public void TryValidateAgainstShape_OrdinalBuffers_PreservesDiagnosticsAndDoesNotAllocateAfterWarmup()
    {
        const int Iterations = 1_000;
        var (graph, definition, value) = CreateComplexFixture();
        GraphShapeId shape = new(graph, definition.Id);
        var layout = ObservationLayout.Create(
            shape,
            definition.Fields.Reverse().Select(static field => field.Name.Value));
        var values = new ObservationValue[layout.Count];
        var presence = new ulong[(layout.Count + 63) / 64];
        foreach (var (fieldIdentity, fieldValue) in value.Fields!)
        {
            var ordinal = layout.GetOrdinal(fieldIdentity);
            values[ordinal] = fieldValue;
            presence[ordinal >> 6] |= 1UL << (ordinal & 63);
        }

        for (var iteration = 0; iteration < 100; iteration++)
            _ = ObservationValidator.TryValidateAgainstShape(shape, layout, values, presence, out _);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var isValid = true;
        string? validationError = null;
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            isValid &= ObservationValidator.TryValidateAgainstShape(
                shape,
                layout,
                values,
                presence,
                out validationError);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(isValid, validationError);
        Assert.Null(validationError);
        Assert.Equal(0, allocated);

        values[layout.GetOrdinal("status")] = ObservationValue.FromString("suspended");
        Assert.False(ObservationValidator.TryValidateAgainstShape(
            shape,
            layout,
            values,
            presence,
            out validationError));
        var invalidValue = value.WithField(
            FieldPath.Parse("status"),
            ObservationValue.FromString("suspended"));
        Assert.False(ObservationValidator.TryValidateAgainstShape(
            invalidValue,
            definition,
            out var dictionaryValidationError,
            graph));
        Assert.Equal(dictionaryValidationError, validationError);
    }

    [Fact]
    public void TryValidateAgainstShape_OrdinalBuffers_PreservesDeclarationOrderForFieldsOmittedFromLayout()
    {
        Shape definition = new(
            new("state"),
            [
                new(new("first"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("second"), new ScalarTypeRef(ScalarTypeKind.Int64))
            ]);
        ShapeGraph graph = new(new("state-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var layout = ObservationLayout.Create(shape, ["second"]);
        ObservationValue[] values = [ObservationValue.FromString("invalid")];
        ulong[] presence = [1UL];

        var isValid = ObservationValidator.TryValidateAgainstShape(
            shape,
            layout,
            values,
            presence,
            out var validationError);

        Assert.False(isValid);
        Assert.Contains("required field 'first'", validationError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateAgainstShape_ValidComplexValue_DoesNotAllocateAfterWarmup()
    {
        var (graph, shape, value) = CreateComplexFixture();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            _ = ObservationValidator.TryValidateAgainstShape(
                value,
                shape,
                out _,
                graph);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var isValid = true;
        string? validationError = null;
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            isValid &= ObservationValidator.TryValidateAgainstShape(
                value,
                shape,
                out validationError,
                graph);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(isValid, validationError);
        Assert.Null(validationError);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TryValidateAgainstShape_CanonicalPrimitiveLiterals_DoNotAllocateAfterWarmup()
    {
        var bytes = new byte[] { 0, 1, 2, 3, 254, 255 };
        TypeDefinition.Enum boolean = new(
            id: new("boolean-literal"),
            underlying: PrimitiveType.Bool,
            values: [new("Enabled", "true")]);
        TypeDefinition.Enum integer = new(
            id: new("integer-literal"),
            underlying: PrimitiveType.Int64,
            values: [new("Answer", "42")]);
        TypeDefinition.Enum decimalNumber = new(
            id: new("decimal-literal"),
            underlying: PrimitiveType.Decimal,
            values: [new("Fraction", "12.5")]);
        TypeDefinition.Enum binary = new(
            id: new("binary-literal"),
            underlying: PrimitiveType.Bytes,
            values: [new("Token", Convert.ToBase64String(bytes))]);
        Shape shape = new(
            id: new("primitive-literal-state"),
            fields:
            [
                new(new("boolean"), new NamedTypeRef(boolean.Id)),
                new(new("integer"), new NamedTypeRef(integer.Id)),
                new(new("decimal"), new NamedTypeRef(decimalNumber.Id)),
                new(new("binary"), new NamedTypeRef(binary.Id))
            ]);
        ShapeGraph graph = new(
            id: new("primitive-literal-state-v1"),
            shapes: [shape],
            namedTypes: [boolean, integer, decimalNumber, binary]);
        var value = Object(
            ("boolean", ObservationValue.FromBool(true)),
            ("integer", ObservationValue.FromInt64(42)),
            ("decimal", ObservationValue.FromDecimal(12.5m)),
            ("binary", ObservationValue.FromBytes(bytes)));

        for (var iteration = 0; iteration < 100; iteration++)
            _ = ObservationValidator.TryValidateAgainstShape(value, shape, out _, graph);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var isValid = true;
        string? validationError = null;
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            isValid &= ObservationValidator.TryValidateAgainstShape(
                value,
                shape,
                out validationError,
                graph);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(isValid, validationError);
        Assert.Null(validationError);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TryValidateAgainstShape_RejectsNonCanonicalKeysFromCaseInsensitiveDictionary()
    {
        Shape shape = new(
            id: new("state"),
            fields: [new(name: new("name"), type: new ScalarTypeRef(ScalarTypeKind.String))]);
        IReadOnlyDictionary<string, ObservationValue> fields =
            new Dictionary<string, ObservationValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["NAME"] = ObservationValue.FromString("Ada")
            };

        var isValid = ObservationValidator.TryValidateAgainstShape(
            fields,
            shape,
            out var validationError);

        Assert.False(isValid);
        Assert.Contains("unknown field 'NAME'", validationError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateAgainstShape_RejectsInvalidNamedEnumUnionAndQuantityValues()
    {
        var (graph, shape, validValue) = CreateComplexFixture();

        var invalidEnum = validValue.WithField(
            FieldPath.Parse("status"),
            ObservationValue.FromString("suspended"));
        var invalidUnion = validValue.WithField(
            FieldPath.Parse("payload.kind"),
            ObservationValue.FromString("missing"));
        var invalidQuantity = validValue.WithField(
            FieldPath.Parse("distance.baseValue"),
            ObservationValue.FromString("far"));

        Assert.False(ObservationValidator.TryValidateAgainstShape(
            invalidEnum,
            shape,
            out var enumError,
            graph));
        Assert.False(ObservationValidator.TryValidateAgainstShape(
            invalidUnion,
            shape,
            out var unionError,
            graph));
        Assert.False(ObservationValidator.TryValidateAgainstShape(
            invalidQuantity,
            shape,
            out var quantityError,
            graph));

        Assert.Contains("does not match enum type 'status'", enumError, StringComparison.Ordinal);
        Assert.Contains("not valid for union type 'payload'", unionError, StringComparison.Ordinal);
        Assert.Contains("base value for quantity 'Distance'", quantityError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateAgainstShape_RejectsNonPortableJsonAtDeterministicPath()
    {
        Shape shape = new(
            id: new("state"),
            fields: [new(name: new("data"), type: new JsonTypeRef(JsonTypeKind.Object))]);
        ShapeGraph graph = new(new("state-v1"), [shape]);
        var value = Object(
            ("data", Object(
                ("zeta", ObservationValue.FromDouble(double.PositiveInfinity)),
                ("alpha", ObservationValue.FromDouble(double.NaN)))));

        var isValid = ObservationValidator.TryValidateAgainstShape(
            value,
            shape,
            out var validationError,
            graph);

        Assert.False(isValid);
        Assert.Contains("$.data.alpha", validationError, StringComparison.Ordinal);
        Assert.Contains("non-finite number", validationError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateAgainstShape_BoundsArbitraryJsonDepth()
    {
        Shape shape = new(
            id: new("state"),
            fields: [new(name: new("data"), type: new JsonTypeRef(JsonTypeKind.Any))]);
        ShapeGraph graph = new(new("state-v1"), [shape]);
        var nested = ObservationValue.FromString("terminal");
        for (var depth = 0; depth < 65; depth++)
            nested = ObservationValue.FromArray([nested]);
        var value = Object(("data", nested));

        var isValid = ObservationValidator.TryValidateAgainstShape(
            value,
            shape,
            out var validationError,
            graph);

        Assert.False(isValid);
        Assert.Contains("maximum portable value depth", validationError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateAgainstShape_BoundsRecursiveNamedUnionMatching()
    {
        TypeId recursiveTypeId = new("recursive");
        TypeDefinition.Union recursive = new(
            recursiveTypeId,
            new UnionDiscriminator("kind"),
            [new UnionCase("Loop", new NamedTypeRef(recursiveTypeId), "loop")]);
        Shape shape = new(
            id: new("state"),
            fields: [new(name: new("payload"), type: new NamedTypeRef(recursiveTypeId))]);
        ShapeGraph graph = new(new("state-v1"), [shape], [recursive]);
        var value = Object(("payload", Object(("kind", ObservationValue.FromString("loop")))));

        var isValid = ObservationValidator.TryValidateAgainstShape(
            value,
            shape,
            out var validationError,
            graph);

        Assert.False(isValid);
        Assert.Contains("maximum validation depth", validationError, StringComparison.Ordinal);
    }

    static (ShapeGraph Graph, Shape Shape, ObservationValue Value) CreateComplexFixture()
    {
        TypeDefinition.Enum status = new(
            id: new("status"),
            underlying: PrimitiveType.String,
            values: [new("Active", "active"), new("Inactive", "inactive")]);
        TypeDefinition.Union payload = new(
            id: new("payload"),
            discriminator: new("kind"),
            cases:
            [
                new(
                    name: "Text",
                    type: new ObjectTypeRef(
                    [
                        new("kind", new ScalarTypeRef(ScalarTypeKind.String)),
                        new("message", new ScalarTypeRef(ScalarTypeKind.String))
                    ]),
                    discriminatorValue: "text")
            ]);
        Shape shape = new(
            id: new("state"),
            fields:
            [
                new(name: new("status"), type: new NamedTypeRef(status.Id)),
                new(name: new("payload"), type: new NamedTypeRef(payload.Id)),
                new(name: new("distance"), type: new QuantityTypeRef("Distance")),
                new(name: new("data"), type: new JsonTypeRef(JsonTypeKind.Object)),
                new(
                    name: new("aliases"),
                    type: new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.String))),
                new(
                    name: new("owner"),
                    type: new EntityReferenceTypeRef(new("Customer"))),
                new(name: new("date"), type: new OpaqueRuntimeTypeRef("DateOnly"))
            ]);
        ShapeGraph graph = new(new("state-v1"), [shape], [status, payload]);
        var value = Object(
            ("status", ObservationValue.FromString("active")),
            ("payload", Object(
                ("kind", ObservationValue.FromString("text")),
                ("message", ObservationValue.FromString("hello")))),
            ("distance", Object(("baseValue", ObservationValue.FromDecimal(12.5m)))),
            ("data", Object(
                ("enabled", ObservationValue.FromBool(true)),
                ("items", ObservationValue.FromArray(
                [
                    ObservationValue.FromString("one"),
                    ObservationValue.FromInt64(2)
                ])))),
            ("aliases", ObservationValue.FromArray(
            [
                ObservationValue.FromString("Ada"),
                ObservationValue.FromString("A")
            ])),
            ("owner", ObservationValue.FromString("customer-1")),
            ("date", ObservationValue.FromDateOnly(new(2026, 8, 26))));
        return (graph, shape, value);
    }

    static ObservationValue Object(params (string Name, ObservationValue Value)[] fields) =>
        ObservationValue.FromObject(fields.ToImmutableDictionary(
            static field => field.Name,
            static field => field.Value,
            StringComparer.Ordinal));
}
