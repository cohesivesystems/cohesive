using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PreparedGraphProjectionTests
{
    static readonly ValueContract Contract = new(new JsonTypeRef());

    internal static ShapeGraphDocument Graph(int fields) => ShapeGraphDocument.FromGraph(new(new("test"),
        [new(new("message"), [.. Enumerable.Range(0, fields).Select(i => new FieldDefinition(new($"field{i}"),
            new ScalarTypeRef(ScalarTypeKind.String), annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty
                .Add(new("description"), AnnotationValue.FromString("é<>&😀"))
                .Add(new("structured"), AnnotationValue.FromObject(new { Nested = 42 }))))]) ]));

    [Fact]
    public void GraphMaterializationMatchesSerializerAndKeepsOwnedAnnotationSnapshots()
    {
        var observation = ObservationValue.FromObject(Graph(128));
        var options = ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions();
        var expected = observation.DeserializeIndependent<ShapeGraphDocument>(options)!;
        var actual = HostedQueryValueAdapter.Decode<ShapeGraphDocument>(PortableValue.Concrete(Contract, observation), Contract).Success!;
        Assert.Equal(JsonSerializer.Serialize(expected, options), JsonSerializer.Serialize(actual, options));
        for (var i = 0; i < expected.Graph.Shapes[0].Fields.Length; i++)
            foreach (var pair in expected.Graph.Shapes[0].Fields[i].Annotations)
                Assert.Equal(pair.Value.Value.GetRawText(), actual.Graph.Shapes[0].Fields[i].Annotations[pair.Key].Value.GetRawText());
        Assert.Same(actual, HostedQueryValueAdapter.Decode<ShapeGraphDocument>(PortableValue.Concrete(Contract, observation), Contract).Success);
        // Immutable reads remain admitted against the current contract even after a cached interpretation exists.
        Assert.Equal(ResultType.Failure, HostedQueryValueAdapter.Decode<ShapeGraphDocument>(PortableValue.Concrete(Contract, observation),
            new(new ScalarTypeRef(ScalarTypeKind.String))).Type);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(null)]
    [InlineData("wrong-member")]
    public void PolymorphicFailuresRemainSerializerFailures(string? mutation)
    {
        var observation = ObservationValue.FromObject(new PolyEnvelope([new Literal("value")]));
        var item = observation.GetProperty("Items").EnumerateArray()[0];
        item = mutation switch
        {
            null => item.WithoutField(FieldPath.FromField("$kind")),
            "wrong-member" => item.WithField(FieldPath.FromField("Unexpected"), ObservationValue.FromBool(true)),
            _ => item.WithField(FieldPath.FromField("$kind"), ObservationValue.FromString(mutation))
        };
        observation = observation.WithField(FieldPath.FromField("Items"), ObservationValue.FromArray(new[] { item }));
        Assert.NotNull(Record.Exception(() => observation.Deserialize<PolyEnvelope>(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions())));
        for (var retry = 0; retry < 2; retry++)
            Assert.Equal(ResultType.Failure, HostedQueryValueAdapter.Decode<PolyEnvelope>(PortableValue.Concrete(Contract, observation), Contract).Type);
    }

    [Fact]
    public void PolymorphicCollectionUsesPreparedChildrenAndPreservesNulls()
    {
        var observation = ObservationValue.FromObject(new PolyEnvelope([new Literal("é<>&😀"), null!, new Literal("second")]));
        var actual = HostedQueryValueAdapter.Decode<PolyEnvelope>(PortableValue.Concrete(Contract, observation), Contract).Success!;
        Assert.Equal("é<>&😀", Assert.IsType<Literal>(actual.Items[0]).Text);
        Assert.Null(actual.Items[1]);
        Assert.Equal("second", Assert.IsType<Literal>(actual.Items[2]).Text);
    }

    [Fact]
    public void FirstGraphDecodeHasBoundedAllocationPerField()
    {
        var source = Graph(128);
        // Fresh observation storage each sample measures decoding, rather than a warm result-cache hit.
        for (var warm = 0; warm < 4; warm++) Decode(source);
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var input = PortableValue.Concrete(Contract, ObservationValue.FromObject(Graph(128)));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = HostedQueryValueAdapter.Decode<ShapeGraphDocument>(input, Contract);
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(ResultType.Success, result.Type);
        }
        Array.Sort(samples);
        Assert.True(samples[3] <= 280_000, $"First graph decode allocated {samples[3]} bytes for 128 fields.");
        static void Decode(ShapeGraphDocument source) => HostedQueryValueAdapter.Decode<ShapeGraphDocument>(
            PortableValue.Concrete(Contract, ObservationValue.FromObject(source)), Contract);
    }

    [Fact]
    public void UnknownImmutablePolymorphicRootCanFailRepeatedlyWithoutRecursiveCacheAdmission()
    {
        using var json = JsonDocument.Parse("{\"$kind\":\"unknown\",\"Text\":\"value\"}");
        var input = PortableValue.Concrete(Contract, ObservationValue.FromJsonElement(json.RootElement));
        for (var i = 0; i < 2; i++)
        {
            var actual = HostedQueryValueAdapter.Decode<ImmutableNode>(input, Contract);
            Assert.Equal(ResultType.Failure, actual.Type);
            Assert.Contains("unknown", actual.Failure!.Message);
        }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
    [JsonDerivedType(typeof(ImmutableLiteral), "literal")]
    public abstract record ImmutableNode : IImmutableObservationProjection;
    public sealed record ImmutableLiteral(string Text) : ImmutableNode;

    [Fact]
    public void CustomAnnotationReaderRetainsItsSerializerContract()
    {
        var options = new JsonSerializerOptions(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions());
        options.Converters.Insert(0, new CustomAnnotationReader());
        options.MakeReadOnly();
        var observation = ObservationValue.FromObject(new AnnotationEnvelope(
            ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("label"), AnnotationValue.FromString("original"))));
        var prepared = (Func<ObservationValue, AnnotationEnvelope>)DefaultObservationValueConverterCache.PrepareClrContract(typeof(AnnotationEnvelope), options)!;
        var expected = observation.DeserializeIndependent<AnnotationEnvelope>(options)!;
        var actual = prepared(observation);
        Assert.Equal(expected.Values[new("label")].Value.GetRawText(), actual.Values[new("label")].Value.GetRawText());
        Assert.Equal("custom", actual.Values[new("label")].Value.GetString());
    }

    public sealed record AnnotationEnvelope(ImmutableDictionary<AnnotationKey, AnnotationValue> Values);
    public sealed class CustomAnnotationReader : JsonConverter<AnnotationValue>
    {
        public override AnnotationValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            reader.Skip();
            return AnnotationValue.FromString("custom");
        }
        public override void Write(Utf8JsonWriter writer, AnnotationValue value, JsonSerializerOptions options) => value.WriteTo(writer);
    }

    public sealed record PolyEnvelope(ImmutableArray<Node> Items);
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
    [JsonDerivedType(typeof(Literal), "literal")]
    public abstract record Node;
    public sealed record Literal(string Text) : Node;
}
