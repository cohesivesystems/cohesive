using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.Model;

public sealed class InputConstraintReviewTests
{
    public sealed record DictionaryInput([property: MaxLength(2)] Dictionary<string, string> Values);
    public sealed record ReadOnlyDictionaryInput([property: MaxLength(2)] IReadOnlyDictionary<string, string> Values);
    public sealed record ListInput([property: MaxLength(2)] List<string> Values);

    [Fact]
    public void LengthAttributesRejectObjectProjectedCollectionsDuringAuthoring()
    {
        var input = new DictionaryInput(new() { ["a"] = "a" });
        Assert.True(Validator.TryValidateObject(input, new ValidationContext(input), [], true));
        Assert.Equal(ObservationValueKind.Object, ObservationValue.FromObject(input).GetProperty("Values").Kind);
        Assert.Contains("constraint.lengthType", Assert.Throws<NotSupportedException>(() =>
            new ClrShapeGraphBuilder().UseDataAnnotations().AddShape<DictionaryInput>().Build()).Message);
        Assert.Throws<NotSupportedException>(() =>
            new ClrShapeGraphBuilder().UseDataAnnotations().AddShape<ReadOnlyDictionaryInput>().Build());
        var shapes = new ClrShapeGraphBuilder().UseDataAnnotations().AddShape<ListInput>().BuildResult();
        Assert.True(ObservationValidator.ValidateInput(new ListInput(["a", "b"]), shapes).IsValid);
        Assert.False(ObservationValidator.ValidateInput(new ListInput(["a", "b", "c"]), shapes).IsValid);
    }

