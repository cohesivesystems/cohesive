using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using CoreObservation = Cohesive.Model.Observation;

namespace Cohesive.Tests.Model;

public sealed class CoreObservationMaterializationTests
{
    [Fact]
    public void Materialize_DefaultPlanSupportsImmutableRecordsAndIsCachedByQualifiedShape()
    {
        var firstMetadata = BuildMetadata<ImmutableCustomer>("customer-materialization-v1");
        var first = Observe<ImmutableCustomer>(
            firstMetadata,
            (nameof(ImmutableCustomer.Name), ObservationValue.FromString("Ada")),
            (nameof(ImmutableCustomer.OrderCount), ObservationValue.FromInt64(3)));
        var second = Observe<ImmutableCustomer>(
            firstMetadata,
            (nameof(ImmutableCustomer.Name), ObservationValue.FromString("Grace")),
            (nameof(ImmutableCustomer.OrderCount), ObservationValue.FromInt64(5)));
        var anotherVersionMetadata = BuildMetadata<ImmutableCustomer>("customer-materialization-v2");
        var anotherVersion = Observe<ImmutableCustomer>(
            anotherVersionMetadata,
            (nameof(ImmutableCustomer.Name), ObservationValue.FromString("Lin")),
            (nameof(ImmutableCustomer.OrderCount), ObservationValue.FromInt64(8)));

        var firstPlan = ObservationMaterializer.GetDefault<ImmutableCustomer>(first);
        var secondPlan = ObservationMaterializer.GetDefault<ImmutableCustomer>(second);
        var anotherVersionPlan = ObservationMaterializer.GetDefault<ImmutableCustomer>(anotherVersion);

        Assert.Equal(new ImmutableCustomer("Ada", 3), first.Materialize<ImmutableCustomer>());
        Assert.Equal(new ImmutableCustomer("Grace", 5), secondPlan.Materialize(second));
        Assert.Same(firstPlan, secondPlan);
        Assert.NotSame(firstPlan, anotherVersionPlan);
        Assert.Equal(first.ShapeId, firstPlan.ShapeId);
        Assert.Equal(anotherVersion.ShapeId, anotherVersionPlan.ShapeId);
    }

