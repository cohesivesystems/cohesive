using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Tests;

public sealed class DeterministicPocoGenerationTests
{
    [Fact]
    public void SameDefinitionAndSeed_ProducesSameValueAndReplayEvidence()
    {
        var customers = CustomerDefinition(order: ["Name", "Age", "IsActive"]);
        var generator = customers.Compile();

        var first = generator.Generate(seed: 42);
        var second = generator.Generate(seed: 42);

        Assert.Equal(first.Value, second.Value);
        Assert.Equal(first.Observation, second.Observation);
        Assert.Equal(first.Replay, second.Replay);
        Assert.Equal(42, first.Replay.RootSeed);
        Assert.Equal(ReferenceGenerationInterpreter.Identity, first.Replay.Interpreter);
        Assert.Equal(ReferenceGenerationInterpreter.EntropyAlgorithm, first.Replay.EntropyAlgorithm);
        Assert.Equal(generator.Plan.Fingerprint, first.Replay.DefinitionFingerprint);
    }

    [Fact]
    public void MemberReordering_DoesNotChangeDefinitionFingerprintOrGeneratedValues()
    {
        var first = CustomerDefinition(order: ["Name", "Age", "IsActive"]).Compile();
        var reordered = CustomerDefinition(order: ["IsActive", "Name", "Age"]).Compile();

        var firstGenerated = first.Generate(seed: 1729);
        var reorderedGenerated = reordered.Generate(seed: 1729);

        Assert.Equal(first.Plan.Fingerprint, reordered.Plan.Fingerprint);
        Assert.Equal(firstGenerated.Observation, reorderedGenerated.Observation);
        Assert.Equal(firstGenerated.Value, reorderedGenerated.Value);
    }

    [Fact]
    public void AddingUnrelatedMember_DoesNotPerturbExistingSemanticAddresses()
    {
        var basePlan = RequirePlan(DirectDefinition(
            new RecordGenerationMember(new("age"), new Int32GenerationNode(18, 90)),
            new RecordGenerationMember(new("active"), new BernoulliGenerationNode(0.85))));
        var extendedPlan = RequirePlan(DirectDefinition(
            new RecordGenerationMember(new("name"), new ConstantGenerationNode(
                new ScalarTypeRef(ScalarTypeKind.String),
                ObservationValue.FromString("Ada"))),
            new RecordGenerationMember(new("active"), new BernoulliGenerationNode(0.85)),
            new RecordGenerationMember(new("age"), new Int32GenerationNode(18, 90))));

        var before = ReferenceGenerationInterpreter.Generate(basePlan, seed: 91).Observation;
        var after = ReferenceGenerationInterpreter.Generate(extendedPlan, seed: 91).Observation;

        Assert.Equal(before.GetField("age"), after.GetField("age"));
        Assert.Equal(before.GetField("active"), after.GetField("active"));
        Assert.NotEqual(basePlan.Fingerprint, extendedPlan.Fingerprint);
    }

    [Fact]
    public void DifferentSemanticAddresses_UseIndependentDeterministicStreams()
    {
        var plan = RequirePlan(DirectDefinition(
            new RecordGenerationMember(new("first"), new Int32GenerationNode(int.MinValue, int.MaxValue)),
            new RecordGenerationMember(new("second"), new Int32GenerationNode(int.MinValue, int.MaxValue))));

        var generated = ReferenceGenerationInterpreter.Generate(plan, seed: 1234).Observation;

        Assert.NotEqual(generated.GetField("first"), generated.GetField("second"));
        Assert.Equal(
            generated,
            ReferenceGenerationInterpreter.Generate(plan, seed: 1234).Observation);
    }

    [Fact]
    public void SequenceGeneration_IsDeterministicByItemIndex()
    {
        var generator = CustomerDefinition(order: ["Name", "Age", "IsActive"]).Compile();

        var sequence = generator.GenerateSequence(seed: 71, count: 8);

        Assert.Equal(8, sequence.Length);
        for (var index = 0; index < sequence.Length; index++)
        {
            Assert.Equal(index, sequence[index].Replay.SequenceIndex);
            Assert.Equal(generator.Generate(seed: 71, sequenceIndex: index), sequence[index]);
        }
    }

    [Fact]
    public void WeightedCategorical_ProducesOnlyDeclaredPortableValues()
    {
        var definition = Simulation.Define<TierSample>(sample => sample.Member(
            value => value.Tier,
            Gen.Categorical(
                Gen.Weighted("gold", weight: 1d),
                Gen.Weighted("silver", weight: 3d),
                Gen.Weighted("bronze", weight: 6d))));
        var generator = definition.Compile();

        var values = generator.GenerateSequence(seed: 100, count: 32)
            .Select(static generated => generated.Value.Tier)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(values);
        Assert.Subset(new HashSet<string>(["gold", "silver", "bronze"], StringComparer.Ordinal), values);
    }

