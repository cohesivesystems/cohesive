using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Tests.Model;

public sealed class InputConstraintTests
{
    public sealed record Request([property: Required, MinLength(1), MaxLength(64)] string? RequestId);
    public sealed record OptionalRequest([property: MaxLength(64)] string? RequestId);
    public sealed record EmptyAllowed([property: Required(AllowEmptyStrings = true)] string? RequestId);
    public sealed record Bytes([property: MaxLength(2)] byte[] Values);
    public sealed record WrongLength([property: MinLength(1)] int Value);
    public sealed record Items([property: MinLength(1), MaxLength(2)] string[]? Values);
    public sealed record Nested(Request Child, Request[] Children);
    public sealed record Unsupported([property: Range(1, 4)] int Value);
    public sealed record Conflicting([property: MinLength(3), MaxLength(2)] string Value);
    public sealed record InvalidMaximum([property: MaxLength(0)] string Value);
    public sealed record CustomValidation([property: Custom] string Value);
    sealed class CustomAttribute : ValidationAttribute;

    static GraphShapeId ShapeFor<T>() where T : notnull =>
        new ClrShapeGraphBuilder().UseDataAnnotations().AddShape<T>().BuildResult().GetShape<T>();

    public static IEnumerable<object?[]> Strings()
    {
        foreach (var value in new[] { null, "", " ", "ok", new string('a', 63), new string('a', 64), new string('a', 65),
                     string.Concat(Enumerable.Repeat("\U00010000", 32)), string.Concat(Enumerable.Repeat("\U00010000", 32)) + "a",
                     string.Concat(Enumerable.Repeat("e\u0301", 32)), string.Concat(Enumerable.Repeat("e\u0301", 32)) + "a" })
            yield return [value];
    }

    [Theory, MemberData(nameof(Strings))]
    public void RecordProjectionMatchesDataAnnotationsAcrossAdmissionPaths(string? text)
    {
        var request = new Request(text);
        var expected = Validator.TryValidateObject(request, new ValidationContext(request), [], true);
        var scoped = ShapeFor<Request>();
        var shape = scoped.Graph.GetShape(scoped.ShapeId);
        var value = ObservationValue.FromObject(request);
        Assert.Equal(expected, ObservationValidator.ValidateInput(value, scoped).IsValid);
        Assert.Equal(expected, ObservationValidator.TryValidateAgainstShape(value, shape, out _, scoped.Graph));
        Assert.Equal(expected, ObservationValidator.TryValidateAgainstShape(value.Fields!, shape, out _, scoped.Graph));
        var layout = ObservationLayout.Create(scoped);
        ObservationValue[] slots = [value.GetProperty(nameof(Request.RequestId))];
        Assert.Equal(expected, ObservationValidator.TryValidateAgainstShape(scoped, layout, slots, [1], out _));
        var admission = new Observation.CompleteProjectionAdmission(layout);
        Assert.Equal(expected, ObservationValidator.TryValidateCompleteProjection(admission, slots));
        if (expected) admission.Admit([.. slots]);
        else Assert.Throws<ArgumentException>(() => admission.Admit([.. slots]));
        var contract = ValueContract.FromShape(shape, scoped.QualifiedId);
        Assert.Equal(expected, PortableExecutionValidator.Validate(PortableValue.Concrete(contract, value), scoped.Graph).IsValid);
    }