    [Fact]
    public void Compile_SupportsMutablePocosExplicitMappingsConvertersAndFrozenSerializerOptions()
    {
        var graph = new ShapeGraph(
            id: new("external-customer-v1"),
            shapes:
            [
                new Shape(
                    id: new("external-customer"),
                    fields:
                    [
                        new(new("legal_name"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new(new("count_text"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new(new("raw_code"), new ScalarTypeRef(ScalarTypeKind.String))
                    ])
            ]);
        var shape = new GraphShapeId(graph, new("external-customer"));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        var materializer = ObservationMaterializer
            .For<MutableCustomer>(shape)
            .Map("legal_name", customer => customer.Name)
            .Map("count_text", customer => customer.Count)
            .Map("raw_code", customer => customer.Code, value => value.GetString()!.ToUpperInvariant())
            .WithSerializerOptions(options)
            .Compile();
        options.NumberHandling = JsonNumberHandling.Strict;
        var observation = CoreObservation.Create(
            shape,
            Fields(
                ("legal_name", ObservationValue.FromString("Ada")),
                ("count_text", ObservationValue.FromString("42")),
                ("raw_code", ObservationValue.FromString("x12"))));

        var result = materializer.Materialize(observation);

        Assert.Equal("Ada", result.Name);
        Assert.Equal(42, result.Count);
        Assert.Equal("X12", result.Code);
    }

    [Fact]
    public void Compile_FromQualifiedIdentitySupportsPhysicalReadersAndFieldIdentityConventions()
    {
        Shape definition = new(
            new("customer"),
            [
                new(new("customer_name"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("order_count"), new ScalarTypeRef(ScalarTypeKind.Int64))
            ]);
        ShapeGraph graph = new(new("customer-materialization-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var observation = CoreObservation.Create(
            shape,
            Fields(
                ("customer_name", ObservationValue.FromString("Ada")),
                ("order_count", ObservationValue.FromInt64(3))));
        var materializer = ObservationMaterializer
            .For<ImmutableCustomer>(shape.QualifiedId)
            .WithImplicitFieldIdentityConvention(property => property.Name switch
            {
                nameof(ImmutableCustomer.Name) => "customer_name",
                nameof(ImmutableCustomer.OrderCount) => "order_count",
                _ => property.Name
            })
            .Compile();

        var result = materializer.Materialize((IObservationFieldReader)observation);

        Assert.Equal(new ImmutableCustomer("Ada", 3), result);
    }

    [Fact]
    public void Compile_RejectsDefaultQualifiedIdentityAndEmptyFieldIdentityConvention()
    {
        var defaultIdentityFailure = Assert.Throws<ArgumentException>(() =>
            ObservationMaterializer.For<ImmutableCustomer>(default(QualifiedShapeId)));
        var shape = ShapeFor<ImmutableCustomer>(BuildMetadata<ImmutableCustomer>("customer-materialization-v1"));
        var conventionFailure = Assert.Throws<InvalidOperationException>(() =>
            ObservationMaterializer
                .For<ImmutableCustomer>(shape.QualifiedId)
                .WithImplicitFieldIdentityConvention(static _ => " ")
                .Compile());

        Assert.Equal("shapeId", defaultIdentityFailure.ParamName);
        Assert.Contains("empty identity", conventionFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithClrShapeMetadata_UsesEffectiveSystemTextJsonFieldIdentities()
    {
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var metadata = new ClrShapeGraphBuilder()
            .AddMetadataProvider(new SystemTextJsonClrShapeMetadataProvider(serializerOptions))
            .AddShape<JsonNamedCustomer>()
            .BuildResult(new("json-named-customer-v1"));
        var shape = ShapeFor<JsonNamedCustomer>(metadata);
        var observation = CoreObservation.Create(
            shape,
            Fields(
                ("legal_name", ObservationValue.FromString("Ada")),
                ("order_count", ObservationValue.FromInt64(7))));
        var materializer = ObservationMaterializer
            .For<JsonNamedCustomer>(shape)
            .WithClrShapeMetadata(metadata)
            .Compile();

        var result = materializer.Materialize(observation);

        Assert.Equal(new JsonNamedCustomer("Ada", 7), result);
        Assert.Equal(
            "legal_name",
            metadata.ResolveMemberPath(
                typeof(JsonNamedCustomer),
                [typeof(JsonNamedCustomer).GetProperty(nameof(JsonNamedCustomer.Name))!])
                .Segments[0]
                .Segment);
    }

    [Fact]
    public void WithClrShapeMetadata_RejectsAnotherGraphOrRootShape()
    {
        var metadata = BuildMetadata<ImmutableCustomer>("metadata-customer-v1");
        var anotherGraph = BuildMetadata<ImmutableCustomer>("metadata-customer-v2");
        var graphMismatch = ObservationMaterializer
            .For<ImmutableCustomer>(ShapeFor<ImmutableCustomer>(anotherGraph))
            .WithClrShapeMetadata(metadata);
        var wrongRootMetadata = BuildMetadata<OtherCustomer>("metadata-customer-v1");
        var missingTarget = ObservationMaterializer
            .For<ImmutableCustomer>(ShapeFor<OtherCustomer>(wrongRootMetadata))
            .WithClrShapeMetadata(wrongRootMetadata);

        var graphFailure = Assert.Throws<InvalidOperationException>(() => graphMismatch.Compile());
        var targetFailure = Assert.Throws<InvalidOperationException>(() => missingTarget.Compile());

        Assert.Contains("does not match materializer graph", graphFailure.Message, StringComparison.Ordinal);
        Assert.Contains("does not contain root target type", targetFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultMaterializers_AllocateOnlyTheDestinationObjectGraphForNestedState()
    {
        var observation = ProjectionObservation();
        var materializer = ObservationMaterializer.For<ProjectionState>(
            new GraphShapeId(observation.Graph, observation.Shape.Id)).Compile();
        Func<ProjectionState> handwritten = () => MaterializeProjectionHandwritten(observation.Observation);
        Func<ProjectionState> compiled = () => materializer.Materialize(observation.Observation);
        Func<ProjectionState> cachedDefault = () => observation.Observation.Materialize<ProjectionState>();

        var handwrittenAllocated = MeasureAllocations(handwritten, out var handwrittenResult);
        var compiledAllocated = MeasureAllocations(compiled, out var compiledResult);
        var cachedDefaultAllocated = MeasureAllocations(cachedDefault, out var cachedDefaultResult);

        AssertProjection(handwrittenResult);
        AssertProjection(compiledResult);
        AssertProjection(cachedDefaultResult);
        Assert.True(handwrittenAllocated > 0);
        Assert.Equal(handwrittenAllocated, compiledAllocated);
        Assert.Equal(handwrittenAllocated, cachedDefaultAllocated);
    }

    [Fact]
    public void DefaultPlan_CustomNestedJsonContractUsesJsonCompatibilityPath()
    {
        Shape definition = new(
            new("custom-contract-state"),
            [new(new("Code"), new ScalarTypeRef(ScalarTypeKind.String))]);
        ShapeGraph graph = new(new("custom-contract-state-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var observation = CoreObservation.Create(
            shape,
            Fields(("Code", ObservationValue.FromString("x12"))));

        var result = ObservationMaterializer
            .For<CustomContractState>(shape)
            .Compile()
            .Materialize(observation);

        Assert.Equal(new CustomCode("converted:x12"), result.Code);
    }

    [Fact]
    public void DefaultPlan_DoesNotApplyBooleanStringCoercionBeyondJsonContract()
    {
        Shape definition = new(
            new("string-boolean-state"),
            [new(new("Enabled"), new ScalarTypeRef(ScalarTypeKind.String))]);
        ShapeGraph graph = new(new("string-boolean-state-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var observation = CoreObservation.Create(
            shape,
            Fields(("Enabled", ObservationValue.FromString("true"))));
        var materializer = ObservationMaterializer.For<StringBooleanState>(shape).Compile();

        Assert.Throws<JsonException>(() => materializer.Materialize(observation));
    }

    [Fact]
    public void MissingFields_RespectOptionalExplicitDefaultThrowAndAllMemberPolicies()
    {
        var optionalShape = OptionalShape<OptionalCustomer>("optional-customer-v1");
        var optionalObservation = CoreObservation.Create(
            optionalShape,
            Fields((nameof(OptionalCustomer.Name), ObservationValue.FromString("Ada"))));
        var defaultMaterializer = ObservationMaterializer.For<OptionalCustomer>(optionalShape).Compile();
        var throwingMaterializer = ObservationMaterializer
            .For<OptionalCustomer>(optionalShape)
            .WithMissingFieldBehavior(ObservationMissingFieldBehavior.Throw)
            .Compile();

        var defaultResult = defaultMaterializer.Materialize(optionalObservation);
        var missingFailure = Assert.Throws<InvalidOperationException>(
            () => throwingMaterializer.Materialize(optionalObservation));

        Assert.Equal("Ada", defaultResult.Name);
        Assert.Null(defaultResult.Note);
        Assert.Equal(7, defaultResult.Count);
        Assert.Contains("missing required field", missingFailure.Message, StringComparison.Ordinal);

        var allMembersShape = OptionalShape<MutableRequiredDefaultCustomer>("all-default-customer-v1");
        var allMembersObservation = CoreObservation.Create(
            allMembersShape,
            Fields((nameof(MutableRequiredDefaultCustomer.Name), ObservationValue.FromString("Grace"))));
        var allMembersResult = ObservationMaterializer
            .For<MutableRequiredDefaultCustomer>(allMembersShape)
            .WithMissingFieldBehavior(ObservationMissingFieldBehavior.UseDefaultForAllMembers)
            .Compile()
            .Materialize(allMembersObservation);

        Assert.Equal("Grace", allMembersResult.Name);
        Assert.Equal(0, allMembersResult.Count);
    }

    [Fact]
    public void Compile_RejectsAmbiguousMaximumArityConstructors()
    {
        var metadata = BuildMetadata<AmbiguousCustomer>("ambiguous-customer-v1");

        var exception = Assert.Throws<InvalidOperationException>(() => ObservationMaterializer
            .For<AmbiguousCustomer>(ShapeFor<AmbiguousCustomer>(metadata))
            .Compile());

        Assert.Contains("constructors", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ambiguous", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Materialize_RejectsObservationWithAnotherQualifiedShape()
    {
        var firstMetadata = BuildMetadata<ImmutableCustomer>("customer-shape-v1");
        var secondMetadata = BuildMetadata<ImmutableCustomer>("customer-shape-v2");
        var secondObservation = Observe<ImmutableCustomer>(
            secondMetadata,
            (nameof(ImmutableCustomer.Name), ObservationValue.FromString("Ada")),
            (nameof(ImmutableCustomer.OrderCount), ObservationValue.FromInt64(1)));
        var materializer = ObservationMaterializer
            .For<ImmutableCustomer>(ShapeFor<ImmutableCustomer>(firstMetadata))
            .Compile();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            materializer.Materialize(secondObservation));

        Assert.Contains("does not match observation shape", exception.Message, StringComparison.Ordinal);
    }

    static ClrShapeGraphBuildResult BuildMetadata<T>(string graphId) where T : notnull =>
        new ClrShapeGraphBuilder()
            .AddShape<T>()
            .BuildResult(new(graphId));

    static GraphShapeId ShapeFor<T>(ClrShapeGraphBuildResult metadata) =>
        new(metadata.Graph, metadata.ShapeIds[typeof(T)]);

    static CoreObservation Observe<T>(
        ClrShapeGraphBuildResult metadata,
        params (string Name, ObservationValue Value)[] fields) =>
        CoreObservation.Create(ShapeFor<T>(metadata), Fields(fields));

    static GraphShapeId OptionalShape<T>(string graphId)
    {
        Shape shape = new(
            id: new(typeof(T).Name),
            fields:
            [
                new(new(nameof(OptionalCustomer.Name)), new ScalarTypeRef(ScalarTypeKind.String)),
                new(
                    new(nameof(OptionalCustomer.Note)),
                    new ScalarTypeRef(ScalarTypeKind.String),
                    presence: FieldPresence.Optional),
                new(
                    new(nameof(OptionalCustomer.Count)),
                    new ScalarTypeRef(ScalarTypeKind.Int64),
                    presence: FieldPresence.Optional)
            ]);
        ShapeGraph graph = new(new(graphId), [shape]);
        return new(graph, shape.Id);
    }

    static Dictionary<string, ObservationValue> Fields(
        params (string Name, ObservationValue Value)[] fields) =>
        fields.ToDictionary(
            static field => field.Name,
            static field => field.Value,
            StringComparer.Ordinal);

    static ProjectionObservationFixture ProjectionObservation()
    {
        Shape definition = new(
            new("projection-state"),
            [
                new(new("Id"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("Version"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                new(new("Name"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("Enabled"), new ScalarTypeRef(ScalarTypeKind.Bool)),
                new(new("Balance"), new ScalarTypeRef(ScalarTypeKind.Decimal)),
                new(
                    new("Address"),
                    new ObjectTypeRef(
                    [
                        new("City", new ScalarTypeRef(ScalarTypeKind.String)),
                        new("PostalCode", new ScalarTypeRef(ScalarTypeKind.String))
                    ])),
                new(
                    new("Tags"),
                    new ScalarTypeRef(ScalarTypeKind.String),
                    cardinality: FieldCardinality.Many)
            ]);
        ShapeGraph graph = new(new("projection-state-v1"), [definition]);
        GraphShapeId shape = new(graph, definition.Id);
        var observation = CoreObservation.Create(
            shape,
            Fields(
                ("Id", ObservationValue.FromString("state-42")),
                ("Version", ObservationValue.FromInt64(17)),
                ("Name", ObservationValue.FromString("Primary account")),
                ("Enabled", ObservationValue.FromBool(true)),
                ("Balance", ObservationValue.FromDecimal(1250.75m)),
                ("Address", ObservationValue.FromObject(Fields(
                    ("City", ObservationValue.FromString("Seattle")),
                    ("PostalCode", ObservationValue.FromString("98101"))))),
                ("Tags", ObservationValue.FromArray(
                [
                    ObservationValue.FromString("priority"),
                    ObservationValue.FromString("west")
                ]))));
        return new(graph, definition, observation);
    }

    static ProjectionState MaterializeProjectionHandwritten(CoreObservation observation)
    {
        var fields = observation.Fields;
        var address = fields["Address"].Fields!;
        var observedTags = fields["Tags"].EnumerateArray();
        var tags = new string[observedTags.Length];
        for (var index = 0; index < tags.Length; index++)
        {
            tags[index] = observedTags[index].GetString()!;
        }

        return new(
            fields["Id"].GetString()!,
            fields["Version"].GetInt64(),
            fields["Name"].GetString()!,
            fields["Enabled"].GetBoolean(),
            fields["Balance"].GetDecimal(),
            new(address["City"].GetString()!, address["PostalCode"].GetString()!),
            tags);
    }

    static long MeasureAllocations<T>(Func<T> operation, out T result)
    {
        const int WarmupIterations = 100;
        const int MeasurementIterations = 1_000;
        result = default!;
        for (var iteration = 0; iteration < WarmupIterations; iteration++)
        {
            result = operation();
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < MeasurementIterations; iteration++)
        {
            result = operation();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        GC.KeepAlive(result);
        return allocated;
    }

    static void AssertProjection(ProjectionState state)
    {
        Assert.Equal("state-42", state.Id);
        Assert.Equal(17, state.Version);
        Assert.Equal("Primary account", state.Name);
        Assert.True(state.Enabled);
        Assert.Equal(1250.75m, state.Balance);
        Assert.Equal(new ProjectionAddress("Seattle", "98101"), state.Address);
        Assert.Equal(["priority", "west"], state.Tags);
    }

    sealed record ImmutableCustomer(string Name, int OrderCount);

    sealed record JsonNamedCustomer(
        [property: JsonPropertyName("legal_name")] string Name,
        int OrderCount);

    sealed record OtherCustomer(string Name, int OrderCount);

    sealed record OptionalCustomer(string Name, string? Note, int Count = 7);

    sealed record ProjectionState(
        string Id,
        long Version,
        string Name,
        bool Enabled,
        decimal Balance,
        ProjectionAddress Address,
        string[] Tags);

    sealed record ProjectionAddress(string City, string PostalCode);

    sealed record ProjectionObservationFixture(
        ShapeGraph Graph,
        Shape Shape,
        CoreObservation Observation);

    sealed record CustomContractState(CustomCode Code);

    sealed record StringBooleanState(bool Enabled);

    [JsonConverter(typeof(CustomCodeJsonConverter))]
    sealed record CustomCode(string Value);

    sealed class CustomCodeJsonConverter : JsonConverter<CustomCode>
    {
        public override CustomCode Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            new($"converted:{reader.GetString()}");

        public override void Write(
            Utf8JsonWriter writer,
            CustomCode value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    sealed class MutableCustomer
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }

        public string Code { get; set; } = string.Empty;
    }

    sealed class MutableRequiredDefaultCustomer
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; } = 99;
    }

    sealed class AmbiguousCustomer
    {
        public AmbiguousCustomer(string a) => A = a;

        public AmbiguousCustomer(int b) => B = b;

        public string A { get; } = string.Empty;

        public int B { get; }
    }
}
