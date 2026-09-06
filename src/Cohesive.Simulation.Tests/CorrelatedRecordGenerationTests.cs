using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cohesive.Model;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Tests;

public sealed class CorrelatedRecordGenerationTests
{
    [Fact]
    public void TypedRecordBinding_SamplesOnceAndProjectsCoherentFields()
    {
        var definition = ShipmentDefinition();
        var generator = definition.Compile();

        var generated = generator.GenerateSequence(seed: 1729, count: 64);

        Assert.All(generated, item =>
        {
            var route = Assert.Single(Routes, route =>
                route.Origin == item.Value.Origin
                && route.Destination == item.Value.Destination);
            Assert.Equal(route.DistanceMiles, item.Value.DistanceMiles);
        });
        var repeated = generator.GenerateSequence(seed: 1729, count: 64);
        Assert.Equal(
            generated.Select(static item => item.Value),
            repeated.Select(static item => item.Value));
        Assert.Equal(
            generated.Select(static item => item.Observation),
            repeated.Select(static item => item.Observation));
        Assert.Equal(
            generated.Select(static item => item.Replay),
            repeated.Select(static item => item.Replay));
    }

    [Fact]
    public void BindingAndMemberDeclarationOrder_IsNonSemantic()
    {
        var routeSource = RouteSource();
        var carrierSource = Gen.Categorical(
            Gen.Weighted(new Carrier("cohesive", "priority"), weight: 1d),
            Gen.Weighted(new Carrier("contoso", "economy"), weight: 1d));
        var route = new RecordGenerationBinding(new("route"), routeSource.Node);
        var carrier = new RecordGenerationBinding(new("carrier"), carrierSource.Node);
        var members = ShipmentMembers(routeSource.Node.ValueType, carrierSource.Node.ValueType);
        var first = RequirePlan(DirectDefinition([route, carrier], members));
        var reordered = RequirePlan(DirectDefinition(
            [carrier, route],
            [.. members.Reverse()]));

        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.Equal(
            ReferenceGenerationInterpreter.Generate(first, seed: 42).Observation,
            ReferenceGenerationInterpreter.Generate(reordered, seed: 42).Observation);
    }

    [Fact]
    public void AddingUnusedBinding_DoesNotPerturbDirectMemberEntropy()
    {
        var number = new RecordGenerationMember(new("Number"), new Int32GenerationNode(1, 1_000));
        var baseline = RequirePlan(DirectDefinition([], [number]));
        var extended = RequirePlan(DirectDefinition(
            [new(new("route"), RouteSource().Node)],
            [number]));

        var before = ReferenceGenerationInterpreter.Generate(baseline, seed: 91).Observation;
        var after = ReferenceGenerationInterpreter.Generate(extended, seed: 91).Observation;

        Assert.Equal(before.GetField("Number"), after.GetField("Number"));
        Assert.NotEqual(baseline.Fingerprint, extended.Fingerprint);
    }

    [Fact]
    public void Replay_RestoresTheExactCorrelatedObservation()
    {
        var plan = ShipmentDefinition().Compile().Plan;
        var generated = ReferenceGenerationInterpreter.Generate(plan, seed: 84, sequenceIndex: 17);

        var replayed = ReferenceGenerationInterpreter.Replay(plan, generated.Replay.ToToken());

        Assert.Equal(generated, replayed);
    }

    [Fact]
    public void PropertyCaseShrinking_ChangesTheSampledRecordAsOneCoherentUnit()
    {
        var generator = ShipmentDefinition().Compile();
        var seed = Enumerable.Range(0, 1_000)
            .First(candidate => generator.Generate(seed: candidate).Value != new Shipment("SEA", "PDX", 174));

        var result = generator.CheckProperty(seed, property: static _ => false);

        var counterexample = Assert.IsType<PropertyCase>(result.BestCounterexample);
        Assert.Equal(
            new Shipment("SEA", "PDX", 174),
            generator.Materializer.Materialize(counterexample.Observation));
        Assert.NotEmpty(counterexample.Replay.ShrinkChoices);
        Assert.Equal(
            counterexample.Observation,
            ReferencePropertyCaseInterpreter.Replay(generator.Plan, counterexample.Replay));
    }

