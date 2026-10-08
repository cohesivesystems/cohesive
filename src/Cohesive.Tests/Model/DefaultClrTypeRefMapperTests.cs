using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Authoring;

namespace Cohesive.Tests.Model;

public sealed class DefaultClrTypeRefMapperTests
{
    readonly DefaultClrTypeRefMapper mapper = new();

    [Fact]
    public void Map_UsesCanonicalScalarKindsSharedWithClrShapeAuthoring()
    {
        (Type ClrType, ScalarTypeKind Expected)[] cases =
        [
            (typeof(long), ScalarTypeKind.Int64),
            (typeof(long?), ScalarTypeKind.Int64),
            (typeof(DateOnly), ScalarTypeKind.Date),
            (typeof(DateTime), ScalarTypeKind.DateTime),
            (typeof(DateTimeOffset), ScalarTypeKind.Instant),
            (typeof(byte[]), ScalarTypeKind.Bytes)
        ];

        foreach (var (clrType, expected) in cases)
        {
            var scalar = Assert.IsType<ScalarTypeRef>(mapper.Map(clrType, nullability: null));
            Assert.Equal(expected, scalar.Kind);
        }
    }

    [Fact]
    public void Map_StructuralObjectUsesSerializedMemberNamesInOrdinalOrder()
    {
        var type = Assert.IsType<ObjectTypeRef>(mapper.Map(typeof(SerializedEnvelope), nullability: null));

        Assert.Collection(
            type.Fields,
            field =>
            {
                Assert.Equal("alpha", field.Name);
                Assert.Equal(ScalarTypeKind.Instant, Assert.IsType<ScalarTypeRef>(field.Type).Kind);
            },
            field =>
            {
                Assert.Equal("zeta", field.Name);
                Assert.Equal(ScalarTypeKind.Int64, Assert.IsType<ScalarTypeRef>(field.Type).Kind);
            });
    }

    [Fact]
    public void Map_CollidingSerializedMemberNamesProduceDiagnosticOpaqueType()
    {
        var type = Assert.IsType<OpaqueRuntimeTypeRef>(
            mapper.Map(typeof(AmbiguousSerializedEnvelope), nullability: null));

        Assert.Equal(
            TypeInferenceDiagnosticReasons.AmbiguousSerializedProperty,
            type.InferenceDiagnostic?.Reason);
    }

    [Fact]
    public void Map_DeclaredPortableJsonValueUsesItsJsonContractAtEveryOccurrence()
    {
        var document = Assert.IsType<JsonTypeRef>(
            mapper.Map(typeof(PortableDocument), nullability: null));
        var envelope = Assert.IsType<ObjectTypeRef>(
            mapper.Map(typeof(PortableDocumentEnvelope), nullability: null));

        Assert.Equal(JsonTypeKind.Object, document.Kind);
        Assert.Equal(
            JsonTypeKind.Object,
            Assert.IsType<JsonTypeRef>(Assert.Single(envelope.Fields).Type).Kind);
    }

    [Fact]
    public void Map_JsonStringEnumUsesExactCanonicalWireMembersAcceptedByObservationValues()
    {
        var type = Assert.IsType<EnumTypeRef>(mapper.Map(typeof(WireDisposition), nullability: null));
        var contract = new ValueContract(type);
        var observed = ObservationValue.FromObject(WireDisposition.PartnerOverlay);

        Assert.Equal(["standard", "partner-overlay"], type.Members.ToArray());
        Assert.Equal("partner-overlay", observed.GetString());
        Assert.True(contract.IsSatisfiedByConstant(observed));
    }

    [Fact]
    public void Map_PlainEnumRetainsClrMemberNames()
    {
        var type = Assert.IsType<EnumTypeRef>(mapper.Map(typeof(PlainDisposition), nullability: null));

        Assert.Equal(
            [nameof(PlainDisposition.Standard), nameof(PlainDisposition.PartnerOverlay)],
            type.Members.ToArray());
        Assert.True(new ValueContract(type).IsSatisfiedByConstant(
            ObservationValue.FromObject(PlainDisposition.PartnerOverlay)));
    }

    [Fact]
    public void Map_CustomEnumConverterDoesNotClaimAnExactMemberCatalog()
    {
        var type = Assert.IsType<OpaqueRuntimeTypeRef>(
            mapper.Map(typeof(CustomConvertedDisposition), nullability: null));

        Assert.Equal(TypeInferenceDiagnosticReasons.UnsupportedEnumConverter, type.InferenceDiagnostic?.Reason);
    }

