using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalDocumentTextTests
{
    sealed record Payload(ObservationValue Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextAndBytesAreIdenticalForEveryValueKindAndProfile(bool insensitive)
    {
        var options = StrictDocumentJson.CreateOptions();
        options.PropertyNameCaseInsensitive = insensitive;
        options.Converters.Add(PortableValueJsonConverter.TaggedObservationValues);
        ObservationValue[] values = [ObservationValue.Undefined, ObservationValue.Null, ObservationValue.FromInt64(long.MinValue),
            ObservationValue.FromDouble(-0d), new(ObservationValueKind.Decimal, dec: decimal.MaxValue), ObservationValue.FromBool(true),
            ObservationValue.FromString("é <>& 🚚 \\"), ObservationValue.FromBytes(new byte[] { 0, 255 }),
            ObservationValue.FromDateTimeOffset(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(-7))),
            ObservationValue.FromDateOnly(new DateOnly(2026, 10, 18)), ObservationValue.FromTimeOnly(new TimeOnly(12, 34, 56)),
            ObservationValue.FromTimeSpan(TimeSpan.FromDays(-1)), ObservationValue.EmptyObject,
            ObservationValue.FromImmutableArray([ObservationValue.Null, ObservationValue.FromDouble(1e-29)])];
        Assert.Equal(Enum.GetValues<ObservationValueKind>().Order(), values.Select(value => value.Kind).Order());
        foreach (var value in values)
        {
            var payload = new Payload(value);
            Assert.Equal(StrictDocumentJson.GetCanonicalBytes(payload, options), Encoding.UTF8.GetBytes(StrictDocumentJson.GetCanonicalText(payload, options)));
        }
        var nested = ObservationValue.FromObject(values.Select((value, index) => KeyValuePair.Create("f" + index, value)).ToDictionary());
        Assert.Equal(StrictDocumentJson.GetCanonicalBytes(new Payload(nested), options),
            Encoding.UTF8.GetBytes(StrictDocumentJson.GetCanonicalText(new Payload(nested), options)));
    }

    [Fact]
    public void TextPathAvoidsAnOwnedFullPayloadByteArray()
    {
        var payload = new Payload(ObservationValue.FromString(new string('x', 65536)));
        var options = StrictDocumentJson.CreateOptions();
        for (var warm = 0; warm < 20; warm++) _ = StrictDocumentJson.GetCanonicalText(payload, options);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = StrictDocumentJson.GetCanonicalText(payload, options);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 145000);
        Assert.Equal(StrictDocumentJson.GetCanonicalBytes(payload, options), Encoding.UTF8.GetBytes(result));
    }

    [Fact]
    public void TextPreservesCanonicalFailureContracts()
    {
        var options = StrictDocumentJson.CreateOptions();
        Assert.Throws<ArgumentNullException>(() => StrictDocumentJson.GetCanonicalText<Payload>(null!, options));
        Assert.Throws<ArgumentNullException>(() => StrictDocumentJson.GetCanonicalText(new Payload(ObservationValue.Null), null!));
        Assert.Throws<ArgumentException>(() => StrictDocumentJson.GetCanonicalText(new DuplicateProperties(), options));
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(DuplicateConverter))]
    sealed class DuplicateProperties;

    sealed class DuplicateConverter : System.Text.Json.Serialization.JsonConverter<DuplicateProperties>
    {
        public override DuplicateProperties Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, DuplicateProperties value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteNumber("a", 1); writer.WriteNumber("a", 2); writer.WriteEndObject();
        }
    }
}
