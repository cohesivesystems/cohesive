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

    sealed record ImmutableCustomer(string Name, int OrderCount);

    sealed record JsonNamedCustomer(
        [property: JsonPropertyName("legal_name")] string Name,
        int OrderCount);

    sealed record OtherCustomer(string Name, int OrderCount);

    sealed record OptionalCustomer(string Name, string? Note, int Count = 7);

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