    [Fact]
    public void MutableClassAndImmutableRecord_MaterializeThroughCoreObservationMaterializer()
    {
        var mutableGenerator = Simulation.Define<MutableCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant("Ada"))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)))
            .Compile();
        var immutableGenerator = Simulation.Define<ImmutableCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant("Grace"))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)))
            .Compile();
        var mutable = mutableGenerator.Generate(seed: 12);
        var immutable = immutableGenerator.Generate(seed: 12);

        Assert.Equal("Ada", mutable.Value.Name);
        Assert.InRange(mutable.Value.Age, 18, 90);
        Assert.Equal("Grace", immutable.Value.Name);
        Assert.InRange(immutable.Value.Age, 18, 90);
        Assert.Equal(mutableGenerator.Materializer.ShapeId, mutable.Observation.ShapeId);
        Assert.Equal(immutableGenerator.Materializer.ShapeId, immutable.Observation.ShapeId);
    }

    [Fact]
    public void ExplicitCoreMaterializer_RemainsALocalClrInterpretation()
    {
        var definition = Simulation.Define<MutableCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant("Ada"))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
        var materializer = ObservationMaterializer.For<MutableCustomer>(definition.OutputShape)
            .Map(
                fieldIdentity: "Name",
                target: value => value.Name,
                convert: value => value.String!.ToUpperInvariant())
            .Map(fieldIdentity: "Age", target: value => value.Age)
            .Compile();

        var configured = definition.WithMaterializer(materializer);
        var generated = configured
            .Compile()
            .Generate(seed: 12);

        Assert.Equal("ADA", generated.Value.Name);
        Assert.Equal("Ada", generated.Observation.GetField("Name").String);
        Assert.Same(definition.Definition, configured.Definition);
    }

    [Theory]
    [InlineData("range", "simulation.generation.int32RangeInvalid")]
    [InlineData("probability", "simulation.generation.bernoulliProbabilityInvalid")]
    [InlineData("weight", "simulation.generation.categoricalWeightInvalid")]
    public void InvalidGeneratorDefinitions_ProducePreciseDiagnostics(string scenario, string expectedCode)
    {
        ValueGeneratorNode node = scenario switch
        {
            "range" => new Int32GenerationNode(minimum: 2, maximum: 1),
            "probability" => new BernoulliGenerationNode(double.NaN),
            "weight" => new WeightedCategoricalGenerationNode(
                new ScalarTypeRef(ScalarTypeKind.String),
                [new(ObservationValue.FromString("invalid"), weight: 0d)]),
            _ => throw new InvalidOperationException($"Unknown test scenario '{scenario}'.")
        };
        var result = GenerationCompiler.Compile(DirectDefinition(
            new RecordGenerationMember(new("value"), node)));

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Validation.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void DuplicateAndMissingPocoBindings_ProducePreciseDiagnostics()
    {
        var duplicate = Simulation.Define<MutableCustomer>(customer => customer
            .Member(value => value.Name, Gen.Constant("Ada"))
            .Member(value => value.Name, Gen.Constant("Grace")));
        var result = duplicate.CompileResult();

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Validation.Diagnostics, diagnostic =>
            diagnostic.Code == "simulation.generation.memberIdentityDuplicate");
        Assert.Contains(result.Validation.Diagnostics, diagnostic =>
            diagnostic.Code == "simulation.generation.clrBindingDuplicate");
        Assert.Contains(result.Validation.Diagnostics, diagnostic =>
            diagnostic.Code == "simulation.generation.clrBindingMissing");
    }

    [Fact]
    public void UnsupportedMaterialization_ProducesStructuredDiagnostic()
    {
        var result = Simulation.Define<NoPublicConstructor>(value => value.Member(
                member => member.Name,
                Gen.Constant("Ada")))
            .CompileResult();

        Assert.False(result.IsSuccessful);
        Assert.Contains(result.Validation.Diagnostics, diagnostic =>
            diagnostic.Code == "simulation.generation.materializerUnsupported");
    }

    [Fact]
    public void PackageDependencyBoundary_ReferencesOnlyCohesiveCore()
    {
        var cohesiveReferences = typeof(Simulation).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name!)
            .Where(static name => name.StartsWith("Cohesive", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal("Cohesive", Assert.Single(cohesiveReferences));
    }

    static PocoGenerationDefinition<Customer> CustomerDefinition(IReadOnlyList<string> order)
    {
        return Simulation.Define<Customer>(customer =>
        {
            foreach (var member in order)
            {
                switch (member)
                {
                    case "Name":
                        customer.Member(value => value.Name, Gen.Constant("Ada"));
                        break;
                    case "Age":
                        customer.Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90));
                        break;
                    case "IsActive":
                        customer.Member(value => value.IsActive, Gen.Bernoulli(probability: 0.85));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown customer member '{member}'.");
                }
            }
        });
    }

    static GenerationDefinition DirectDefinition(params RecordGenerationMember[] members)
    {
        var shapeId = new ShapeId("sample");
        var fields = members
            .GroupBy(static member => member.Identity.Value, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static member => member.Identity.Value, StringComparer.Ordinal)
            .Select(static member => new FieldDefinition(
                name: member.Identity,
                type: member.Generator.ValueType))
            .ToImmutableArray();
        var memberKey = string.Join(
            ",",
            fields.Select(static field => field.Name.Value));
        var graph = new ShapeGraph(
            new GraphId($"simulation:test:{memberKey}"),
            [new Shape(shapeId, fields)]);
        return new(
            id: "simulation:test:definition",
            revision: "r1",
            shapeGraph: graph,
            root: new RecordGenerationNode(shapeId, [.. members]));
    }

    static CompiledGenerationPlan RequirePlan(GenerationDefinition definition)
    {
        var result = GenerationCompiler.Compile(definition);
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<CompiledGenerationPlan>(result.Plan);
    }

    public sealed record Customer(string Name, int Age, bool IsActive);

    public sealed class MutableCustomer
    {
        public string Name { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    public sealed record ImmutableCustomer(string Name, int Age);

    public sealed record TierSample(string Tier);

    public sealed class NoPublicConstructor
    {
        NoPublicConstructor(string name) => Name = name;

        public string Name { get; }
    }
}
