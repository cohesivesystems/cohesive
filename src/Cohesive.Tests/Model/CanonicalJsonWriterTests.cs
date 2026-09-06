using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class CanonicalJsonWriterTests
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    static readonly JsonWriterOptions CanonicalObservationWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    [Fact]
    public void ObservationValueWriters_AreByteEquivalentAcrossTheCompleteValueDomain()
    {
        var fixtures = CreateCanonicalObservationValueFixtures();
        Assert.Equal(
            Enum.GetValues<ObservationValueKind>(),
            fixtures
                .Select(static value => value.Kind)
                .Distinct()
                .OrderBy(static kind => kind)
                .ToArray());

        foreach (var fixture in fixtures)
            AssertObservationValueWriterEquivalence(fixture);

        Random random = new(0x51A1_2026);
        for (var index = 0; index < 512; index++)
            AssertObservationValueWriterEquivalence(CreateGeneratedObservationValue(random, maximumDepth: 5));
    }

    [Fact]
    public void ObservationValueWriters_RejectTheSameUnsupportedValuesAndBytePolicy()
    {
        ObservationValue[] unsupported =
        [
            ObservationValue.FromDouble(double.NaN),
            ObservationValue.FromDouble(double.NegativeInfinity),
            ObservationValue.FromDouble(double.PositiveInfinity),
            new((ObservationValueKind)int.MaxValue)
        ];

        foreach (var value in unsupported)
        {
            var bufferedFailure = Record.Exception(() => WriteBufferedObservationValue(value));
            var streamingFailure = Record.Exception(() => WriteStreamingObservationValue(value));

            Assert.NotNull(bufferedFailure);
            Assert.NotNull(streamingFailure);
            Assert.Equal(bufferedFailure.GetType(), streamingFailure.GetType());
        }

        var bytes = ObservationValue.FromBytes(new byte[] { 0, 1, 2, 253, 254, 255 });
        Assert.Throws<InvalidOperationException>(() =>
            WriteBufferedObservationValue(bytes, ObservationBytesJsonEncoding.Throw));
        Assert.Throws<InvalidOperationException>(() =>
            WriteStreamingObservationValue(bytes, ObservationBytesJsonEncoding.Throw));
    }

    [Fact]
    public void ArrayClassification_UsesStableStructuralPathsWithoutContentInference()
    {
        JsonObject content = new()
        {
            ["ordered"] = new JsonArray(
                new JsonObject { ["id"] = "b" },
                new JsonObject { ["id"] = "a" }),
            ["sets"] = new JsonArray(
                new JsonObject
                {
                    ["members"] = new JsonArray(
                        new JsonObject { ["id"] = "b" },
                        new JsonObject { ["id"] = "a" })
                })
        };
        List<string> visitedPaths = [];

        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            path =>
            {
                visitedPaths.Add(path.Value);
                return path.Value == "/sets/*/members"
                    ? CanonicalJsonArrayOrdering.ObjectSet("id")
                    : CanonicalJsonArrayOrdering.Sequence;
            });

        Assert.Equal(
            "{\"ordered\":[{\"id\":\"b\"},{\"id\":\"a\"}],\"sets\":[{\"members\":[{\"id\":\"a\"},{\"id\":\"b\"}]}]}",
            Encoding.UTF8.GetString(canonical));
        Assert.Equal(["/ordered", "/sets", "/sets/*/members"], visitedPaths);
    }

    [Fact]
    public void SequenceArrayPolicy_MatchesEquivalentPathClassifiedOutput()
    {
        JsonObject content = new()
        {
            ["nested"] = new JsonArray(
                new JsonObject
                {
                    ["values"] = new JsonArray(2, 1),
                    ["amount"] = JsonNode.Parse("1.2300")
                })
        };

        var expected = CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            static _ => CanonicalJsonArrayOrdering.Sequence,
            CanonicalJsonNumberSemantics.ExactDecimalRational);

        var actual = CanonicalJsonWriter.GetCanonicalSequenceBytes(
            content,
            Options,
            CanonicalJsonNumberSemantics.ExactDecimalRational);

        Assert.Equal(expected, actual);
        Assert.Equal("{\"nested\":[{\"amount\":1.23,\"values\":[2,1]}]}", Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void ArrayPath_EscapesPropertySegmentsAndHasValidDefaultRoot()
    {
        JsonObject content = new()
        {
            ["a/b*~"] = new JsonArray()
        };
        CanonicalJsonArrayPath visited = default;

        _ = CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            path =>
            {
                visited = path;
                return CanonicalJsonArrayOrdering.Sequence;
            });

        Assert.Equal(string.Empty, default(CanonicalJsonArrayPath).Value);
        Assert.Equal("/a~1b~2~0", visited.Value);
    }

    [Fact]
    public void ObjectSet_RejectsMissingAndDuplicateSortKeys()
    {
        JsonObject missing = new()
        {
            ["items"] = new JsonArray(new JsonObject { ["value"] = 1 })
        };
        JsonObject duplicate = new()
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "same", ["value"] = 1 },
                new JsonObject { ["id"] = "same", ["value"] = 2 })
        };

        Assert.Throws<InvalidOperationException>(() => WriteObjectSet(missing));
        Assert.Throws<InvalidOperationException>(() => WriteObjectSet(duplicate));
    }

    [Fact]
    public void StringSet_RejectsDuplicateItems()
    {
        JsonObject content = new()
        {
            ["items"] = new JsonArray("duplicate", "duplicate")
        };

        Assert.Throws<InvalidOperationException>(() => CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            static path => path.Value == "/items"
                ? CanonicalJsonArrayOrdering.StringSet
                : CanonicalJsonArrayOrdering.Sequence));
    }

    static byte[] WriteObjectSet(JsonObject content) =>
        CanonicalJsonWriter.GetCanonicalBytes(
            content,
            Options,
            static path => path.Value == "/items"
                ? CanonicalJsonArrayOrdering.ObjectSet("id")
                : CanonicalJsonArrayOrdering.Sequence);

    static IReadOnlyList<ObservationValue> CreateCanonicalObservationValueFixtures() =>
    [
        ObservationValue.Undefined,
        ObservationValue.Null,
        ObservationValue.FromInt64(long.MinValue),
        ObservationValue.FromInt64(long.MaxValue),
        ObservationValue.FromDouble(-0d),
        ObservationValue.FromDouble(double.Epsilon),
        ObservationValue.FromDouble(1.2345678901234567d),
        ObservationValue.FromDouble(1e300d),
        ObservationValue.FromDecimal(1.2300m),
        ObservationValue.FromDecimal(decimal.MinValue),
        ObservationValue.FromBool(false),
        ObservationValue.FromBool(true),
        ObservationValue.FromString(string.Empty),
        ObservationValue.FromString("quote:\" slash:\\ controls:\b\f\n\r\t html:<>& separators:\u2028\u2029 emoji:\U0001F642"),
        ObservationValue.FromString("unpaired-surrogates:\uD800:\uDC00"),
        ObservationValue.FromBytes(new byte[] { 0, 1, 2, 3, 252, 253, 254, 255 }),
        ObservationValue.FromDateTimeOffset(new DateTimeOffset(2026, 8, 28, 12, 34, 56, TimeSpan.FromHours(-6))),
        ObservationValue.FromDateOnly(new DateOnly(2026, 8, 28)),
        ObservationValue.FromTimeOnly(new TimeOnly(23, 59, 58, 999)),
        ObservationValue.FromTimeSpan(TimeSpan.FromTicks(-123_456_789)),
        ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["z-last"] = ObservationValue.FromInt64(3),
            ["a-first"] = ObservationValue.FromArray(
            [
                ObservationValue.FromString("nested"),
                ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["escaped\"property"] = ObservationValue.FromBool(true),
                    ["unicode-λ"] = ObservationValue.FromDecimal(0.0000000000000000000000000001m)
                })
            ]),
            ["m-middle"] = ObservationValue.Null
        }),
        ObservationValue.FromArray(
        [
            ObservationValue.Undefined,
            ObservationValue.FromBytes(new byte[] { 1, 2, 3 }),
            ObservationValue.EmptyObject,
            ObservationValue.FromArray([])
        ])
    ];

    static ObservationValue CreateGeneratedObservationValue(Random random, int maximumDepth)
    {
        if (maximumDepth > 0)
        {
            switch (random.Next(4))
            {
                case 0:
                    Dictionary<string, ObservationValue> properties = new(StringComparer.Ordinal);
                    var propertyCount = random.Next(0, 6);
                    for (var index = 0; index < propertyCount; index++)
                    {
                        properties[$"{GeneratedString(random)}-{index}"] =
                            CreateGeneratedObservationValue(random, maximumDepth - 1);
                    }
                    return ObservationValue.FromObject(properties);
                case 1:
                    var itemCount = random.Next(0, 7);
                    ObservationValue[] items = new ObservationValue[itemCount];
                    for (var index = 0; index < itemCount; index++)
                        items[index] = CreateGeneratedObservationValue(random, maximumDepth - 1);
                    return ObservationValue.FromArray(items);
            }
        }

        return random.Next(10) switch
        {
            0 => ObservationValue.Undefined,
            1 => ObservationValue.Null,
            2 => ObservationValue.FromInt64(random.NextInt64()),
            3 => ObservationValue.FromDouble((random.NextDouble() - 0.5d) * 1e200d),
            4 => ObservationValue.FromDecimal((decimal)(random.NextDouble() - 0.5d) * 1_000_000_000m),
            5 => ObservationValue.FromBool(random.Next(2) == 0),
            6 => ObservationValue.FromString(GeneratedString(random)),
            7 => ObservationValue.FromBytes(GeneratedBytes(random)),
            8 => ObservationValue.FromDateOnly(DateOnly.FromDayNumber(random.Next(1, DateOnly.MaxValue.DayNumber))),
            _ => ObservationValue.FromTimeSpan(TimeSpan.FromTicks(random.NextInt64(-TimeSpan.TicksPerDay, TimeSpan.TicksPerDay)))
        };
    }

    static string GeneratedString(Random random)
    {
        string[] fragments =
        [
            "plain",
            "quote-\"-slash-\\",
            "controls-\0-\n-\t",
            "html-<>&",
            "unicode-λ-中-🙂",
            "separators-\u2028-\u2029"
        ];
        return $"{fragments[random.Next(fragments.Length)]}-{random.NextInt64()}";
    }

    static byte[] GeneratedBytes(Random random)
    {
        byte[] bytes = new byte[random.Next(0, 65)];
        random.NextBytes(bytes);
        return bytes;
    }

    static void AssertObservationValueWriterEquivalence(ObservationValue value)
    {
        var buffered = WriteBufferedObservationValue(value);
        var streaming = WriteStreamingObservationValue(value);
        Assert.True(
            buffered.AsSpan().SequenceEqual(streaming),
            $"Canonical writers diverged for observation value kind '{value.Kind}'.\n" +
            $"Buffered:  {Encoding.UTF8.GetString(buffered)}\n" +
            $"Streaming: {Encoding.UTF8.GetString(streaming)}");
    }

    static byte[] WriteBufferedObservationValue(
        ObservationValue value,
        ObservationBytesJsonEncoding bytesEncoding = ObservationBytesJsonEncoding.Base64String)
    {
        ArrayBufferWriter<byte> output = new();
        using Utf8JsonWriter writer = new(output, CanonicalObservationWriterOptions);
        CanonicalJsonWriter.WriteCanonicalObservationValue(writer, value, bytesEncoding);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    static byte[] WriteStreamingObservationValue(
        ObservationValue value,
        ObservationBytesJsonEncoding bytesEncoding = ObservationBytesJsonEncoding.Base64String)
    {
        ArrayBufferWriter<byte> output = new();
        CanonicalJsonWriter.WriteCanonicalObservationValue(output, value, bytesEncoding);
        return output.WrittenSpan.ToArray();
    }
}