    [Theory]
    [InlineData("id", "ok", true)] [InlineData("ID", "ok", true)]
    [InlineData("id", "long", false)] [InlineData("ID", "long", false)]
    public void NamedStructuralAdmissionAndContractsShareConstraintValueLookup(string spelling, string text, bool expected)
    {
        var scalar = new ScalarTypeRef(ScalarTypeKind.String);
        ImmutableArray<ShapeConstraint> constraints = [new RequiredConstraint(FieldPath.FromField("id")), new MaxLengthConstraint(2, FieldPath.FromField("id"))];
        var field = new FieldDefinition(new("id"), scalar, presence: FieldPresence.Optional);
        Shape shape = new(new("input"), [field], constraints: constraints);
        TypeDefinition.Structural structural = new(new("record"), [new(new("id"), scalar, presence: FieldPresence.Optional)], constraints: constraints);
        var named = new NamedTypeRef(structural.Id);
        var graph = new ShapeGraph(new("lookup"), [shape], [structural]);
        var value = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { [spelling] = ObservationValue.FromString(text) });
        Assert.Equal(expected, ObservationValidator.TryValidateAgainstType(value, named, out _, graph));
        Assert.Equal(expected, PortableExecutionValidator.Validate(PortableValue.Concrete(new(named), value), graph).IsValid);
        var contract = ValueContract.FromShape(shape);
        Assert.Equal(expected, contract.IsSatisfiedByConstant(value));
        Assert.Equal(expected, PortableExecutionValidator.Validate(PortableValue.Concrete(contract, value)).IsValid);
        var failure = ConstraintSemantics.Evaluate(new ConstraintValues(value, structural), constraints);
        Assert.Equal(expected, failure is null);
        if (!expected) Assert.Equal("constraint.maxLength", failure!.Value.Code);
    }

    [Fact]
    public void ExactSpellingWinsAndMissingDeclaredFieldsKeepOptionalSemantics()
    {
        var shape = new Shape(new("input"), [new(new("id"), new ScalarTypeRef(ScalarTypeKind.String), presence: FieldPresence.Optional)],
            constraints: [new MaxLengthConstraint(2, FieldPath.FromField("id"))]);
        var contract = ValueContract.FromShape(shape);
        var value = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        { ["ID"] = ObservationValue.FromString("too long"), ["id"] = ObservationValue.FromString("ok") });
        Assert.True(contract.IsSatisfiedByConstant(value));
        Assert.True(contract.IsSatisfiedByConstant(ObservationValue.EmptyObject));
        var badTarget = new MaxLengthConstraint(2, FieldPath.FromField("ID"));
        Assert.Throws<NotSupportedException>(() => new ValueContract(contract.Type, constraints: [badTarget]));
        Assert.Equal("constraint.unsupportedTarget",
            ConstraintSemantics.Evaluate(new ConstraintValues(value, shape: shape), [badTarget])!.Value.Code);
    }

    [Fact]
    public void ShapeResolutionIsIdempotentAndDoesNotDuplicateRules()
    {
        var required = new RequiredConstraint();
        Shape shape = new(new("input"), [new(new("id"), new ScalarTypeRef(ScalarTypeKind.String), constraints: [new MaxLengthConstraint(2)])],
            constraints: [required]);
        var graph = new ShapeGraph(new("resolution"), [shape]);
        var identity = new QualifiedShapeId(graph.Id, shape.Id);
        var unresolved = new ValueContract(shape: identity, constraints: [required]);
        var resolved = unresolved.ResolveShape(graph);
        Assert.Equal(2, resolved.Constraints!.Value.Length);
        Assert.Same(resolved, resolved.ResolveShape(graph));
        Assert.Equal(Fingerprint(resolved), Fingerprint(resolved.ResolveShape(graph)));
        var authored = ValueContract.FromShape(shape, identity);
        Assert.Equal(authored, resolved);
        Assert.Same(authored, authored.ResolveShape(graph));

        // The array's bound is local; the item's bound stays on the retained graph shape.
        var many = new ValueContract(shape: identity, cardinality: FieldCardinality.Many,
            constraints: [new MaxLengthConstraint(2)]).ResolveShape(graph);
        Assert.Same(many, many.ResolveShape(graph));
        Assert.Single(many.Constraints!.Value);
        Assert.Equal(identity, many.Shape);
        var valid = Row("ok"); var invalid = Row("long");
        Check([valid], true); Check([invalid], false); Check([valid, valid, valid], false);
        Assert.False(PortableExecutionValidator.Validate(PortableValue.Concrete(many, ObservationValue.FromImmutableArray([valid]))).IsValid);
        void Check(ImmutableArray<ObservationValue> items, bool expected) => Assert.Equal(expected,
            PortableExecutionValidator.Validate(PortableValue.Concrete(many, ObservationValue.FromImmutableArray(items)), graph).IsValid);
    }

    [Fact]
    public void PersistedConstraintMessagesIntentionallyParticipateInContractIdentity()
    {
        var before = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String), constraints: [new MaxLengthConstraint(2, message: "Short ID")]);
        var after = new ValueContract(before.Type, constraints: [new MaxLengthConstraint(2, message: "ID is too long")]);
        Assert.NotEqual(before, after);
        Assert.NotEqual(Fingerprint(before), Fingerprint(after));
        foreach (var text in new[] { "ok", "long" })
            Assert.Equal(before.IsSatisfiedByConstant(ObservationValue.FromString(text)), after.IsSatisfiedByConstant(ObservationValue.FromString(text)));
        Assert.Equal(before, JsonSerializer.Deserialize<ValueContract>(JsonSerializer.Serialize(before)));
    }

    static ObservationValue Row(string id) => ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["id"] = ObservationValue.FromString(id) });

    static ExecutionDefinitionFingerprint Fingerprint(ValueContract input)
    {
        var definition = new TransitionDefinition(input, new(new JsonTypeRef(JsonTypeKind.Object)),
            new(new ScalarTypeRef(ScalarTypeKind.String)), [],
            new(new("body"), [new OutcomeTransitionNode(new("outcome"), TransitionOutcomeDisposition.NoChange, Expr.Const("ok"))]));
        return TransitionDefinitionDocuments.Create(new("constraint-test"), new("1"), definition,
            new(new("tests"), new("input-constraints"), DocumentOrigin.Generated)).Metadata.Fingerprint;
    }
}
