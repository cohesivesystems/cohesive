using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PreparedObservationProjectionTests
{
    static readonly ValueContract Contract = new(new JsonTypeRef());

    [Fact]
    public void HostedProjectionSharesOnlyImmutableChildrenAndRetainsFreshAdmission()
    {
        var value = ObservationValue.FromObject(new Envelope(new ImmutableName("source"), [1, 2]));
        var input = PortableValue.Concrete(Contract, value);
        var first = HostedQueryValueAdapter.Decode<Envelope>(input, Contract).Success!;
        var second = HostedQueryValueAdapter.Decode<Envelope>(input, Contract).Success!;
        Assert.NotSame(first, second);
        Assert.NotSame(first.Numbers, second.Numbers);
        Assert.Same(first.Name, second.Name);
        first.Numbers[0] = 99;
        Assert.Equal(1, second.Numbers[0]);

        var changed = value.WithField(FieldPath.FromField(nameof(Envelope.Name)), ObservationValue.FromObject(new ImmutableName("changed")));
        var third = HostedQueryValueAdapter.Decode<Envelope>(PortableValue.Concrete(Contract, changed), Contract).Success!;
        Assert.NotSame(first.Name, third.Name);
        Assert.Equal("changed", third.Name.Value);
        var mismatch = HostedQueryValueAdapter.Decode<Envelope>(input, new(new ScalarTypeRef(ScalarTypeKind.String)));
        Assert.Equal(HostedQueryValueDiagnosticCodes.InputContractMismatch, mismatch.Failure!.Code);
    }

    [Fact]
    public void FrozenProfilesAndConcurrentReadsHaveExactIdentity()
    {
        var value = ObservationValue.FromObject(new ImmutableName("source"));
        var options = new JsonSerializerOptions();
        // Mutable profiles keep caller-owned results independent.
        Assert.NotSame(value.Deserialize<ImmutableName>(options), value.Deserialize<ImmutableName>(options));
        options.MakeReadOnly(populateMissingResolver: true);
        var expected = value.Deserialize<ImmutableName>(options);
        Parallel.For(0, 32, _ => Assert.Same(expected, value.Deserialize<ImmutableName>(options)));
        var other = new JsonSerializerOptions(options);
        other.MakeReadOnly(populateMissingResolver: true);
        Assert.NotSame(expected, value.Deserialize<ImmutableName>(other));
        var changed = value.WithField(FieldPath.FromField(nameof(ImmutableName.Value)), ObservationValue.FromString("changed"));
        Assert.Equal("changed", changed.Deserialize<ImmutableName>(options)!.Value);
    }

    [Fact]
    public void ConcurrentFirstReadsDecodeExactlyOnce()
    {
        var value = ObservationValue.FromObject(new ImmutableName("concurrent"));
        var converter = new CountingConverter();
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        options.MakeReadOnly(populateMissingResolver: true);
        var results = new ImmutableName?[32];
        Parallel.For(0, results.Length, index => results[index] = value.Deserialize<ImmutableName>(options));
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, converter.Reads);
    }

    sealed class CountingConverter : JsonConverter<ImmutableName>
    {
        internal int Reads;
        public override ImmutableName Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            Interlocked.Increment(ref Reads);
            reader.Skip();
            return new("concurrent");
        }
        public override void Write(Utf8JsonWriter writer, ImmutableName value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void WarmImmutableProjectionReadsHaveNoPerReadAllocation()
    {
        var value = ObservationValue.FromObject(new ImmutableName("source"));
        var expected = value.Deserialize<ImmutableName>();
        for (var i = 0; i < 16; i++) value.Deserialize<ImmutableName>();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 128; i++) Assert.Same(expected, value.Deserialize<ImmutableName>());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= 256, $"Warm projection reads allocated {allocated} bytes.");
    }

    [Fact]
    public void ProjectionPreparationDoesNotRetainDiscardedPayloadsOrProfiles()
    {
        var discarded = CreateDiscardedProjection();
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(discarded.Payload.IsAlive);
        Assert.False(discarded.Profile.IsAlive);
        Assert.False(discarded.Projection.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (WeakReference Payload, WeakReference Profile, WeakReference Projection) CreateDiscardedProjection()
    {
        var value = ObservationValue.FromObject(new ImmutableName("source"));
        var options = new JsonSerializerOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        var projection = value.Deserialize<ImmutableName>(options);
        return (new(value.Fields!), new(options), new(projection!));
    }

    [Fact]
    public void LivePayloadDoesNotRetainDiscardedSerializerProfiles()
    {
        var value = ObservationValue.FromObject(new ImmutableName("source"));
        var profile = CreateDiscardedProfile(value);
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(profile.IsAlive);
        GC.KeepAlive(value);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference CreateDiscardedProfile(ObservationValue value)
    {
        var options = new JsonSerializerOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        value.Deserialize<ImmutableName>(options);
        return new(options);
    }

    [Fact]
    public void ImmutableProjectionCacheIncludesBinaryEncoding()
    {
        var value = ObservationValue.FromFieldAssignments([new(FieldPath.FromField(nameof(ImmutablePayload.Data)), ObservationValue.FromBytes(new byte[] { 1, 2, 3 }))]);
        Assert.Throws<InvalidOperationException>(() => value.Deserialize<ImmutablePayload>());
        var decoded = value.Deserialize<ImmutablePayload>(bytesEncoding: ObservationBytesJsonEncoding.Base64String);
        Assert.Equal("AQID", decoded!.Data.String);
        Assert.Same(decoded, value.Deserialize<ImmutablePayload>(bytesEncoding: ObservationBytesJsonEncoding.Base64String));
        Assert.Throws<InvalidOperationException>(() => value.Deserialize<ImmutablePayload>());
    }

    [Fact]
    public void FailedImmutableProjectionIsRetryable()
    {
        var converter = new RetryConverter();
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        options.MakeReadOnly(populateMissingResolver: true);
        var value = ObservationValue.FromObject(new ImmutableName("source"));
        Assert.Throws<JsonException>(() => value.Deserialize<ImmutableName>(options));
        var expected = value.Deserialize<ImmutableName>(options);
        Assert.Same(expected, value.Deserialize<ImmutableName>(options));
        Assert.Equal(2, converter.Reads);
    }

    [Theory]
    [InlineData("{\"Name\":{\"Value\":\"source\"},\"Numbers\":[],\"unknown\":1}")]
    [InlineData("{\"Name\":{\"Value\":\"source\",\"unknown\":1},\"Numbers\":[]}")]
    [InlineData("{\"Name\":{\"Value\":\"source\"},\"Numbers\":[],\"numbers\":[1]}")]
    public void PreparedContractsRetainStrictUnknownMemberAndCaseRules(string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = PortableValue.Concrete(Contract, ObservationValue.FromJsonElement(document.RootElement));
        var actual = HostedQueryValueAdapter.Decode<Envelope>(value, Contract);
        Assert.Equal(HostedQueryValueDiagnosticCodes.ValueConversionFailed, actual.Failure!.Code);
    }

    [Fact]
    public void PreparedContractsHonorScalarWrapperConvertersAndIgnoreComputedSetlessProperties()
    {
        var valid = ObservationValue.FromObject(new { Id = "source", Number = 2, Computed = 100 });
        var result = HostedQueryValueAdapter.Decode<WithComputed>(PortableValue.Concrete(Contract, valid), Contract);
        Assert.Equal(ResultType.Success, result.Type);
        Assert.Equal("source", result.Success!.Id.Value);
        Assert.Equal(4, result.Success.Computed);
        var invalid = valid.WithField(FieldPath.FromField(nameof(WithComputed.Id)), ObservationValue.FromObject(new { Value = "source" }));
        Assert.Equal(HostedQueryValueDiagnosticCodes.ValueConversionFailed,
            HostedQueryValueAdapter.Decode<WithComputed>(PortableValue.Concrete(Contract, invalid), Contract).Failure!.Code);
    }

    [Fact]
    public void PreparedContractUsesTheExplicitJsonConstructor()
    {
        var input = PortableValue.Concrete(Contract, ObservationValue.FromObject(new { Name = "source", Computed = "ignored" }));
        var result = HostedQueryValueAdapter.Decode<ExplicitConstructor>(input, Contract);
        Assert.Equal(ResultType.Success, result.Type);
        Assert.Equal("source", result.Success!.Name);
        Assert.Equal("source!", result.Success.Computed);
    }

    public sealed class ExplicitConstructor
    {
        public ExplicitConstructor() => Name = "wrong constructor";
        [JsonConstructor] public ExplicitConstructor(string name) => Name = name;
        public string Name { get; }
        public string Computed => Name + "!";
    }

    [Theory]
    [InlineData("{\"Number\":3,\"Enabled\":true}")]
    [InlineData("{\"Enabled\":false}")]
    [InlineData("{\"Number\":null,\"Enabled\":true}")]
    [InlineData("{\"Number\":\"3\",\"Enabled\":true}")]
    [InlineData("{\"Number\":3.5,\"Enabled\":true}")]
    [InlineData("{\"Number\":3,\"enabled\":true}")]
    public void PreparedAdmissionMatchesTheOwningSerializerForSuccessAndFailure(string json)
    {
        using var document = JsonDocument.Parse(json);
        var observed = ObservationValue.FromJsonElement(document.RootElement);
        ScalarPayload? expected = null;
        var exception = Record.Exception(() => expected = observed.Deserialize<ScalarPayload>(
            ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions()));
        var actual = HostedQueryValueAdapter.Decode<ScalarPayload>(PortableValue.Concrete(Contract, observed), Contract);
        if (exception is null) { Assert.Equal(ResultType.Success, actual.Type); Assert.Equal(expected, actual.Success); }
        else Assert.Equal(HostedQueryValueDiagnosticCodes.ValueConversionFailed, actual.Failure!.Code);
    }

    public sealed record ScalarPayload(int Number, bool Enabled);

    [Fact]
    public void PreparedConstructorsPreserveCanonicalMemberConverterOrder()
    {
        using var document = JsonDocument.Parse("{\"Z\":1,\"A\":2}");
        var value = ObservationValue.FromJsonElement(document.RootElement);
        OrderedLeafConverter.Sequence = 0;
        var expected = value.Deserialize<Reordered>(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions());
        OrderedLeafConverter.Sequence = 0;
        var actual = HostedQueryValueAdapter.Decode<Reordered>(PortableValue.Concrete(Contract, value), Contract).Success!;
        Assert.Equal(expected, actual);
    }

    public sealed record Reordered(OrderedLeaf Z, OrderedLeaf A);
    [JsonConverter(typeof(OrderedLeafConverter))]
    public sealed record OrderedLeaf(int Value, int Order);
    public sealed class OrderedLeafConverter : JsonConverter<OrderedLeaf>
    {
        internal static int Sequence;
        public override OrderedLeaf Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            new(reader.GetInt32(), ++Sequence);
        public override void Write(Utf8JsonWriter writer, OrderedLeaf value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void IgnoredComputedFieldsStillRetainWholePayloadJsonLimits()
    {
        var original = ObservationValue.FromObject(new { Id = "source", Number = 2, Computed = 4 });
        var deep = ObservationValue.Null;
        for (var i = 0; i < 70; i++) deep = ObservationValue.FromImmutableArray([deep]);
        foreach (var ignored in new[] { deep, ObservationValue.FromBytes(new byte[] { 1 }) })
        {
            var value = original.WithField(FieldPath.FromField(nameof(WithComputed.Computed)), ignored);
            Assert.NotNull(Record.Exception(() => value.Deserialize<WithComputed>(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions())));
            var result = HostedQueryValueAdapter.Decode<WithComputed>(PortableValue.Concrete(Contract, value), Contract);
            Assert.Equal(HostedQueryValueDiagnosticCodes.ValueConversionFailed, result.Failure!.Code);
            // Observation bindings retain native storage and have never required a JSON round trip.
            Assert.Equal(ResultType.Success, HostedQueryValueAdapter.Decode<ObservationValue>(PortableValue.Concrete(Contract, value), Contract).Type);
        }
    }

    [Fact]
    public void MalformedClrTextKeepsTheOwningSerializerNormalization()
    {
        var value = ObservationValue.FromObject(new { Text = new string((char)0xd800, 1), Number = 2 });
        var expected = value.Deserialize<UnicodePayload>(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions());
        var actual = HostedQueryValueAdapter.Decode<UnicodePayload>(PortableValue.Concrete(Contract, value), Contract);
        Assert.Equal(ResultType.Success, actual.Type);
        Assert.Equal(expected, actual.Success);
    }

    public sealed record UnicodePayload(string Text, int Number);

    [Fact]
    public void PreparedNumbersRetainSerializerRoundingScaleAndSignedZero()
    {
        var samples = new List<ObservationValue>
        {
            ObservationValue.FromDecimal(1.2300m),
            ObservationValue.FromDecimal(new decimal(0, 0, 0, true, 4)),
            ObservationValue.FromDecimal(decimal.MaxValue), ObservationValue.FromDouble(-0d),
            ObservationValue.FromDouble(0.1d), ObservationValue.FromInt64(long.MaxValue)
        };
        var random = new Random(1701);
        for (var i = 0; i < 128; i++) samples.Add(ObservationValue.FromDecimal(new decimal(
            random.Next(), random.Next(), random.Next(), i % 2 == 0, (byte)(i % 29))));
        for (var i = 0; i < 128; i++) samples.Add(ObservationValue.FromDouble((random.NextDouble() - 0.5) * 1e20));
        foreach (var sample in samples)
        {
            var value = ObservationValue.FromFieldAssignments([
                new(FieldPath.FromField(nameof(Numbers.Decimal)), sample),
                new(FieldPath.FromField(nameof(Numbers.Double)), sample)]);
            var expected = value.Deserialize<Numbers>(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions())!;
            var actual = HostedQueryValueAdapter.Decode<Numbers>(PortableValue.Concrete(Contract, value), Contract).Success!;
            Assert.Equal(decimal.GetBits(expected.Decimal), decimal.GetBits(actual.Decimal));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Double), BitConverter.DoubleToInt64Bits(actual.Double));
        }
    }

    public sealed record Numbers(decimal Decimal, double Double);

    [Theory]
    [InlineData("ready")]
    [InlineData("ready, Done")]
    [InlineData("READY")]
    [InlineData("123")]
    public void PreparedEnumLiteralsRetainAliasesFlagsAndRejectionRules(string token)
    {
        var value = ObservationValue.FromObject(new { State = token, Number = 2 });
        EnumEnvelope? expected = null;
        var failure = Record.Exception(() => expected = value.Deserialize<EnumEnvelope>(
            ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions()));
        var actual = HostedQueryValueAdapter.Decode<EnumEnvelope>(PortableValue.Concrete(Contract, value), Contract);
        if (failure is null) { Assert.Equal(ResultType.Success, actual.Type); Assert.Equal(expected, actual.Success); }
        else Assert.Equal(HostedQueryValueDiagnosticCodes.ValueConversionFailed, actual.Failure!.Code);
    }

    [Fact]
    public void WarmEnumRowsDoNotAllocateJsonForEachMember()
    {
        var input = PortableValue.Concrete(Contract, ObservationValue.FromObject(new EnumRows(
            Enumerable.Range(0, 1024).Select(_ => new EnumRow(State.Ready, State.Done, State.Ready)).ToArray())));
        for (var i = 0; i < 4; i++) HostedQueryValueAdapter.Decode<EnumRows>(input, Contract);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var decoded = HostedQueryValueAdapter.Decode<EnumRows>(input, Contract);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(State.Done, decoded.Success!.Rows[0].Second);
        }
        Array.Sort(samples);
        Assert.True(samples[3] <= 96 * 1024, $"Enum rows allocated {samples[3]} bytes.");
    }

    [Flags, JsonConverter(typeof(JsonStringEnumConverter<State>))]
    public enum State { None = 0, [JsonStringEnumMemberName("ready")] Ready = 1, Done = 2 }
    public sealed record EnumEnvelope(State State, int Number);
    public sealed record EnumRow(State First, State Second, State Third);
    public sealed record EnumRows(EnumRow[] Rows);

    [Fact]
    public void RequiredConstructorAndNullableHostPoliciesRetainTheCompleteSerializer()
    {
        foreach (var requiredConstructor in new[] { false, true })
        {
            var options = new JsonSerializerOptions(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions())
            {
                RespectRequiredConstructorParameters = requiredConstructor,
                RespectNullableAnnotations = !requiredConstructor
            };
            options.MakeReadOnly(populateMissingResolver: true);
            Assert.Null(DefaultObservationValueConverterCache.PrepareClrContract(typeof(UnicodePayload), options));
        }
    }

    public sealed record ImmutablePayload(ObservationValue Data) : IImmutableObservationProjection;
    public sealed record ImmutableName(string Value, int Revision = 1) : IImmutableObservationProjection;
    public sealed record Envelope(ImmutableName Name, int[] Numbers);
    public sealed record WithComputed(GraphId Id, int Number) { public int Computed => Number * 2; }

    sealed class RetryConverter : JsonConverter<ImmutableName>
    {
        internal int Reads;
        public override ImmutableName Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (++Reads == 1) throw new JsonException("Transient owning decoder failure.");
            reader.Skip();
            return new("retained");
        }
        public override void Write(Utf8JsonWriter writer, ImmutableName value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }
}