    [Fact]
    public void Map_FreshMappersReuseImmutableEnumMembersWithoutRepeatedDiscovery()
    {
        var first = Assert.IsType<EnumTypeRef>(new DefaultClrTypeRefMapper().Map(typeof(WireDisposition), null));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var second = Assert.IsType<EnumTypeRef>(new DefaultClrTypeRefMapper().Map(typeof(WireDisposition), null));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 1, 256);
        Assert.True(first.Members == second.Members); // Same immutable backing array.
    }

    [Fact]
    public void Map_GenericStringEnumConverterKeepsDeclaredWireNames()
    {
        var result = Assert.IsType<EnumTypeRef>(mapper.Map(typeof(GenericWireDisposition), null));
        Assert.Equal(["accepted", "rejected"], result.Members.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumCatalogFallbackDoesNotChangeStrictCustomConverterFailure(bool fallbackFirst)
    {
        var type = fallbackFirst ? typeof(FallbackFirstDisposition) : typeof(StrictFirstDisposition);
        var first = DiscoverCatalog(type, fallbackFirst);
        var second = DiscoverCatalog(type, !fallbackFirst);
        var strict = fallbackFirst ? second : first;
        var fallback = fallbackFirst ? first : second;
        Assert.False(strict.Success);
        Assert.Null(strict.Catalog);
        Assert.Equal("UnsupportedConverter", strict.Failure);
        Assert.Equal(typeof(CustomEnumFactory), strict.Converter);
        Assert.True(fallback.Success);
        Assert.Same(fallback.Catalog, DiscoverCatalog(type, true).Catalog);
        Assert.False(DiscoverCatalog(type, false).Success);
    }

    [Fact]
    public void EnumCatalogCoordinatesConcurrentFirstUseAndRejectsAmbiguousNamesInBothPolicies()
    {
        System.Collections.Concurrent.ConcurrentBag<object?> catalogs = [];
        Parallel.For(0, 64, _ => catalogs.Add(DiscoverCatalog(typeof(ColdWireDisposition), false).Catalog));
        var first = catalogs.First();
        Assert.NotNull(first);
        Assert.All(catalogs, value => Assert.Same(first, value));
        Assert.Same(first, DiscoverCatalog(typeof(ColdWireDisposition), true).Catalog);
        foreach (var fallback in new[] { false, true })
        {
            var ambiguous = DiscoverCatalog(typeof(AmbiguousWireDisposition), fallback);
            Assert.False(ambiguous.Success);
            Assert.Null(ambiguous.Catalog);
            Assert.Equal("AmbiguousWireMember", ambiguous.Failure);
        }
        var inferred = Assert.IsType<OpaqueRuntimeTypeRef>(mapper.Map(typeof(AmbiguousWireDisposition), null));
        Assert.Equal(TypeInferenceDiagnosticReasons.AmbiguousSerializedEnumMember, inferred.InferenceDiagnostic?.Reason);
    }

    static (bool Success, object? Catalog, string Failure, Type? Converter) DiscoverCatalog(Type type, bool fallback)
    {
        var catalogType = typeof(DefaultClrTypeRefMapper).Assembly.GetType("Cohesive.Model.Serialization.SerializedEnumMemberCatalog")!;
        object?[] arguments = [type, null, null, null, fallback];
        var success = (bool)catalogType.GetMethod("TryCreate")!.Invoke(null, arguments)!;
        return (success, arguments[1], arguments[2]!.ToString()!, (Type?)arguments[3]);
    }

    [JsonConverter(typeof(JsonStringEnumConverter<GenericWireDisposition>))]
    enum GenericWireDisposition
    {
        [JsonStringEnumMemberName("accepted")] Accepted,
        [JsonStringEnumMemberName("rejected")] Rejected
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    enum ColdWireDisposition { First, Second }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    enum AmbiguousWireDisposition
    {
        [JsonStringEnumMemberName("same")] First,
        [JsonStringEnumMemberName("same")] Second
    }

    [JsonConverter(typeof(CustomEnumFactory))]
    enum FallbackFirstDisposition { First }

    [JsonConverter(typeof(CustomEnumFactory))]
    enum StrictFirstDisposition { First }

    sealed class CustomEnumFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            new JsonStringEnumConverter().CreateConverter(typeToConvert, options);
    }

    [Fact]
    public void Map_RepeatedPropertiesPreserveOccurrenceNullabilityAndRecursivePaths()
    {
        var root = Assert.IsType<ObjectTypeRef>(mapper.Map(typeof(RepeatedEnvelope), null));
        Assert.Equal(2, root.Fields.Count());
        foreach (var field in root.Fields)
        {
            var child = Assert.IsType<ObjectTypeRef>(field.Type);
            var optional = Assert.Single(child.Fields, x => x.Name == nameof(RepeatedChild.Optional));
            Assert.Equal(FieldNullability.Nullable, optional.Nullability);
            var required = Assert.Single(child.Fields, x => x.Name == nameof(RepeatedChild.Required));
            Assert.Equal(FieldNullability.NonNullable, required.Nullability);
            var recursive = Assert.Single(child.Fields, x => x.Name == nameof(RepeatedChild.Parent));
            Assert.Equal(TypeInferenceDiagnosticReasons.RecursiveType,
                Assert.IsType<OpaqueRuntimeTypeRef>(recursive.Type).InferenceDiagnostic?.Reason);
        }
        // A different root must discover its own recursion boundary after the first traversal ends.
        var childRoot = Assert.IsType<ObjectTypeRef>(mapper.Map(typeof(RepeatedChild), null));
        var parent = Assert.IsType<ObjectTypeRef>(Assert.Single(childRoot.Fields,
            x => x.Name == nameof(RepeatedChild.Parent)).Type);
        Assert.All(parent.Fields, field => Assert.IsType<OpaqueRuntimeTypeRef>(field.Type));
    }

    [Fact]
    public void Map_ConcurrentInvocationsKeepExplicitMappingsIsolated()
    {
        var explicitMapper = new DefaultClrTypeRefMapper(new Dictionary<Type, TypeRef>
        {
            [typeof(RepeatedChild)] = new ScalarTypeRef(ScalarTypeKind.String)
        });
        Parallel.For(0, 32, _ =>
        {
            var inferred = Assert.IsType<ObjectTypeRef>(mapper.Map(typeof(RepeatedEnvelope), null));
            Assert.All(inferred.Fields, field => Assert.IsType<ObjectTypeRef>(field.Type));
            var declared = Assert.IsType<ObjectTypeRef>(explicitMapper.Map(typeof(RepeatedEnvelope), null));
            Assert.All(declared.Fields, field => Assert.IsType<ScalarTypeRef>(field.Type));
        });
    }

    [Fact]
    public void Map_CollectionElementsRetainNestedGenericNullability()
    {
        var root = Assert.IsType<ObjectTypeRef>(mapper.Map(typeof(Pairs), null));
        var array = Assert.IsType<ArrayTypeRef>(Assert.Single(root.Fields).Type);
        var pair = Assert.IsType<ObjectTypeRef>(array.ElementType);
        Assert.Equal(FieldNullability.NonNullable, Assert.Single(pair.Fields, x => x.Name == "Key").Nullability);
        Assert.Equal(FieldNullability.Nullable, Assert.Single(pair.Fields, x => x.Name == "Value").Nullability);
    }

    [Fact]
    public void Map_FreshMappersReusePropertyMetadataWithoutRetainingOccurrenceContracts()
    {
        _ = new DefaultClrTypeRefMapper().Map(typeof(Leaf), null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = new DefaultClrTypeRefMapper().Map(typeof(Leaf), null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(result);
        Assert.InRange(allocated, 1, 1_400);
    }

    [Fact]
    public void Map_CallerOccurrenceNullabilityRemainsIndependentOfSharedPropertyMetadata()
    {
        NullabilityInfoContext context = new();
        var required = context.Create(typeof(RootOccurrences).GetProperty(nameof(RootOccurrences.Required))!);
        var optional = context.Create(typeof(RootOccurrences).GetProperty(nameof(RootOccurrences.Optional))!);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(FieldNullability.Nullable, ValueNullability(optional));
            Assert.Equal(FieldNullability.NonNullable, ValueNullability(required));
        }

        FieldNullability ValueNullability(NullabilityInfo occurrence)
        {
            var array = Assert.IsType<ArrayTypeRef>(mapper.Map(occurrence.Type, occurrence));
            var pair = Assert.IsType<ObjectTypeRef>(array.ElementType);
            return Assert.Single(pair.Fields, field => field.Name == "Value").Nullability;
        }
    }

    sealed record RootOccurrences(IReadOnlyList<KeyValuePair<string, string>> Required,
        IReadOnlyList<KeyValuePair<string, string?>> Optional);

    [Fact]
    public void Map_RepeatedShapeBoundsTemporaryAllocations()
    {
        // Warm shared property discovery and nullability; include retained IR and traversal-owned work.
        mapper.Map(typeof(LargeEnvelope), null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = mapper.Map(typeof(LargeEnvelope), null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(result);
        Assert.InRange(allocated, 1, 35_000);
    }

    [Fact]
    public async Task Map_ConcurrentColdMetadataKeepsGraphsAndExplicitMappingsIndependent()
    {
        var mappings = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            Assert.IsType<ObjectTypeRef>(new DefaultClrTypeRefMapper().Map(typeof(ColdMetadataEnvelope), null)))));
        foreach (var mapping in mappings)
        {
            Assert.Equal(new[] { "alpha", "zeta" }, mapping.Fields.Select(field => field.Name));
            Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(mapping.Fields[0].Type).Kind);
            Assert.Equal(FieldNullability.Nullable, mapping.Fields[0].Nullability);
        }
        for (var index = 1; index < mappings.Length; index++)
            Assert.NotSame(mappings[0], mappings[index]);
        var overridden = new DefaultClrTypeRefMapper(new Dictionary<Type, TypeRef>
        {
            [typeof(string)] = new ScalarTypeRef(ScalarTypeKind.Instant)
        });
        var explicitGraph = Assert.IsType<ObjectTypeRef>(overridden.Map(typeof(ColdMetadataEnvelope), null));
        Assert.Equal(ScalarTypeKind.Instant, Assert.IsType<ScalarTypeRef>(explicitGraph.Fields[0].Type).Kind);
        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(mappings[0].Fields[0].Type).Kind);
    }

    sealed record ColdMetadataEnvelope(
        [property: JsonPropertyName("zeta")] long Number,
        [property: JsonPropertyName("alpha")] string? Text);

    sealed record Pairs(IReadOnlyList<KeyValuePair<string, string?>> Items);
    sealed record Leaf(string Name, string? Description, long Sequence, DateTimeOffset Time);
    sealed record Branch(Leaf First, Leaf Second, Leaf Third, Leaf Fourth);
    sealed record LargeEnvelope(Branch A, Branch B, Branch C, Branch D, Branch E, Branch F, Branch G, Branch H);

    sealed record RepeatedEnvelope(RepeatedChild First, RepeatedChild? Second);
    sealed record RepeatedChild(string Required, string? Optional, RepeatedEnvelope? Parent);

    sealed record SerializedEnvelope(
        [property: JsonPropertyName("zeta")] long Sequence,
        [property: JsonPropertyName("alpha")] DateTimeOffset ObservedAt);

    sealed record AmbiguousSerializedEnvelope(
        [property: JsonPropertyName("same")] string First,
        [property: JsonPropertyName("same")] string Second);

    [PortableJsonValue(JsonTypeKind.Object)]
    sealed record PortableDocument(IReadOnlyDictionary<string, object?> Content);

    sealed record PortableDocumentEnvelope(PortableDocument Document);

    [JsonConverter(typeof(JsonStringEnumConverter))]
    enum WireDisposition
    {
        [JsonStringEnumMemberName("standard")]
        Standard,

        [JsonStringEnumMemberName("partner-overlay")]
        PartnerOverlay
    }

    enum PlainDisposition
    {
        Standard,
        PartnerOverlay
    }

    [JsonConverter(typeof(CustomConvertedDispositionConverter))]
    enum CustomConvertedDisposition
    {
        Standard
    }

    sealed class CustomConvertedDispositionConverter : JsonConverter<CustomConvertedDisposition>
    {
        public override CustomConvertedDisposition Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => CustomConvertedDisposition.Standard;

        public override void Write(
            Utf8JsonWriter writer,
            CustomConvertedDisposition value,
            JsonSerializerOptions options) => writer.WriteNumberValue((int)value);
    }
}
