using System.Collections.Immutable;

namespace Cohesive.Tests.Model;

public sealed class ObservationValidatorTests(Xunit.Abstractions.ITestOutputHelper output)
{
    const int AllocationWarmupIterations = 1_000;

    [Fact]
    public void PreparedObjectLookupPreservesCaseDuplicateRulesAndInstanceIsolation()
    {
        var type = new ObjectTypeRef([new("name", new ScalarTypeRef(ScalarTypeKind.String))]);
        Assert.True(ObservationValidator.TryValidateAgainstType(Object(("NAME", ObservationValue.FromString("first"))), type, out _));
        // Two spellings still trigger the existing unknown-property/count rule.
        Assert.False(ObservationValidator.TryValidateAgainstType(Object(
            ("name", ObservationValue.FromString("valid")), ("NAME", ObservationValue.FromInt64(1))), type, out _));
        Assert.False(ObservationValidator.TryValidateAgainstType(Object(("NAME", ObservationValue.FromInt64(1))), type, out _));
        Assert.True(ObservationValidator.TryValidateAgainstType(Object(("name", ObservationValue.FromString("last"))), type, out _));
    }

    [Fact]
    public void PreparedUnknownPropertyDiagnosticIsOrdinalAndTypeScoped()
    {
        var type = new ObjectTypeRef([new("known", new ScalarTypeRef(ScalarTypeKind.String))]);
        var value = Object(("known", ObservationValue.FromString("ok")),
            ("z-extra", ObservationValue.FromString("z")), ("a-extra", ObservationValue.FromString("a")));
        Assert.False(ObservationValidator.TryValidateAgainstType(value, type, out var error));
        Assert.Contains("a-extra", error);
        var other = new ObjectTypeRef([new("known", new ScalarTypeRef(ScalarTypeKind.Int64))]);
        Assert.False(ObservationValidator.TryValidateAgainstType(Object(("known", ObservationValue.FromString("ok"))), other, out _));
    }

