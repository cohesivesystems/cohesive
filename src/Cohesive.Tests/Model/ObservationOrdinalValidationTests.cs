using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.Tests.Model;

public sealed class ObservationOrdinalValidationTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public void ExactBindingsPreserveOriginalOutcomesAndDiagnostics(bool packed, bool permuted, bool named)
    {
        var fixture = new Fixture(named, permuted);
        ObservationValue[] slots = new ObservationValue[fixture.RecordLayout.Count];
        Put("field0", ObservationValue.FromInt64(7)); Put("field64", ObservationValue.Null); Put("field128", ObservationValue.FromInt64(9));
        var good = Record();
        Check(good);
        Put("field0", ObservationValue.Null); Check(Record());
        Put("field0", default); Check(Record());
        Put("field0", ObservationValue.FromString("bad")); Check(Record());
        Put("field0", ObservationValue.FromDouble(1.5)); Check(Record());
        Put("field0", ObservationValue.FromInt64(7));
        Check(ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["FIELD0"] = ObservationValue.FromInt64(2) }));
        Check(ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["field0"] = ObservationValue.FromInt64(2), ["FIELD0"] = ObservationValue.FromInt64(3) }));
        Check(ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["field0"] = ObservationValue.FromInt64(2), ["unknown"] = ObservationValue.FromInt64(3) }));
        Check(ObservationValue.Null); Check(ObservationValue.FromBool(true));
        // A semantically identical layout object is insufficient evidence for the bound path.
        var other = ObservationLayout.Create(new(fixture.RecordLayout.Graph, fixture.RecordLayout.ShapeId.ShapeId),
            fixture.RecordLayout.FieldIdentities.Reverse().ToImmutableArray());
        var otherSlots = new ObservationValue[other.Count];
        other.TryGetOrdinal("field0", out var otherOrdinal); otherSlots[otherOrdinal] = ObservationValue.FromInt64(3);
        Check(ObservationValue.FromObject(CompactOrdinalObservationFields.Create(other, otherSlots)));
        Assert.Null(fixture.Original.BoundLayout); Assert.Null(fixture.Original.BoundGraph);
        Assert.NotSame(fixture.Original, fixture.Bound);

        void Put(string name, ObservationValue value) { fixture.RecordLayout.TryGetOrdinal(name, out var ordinal); slots[ordinal] = value; }
        ObservationValue Record() => packed
            ? ObservationValue.FromObject(CompactOrdinalObservationFields.Create(fixture.RecordLayout, slots))
            : ObservationValue.FromOrdinalFields(new(fixture.RecordLayout, ImmutableArray.CreateRange(slots), slots.Count(v => v.Kind != ObservationValueKind.Undefined)));
        void Check(ObservationValue row)
        {
            ObservationValue[] values = [ObservationValue.FromImmutableArray([good, row])];
            var expected = ObservationValidator.TryValidateAgainstShape(fixture.Root, fixture.RootLayout, values, [1], out var expectedError);
            var actual = ObservationValidator.TryValidateAgainstShape(fixture.Root, fixture.RootLayout, values, [1], out var actualError, [fixture.Bound]);
            Assert.Equal(expected, actual); Assert.Equal(expectedError, actualError);
        }
    }

    [Fact]
    public void WarmSparseOrdinalValidationAllocatesNoTemporaryLookup()
    {
        var fixture = new Fixture(named: false, permuted: true);
        var slots = new ObservationValue[129];
        fixture.RecordLayout.TryGetOrdinal("field0", out var ordinal); slots[ordinal] = ObservationValue.FromInt64(1);
        var row = ObservationValue.FromObject(CompactOrdinalObservationFields.Create(fixture.RecordLayout, slots));
        ObservationValue[] values = [ObservationValue.FromImmutableArray([row, row, row, row])];
        ObservationValidationPlan?[] plans = [fixture.Bound]; ulong[] presence = [1];
        for (var i = 0; i < 200; i++) Assert.True(ObservationValidator.TryValidateAgainstShape(fixture.Root, fixture.RootLayout, values, presence, out _, plans));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = true;
        for (var i = 0; i < 100; i++) accepted &= ObservationValidator.TryValidateAgainstShape(fixture.Root, fixture.RootLayout, values, presence, out _, plans);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(accepted); Assert.Equal(0, allocated);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) accepted &= ObservationValidator.TryValidateAgainstShape(fixture.Root, fixture.RootLayout, values, presence, out _);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() > before); Assert.True(accepted);
    }

    [Fact]
    public void BindingsRejectWrongGraphAndChangedDeclarations()
    {
        var fixture = new Fixture(named: false, permuted: false);
        var other = new Fixture(named: false, permuted: false);
        Assert.Throws<ArgumentException>(() => fixture.Original.BindLayouts(other.Root.Graph,
            new Dictionary<TypeRef, ObservationLayout>(ReferenceEqualityComparer.Instance)));
        ObservationValue[] values = [ObservationValue.FromImmutableArray([])];
        Assert.Throws<ArgumentException>(() => ObservationValidator.TryValidateAgainstShape(
            fixture.Root, fixture.RootLayout, values, [1], out _, [other.Bound]));
        Assert.Throws<ArgumentException>(() => ObservationValidator.TryValidateAgainstShape(
            fixture.Root, fixture.RootLayout, values, [1], out _, [fixture.Bound, fixture.Bound]));
        Shape bad = new(new("bad"), [new(new("field0"), new ScalarTypeRef(ScalarTypeKind.String))]);
        ShapeGraph graph = new(new("bad"), [bad]);
        Assert.Throws<ArgumentException>(() => fixture.Original.BindLayouts(fixture.Root.Graph,
            new Dictionary<TypeRef, ObservationLayout>(ReferenceEqualityComparer.Instance) { [fixture.Type] = ObservationLayout.Create(new(graph, bad.Id)) }));
        var definitions = fixture.RecordLayout.FieldIdentities.Select((_, i) => fixture.RecordLayout.GetFieldDefinition(i)).ToArray();
        definitions[0] = new(definitions[0].Name, definitions[0].Type, presence: FieldPresence.Optional, nullability: FieldNullability.Nullable);
        Shape changed = new(new("changed"), [.. definitions]); ShapeGraph changedGraph = new(new("changed"), [changed]);
        Assert.Throws<ArgumentException>(() => fixture.Original.BindLayouts(fixture.Root.Graph,
            new Dictionary<TypeRef, ObservationLayout>(ReferenceEqualityComparer.Instance) { [fixture.Type] = ObservationLayout.Create(new(changedGraph, changed.Id)) }));
    }

    [Fact]
    public void RecursiveBindingsKeepOriginalDepthAndFailureOrder()
    {
        var scalar = new ScalarTypeRef(ScalarTypeKind.Int64);
        var next = new NamedTypeRef(new("node"));
        var type = new NamedTypeRef(next.TypeId);
        TypeDefinition.Structural node = new(next.TypeId, [new(new("value"), scalar),
            new(new("next"), next, presence: FieldPresence.Optional)]);
        Shape definition = new(new("root"), [new(new("payload"), type)]);
        ShapeGraph graph = new(new("recursive-binding"), [definition], [node]);
        GraphShapeId root = new(graph, definition.Id); var rootLayout = ObservationLayout.Create(root);
        Shape record = new(new("record"), [new(new("value"), scalar), new(new("next"), next, presence: FieldPresence.Optional)]);
        ShapeGraph physical = new(new("physical"), [record], [node]);
        var layout = ObservationLayout.Create(new(physical, record.Id));
        var original = ObservationValidationPlan.Get(type, graph);
        var bound = original.BindLayouts(graph, new Dictionary<TypeRef, ObservationLayout>(ReferenceEqualityComparer.Instance)
            { [type] = layout, [next] = layout });
        Assert.Same(bound.Children[1], bound.Children[1]!.Children[1]);
        foreach (var depth in new[] { 1, 2, 62, 63, 64, 65, 70 })
        {
            ObservationValue row = default;
            for (var index = 0; index < depth; index++)
                row = ObservationValue.FromObject(CompactOrdinalObservationFields.Create(layout, [ObservationValue.FromInt64(index), row]));
            ObservationValue[] values = [row];
            var expected = ObservationValidator.TryValidateAgainstShape(root, rootLayout, values, [1], out var expectedError);
            var actual = ObservationValidator.TryValidateAgainstShape(root, rootLayout, values, [1], out var actualError, [bound]);
            Assert.Equal(expected, actual); Assert.Equal(expectedError, actualError);
        }
    }

    sealed class Fixture
    {
        internal GraphShapeId Root { get; }
        internal ObservationLayout RootLayout { get; }
        internal ObservationLayout RecordLayout { get; }
        internal TypeRef Type { get; }
        internal ObservationValidationPlan Original { get; }
        internal ObservationValidationPlan Bound { get; }
        internal Fixture(bool named, bool permuted)
        {
            ImmutableArray<ObjectFieldTypeDef> fields = [.. Enumerable.Range(0, 129).Select(index => new ObjectFieldTypeDef(
                "field" + index, new ScalarTypeRef(ScalarTypeKind.Int64), presence: index == 0 ? FieldPresence.Required : FieldPresence.Optional,
                nullability: index == 0 ? FieldNullability.NonNullable : FieldNullability.Nullable))];
            Type = new ObjectTypeRef(fields);
            ImmutableArray<TypeDefinition> types = [];
            if (named)
            {
                TypeDefinition.Structural structural = new(new("record"), [.. fields.Select(f => new StructuralField(new(f.Name), f.Type, f.Cardinality, f.Presence, f.Nullability))]);
                Type = new NamedTypeRef(structural.Id); types = [structural];
            }
            Shape root = new(new("root"), [new(new("payload"), Type, cardinality: FieldCardinality.Many)]);
            ShapeGraph graph = new(new("ordinal-validation"), [root], types);
            Root = new(graph, root.Id); RootLayout = ObservationLayout.Create(Root);
            Shape projection = new(new("physical"), [.. fields.Select(f => new FieldDefinition(new(f.Name), f.Type, f.Cardinality, f.Presence, f.Nullability))]);
            ShapeGraph physical = new(new("physical"), [projection], types);
            RecordLayout = permuted ? ObservationLayout.Create(new(physical, projection.Id), [.. projection.Fields.Reverse().Select(f => f.Name.Value)])
                : ObservationLayout.Create(new(physical, projection.Id));
            Original = ObservationValidationPlan.Get(Type, graph);
            Bound = Original.BindLayouts(graph, new Dictionary<TypeRef, ObservationLayout>(ReferenceEqualityComparer.Instance) { [Type] = RecordLayout });
        }
    }
}
