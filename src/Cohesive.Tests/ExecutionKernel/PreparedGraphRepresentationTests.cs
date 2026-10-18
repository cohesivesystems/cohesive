using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PreparedGraphRepresentationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly ValueContract Contract = new(new JsonTypeRef());

    static ObservationValue Expected(ShapeGraphDocument source)
    {
        using var json = JsonSerializer.SerializeToDocument(source, ObservationValue.DeclaredJsonProfile);
        return ObservationValue.FromJsonElement(json.RootElement);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(1024)]
    public void ProjectionMatchesDeclaredSerializerAndIsReusedAcrossEnvelopes(int fields)
    {
        var source = PreparedGraphProjectionTests.Graph(fields);
        var first = ObservationValue.FromObject(source);
        Assert.Equal(Expected(source), first);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(Expected(source)), JsonSerializer.SerializeToUtf8Bytes(first));
        Assert.Same(first.Fields, ObservationValue.FromObject(source).Fields);
        var envelope = ObservationValue.FromObject(new { Document = source }).GetProperty("Document");
        Assert.Same(first.Fields, envelope.Fields);
        var revised = source with { Metadata = new(name: "changed") };
        var second = ObservationValue.FromObject(revised);
        Assert.NotSame(first.Fields, second.Fields);
        Assert.Same(first.GetProperty("Graph").Fields, second.GetProperty("Graph").Fields);
        var decoded = Decode(first);
        var decodedRevision = Decode(second);
        Assert.Same(decoded.Graph, decodedRevision.Graph);
        Assert.NotSame(source.Graph, decoded.Graph); // First interpretation still runs constructors.
        Assert.Same(first.Fields, ObservationValue.FromObject(decoded).Fields);
        Assert.Same(second.Fields, ObservationValue.FromObject(decodedRevision).Fields);
        Assert.Equal("changed", decodedRevision.Metadata.Name);
    }

    [Fact]
    public void CachedInterpretationsDoNotFollowChangedOrExternalPayloads()
    {
        var source = PreparedGraphProjectionTests.Graph(1);
        var observed = ObservationValue.FromObject(source);
        var decoded = Decode(observed);
        using var json = JsonSerializer.SerializeToDocument(source, ObservationValue.DeclaredJsonProfile);
        var external = Decode(ObservationValue.FromJsonElement(json.RootElement));
        Assert.NotSame(decoded.Graph, external.Graph);
        var changed = observed.WithField(FieldPath.FromField("Graph"),
            observed.GetProperty("Graph").WithField(FieldPath.FromField("Id"), ObservationValue.FromString("changed")));
        Assert.Equal("changed", Decode(changed).Graph.Id.Value);
        Assert.Equal("test", decoded.Graph.Id.Value);
        Assert.Equal(ResultType.Failure, HostedQueryValueAdapter.Decode<ShapeGraphDocument>(
            PortableValue.Concrete(Contract, observed), new(new ScalarTypeRef(ScalarTypeKind.String))).Type);
        var unknown = observed.WithField(FieldPath.FromField("Unexpected"), ObservationValue.FromBool(true));
        Assert.Equal(ResultType.Failure, HostedQueryValueAdapter.Decode<ShapeGraphDocument>(PortableValue.Concrete(Contract, unknown), Contract).Type);
    }

    [Fact]
    public void EveryDeclaredTypeAndConstraintCaseMatchesTheSerializer()
    {
        TypeRef[] types = [new NamedTypeRef(new("record")), new OpaqueRuntimeTypeRef("external"),
            new ScalarTypeRef(ScalarTypeKind.Decimal), new EnumTypeRef("codes", ["one", "two"]),
            new EntityReferenceTypeRef(new("entity")), new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.String)),
            new ObjectTypeRef([new("nested", new ScalarTypeRef(ScalarTypeKind.String))]),
            new QuantityTypeRef("Distance"), new JsonTypeRef(JsonTypeKind.Object)];
        ShapeConstraint[] constraints = [new RequiredConstraint(), new MinLengthConstraint(1), new MaxLengthConstraint(10),
            new RangeConstraint(-1.2300m, 10), new RegexConstraint("[é]"), new AllowedValuesConstraint(["é", "😀"]),
            new OccurrenceConstraint(0, 3)];
        var options = ObservationValue.DeclaredJsonProfile;
        Assert.Equal(options.GetTypeInfo(typeof(TypeRef)).PolymorphismOptions!.DerivedTypes.Select(item => item.DerivedType).OrderBy(type => type.Name),
            types.Select(type => type.GetType()).OrderBy(type => type.Name));
        Assert.Equal(options.GetTypeInfo(typeof(ShapeConstraint)).PolymorphismOptions!.DerivedTypes.Select(item => item.DerivedType).OrderBy(type => type.Name),
            constraints.Select(type => type.GetType()).OrderBy(type => type.Name));
        TypeDefinition[] named = [new TypeDefinition.Structural(new("record"), []),
            new TypeDefinition.Enum(new("codes"), PrimitiveType.String, [new("one", "value")]),
            new TypeDefinition.Union(new("choice"), new("kind"), [new("one", types[0])])];
        Assert.Equal(options.GetTypeInfo(typeof(TypeDefinition)).PolymorphismOptions!.DerivedTypes.Select(item => item.DerivedType).OrderBy(type => type.Name),
            named.Select(type => type.GetType()).OrderBy(type => type.Name));
        var source = ShapeGraphDocument.FromGraph(new(new("all-cases"),
            [new(new("root"), [..types.Select((type, index) => new FieldDefinition(new($"field{index}"), type))], [..constraints])], [..named]));
        Assert.Equal(Expected(source), ObservationValue.FromObject(source));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(Expected(source)), JsonSerializer.SerializeToUtf8Bytes(ObservationValue.FromObject(source)));
    }

    [Fact]
    public void AnnotationDepthBoundaryMatchesTheSerializerOnRepeatedReads()
    {
        for (var depth = 54; depth <= 65; depth++)
        {
            using var json = JsonDocument.Parse(new string('[', depth) + "null" + new string(']', depth), new() { MaxDepth = 128 });
            var source = ShapeGraphDocument.FromGraph(new(new("deep"), [new(new("root"), [])],
                annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("deep"), new(json.RootElement))));
            var expected = Record.Exception(() => Expected(source));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var actual = Record.Exception(() => ObservationValue.FromObject(source));
                Assert.Equal(expected is null, actual is null);
                if (expected is not null) Assert.IsType<JsonException>(actual);
            }
        }
    }

    [Fact]
    public void ConcurrentFirstProjectionPublishesOneRepresentation()
    {
        var source = PreparedGraphProjectionTests.Graph(128);
        var values = new ObservationValue[32];
        Parallel.For(0, values.Length, i => values[i] = ObservationValue.FromObject(source));
        Assert.All(values, value => Assert.Same(values[0].Fields, value.Fields));
    }

    [Fact]
    public void PreparedRepresentationDoesNotRetainDiscardedGraphs()
    {
        var references = Discard();
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] Discard()
    {
        var source = PreparedGraphProjectionTests.Graph(128);
        var observed = ObservationValue.FromObject(source);
        var decoded = Decode(observed);
        ObservationValue.FromObject(decoded);
        return [new(source), new(source.Graph), new(observed.Fields!), new(decoded), new(decoded.Graph)];
    }

    [Theory]
    [InlineData("{\"name\":1,\"name\":2}")]
    [InlineData("{\"name\":1,\"na\\u006de\":1}")]
    [InlineData("{\"nested\":[{\"é\":1,\"\\u00e9\":2}]}")]
    public void DuplicateAnnotationPropertiesRemainRejectedAndFailureIsRetryable(string json)
    {
        using var token = JsonDocument.Parse(json);
        var source = ShapeGraphDocument.FromGraph(new(new("duplicates"), [new(new("root"), [])],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("external"), new(token.RootElement))));
        Assert.Throws<JsonException>(() => Expected(source));
        for (var i = 0; i < 2; i++) Assert.Throws<JsonException>(() => ObservationValue.FromObject(source));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void StringAndWrapperProjectionRetainSerializerUnicodePolicy(int kind)
    {
        var text = kind switch { 0 => "é<>&😀", 1 => new string((char)0xd800, 1), _ => new string((char)0xdfff, 1) };
        var source = ShapeGraphDocument.FromGraph(new(new("unicode" + text), [new(new("root"), [])],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("text"), AnnotationValue.FromString(text))),
            new(name: text));
        Assert.Equal(Expected(source), ObservationValue.FromObject(source));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(Expected(source)), JsonSerializer.SerializeToUtf8Bytes(ObservationValue.FromObject(source)));
    }

    [Fact]
    public void ImmutableObjectIsPreparedByIdentityAndRetainsDeclaredTypeDispatch()
    {
        var source = new CountedValue("source");
        var first = ObservationValue.FromObject(source);
        for (var i = 0; i < 16; i++) Assert.Same(first.Fields, ObservationValue.FromObject(source).Fields);
        Assert.Equal(1, source.Writes);
        var equal = new CountedValue("source");
        Assert.Equal(first, ObservationValue.FromObject(equal));
        Assert.Equal(1, equal.Writes);
        Assert.Equal(1, source.Writes);
        BaseValue derived = new DerivedValue("value");
        var tagged = ObservationValue.FromObject(new { Value = derived }).GetProperty("Value");
        var concrete = ObservationValue.FromObject((DerivedValue)derived);
        Assert.Equal("derived", tagged.GetProperty("$kind").String);
        Assert.False(concrete.TryGetField(FieldPath.FromField("$kind"), out _));
        using var json = JsonSerializer.SerializeToDocument(derived, typeof(BaseValue), ObservationValue.DeclaredJsonProfile);
        Assert.Equal(ObservationValue.FromJsonElement(json.RootElement), tagged);
    }

    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed class CountedValue(string value) : IImmutableObservationProjection, IJsonOnSerializing
    {
        public string Value { get; } = value;
        internal int Writes; // Test instrumentation only; emitted semantics remain immutable.
        void IJsonOnSerializing.OnSerializing() => Interlocked.Increment(ref Writes);
    }
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
    [JsonDerivedType(typeof(DerivedValue), "derived")]
    public abstract record BaseValue : IImmutableObservationProjection;
    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed record DerivedValue(string Name) : BaseValue;

    [Fact]
    public async Task CyclesFailWithoutRetainingFailuresOrLockOrderDeadlocks()
    {
        await Task.WhenAll(Task.Run(() => Reject(CyclicValue.First)), Task.Run(() => Reject(CyclicValue.Second)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        static void Reject(CyclicValue value)
        {
            for (var retry = 0; retry < 2; retry++) Assert.Throws<JsonException>(() => ObservationValue.FromObject(value));
        }
    }

    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed class CyclicValue : IImmutableObservationProjection
    {
        internal static readonly CyclicValue First = new(true), Second = new(false);
        readonly bool first;
        CyclicValue(bool first) => this.first = first;
        public CyclicValue Peer => first ? Second : First;
    }

    [Fact]
    public void ImmutableChildDepthDoesNotResetTheSerializerBudget()
    {
        Chain? value = null;
        for (var depth = 1; depth <= 66; depth++)
        {
            value = new(value);
            var expected = Record.Exception(() => JsonSerializer.SerializeToDocument(value, ObservationValue.DeclaredJsonProfile).Dispose());
            var actual = Record.Exception(() => ObservationValue.FromObject(value));
            Assert.Equal(expected is null, actual is null);
            if (actual is not null) Assert.IsType<JsonException>(actual);
        }
    }
    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed class Chain(Chain? next) : IImmutableObservationProjection
    {
        public Chain? Next { get; } = next;
    }

    [Fact]
    public void ReprojectionDoesNotReuseFieldsChangedByConstructorNormalization()
    {
        var source = PreparedGraphProjectionTests.Graph(1) with { Metadata = new() { Name = "  trimmed  " } };
        var observed = ObservationValue.FromObject(source);
        var decoded = Decode(observed);
        var projected = ObservationValue.FromObject(decoded);
        Assert.NotSame(observed.Fields, projected.Fields);
        Assert.Same(observed.GetProperty("Graph").Fields, projected.GetProperty("Graph").Fields);
        Assert.Equal("trimmed", projected.GetProperty("Metadata").GetProperty("Name").String);
        Assert.Equal(Expected(decoded), projected);
    }

    [Fact]
    public void CustomReadProfilesCannotSupplyEncodingEvidenceAndDecimalScaleRemainsExact()
    {
        ObservationValue.FromObject(new DecimalValue(1.2300m)); // Prepare the declared contract.
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            { ["Value"] = new(ObservationValueKind.Decimal, dec: 1.2300m) });
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DecimalReader()); options.MakeReadOnly(true);
        var decoded = observed.Deserialize<DecimalValue>(options)!;
        var projected = ObservationValue.FromObject(decoded);
        Assert.NotSame(observed.Fields, projected.Fields);
        using var json = JsonSerializer.SerializeToDocument(decoded, ObservationValue.DeclaredJsonProfile);
        Assert.Equal(ObservationValue.FromJsonElement(json.RootElement), projected);
        Assert.Equal(2, (decimal.GetBits(projected.GetProperty("Value").Decimal)[3] >> 16) & 0xff);
        var unknown = observed.WithField(FieldPath.FromField("Unknown"), ObservationValue.FromBool(true));
        var fromUnknown = unknown.Deserialize<DecimalValue>()!;
        Assert.False(ObservationValue.FromObject(fromUnknown).TryGetField(FieldPath.FromField("Unknown"), out _));
    }
    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed record DecimalValue(decimal Value) : IImmutableObservationProjection;
    public sealed class DecimalReader : JsonConverter<DecimalValue>
    {
        public override DecimalValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { reader.Skip(); return new(1.23m); }
        public override void Write(Utf8JsonWriter writer, DecimalValue value, JsonSerializerOptions options) => throw new NotSupportedException();
    }

    [Fact]
    public void FirstReprojectionOfDecodedGraphHasAnAllocationCeiling()
    {
        var samples = new long[7];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var decoded = Decode(ObservationValue.FromObject(PreparedGraphProjectionTests.Graph(128)));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var projected = ObservationValue.FromObject(decoded);
            samples[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(projected);
        }
        Array.Sort(samples);
        output.WriteLine($"First decoded graph reprojection: {samples[3]} B.");
        Assert.True(samples[3] <= 40_960, $"First decoded graph reprojection allocated {samples[3]} bytes.");
    }

    [Fact]
    public void LivePayloadPrunesExpiredProfileInterpretationsOnWarmReads()
    {
        var value = ObservationValue.FromObject(new DecimalValue(1.23m));
        var stable = value.Deserialize<DecimalValue>();
        var discarded = ReadWithDiscardedProfile(value);
        Collect();
        Assert.False(discarded.Profile.IsAlive);
        Assert.Same(stable, value.Deserialize<DecimalValue>());
        Collect();
        Assert.False(discarded.Value.IsAlive);
        GC.KeepAlive(value);
        static void Collect() { for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference Profile, WeakReference Value) ReadWithDiscardedProfile(ObservationValue value)
    {
        var options = new JsonSerializerOptions(); options.MakeReadOnly(true);
        var decoded = value.Deserialize<DecimalValue>(options)!;
        return (new(options), new(decoded));
    }

    [Fact]
    public void FrameworkDecodedGraphKeepsItsRepresentationAcrossCollection()
    {
        var prepared = CreateDecodedGraph();
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.True(prepared.Fields.IsAlive);
        Assert.Same(prepared.Fields.Target, ObservationValue.FromObject(prepared.Model).Fields);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (ShapeGraphDocument Model, WeakReference Fields) CreateDecodedGraph()
    {
        var observation = ObservationValue.FromObject(PreparedGraphProjectionTests.Graph(128));
        return (Decode(observation), new(observation.Fields!));
    }

    [Theory]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDFFF\"")]
    [InlineData("{\"\\uD800\":1}")]
    [InlineData("{\"\\uDFFF\":1}")]
    public void MalformedAnnotationEscapesRetainDeclaredSerializerBehavior(string text)
    {
        using var json = JsonDocument.Parse(text);
        var source = ShapeGraphDocument.FromGraph(new(new("escapes"), [new(new("root"), [])],
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("token"), new(json.RootElement))));
        var expected = Record.Exception(() => Expected(source));
        var actual = Record.Exception(() => ObservationValue.FromObject(source));
        Assert.Equal(expected is null, actual is null);
        if (expected is null) Assert.Equal(Expected(source), ObservationValue.FromObject(source));
    }

    [Fact]
    public void DefaultImmutableArrayRetainsSerializerBehavior()
    {
        var source = new ArrayValue(default);
        var expected = Record.Exception(() => JsonSerializer.SerializeToDocument(source, ObservationValue.DeclaredJsonProfile).Dispose());
        var actual = Record.Exception(() => ObservationValue.FromObject(source));
        Assert.Equal(expected is null, actual is null);
        Assert.False(actual is NullReferenceException);
    }
    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed record ArrayValue(ImmutableArray<int> Items) : IImmutableObservationProjection;

    [Fact]
    public void WarmProjectionHasAnAllocationCeilingIndependentOfGraphSize()
    {
        var source = PreparedGraphProjectionTests.Graph(1024);
        ObservationValue.FromObject(source);
        var samples = new long[7];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 128; i++) ObservationValue.FromObject(source);
            samples[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.True(samples[3] <= 256, $"128 prepared projections allocated {samples[3]} bytes.");
    }

    static ShapeGraphDocument Decode(ObservationValue value)
    {
        var result = HostedQueryValueAdapter.Decode<ShapeGraphDocument>(PortableValue.Concrete(Contract, value), Contract);
        Assert.Equal(ResultType.Success, result.Type);
        return result.Success!;
    }
}
