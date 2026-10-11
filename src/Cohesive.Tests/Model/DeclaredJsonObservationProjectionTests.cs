using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class DeclaredJsonObservationProjectionTests
{
    static readonly JsonSerializerOptions Options = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DeclaredJsonValueConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[true,false,null,\"é<>&🙂\",1.2300,-0.0,1e-300,1e300]")]
    [InlineData("{\"z\":[],\"a\":{},\"n\":123456789012345678901234567890}")]
    public void RootAndDeclaredPropertyMatchPreviousNodeProjection(string json)
    {
        var value = new RawValue(json);
        var expected = Previous(value, typeof(RawValue));
        Assert.Equal(expected, ObservationValue.FromObject(value));
        Assert.Equal(expected, ObservationValue.FromObject(new RawEnvelope(value)).GetProperty(nameof(RawEnvelope.Value)));
    }

    [Fact]
    public void DeclaredPolymorphicPropertyAndSequenceRetainDiscriminators()
    {
        BaseValue value = new DerivedValue("é", 1.2300m);
        var envelope = new PolymorphicEnvelope(value, [value, new DerivedValue("other", -0m)]);
        var observed = ObservationValue.FromObject(envelope);
        Assert.Equal(Previous(value, typeof(BaseValue)), observed.GetProperty(nameof(PolymorphicEnvelope.Value)));
        Assert.Equal(Previous(envelope.Items, typeof(IReadOnlyList<BaseValue>)), observed.GetProperty(nameof(PolymorphicEnvelope.Items)));
        Assert.Equal("derived", observed.GetProperty(nameof(PolymorphicEnvelope.Value)).GetProperty("$kind").GetString());
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1,\"a\":1}")]
    [InlineData("{\"array\":[{\"a/b~\":1,\"a/b~\":2}]}")]
    public void ConverterDuplicatesRetainPreviousFailureType(string json)
    {
        var value = new RawValue(json);
        var expected = Record.Exception(() => Previous(value, typeof(RawValue)));
        var root = Record.Exception(() => ObservationValue.FromObject(value));
        var nested = Record.Exception(() => ObservationValue.FromObject(new RawEnvelope(value)));
        Assert.NotNull(expected);
        Assert.NotNull(root);
        Assert.NotNull(nested);
        Assert.Equal(expected.GetType(), root.GetType());
        Assert.Equal(expected.GetType(), nested.GetType());
    }

    [Fact]
    public void PublicTokenProjectionRetainsLastPropertyWins()
    {
        using var source = JsonDocument.Parse("{\"a\":1,\"a\":2}");
        Assert.Equal(2, ObservationValue.FromJsonElement(source.RootElement).GetProperty("a").GetInt32());
    }

    [Fact]
    public void ProjectionOwnsValuesAfterTemporaryDocumentDisposal()
    {
        var value = new RawValue("{\"rows\":[{\"number\":12,\"text\":\"retained\"}]}");
        var observed = ObservationValue.FromObject(value);
        // Exercise the shared JSON pools after the temporary document has been returned.
        for (var i = 0; i < 64; i++) ObservationValue.FromObject(new RawValue("{\"other\":\"overwrite\"}"));
        Assert.Equal(12, observed.GetProperty("rows").Array[0].GetProperty("number").GetInt32());
        Assert.Equal("retained", observed.GetProperty("rows").Array[0].GetProperty("text").GetString());
        Assert.Equal(Previous(value, typeof(RawValue)), observed);
    }

    [Fact]
    public void LargeConvertedProjectionAvoidsMutableJsonTreeAllocations()
    {
        var value = new RawValue(JsonSerializer.Serialize(Enumerable.Range(0, 1024)
            .Select(i => new { number = i, text = "é<>&🙂", nested = new { enabled = true } })));
        for (var i = 0; i < 4; i++)
        {
            Previous(value, typeof(RawValue));
            ObservationValue.FromObject(value);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var expected = Previous(value, typeof(RawValue));
        var oldAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var actual = ObservationValue.FromObject(value);
        var allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.True(oldAllocation - allocation > value.Json.Length,
            $"Node projection allocated {oldAllocation}; immutable projection allocated {allocation}, input {value.Json.Length} characters.");
    }

    static ObservationValue Previous(object value, Type declaredType) =>
        ObservationValue.FromJsonNode(JsonSerializer.SerializeToNode(value, declaredType, Options));

    [JsonConverter(typeof(RawValueConverter))]
    public sealed record RawValue(string Json);
    public sealed class RawValueConverter : JsonConverter<RawValue>
    {
        public override RawValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, RawValue value, JsonSerializerOptions options) => writer.WriteRawValue(value.Json);
    }
    sealed record RawEnvelope(RawValue Value);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
    [JsonDerivedType(typeof(DerivedValue), "derived")]
    public abstract record BaseValue;
    public sealed record DerivedValue(string Name, decimal Amount) : BaseValue;
    sealed record PolymorphicEnvelope(BaseValue Value, IReadOnlyList<BaseValue> Items);
}