    [Fact]
    public void AttributesAreOptInAndDiagnosticsIdentifyTheField()
    {
        var plain = new ClrShapeGraphBuilder().AddShape<Request>().BuildResult().GetShape<Request>();
        var longValue = ObservationValue.FromObject(new Request(new string('a', 65)));
        Assert.True(ObservationValidator.ValidateInput(longValue, plain).IsValid);
        var diagnostic = Assert.Single(ObservationValidator.ValidateInput(longValue, ShapeFor<Request>()).Diagnostics);
        Assert.Equal("constraint.maxLength", diagnostic.Code);
        Assert.Equal("$.RequestId", diagnostic.Location);
        var missing = ObservationValue.FromObject(new Dictionary<string, ObservationValue>());
        Assert.Equal("constraint.required", Assert.Single(ObservationValidator.ValidateInput(missing, ShapeFor<Request>()).Diagnostics).Code);
        var optional = ShapeFor<OptionalRequest>();
        // Inferred presence can remain required; length alone must not change that declaration.
        Assert.True(ObservationValidator.ValidateInput(ObservationValue.FromObject(new OptionalRequest(null)), optional).IsValid);
        Assert.True(ObservationValidator.ValidateInput(ObservationValue.FromObject(new EmptyAllowed(" ")), ShapeFor<EmptyAllowed>()).IsValid);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, false)]
    public void CollectionsCountItems(int count, bool expected)
    {
        var input = new Items(Enumerable.Repeat(new string('x', 100), count).ToArray());
        Assert.Equal(expected, Validator.TryValidateObject(input, new ValidationContext(input), [], true));
        Assert.Equal(expected, ObservationValidator.ValidateInput(ObservationValue.FromObject(input), ShapeFor<Items>()).IsValid);
    }

    [Fact]
    public void TypedAdmissionUsesRegisteredRecordAndByteArrayCounts()
    {
        var shapes = new ClrShapeGraphBuilder().UseDataAnnotations().AddShape<Request>().AddShape<Bytes>().BuildResult();
        Assert.True(ObservationValidator.ValidateInput(new Request("ok"), shapes).IsValid);
        Assert.False(ObservationValidator.ValidateInput(new Request(new string('x', 65)), shapes).IsValid);
        Assert.True(ObservationValidator.ValidateInput(new Bytes([1, 2]), shapes).IsValid);
        Assert.False(ObservationValidator.ValidateInput(new Bytes([1, 2, 3]), shapes).IsValid);
    }

    [Fact]
    public void NestedConstraintsAreCheckedAtTheirOwnShape()
    {
        var shape = ShapeFor<Nested>();
        var good = new Request("ok"); var bad = new Request(new string('x', 65));
        Assert.True(ObservationValidator.ValidateInput(ObservationValue.FromObject(new Nested(good, [good])), shape).IsValid);
        foreach (var input in new[] { new Nested(bad, [good]), new Nested(good, [bad]) })
            Assert.False(ObservationValidator.ValidateInput(ObservationValue.FromObject(input), shape).IsValid);
    }

    [Fact]
    public void UnsupportedAttributesAndConflictingBoundsFailDuringAuthoring()
    {
        Assert.Contains("constraint.unsupportedAttribute", Assert.Throws<NotSupportedException>(() => ShapeFor<Unsupported>()).Message);
        Assert.Throws<NotSupportedException>(() => ShapeFor<CustomValidation>());
        Assert.Throws<NotSupportedException>(() => ShapeFor<WrongLength>());
        Assert.Contains("constraint.conflictingBounds", Assert.Throws<ArgumentException>(() => ShapeFor<Conflicting>()).Message);
        Assert.Throws<ArgumentException>(() => ShapeFor<InvalidMaximum>());
    }

    [Fact]
    public void ExplicitShapeTargetsAndUnsupportedConstraintsNeverPassSilently()
    {
        var field = new FieldDefinition(new("id"), new ScalarTypeRef(ScalarTypeKind.String), presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable);
        foreach (var constraints in new ImmutableArray<ShapeConstraint>[] {
                     [new MaxLengthConstraint(2, FieldPath.FromField("id"))],
                     [new MaxLengthConstraint(2, FieldPath.FromField("unknown"))],
                     [new RegexConstraint(".*")] })
        {
            var shape = new Shape(new("Input"), [field], constraints: constraints);
            var graph = new ShapeGraph(new("test"), [shape]); var scoped = new GraphShapeId(graph, shape.Id);
            var value = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["id"] = ObservationValue.FromString("long") });
            Assert.False(ObservationValidator.ValidateInput(value, scoped).IsValid);
            Assert.False(ObservationValidator.TryValidateAgainstShape(scoped, ObservationLayout.Create(scoped), [ObservationValue.FromString("long")], [1], out _));
        }
        var optionalField = new FieldDefinition(new("id"), field.Type, presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable, constraints: [new MaxLengthConstraint(2)]);
        var optional = new Shape(new("Input"), [optionalField]);
        Assert.True(ObservationValidator.TryValidateAgainstShape(new Dictionary<string, ObservationValue>(), optional, out _));
        Assert.False(ObservationValidator.TryValidateAgainstShape(new Dictionary<string, ObservationValue> { ["id"] = ObservationValue.FromInt64(1) }, optional, out var error));
        Assert.Contains("constraint.lengthType", error);
    }

    [Fact]
    public void ValueContractsResolveConstraintsThroughTheirQualifiedShape()
    {
        var scoped = ShapeFor<Request>();
        var shape = scoped.Graph.GetShape(scoped.ShapeId);
        Assert.NotNull(ValueContract.FromShape(shape).Constraints);
        Assert.NotNull(ValueContract.FromField(shape.Fields[0]).Constraints);
        var original = ValueContract.FromShape(shape, scoped.QualifiedId);
        var copy = JsonSerializer.Deserialize<ValueContract>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original, copy); Assert.Equal(original.GetHashCode(), copy.GetHashCode());
        var invalid = PortableValue.Concrete(copy, ObservationValue.FromObject(new Request(new string('a', 65))));
        Assert.False(PortableExecutionValidator.Validate(invalid, scoped.Graph).IsValid);
        Assert.False(PortableExecutionValidator.Validate(invalid).IsValid);
    }

    [Theory]
    [InlineData("ok", true)] [InlineData("long", false)] [InlineData("", false)]
    public void BoundNamedStructuralPlansRetainFieldAndShapeConstraints(string text, bool expected)
    {
        var scalar = new ScalarTypeRef(ScalarTypeKind.String);
        TypeDefinition.Structural record = new(new("record"),
            [new(new("id"), scalar, constraints: [new MaxLengthConstraint(2)])],
            constraints: [new RequiredConstraint(FieldPath.FromField("id"), rejectEmptyStrings: true)]);
        var named = new NamedTypeRef(record.Id);
        Shape root = new(new("root"), [new(new("item"), named)]);
        Shape physical = new(new("physical"), [new(new("id"), scalar)]);
        var graph = new ShapeGraph(new("bound"), [root, physical], [record]);
        var scoped = new GraphShapeId(graph, root.Id);
        var rootLayout = ObservationLayout.Create(scoped);
        var recordLayout = ObservationLayout.Create(new(graph, physical.Id));
        var bound = ObservationValidationPlan.Get(named, graph).BindLayouts(graph,
            new Dictionary<TypeRef, ObservationLayout>(ReferenceEqualityComparer.Instance) { [named] = recordLayout });
        var row = ObservationValue.FromObject(CompactOrdinalObservationFields.Create(recordLayout, [ObservationValue.FromString(text)]));
        Assert.Equal(expected, ObservationValidator.TryValidateAgainstShape(scoped, rootLayout, [row], [1], out var interpreted));
        Assert.Equal(expected, ObservationValidator.TryValidateAgainstShape(scoped, rootLayout, [row], [1], out var prepared, [bound]));
        Assert.Equal(interpreted, prepared);
    }

    [Fact]
    public void UndeclaredTargetsCannotDisappearThroughValueContracts()
    {
        Shape shape = new(new("input"), [], constraints: [new MaxLengthConstraint(1, FieldPath.FromField("missing"))]);
        var graph = new ShapeGraph(new("targets"), [shape]);
        Assert.Throws<NotSupportedException>(() => ValueContract.FromShape(shape));
        var contract = new ValueContract(shape: new(graph.Id, shape.Id));
        Assert.False(PortableExecutionValidator.Validate(PortableValue.Concrete(contract, ObservationValue.EmptyObject), graph).IsValid);
    }

    sealed class ExplicitMinimum : IClrShapeMetadataProvider
    {
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context) => context.Target == ClrShapeMetadataTarget.Field
            ? new() { Constraints = [new MinLengthConstraint(65)] } : ClrShapeMetadata.Empty;
    }

    [Fact]
    public void ExplicitAndAttributeBoundsCombineWithoutOverridingEachOther()
    {
        Assert.Contains("constraint.conflictingBounds", Assert.Throws<ArgumentException>(() =>
            new ClrShapeGraphBuilder().UseDataAnnotations().AddMetadataProvider(new ExplicitMinimum())
                .AddShape<Request>().Build()).Message);
    }

    [Fact]
    public void RequiredPresenceAndNonblankStringsAreDistinctPortableRules()
    {
        var presence = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String), constraints: [new RequiredConstraint()]);
        var nonblank = new ValueContract(presence.Type, constraints: [new RequiredConstraint(rejectEmptyStrings: true)]);
        Assert.True(presence.IsSatisfiedByConstant(ObservationValue.FromString("")));
        Assert.False(nonblank.IsSatisfiedByConstant(ObservationValue.FromString("")));
        Assert.False(nonblank.IsSatisfiedByConstant(ObservationValue.Null));
        var copy = JsonSerializer.Deserialize<ValueContract>(JsonSerializer.Serialize(nonblank))!;
        Assert.Equal(nonblank, copy); Assert.Equal(nonblank.GetHashCode(), copy.GetHashCode());
        Assert.False(copy.IsSatisfiedByConstant(ObservationValue.FromString(" ")));
        Assert.DoesNotContain("Constraints", JsonSerializer.Serialize(new ValueContract(presence.Type)));
    }

    [Fact]
    public void WarmSuccessfulConstraintValidationAllocatesNothing()
    {
        var shape = ShapeFor<Request>();
        var layout = ObservationLayout.Create(shape);
        var value = ObservationValue.FromObject(new Request("ok"));
        ObservationValue[] slots = [ObservationValue.FromString("ok")]; ulong[] presence = [1];
        for (var i = 0; i < 10_000; i++)
        {
            ObservationValidator.ValidateInput(value, shape);
            ObservationValidator.TryValidateAgainstShape(shape, layout, slots, presence, out _);
        }
        var minimum = long.MaxValue;
        var accepted = true;
        for (var sample = 0; sample < 3; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                accepted &= ObservationValidator.ValidateInput(value, shape).IsValid;
                accepted &= ObservationValidator.TryValidateAgainstShape(shape, layout, slots, presence, out _);
            }
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.True(accepted); Assert.Equal(0, minimum);
    }
}
