using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ObservationValueTests
{
    [Fact]
    public void FromObject_DictionaryWithNonStringKeys_Throws()
    {
        IDictionary values = new Dictionary<int, string> { [1] = "x" };

        var ex = Assert.Throws<InvalidOperationException>(() => ObservationValue.FromObject(values));

        Assert.Contains("keys must be strings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromObject_CyclicClrObjectGraph_Throws()
    {
        var node = new RecursiveNode();
        node.Next = node;

        var ex = Assert.Throws<InvalidOperationException>(() => ObservationValue.FromObject(node));

        Assert.Contains("Cyclic CLR object graphs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromObject_ClrObject_HonorsJsonPropertyNameAndJsonIgnore()
    {
        var observed = ObservationValue.FromObject(new AttributedInput
        {
            Value = 7,
            Hidden = 9
        });

        Assert.Equal(ObservationValueKind.Object, observed.Kind);
        Assert.Equal(7L, observed.GetProperty("renamed").GetInt64());
        Assert.False(observed.TryGetProperty("Hidden", out _));
    }

    [Fact]
    public void FromObject_Enumerable_ProducesArray()
    {
        var observed = ObservationValue.FromObject(new[] { 2, 4, 6 });

        Assert.Equal(ObservationValueKind.Array, observed.Kind);
        Assert.Equal(3, observed.GetArrayLength());
        Assert.Equal(4, observed.EnumerateArray()[1].GetInt32());
    }

    [Fact]
    public void FromObject_JsonDocument_ProjectsRootElement()
    {
        using var document = JsonDocument.Parse("{\"x\":12,\"nested\":{\"ok\":true}}");

        var observed = ObservationValue.FromObject(document);

        Assert.Equal(12L, observed.GetProperty("x").GetInt64());
        Assert.True(observed.GetProperty("nested").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void FromClrPropertyBag_Null_ReturnsEmptyObject()
    {
        var observed = ObservationValue.FromClrPropertyBag(null);

        Assert.Equal(ObservationValueKind.Object, observed.Kind);
        Assert.NotNull(observed.Fields);
        Assert.Empty(observed.Fields!);
    }

    [Fact]
    public void ToFieldDictionary_UsesClrPropertyNames()
    {
        var values = ObservationValue.ToFieldDictionary(new SimpleInput
        {
            Value = 5,
            Name = "carrier"
        });

        Assert.Equal(2, values.Count);
        Assert.Equal(5L, values["Value"].GetInt64());
        Assert.Equal("carrier", values["Name"].GetString());
    }

    [Fact]
    public void TryGetInt64_DoubleIntegralSucceeds_AndFractionalFails()
    {
        var integral = ObservationValue.FromDouble(42d);
        var fractional = ObservationValue.FromDouble(42.5d);

        Assert.True(integral.TryGetInt64(out var parsed));
        Assert.Equal(42L, parsed);
        Assert.False(fractional.TryGetInt64(out _));
    }

    [Fact]
    public void TryGetDecimal_FromString_UsesInvariantParsing()
    {
        var observed = ObservationValue.FromString("12.75");

        Assert.True(observed.TryGetDecimal(out var value));
        Assert.Equal(12.75m, value);
        Assert.Equal(12.75m, observed.GetDecimal());
    }

    [Fact]
    public void TryGetBoolean_FromString_ParsesTrueFalse()
    {
        var observed = ObservationValue.FromString("true");

        Assert.True(observed.TryGetBoolean(out var value));
        Assert.True(value);
    }

    [Fact]
    public void FromObject_ObjectExpression()
    {
        var observedObj = ObservationValue.FromObject(new { Field1 = "hello", Field2 = 42, Field3 = new[] { 1, 2, 3 }, Field4 = new[] { new { Nested = 1 }, new { Nested = 2 } } });
        
        Assert.Equal("hello", observedObj.GetProperty("Field1").GetString());
        Assert.Equal(42, observedObj.GetProperty("Field2").GetInt32());
        Assert.Equal(3, observedObj.GetProperty("Field3").GetArrayLength());
        Assert.Equal(1, observedObj.GetProperty("Field4").EnumerateArray()[0].GetProperty("Nested").GetInt32());
        Assert.Equal(2, observedObj.GetProperty("Field4").EnumerateArray()[1].GetProperty("Nested").GetInt32());
    }
    
    [Fact]
    public void FromObject_TemporalClrValues_UseTemporalKinds()
    {
        var dto = new DateTimeOffset(2026, 2, 20, 14, 30, 15, TimeSpan.FromHours(-5));
        var dateOnly = new DateOnly(2026, 2, 20);
        var timeOnly = new TimeOnly(14, 30, 15, 123);
        var timeSpan = TimeSpan.FromMinutes(95);

        var observedDto = ObservationValue.FromObject(dto);
        var observedDateOnly = ObservationValue.FromObject(dateOnly);
        var observedTimeOnly = ObservationValue.FromObject(timeOnly);
        var observedTimeSpan = ObservationValue.FromObject(timeSpan);

        Assert.Equal(ObservationValueKind.DateTimeOffset, observedDto.Kind);
        Assert.Equal(ObservationValueKind.DateOnly, observedDateOnly.Kind);
        Assert.Equal(ObservationValueKind.TimeOnly, observedTimeOnly.Kind);
        Assert.Equal(ObservationValueKind.TimeSpan, observedTimeSpan.Kind);
    }

    [Fact]
    public void TemporalKinds_TryGetAndGet_RoundTrip()
    {
        var dto = new DateTimeOffset(2026, 2, 20, 14, 30, 15, TimeSpan.FromHours(-5));
        var dateOnly = new DateOnly(2026, 2, 20);
        var timeOnly = new TimeOnly(14, 30, 15, 123);
        var timeSpan = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(7);

        var observedDto = ObservationValue.FromDateTimeOffset(dto);
        var observedDateOnly = ObservationValue.FromDateOnly(dateOnly);
        var observedTimeOnly = ObservationValue.FromTimeOnly(timeOnly);
        var observedTimeSpan = ObservationValue.FromTimeSpan(timeSpan);

        Assert.True(observedDto.TryGetDateTimeOffset(out var parsedDto));
        Assert.Equal(dto, parsedDto);
        Assert.Equal(dto, observedDto.GetDateTimeOffset());

        Assert.True(observedDateOnly.TryGetDateOnly(out var parsedDateOnly));
        Assert.Equal(dateOnly, parsedDateOnly);
        Assert.Equal(dateOnly, observedDateOnly.GetDateOnly());

        Assert.True(observedTimeOnly.TryGetTimeOnly(out var parsedTimeOnly));
        Assert.Equal(timeOnly, parsedTimeOnly);
        Assert.Equal(timeOnly, observedTimeOnly.GetTimeOnly());

        Assert.True(observedTimeSpan.TryGetTimeSpan(out var parsedTimeSpan));
        Assert.Equal(timeSpan, parsedTimeSpan);
        Assert.Equal(timeSpan, observedTimeSpan.GetTimeSpan());
    }

    [Fact]
    public void TemporalReaders_ParseFromStringKind()
    {
        var dtoText = "2026-02-20T14:30:15.0000000-05:00";
        var dateOnlyText = "2026-02-20";
        var timeOnlyText = "14:30:15.1230000";
        var timeSpanText = "02:05:07";

        Assert.True(ObservationValue.FromString(dtoText).TryGetDateTimeOffset(out _));
        Assert.True(ObservationValue.FromString(dateOnlyText).TryGetDateOnly(out _));
        Assert.True(ObservationValue.FromString(timeOnlyText).TryGetTimeOnly(out _));
        Assert.True(ObservationValue.FromString(timeSpanText).TryGetTimeSpan(out _));
    }

    [Fact]
    public void GetString_TemporalKinds_ReturnsCanonicalText()
    {
        var dto = new DateTimeOffset(2026, 2, 20, 14, 30, 15, TimeSpan.FromHours(-5));
        var dateOnly = new DateOnly(2026, 2, 20);
        var timeOnly = new TimeOnly(14, 30, 15, 123);
        var timeSpan = TimeSpan.FromMinutes(95);

        Assert.Equal(dto.ToString("O", CultureInfo.InvariantCulture), ObservationValue.FromDateTimeOffset(dto).GetString());
        Assert.Equal(dateOnly.ToString("O", CultureInfo.InvariantCulture), ObservationValue.FromDateOnly(dateOnly).GetString());
        Assert.Equal(timeOnly.ToString("O", CultureInfo.InvariantCulture), ObservationValue.FromTimeOnly(timeOnly).GetString());
        Assert.Equal(timeSpan.ToString("c", CultureInfo.InvariantCulture), ObservationValue.FromTimeSpan(timeSpan).GetString());
    }

    [Fact]
    public void WriteTo_TemporalKinds_WritesJsonStrings()
    {
        var dto = new DateTimeOffset(2026, 2, 20, 14, 30, 15, TimeSpan.FromHours(-5));
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["dto"] = ObservationValue.FromDateTimeOffset(dto),
            ["date"] = ObservationValue.FromDateOnly(new DateOnly(2026, 2, 20)),
            ["time"] = ObservationValue.FromTimeOnly(new TimeOnly(14, 30, 15, 123)),
            ["span"] = ObservationValue.FromTimeSpan(TimeSpan.FromMinutes(95))
        });

        var json = observed.GetRawText();

        Assert.Contains("\"dto\":\"2026-02-20T14:30:15.0000000-05:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"date\":\"2026-02-20\"", json, StringComparison.Ordinal);
        Assert.Contains("\"time\":\"14:30:15.1230000\"", json, StringComparison.Ordinal);
        Assert.Contains("\"span\":\"01:35:00\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepEquals_TemporalKind_NotEqualToPlainString()
    {
        var text = "2026-02-20";
        var temporal = ObservationValue.FromDateOnly(new DateOnly(2026, 2, 20));
        var plain = ObservationValue.FromString(text);

        Assert.False(ObservationValue.DeepEquals(temporal, plain));
        Assert.NotEqual(temporal.GetHashCode(), plain.GetHashCode());
    }

    [Fact]
    public void FromJsonElement_String_DoesNotInferTemporalKinds()
    {
        using var document = JsonDocument.Parse("\"2026-02-20\"");

        var observed = ObservationValue.FromJsonElement(document.RootElement);

        Assert.Equal(ObservationValueKind.String, observed.Kind);
        Assert.Equal("2026-02-20", observed.GetString());
    }

    [Fact]
    public void GetString_NonString_Throws()
    {
        var observed = ObservationValue.FromInt64(3);

        Assert.Throws<InvalidOperationException>(() => observed.GetString());
    }

    [Fact]
    public void TryGetProperty_WhitespaceName_Throws()
    {
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["a"] = ObservationValue.FromInt64(1)
        });

        Assert.Throws<ArgumentException>(() => observed.TryGetProperty(" ", out _));
    }

    [Fact]
    public void WriteTo_DefaultBytesPolicy_ThrowsForBytes()
    {
        var observed = ObservationValue.FromBytes(new byte[] { 1, 2, 3 });
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        Assert.Throws<InvalidOperationException>(() => observed.WriteTo(writer));
    }

    [Fact]
    public void WriteTo_Base64BytesPolicy_WritesExpectedJson()
    {
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["id"] = ObservationValue.FromInt64(7),
            ["payload"] = ObservationValue.FromBytes(new byte[] { 0, 255 }),
            ["active"] = ObservationValue.FromBool(true)
        });
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            observed.WriteTo(writer, ObservationBytesJsonEncoding.Base64String);
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        Assert.Equal("{\"id\":7,\"payload\":\"AP8=\",\"active\":true}", json);
    }

    [Fact]
    public void Deserialize_Object_MapsToClrType()
    {
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["Value"] = ObservationValue.FromInt64(11)
        });

        var model = observed.Deserialize<SimplePayload>();

        Assert.NotNull(model);
        Assert.Equal(11, model!.Value);
    }

    sealed class RecursiveNode
    {
        public RecursiveNode? Next { get; set; }
    }

    sealed class AttributedInput
    {
        [JsonPropertyName("renamed")]
        public int Value { get; init; }

        [JsonIgnore]
        public int Hidden { get; init; }
    }

    sealed class SimpleInput
    {
        public int Value { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    sealed class SimplePayload
    {
        public int Value { get; set; }
    }
}