    [Fact]
    public void WarmExactObjectValidationDoesNotAllocate()
    {
        var type = new ObjectTypeRef([new("name", new ScalarTypeRef(ScalarTypeKind.String))]);
        var value = Object(("name", ObservationValue.FromString("ok")));
        for (var i = 0; i < AllocationWarmupIterations; i++)
            _ = ObservationValidator.TryValidateAgainstType(value, type, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var valid = true;
        for (var i = 0; i < 1000; i++) valid &= ObservationValidator.TryValidateAgainstType(value, type, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(valid);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WideCaseInsensitiveValidationHasBoundedTemporaryAllocation()
    {
        var type = new ObjectTypeRef([..Enumerable.Range(0, 128).Select(i =>
            new ObjectFieldTypeDef($"field{i}", new ScalarTypeRef(ScalarTypeKind.String)))]);
        var value = ObservationValue.FromObject(Enumerable.Range(0, 128).ToImmutableDictionary(
            i => $"FIELD{i}", _ => ObservationValue.FromString("ok")));
        for (var i = 0; i < AllocationWarmupIterations; i++)
            _ = ObservationValidator.TryValidateAgainstType(value, type, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var valid = ObservationValidator.TryValidateAgainstType(value, type, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(valid);
        Assert.InRange(allocated, 0, 10_000);
    }

    [Fact]
    public void NamedStructuralPreparationDoesNotCrossGraphBoundaries()
    {
        var reference = new NamedTypeRef(new("shared-id"));
        var text = new TypeDefinition.Structural(reference.TypeId, [new(new("value"), new ScalarTypeRef(ScalarTypeKind.String))]);
        var number = new TypeDefinition.Structural(reference.TypeId, [new(new("value"), new ScalarTypeRef(ScalarTypeKind.Int64))]);
        var textGraph = new ShapeGraph(new("text"), [], [text]);
        var numberGraph = new ShapeGraph(new("number"), [], [number]);
        var invalid = Object(("VALUE", ObservationValue.FromString("ok")), ("extra", ObservationValue.FromString("x")));
        Assert.False(ObservationValidator.TryValidateAgainstType(invalid, reference, out _, textGraph));
        Assert.False(ObservationValidator.TryValidateAgainstType(invalid, reference, out _, numberGraph));
        var validText = Object(("VALUE", ObservationValue.FromString("ok")));
        Assert.True(ObservationValidator.TryValidateAgainstType(validText, reference, out _, textGraph));
        Assert.False(ObservationValidator.TryValidateAgainstType(validText, reference, out _, numberGraph));
    }

    [Fact]
    public void UnknownFieldPreparationIsReusedAcrossInstances()
    {
        var type = new ObjectTypeRef([..Enumerable.Range(0, 128).Select(i =>
            new ObjectFieldTypeDef($"field{i}", new ScalarTypeRef(ScalarTypeKind.String)))]);
        var first = ObservationValue.FromObject(Enumerable.Range(0, 128).ToImmutableDictionary(
            i => $"field{i}", _ => ObservationValue.FromString("ok")).Add("extra", ObservationValue.FromString("first")));
        var second = ObservationValue.FromObject(first.Fields!.ToImmutableDictionary().SetItem("extra", ObservationValue.FromString("second")));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var firstValid = ObservationValidator.TryValidateAgainstType(first, type, out var firstError);
        var cold = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var valid = ObservationValidator.TryValidateAgainstType(second, type, out var secondError);
        var warm = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(firstValid);
        Assert.False(valid);
        Assert.Equal(firstError, secondError);
        Assert.True(cold > warm);
        Assert.InRange(cold, 0, 20_000);
        Assert.InRange(warm, 0, 1_000);
    }

    [Fact]
    public void ExactPropertyWinsAfterCaseInsensitiveIndexCreation()
    {
        var type = new ObjectTypeRef([
            new("alias", new ScalarTypeRef(ScalarTypeKind.String)),
            new("name", new ScalarTypeRef(ScalarTypeKind.String)),
            new("NAME", new ScalarTypeRef(ScalarTypeKind.Int64))]);
        var value = Object(("ALIAS", ObservationValue.FromString("fallback")),
            ("name", ObservationValue.FromString("exact")), ("NAME", ObservationValue.FromInt64(7)));
        Assert.True(ObservationValidator.TryValidateAgainstType(value, type, out _));
    }

    [Fact]
    public void PreparedLinksPreserveRecursionAndArePublishedOnce()
    {
        var root = new NamedTypeRef(new("recursive-structure"));
        var definition = new TypeDefinition.Structural(root.TypeId,
            [new(new("child"), root, presence: FieldPresence.Optional)]);
        var graph = new ShapeGraph(new("recursive-graph"), [], [definition]);
        ObservationValidationPlan[] plans = new ObservationValidationPlan[32];
        Parallel.For(0, plans.Length, i => plans[i] = ObservationValidationPlan.Get(root, graph));
        foreach (var plan in plans)
        {
            Assert.Same(plans[0], plan);
            Assert.Same(definition, plan.Definition);
            Assert.Same(plan, plan.Children[0]);
        }
        Assert.True(ObservationValidator.TryValidateAgainstType(Object(), root, out _, graph));
        Assert.True(ObservationValidator.TryValidateAgainstType(Object(("child", Object())), root, out _, graph));
        var deep = Object();
        for (var i = 0; i < 70; i++) deep = Object(("child", deep));
        Assert.False(ObservationValidator.TryValidateAgainstType(deep, root, out var error, graph));
        Assert.Contains("maximum validation depth", error);
    }

    [Fact]
    public void MissingNamedLinksRemainGraphSpecificAndInstanceDiagnosticsRemainFresh()
    {
        var root = new NamedTypeRef(new("same-type"));
        var missing = new ShapeGraph(new("same-graph-id"), [], []);
        var defined = new ShapeGraph(new("same-graph-id"), [],
            [new TypeDefinition.Structural(root.TypeId, [new(new("required"), new ScalarTypeRef(ScalarTypeKind.String))])]);
        Assert.False(ObservationValidator.TryValidateAgainstType(Object(), root, out var missingError, missing));
        Assert.False(ObservationValidator.TryValidateAgainstType(Object(), root, out var fieldError, defined));
        Assert.NotEqual(missingError, fieldError);
        Assert.True(ObservationValidator.TryValidateAgainstType(Object(("required", ObservationValue.FromString("ok"))), root, out _, defined));
        Assert.False(ObservationValidator.TryValidateAgainstType(Object(), root, out var noGraphError));
        Assert.NotEqual(missingError, noGraphError);
    }

    [Fact]
    public void PreparedChildLinksAreSharedAcrossRootsInOneGraph()
    {
        var child = new NamedTypeRef(new("child"));
        var graph = new ShapeGraph(new("shared"), [],
            [new TypeDefinition.Structural(child.TypeId, [new(new("value"), new ScalarTypeRef(ScalarTypeKind.String))])]);
        var left = new ObjectTypeRef([new("left", child)]);
        var right = new ObjectTypeRef([new("right", child)]);
        var leftPlan = ObservationValidationPlan.Get(left, graph);
        var rightPlan = ObservationValidationPlan.Get(right, graph);
        Assert.Same(leftPlan.Children[0], rightPlan.Children[0]);
        Assert.Same(leftPlan.Children[0], ObservationValidationPlan.Get(child, graph));
        Assert.NotSame(ObservationValidationPlan.Get(child, graph),
            ObservationValidationPlan.Get(child, new ShapeGraph(graph.Id, [], graph.NamedTypes)));
    }

    [Fact]
    public void PreparedGraphAndRootCanBeCollected()
    {
        var (graph, root) = PrepareCollectibleGraph();
        for (var i = 0; i < 8 && (graph.IsAlive || root.IsAlive); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(graph.IsAlive);
        Assert.False(root.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (WeakReference Graph, WeakReference Root) PrepareCollectibleGraph()
    {
        var root = new NamedTypeRef(new("collectible"));
        var graph = new ShapeGraph(new("collectible"), [],
            [new TypeDefinition.Structural(root.TypeId, [new(new("child"), root, presence: FieldPresence.Optional)])]);
        _ = ObservationValidationPlan.Get(root, graph);
        return (new(graph), new(root));
    }

    [Fact]
    public void PreparedNamedValidationSeparatesColdPreparationAndZeroAllocationWarmExecution()
    {
        var text = new TypeDefinition.Enum(new("text"), PrimitiveType.String, [new("value", "value")]);
        var root = new NamedTypeRef(new("root"));
        var definition = new TypeDefinition.Structural(root.TypeId, [..Enumerable.Range(0, 128).Select(i =>
            new StructuralField(new($"field{i}"), new NamedTypeRef(text.Id)))]);
        var graph = new ShapeGraph(new("cold-boundary"), [], [text, definition]);
        var value = ObservationValue.FromObject(Enumerable.Range(0, 128).ToImmutableDictionary(
            i => $"field{i}", _ => ObservationValue.FromString("value")));
        // Initialize runtime/type dispatch with unrelated keys; target preparation remains cold.
        var warmup = new NamedTypeRef(text.Id);
        var warmupGraph = new ShapeGraph(new("warmup"), [], [text]);
        for (var i = 0; i < AllocationWarmupIterations; i++)
            _ = ObservationValidator.TryValidateAgainstType(ObservationValue.FromString("value"), warmup, out _, warmupGraph);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var firstValid = ObservationValidator.TryValidateAgainstType(value, root, out _, graph);
        var cold = GC.GetAllocatedBytesForCurrentThread() - before;
        var valid = true;
        for (var i = 0; i < AllocationWarmupIterations; i++)
            _ = ObservationValidator.TryValidateAgainstType(value, root, out _, graph);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) valid &= ObservationValidator.TryValidateAgainstType(value, root, out _, graph);
        var warm = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"128-field named graph validation: cold={cold} B, 1000 warm checks={warm} B");
        Assert.True(firstValid && valid);
        Assert.InRange(cold, 1, 64_000);
        Assert.Equal(0, warm);
    }

    [Fact]
    public void DynamicPreparedRootsCanExpireWhileGraphRemainsAlive()
    {
        var graph = new ShapeGraph(new("long-lived"), [],
            [new TypeDefinition.Enum(new("text"), PrimitiveType.String, [new("value", "value")])]);
        var root = PrepareCollectibleRoot(graph);
        for (var i = 0; i < 8 && root.IsAlive; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(root.IsAlive);
        GC.KeepAlive(graph);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference PrepareCollectibleRoot(ShapeGraph graph)
    {
        var root = new ObjectTypeRef([new("temporary", new NamedTypeRef(new("text")))]);
        _ = ObservationValidationPlan.Get(root, graph);
        return new(root);
    }

    [Theory]
    [InlineData("00", true)]
    [InlineData("Original", true)]
    [InlineData("original", false)]
    [InlineData("unknown", false)]
    public void TypeValidationSharesNamedEnumObservationSemantics(string text, bool valid)
    {
        var type = new NamedTypeRef(new("Code"));
        var shape = new Shape(new("root"), [new(new("value"), type)]);
        var graph = new ShapeGraph(new("codes"), [shape],
            [new TypeDefinition.Enum(new("Code"), PrimitiveType.String, [new("Original", "00")])]);
        var value = ObservationValue.FromString(text);
        Assert.Equal(valid, ObservationValidator.TryValidateAgainstType(value, type, out _, graph));
        Assert.Equal(valid, ObservationValidator.TryValidateAgainstShape(
            ImmutableDictionary<string, ObservationValue>.Empty.Add("value", value), shape, out _, graph));
        Assert.False(ObservationValidator.TryValidateAgainstType(value, type, out _));
    }

    [Fact]
    public void NamedEnumTypeValidationDoesNotAllocateAfterWarmup()
    {
        var type = new NamedTypeRef(new("Code"));
        var graph = new ShapeGraph(new("codes"), [],
            [new TypeDefinition.Enum(new("Code"), PrimitiveType.String, [new("Original", "00")])]);
        var value = ObservationValue.FromString("00");
        for (var i = 0; i < AllocationWarmupIterations; i++)
            Assert.True(ObservationValidator.TryValidateAgainstType(value, type, out _, graph));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var success = true;
        for (var i = 0; i < 10_000; i++)
            success &= ObservationValidator.TryValidateAgainstType(value, type, out _, graph);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(success);
        Assert.Equal(0, allocated);
    }


    [Fact]
    public void RequiredNullableOrdinalValidationDoesNotAllocateAfterWarmup()
    {
        Shape definition = new(new("required-nullable"),
            [new(new("note"), new ScalarTypeRef(ScalarTypeKind.String), nullability: FieldNullability.Nullable)]);
        GraphShapeId shape = new(new ShapeGraph(new("nullable/v1"), [definition]), definition.Id);
        var layout = ObservationLayout.Create(shape, ["note"]);
        ObservationValue[] values = [ObservationValue.Null];
        ulong[] present = [1];
        for (var iteration = 0; iteration < AllocationWarmupIterations; iteration++)
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

        for (var iteration = 0; iteration < AllocationWarmupIterations; iteration++)
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
        for (var iteration = 0; iteration < AllocationWarmupIterations; iteration++)
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

        for (var iteration = 0; iteration < AllocationWarmupIterations; iteration++)
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