    [Fact]
    public void TypedAuthoring_LowersToEquivalentDirectIr()
    {
        var authored = ShipmentDefinition().Definition;
        var source = RouteSource();
        var direct = new RecordGenerationNode(
            authored.Root.ShapeId,
            [new(new("route"), source.Node)],
            [
                ProjectedMember<string>("Destination", "route", "Destination"),
                ProjectedMember<int>("DistanceMiles", "route", "DistanceMiles"),
                ProjectedMember<string>("Origin", "route", "Origin")
            ]);

        var directDefinition = new GenerationDefinition(
            authored.Id,
            authored.Revision,
            authored.ShapeGraph,
            direct);

        Assert.Equal(
            GenerationDefinitionJsonSerializer.Serialize(directDefinition),
            GenerationDefinitionJsonSerializer.Serialize(authored));
    }

    [Theory]
    [InlineData("duplicate", "simulation.generation.bindingIdentityDuplicate")]
    [InlineData("unknown", "expr.binding.notVisible")]
    [InlineData("path", "expr.field.pathUnknown")]
    [InlineData("scalar", "simulation.generation.bindingSourceNotStructured")]
    [InlineData("type", "expr.result.typeMismatch")]
    [InlineData("dependent-source", "simulation.generation.bindingSourceExpressionUnsupported")]
    public void InvalidBindings_ProduceStructuredDiagnostics(string scenario, string expectedCode)
    {
        var source = RouteSource();
        ImmutableArray<RecordGenerationBinding> bindings = scenario switch
        {
            "duplicate" =>
            [
                new(new("route"), source.Node),
                new(new("route"), source.Node)
            ],
            "scalar" => [new(new("route"), new Int32GenerationNode(1, 10))],
            "dependent-source" =>
            [
                new(new("route"), new ExpressionGenerationNode(
                    source.Node.ValueType,
                    Expr.BoundValue(new("other"))))
            ],
            _ => [new(new("route"), source.Node)]
        };
        RecordGenerationMember member = scenario switch
        {
            "unknown" => ProjectedMember<string>("Value", "missing", "Origin"),
            "path" => ProjectedMember<string>("Value", "route", "Unknown"),
            "type" => ProjectedMember<int>("Value", "route", "Origin"),
            _ => new(new("Value"), new ConstantGenerationNode(
                new ScalarTypeRef(ScalarTypeKind.String),
                ObservationValue.FromString("valid")))
        };

        var result = GenerationCompiler.Compile(DirectDefinition(bindings, [member]));

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Validation.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void PortableDocument_PinsBindingAndExpressionWireIdentity()
    {
        var plan = ShipmentDefinition().Compile().Plan;
        var json = GenerationDefinitionJsonSerializer.Serialize(plan.Definition);
        var root = JsonNode.Parse(json)!.AsObject();
        var canonicalRoot = root["definition"]!["root"]!.AsObject();
        var binding = Assert.Single(canonicalRoot["bindings"]!.AsArray())!.AsObject();
        var projected = canonicalRoot["members"]!.AsArray()[0]!["generator"]!.AsObject();

        Assert.Equal(
            GenerationDefinitionDocument.CurrentSchemaVersion,
            root["schemaVersion"]!.GetValue<string>());
        Assert.Equal("route", binding["identity"]!.GetValue<string>());
        Assert.Equal(
            GenerationDefinitionWireNames.WeightedCategorical,
            binding["generator"]![GenerationDefinitionWireNames.GeneratorDiscriminator]!.GetValue<string>());
        Assert.Equal(
            GenerationDefinitionWireNames.Expression,
            projected[GenerationDefinitionWireNames.GeneratorDiscriminator]!.GetValue<string>());
        Assert.Equal("field", projected["expression"]!["$expr"]!.GetValue<string>());
        Assert.Equal("route", projected["expression"]!["binding"]!.GetValue<string>());
        Assert.Equal("2134ab80be642927a33da0b545b1a79005ad167e8a8e2cb88f6003c4770c65d6", plan.Fingerprint);
        Assert.Equal(json, GenerationDefinitionJsonSerializer.Serialize(
            GenerationDefinitionJsonSerializer.Deserialize(json)));
    }

    [Fact]
    public void PortableDocument_RejectsNonCanonicalBindingOrderAndUnknownExpressionProperties()
    {
        var route = RouteSource();
        var carrier = Gen.Constant(new Carrier("cohesive", "priority"));
        var definition = DirectDefinition(
            [
                new(new("route"), route.Node),
                new(new("carrier"), carrier.Node)
            ],
            [ProjectedMember<string>("Origin", "route", "Origin")]);
        var json = GenerationDefinitionJsonSerializer.Serialize(definition);

        var reordered = Mutate(json, root =>
        {
            var bindings = root["definition"]!["root"]!["bindings"]!.AsArray();
            var first = bindings[0]!.DeepClone();
            var second = bindings[1]!.DeepClone();
            bindings[0] = second;
            bindings[1] = first;
        });
        var unknown = Mutate(json, root =>
            root["definition"]!["root"]!["members"]![0]!["generator"]!["expression"]!["unexpected"] = true);

        Assert.Contains(
            GenerationDefinitionJsonSerializer.TryDeserialize(reordered, out _).Diagnostics,
            diagnostic => diagnostic.Code == "simulation.generation.document.wireNonCanonical");
        Assert.Contains(
            GenerationDefinitionJsonSerializer.TryDeserialize(unknown, out _).Diagnostics,
            diagnostic => diagnostic.Code == "simulation.generation.document.contentInvalid");
    }

    static PocoGenerationDefinition<Shipment> ShipmentDefinition() =>
        Simulation.Define<Shipment>(shipment =>
        {
            var route = shipment.SampleRecord("route", RouteSource());
            shipment
                .Member(value => value.Origin, route.Project(value => value.Origin))
                .Member(value => value.Destination, route.Project(value => value.Destination))
                .Member(value => value.DistanceMiles, route.Project(value => value.DistanceMiles));
        });

    static Generator<Route> RouteSource() => Gen.Categorical(
        Gen.Weighted(Routes[0], weight: 1d),
        Gen.Weighted(Routes[1], weight: 2d),
        Gen.Weighted(Routes[2], weight: 1d));

    static ImmutableArray<RecordGenerationMember> ShipmentMembers(
        TypeRef routeType,
        TypeRef carrierType) =>
    [
        ProjectedMember<string>("Origin", "route", "Origin"),
        ProjectedMember<string>("Destination", "route", "Destination"),
        ProjectedMember<int>("DistanceMiles", "route", "DistanceMiles"),
        new(new("Carrier"), new ExpressionGenerationNode(
            ((ObjectTypeRef)carrierType).Fields.Single(field => field.Name == "Name").Type,
            Expr.Field(new("carrier"), FieldPath.FromField("Name"))))
    ];

    static RecordGenerationMember ProjectedMember<TValue>(
        string output,
        string binding,
        string path) => new(
        new(output),
        new ExpressionGenerationNode(
            new Cohesive.Model.Authoring.DefaultClrTypeRefMapper().Map(typeof(TValue), nullability: null),
            Expr.Field(new(binding), FieldPath.FromField(path))));

    static GenerationDefinition DirectDefinition(
        ImmutableArray<RecordGenerationBinding> bindings,
        ImmutableArray<RecordGenerationMember> members)
    {
        var shapeId = new ShapeId("sample");
        var fields = members
            .GroupBy(static member => member.Identity.Value, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static member => member.Identity.Value, StringComparer.Ordinal)
            .Select(static member => new FieldDefinition(member.Identity, member.Generator.ValueType))
            .ToImmutableArray();
        var graph = new ShapeGraph(new("simulation:correlated:test"), [new(shapeId, fields)]);
        return new(
            id: "simulation:correlated:test",
            revision: "r1",
            shapeGraph: graph,
            root: new(shapeId, bindings, members));
    }

    static CompiledGenerationPlan RequirePlan(GenerationDefinition definition)
    {
        var result = GenerationCompiler.Compile(definition);
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        return Assert.IsType<CompiledGenerationPlan>(result.Plan);
    }

    static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    static readonly ImmutableArray<Route> Routes =
    [
        new("SEA", "PDX", 174),
        new("LAX", "SFO", 383),
        new("JFK", "BOS", 215)
    ];

    public sealed record Route(string Origin, string Destination, int DistanceMiles);

    public sealed record Carrier(string Name, string ServiceLevel);

    public sealed record Shipment(string Origin, string Destination, int DistanceMiles);
}
