using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class StrictDocumentJsonTests
{
    [Fact]
    public void TypedObjectApi_WritesAndReadsOneCanonicalExactDecimalWire()
    {
        var document = new TestDocument(
            Name: "index-sync",
            Slots: [2, 1],
            BatchSize: 1.2300m,
            Description: null);
        var options = StrictDocumentJson.CreateOptions();

        var canonical = StrictDocumentJson.GetCanonicalBytes(document, options);

        Assert.Equal(
            """{"batchSize":1.23,"description":null,"name":"index-sync","slots":[2,1]}""",
            Encoding.UTF8.GetString(canonical));
        Assert.True(
            StrictDocumentJson.TryReadCanonicalObject<TestDocument>(
                Encoding.UTF8.GetString(canonical),
                options,
                "test document",
                out var restored,
                out var error));
        Assert.NotNull(restored);
        Assert.Equal(document.Name, restored.Name);
        Assert.Equal(document.Slots, restored.Slots);
        Assert.Equal(document.BatchSize, restored.BatchSize);
        Assert.Null(restored.Description);
        Assert.Equal(default, error);
        Assert.Equal(canonical, StrictDocumentJson.GetCanonicalBytes(restored, options));
    }

    [Theory]
    [InlineData("[]", StrictDocumentJsonReadFailure.RootInvalid, "$")]
    [InlineData(
        """{"batchSize":1.23,"description":null,"name":"first","name":"second","slots":[2,1]}""",
        StrictDocumentJsonReadFailure.DuplicateProperty,
        "/name")]
    [InlineData(
        """{"batchSize":1.23,"name":"index-sync","slots":[2,1]}""",
        StrictDocumentJsonReadFailure.WireNonCanonical,
        "$")]
    public void TypedObjectApi_ReportsStrictStructuredReadFailures(
        string json,
        StrictDocumentJsonReadFailure expectedFailure,
        string expectedLocation)
    {
        var success = StrictDocumentJson.TryReadCanonicalObject<TestDocument>(
            json,
            StrictDocumentJson.CreateOptions(),
            "test document",
            out var value,
            out var error);

        Assert.False(success);
        if (expectedFailure == StrictDocumentJsonReadFailure.WireNonCanonical)
        {
            Assert.NotNull(value);
        }
        else
        {
            Assert.Null(value);
        }
        Assert.Equal(expectedFailure, error.Failure);
        Assert.Equal(expectedLocation, error.Location);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData("""{"outer":[{"a/b~":{"value":1,"value":2}}]}""", "", "/outer/0/a~1b~0/value")]
    [InlineData("""[{"items":[{"id":"first"},{"id":"a","id":"b"}]}]""", "/root", "/root/0/items/1/id")]
    public void DuplicatePropertyScan_ReportsNestedEscapedJsonPointer(
        string json,
        string rootPath,
        string expectedLocation)
    {
        using var document = JsonDocument.Parse(json);

        var duplicate = StrictDocumentJson.TryFindDuplicateProperty(
            document.RootElement,
            rootPath,
            out var location);

        Assert.True(duplicate);
        Assert.Equal(expectedLocation, location);
    }

    [Theory]
    [InlineData("{\"name\":1,\"na\\u006de\":2}", "/name")]
    [InlineData("{\"é\":1,\"\\u00e9\":2}", "/é")]
    [InlineData("{\"outer\":{\"inner\":1,\"inner\":2},\"outer\":3}", "/outer/inner")]
    public void DuplicatePropertyScan_UsesDecodedOrdinalNamesAndDepthFirstFailure(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, string.Empty, out var location));
        Assert.Equal(expected, location);
    }

    [Fact]
    public void DuplicatePropertyScan_WarmSuccessfulScansAllocateNoManagedMemory()
    {
        var wide = "{" + string.Join(",", Enumerable.Range(0, 128).Select(index => $"\"field{index}\":{index}")) + "}";
        using var document = JsonDocument.Parse("{\"rows\":[" + string.Join(",", Enumerable.Repeat(wide, 32)) + "],\"unicode\":{\"é\":1,\"other\":2}}");
        for (var warmup = 0; warmup < 4; warmup++)
            Assert.False(StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, string.Empty, out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var duplicate = false;
        for (var iteration = 0; iteration < 16; iteration++)
            duplicate |= StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, string.Empty, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(duplicate);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DuplicatePropertyScan_ConcurrentLeasesRemainIsolatedAfterFailures()
    {
        Parallel.For(0, 32, index =>
        {
            using var invalid = JsonDocument.Parse($"{{\"item{index}\":1,\"item{index}\":2}}");
            Assert.True(StrictDocumentJson.TryFindDuplicateProperty(invalid.RootElement, "/root", out var location));
            Assert.Equal($"/root/item{index}", location);
            using var valid = JsonDocument.Parse("{\"a\":1,\"b\":[{\"a\":2}]}");
            Assert.False(StrictDocumentJson.TryFindDuplicateProperty(valid.RootElement, string.Empty, out location));
            Assert.Equal(string.Empty, location);
        });
    }

    [Fact]
    public void TypedObjectApi_RoundTripsDeclaredJsonStringEnumWireMembers()
    {
        var document = new WireEnumDocument(WireDisposition.PartnerOverlay);
        var options = StrictDocumentJson.CreateOptions();

        var canonical = StrictDocumentJson.GetCanonicalBytes(document, options);

        Assert.Equal("""{"disposition":"partner-overlay"}""", Encoding.UTF8.GetString(canonical));
        Assert.True(
            StrictDocumentJson.TryReadCanonicalObject<WireEnumDocument>(
                Encoding.UTF8.GetString(canonical),
                options,
                "wire enum document",
                out var restored,
                out var error),
            error.Message);
        Assert.Equal(WireDisposition.PartnerOverlay, restored?.Disposition);
    }

    [Theory]
    [InlineData("""{"disposition":"PartnerOverlay"}""")]
    [InlineData("""{"disposition":"PARTNER-OVERLAY"}""")]
    [InlineData("""{"disposition":1}""")]
    public void TypedObjectApi_RejectsNonCanonicalJsonStringEnumRepresentations(string json)
    {
        var success = StrictDocumentJson.TryReadCanonicalObject<WireEnumDocument>(
            json,
            StrictDocumentJson.CreateOptions(),
            "wire enum document",
            out _,
            out var error);

        Assert.False(success);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void TypedObjectApi_IsByteAndFingerprintEquivalentAcrossTheObservationDomain()
    {
        var options = StrictDocumentJson.CreateOptions();
        options.Converters.Add(new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Base64String));
        var fixtures = CanonicalJsonWriterTests.CreateCanonicalObservationValueFixtures();
        Assert.Equal(Enum.GetValues<ObservationValueKind>(),
            fixtures.Select(x => x.Kind).Distinct().OrderBy(x => x).ToArray());
        foreach (var value in fixtures)
            AssertTypedEquivalence(new ObservationDocument(value), options);
        Random random = new(0x51A1_2026);
        for (var index = 0; index < 512; index++)
            AssertTypedEquivalence(new ObservationDocument(
                CanonicalJsonWriterTests.CreateGeneratedObservationValue(random, maximumDepth: 5)), options);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[3,1,null,true,false,\"<>&🙂\"]")]
    [InlineData("{\"z\":-0.0,\"a\":1e300,\"nested\":{\"b\":1.2300,\"a\":1e-300}}")]
    public void TypedObjectApi_PreservesNestedExactNumbersEscapingAndSequences(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        foreach (var formatting in Enum.GetValues<PortableDocumentJsonFormatting>())
            AssertTypedEquivalence(new JsonDocumentValue(parsed.RootElement), StrictDocumentJson.CreateOptions(formatting));
    }

    [Theory]
    [InlineData(false, "{\"a\":1,\"a\":2}")]
    [InlineData(true, "{\"a\":1,\"A\":2}")]
    public void TypedObjectApi_RetainsPropertyCollisionFailures(bool ignoreCase, string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var options = StrictDocumentJson.CreateOptions();
        options.PropertyNameCaseInsensitive = ignoreCase;
        var value = new JsonDocumentValue(parsed.RootElement);
        var referenceFailure = Record.Exception(() => NodeReference(value, options));
        var actualFailure = Record.Exception(() => StrictDocumentJson.GetCanonicalBytes(value, options));
        Assert.NotNull(referenceFailure);
        Assert.NotNull(actualFailure);
        Assert.Equal(referenceFailure.GetType(), actualFailure.GetType());
    }

    [Fact]
    public void TypedObjectApi_ReducesAllocationWithoutCachingCallerOwnedValues()
    {
        var value = new JsonDocumentValue(JsonSerializer.SerializeToElement(Enumerable.Range(0, 128)
            .Select(x => new { z = x, a = "payload", nested = new { enabled = true, values = new[] { 3, 1, 2 } } })));
        var options = StrictDocumentJson.CreateOptions();
        AssertTypedEquivalence(value, options);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var reference = NodeReference(value, options);
        var referenceAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var actual = StrictDocumentJson.GetCanonicalBytes(value, options);
        var actualAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(reference, actual);
        Assert.True(actualAllocation < referenceAllocation / 2,
            $"Immutable path allocated {actualAllocation} bytes; node reference allocated {referenceAllocation}.");
        var mutable = new MutableDocument { Name = "first" };
        var first = StrictDocumentJson.GetCanonicalBytes(mutable, options);
        mutable.Name = "second";
        Assert.NotEqual(first, StrictDocumentJson.GetCanonicalBytes(mutable, options));
    }

    [Theory]
    [InlineData("1e300")]
    [InlineData("-0.0")]
    [InlineData("\"<>&🙂\"")]
    [InlineData("[3,1,2]")]
    public void TypedObjectApi_RetainsCustomConverterRootShapes(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        AssertTypedEquivalence(new RawRoot(parsed.RootElement), StrictDocumentJson.CreateOptions());
    }

    [Fact]
    public void TypedObjectApi_RetainsNullRootAndBinaryPolicyFailures()
    {
        using var parsed = JsonDocument.Parse("null");
        var options = StrictDocumentJson.CreateOptions();
        Assert.Throws<InvalidOperationException>(() => StrictDocumentJson.GetCanonicalBytes(new RawRoot(parsed.RootElement), options));
        Assert.Throws<InvalidOperationException>(() => NodeReference(new RawRoot(parsed.RootElement), options));
        options = StrictDocumentJson.CreateOptions();
        options.Converters.Add(new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Throw));
        var binary = new ObservationDocument(ObservationValue.FromBytes(new byte[] { 1, 2, 3 }));
        var before = Record.Exception(() => NodeReference(binary, options));
        var after = Record.Exception(() => StrictDocumentJson.GetCanonicalBytes(binary, options));
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.GetType(), after.GetType());
    }

    [JsonConverter(typeof(RawRootConverter))]
    sealed record RawRoot(JsonElement Value);
    sealed class RawRootConverter : JsonConverter<RawRoot>
    {
        public override RawRoot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, RawRoot value, JsonSerializerOptions options) => value.Value.WriteTo(writer);
    }

    static void AssertTypedEquivalence<T>(T value, JsonSerializerOptions options) where T : class
    {
        var reference = NodeReference(value, options);
        var actual = StrictDocumentJson.GetCanonicalBytes(value, options);
        Assert.Equal(reference, actual);
        Assert.Equal(SHA256.HashData(reference), SHA256.HashData(actual));
    }

    static byte[] NodeReference<T>(T value, JsonSerializerOptions options) where T : class =>
        CanonicalJsonWriter.GetCanonicalSequenceBytes(
            JsonSerializer.SerializeToNode(value, typeof(T), options)
                ?? throw new InvalidOperationException("Cannot materialize null JSON."), options,
            CanonicalJsonNumberSemantics.ExactDecimalRational);

    sealed record ObservationDocument(ObservationValue Value);
    sealed record JsonDocumentValue(JsonElement Value);
    sealed class MutableDocument { public string Name { get; set; } = string.Empty; }

    sealed record TestDocument(
        string Name,
        int[] Slots,
        decimal BatchSize,
        string? Description);

    sealed record WireEnumDocument(WireDisposition Disposition);

    [JsonConverter(typeof(JsonStringEnumConverter))]
    enum WireDisposition
    {
        [JsonStringEnumMemberName("standard")]
        Standard,

        [JsonStringEnumMemberName("partner-overlay")]
        PartnerOverlay
    }
}
